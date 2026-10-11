using Microsoft.Data.Sqlite;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Time;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// One <c>handbacks</c> row (migration 0015): the copy Weir wrote into a library's output folder for a manager to import,
/// what a manager said about it, and whether Weir still looks after it. The library's folders come with it, because the
/// release rule needs them.
/// </summary>
public sealed record HandbackRow(
    long Id,
    long LibraryId,
    string RelativePath,
    string OutputPath,
    long OutputSize,
    long OutputMtimeNs,
    DateTimeOffset? WrittenAt,
    string? Outcome,
    string? OutcomeBy,
    DateTimeOffset? OutcomeAt,
    string? ImportedPath,
    string? OutcomeReason,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? SettledAt,
    string? ReleaseNote,
    string WatchedFolder,
    string OutputFolder,
    string MediaType,
    ManagerSpeaker? OutcomeSpeaker = null);

/// <summary>What the release rule did with a copy.</summary>
public enum HandbackReleaseKind
{
    /// <summary>The copy was exactly the file Weir wrote, and Weir removed it.</summary>
    Removed,

    /// <summary>The copy was already gone: the manager moved it.</summary>
    AlreadyGone,

    /// <summary>Weir left the copy where it is, and the note says why. Weir does not look at it again.</summary>
    Kept,

    /// <summary>Weir tried and could not remove it (in use, no permission). A later attempt may.</summary>
    InUse,
}

/// <summary>The release rule's answer, with its reason in plain words.</summary>
public sealed record HandbackRelease(HandbackReleaseKind Kind, string Note)
{
    public bool Removed => Kind == HandbackReleaseKind.Removed;

    /// <summary>Weir stops looking after the copy: anything except a removal that could not happen this time.</summary>
    public bool Settles => Kind != HandbackReleaseKind.InUse;
}

/// <summary>
/// The <c>handbacks</c> table, and the one rule every removal of a hand-back copy goes through (#652). A manager's
/// "imported" (Sonarr's and Radarr's webhook, Deluno's outcome) and the Cleanup job's "nobody claimed it" all remove a
/// copy only by <see cref="Release"/>.
/// </summary>
public sealed class HandbackStore
{
    private const string Columns =
        "h.id, h.library_id, h.relative_path, h.output_path, h.output_size, h.output_mtime_ns, h.written_at, h.outcome, h.outcome_by, " +
        "h.outcome_at, h.imported_path, h.outcome_reason, h.released_at, h.settled_at, h.release_note, l.watched_folder, l.output_folder, l.media_type, " +
        "h.outcome_source_key, h.outcome_connection_id, h.outcome_authenticated";

    private const string From = "FROM handbacks h JOIN libraries l ON l.id = h.library_id";

    /// <summary>
    /// A pass wrote <paramref name="outputPath"/> for the file at <paramref name="relativePath"/>: remember exactly what it
    /// wrote, and the size and modification time of the source it was cleaned from (<paramref name="sourceSizeBytes"/> and
    /// <paramref name="sourceModifiedTimeNs"/>, both or neither), which is how a later hand-off of the same source is
    /// recognised even once the file has left the list. A new copy of the same file starts its story over. A copy Weir cannot
    /// measure is not recorded, so it can never be removed.
    /// </summary>
    public async Task RecordWrittenAsync(
        UnitOfWork uow, long libraryId, string relativePath, string outputPath, DateTimeOffset now, long? sourceSizeBytes = null, long? sourceModifiedTimeNs = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        if (string.IsNullOrWhiteSpace(outputPath) || !TryMeasure(outputPath, out var size, out var mtimeNs))
        {
            return;
        }

        await uow.ExecuteAsync(
            "INSERT INTO handbacks (library_id, relative_path, output_path, output_size, output_mtime_ns, written_at, source_size, source_mtime_ns) " +
            "VALUES ($library, $path, $output, $size, $mtime, $now, $source_size, $source_mtime) " +
            "ON CONFLICT (library_id, relative_path) DO UPDATE SET output_path = excluded.output_path, output_size = excluded.output_size, " +
            "output_mtime_ns = excluded.output_mtime_ns, written_at = excluded.written_at, source_size = excluded.source_size, " +
            "source_mtime_ns = excluded.source_mtime_ns, outcome = NULL, outcome_by = NULL, outcome_at = NULL, " +
            "imported_path = NULL, outcome_reason = NULL, released_at = NULL, settled_at = NULL, release_note = NULL, " +
            "outcome_source_key = NULL, outcome_connection_id = NULL, outcome_authenticated = 1, updated_at = CURRENT_TIMESTAMP",
            ("$library", libraryId),
            ("$path", relativePath),
            ("$output", outputPath),
            ("$size", size),
            ("$mtime", mtimeNs),
            ("$now", TimestampColumns.Orm(now)),
            ("$source_size", sourceSizeBytes),
            ("$source_mtime", sourceSizeBytes is null ? null : sourceModifiedTimeNs)).ConfigureAwait(false);
    }

    /// <summary>A file's size and modification time (ns since the Unix epoch, as <c>SourceFiles.Fingerprint</c> measures it).</summary>
    public static bool TryMeasure(string path, out long size, out long mtimeNs)
    {
        size = 0;
        mtimeNs = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            size = info.Length;
            mtimeNs = (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public Task<HandbackRow?> FindAsync(UnitOfWork uow, long libraryId, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QuerySingleAsync(
            $"SELECT {Columns} {From} WHERE h.library_id = $library AND h.relative_path = $path",
            Read,
            ("$library", libraryId),
            ("$path", relativePath));
    }

    /// <summary>Every copy in these libraries, by file, for the Files and Activity lists.</summary>
    public async Task<Dictionary<(long LibraryId, string RelativePath), HandbackRow>> ForLibrariesAsync(UnitOfWork uow, IEnumerable<long> libraryIds)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var ids = libraryIds.Distinct().ToList();
        var found = new Dictionary<(long, string), HandbackRow>();
        if (ids.Count == 0)
        {
            return found;
        }

        var placeholders = string.Join(",", ids.Select((_, index) => $"$l{index}"));
        foreach (var row in await uow.QueryAsync(
            $"SELECT {Columns} {From} WHERE h.library_id IN ({placeholders})",
            Read,
            [.. ids.Select((id, index) => ($"$l{index}", (object?)id))]).ConfigureAwait(false))
        {
            found[(row.LibraryId, row.RelativePath)] = row;
        }

        return found;
    }

    /// <summary>Copies whose file name is <paramref name="fileName"/> (compared without case, so a match is only a candidate).</summary>
    public Task<List<HandbackRow>> WithFileNameAsync(UnitOfWork uow, string fileName)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QueryAsync(
            $"SELECT {Columns} {From} WHERE length(h.output_path) >= length($name) AND lower(substr(h.output_path, -length($name))) = lower($name)",
            Read,
            ("$name", fileName));
    }

    /// <summary>Copies of one kind of library that nobody has said anything about and Weir still looks after, written before <paramref name="writtenBefore"/>.</summary>
    public Task<List<HandbackRow>> UnclaimedAsync(UnitOfWork uow, string mediaScope, DateTimeOffset writtenBefore)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QueryAsync(
            $"SELECT {Columns} {From} WHERE l.media_type = $scope AND h.outcome IS NULL AND h.settled_at IS NULL AND h.written_at <= $cutoff ORDER BY h.id",
            Read,
            ("$scope", mediaScope),
            ("$cutoff", TimestampColumns.Orm(writtenBefore)));
    }

    /// <summary>What a manager said about the copy, and who said it (<paramref name="by"/> is the name Activity shows for it).</summary>
    public Task RecordOutcomeAsync(
        UnitOfWork uow, long id, string outcome, string by, ManagerSpeaker speaker, DateTimeOffset at, string? importedPath, string? reason)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(speaker);
        return uow.ExecuteAsync(
            "UPDATE handbacks SET outcome = $outcome, outcome_by = $by, outcome_source_key = $source, outcome_connection_id = $connection, " +
            "outcome_authenticated = $authenticated, outcome_at = $at, imported_path = $imported, outcome_reason = $reason, " +
            "updated_at = CURRENT_TIMESTAMP WHERE id = $id",
            ("$source", speaker.SourceKey),
            ("$connection", speaker.ConnectionId),
            ("$authenticated", speaker.Authenticated ? 1 : 0),
            ("$outcome", outcome),
            ("$by", by),
            ("$at", TimestampColumns.Orm(at)),
            ("$imported", string.IsNullOrWhiteSpace(importedPath) ? null : WireStrings.Slice(importedPath.Trim(), 4000)),
            ("$reason", string.IsNullOrWhiteSpace(reason) ? null : WireStrings.Slice(reason.Trim(), 2000)),
            ("$id", id));
    }

    /// <summary>
    /// What the release rule did. A copy that settles is never looked at again; one Weir could not remove this time keeps
    /// its note and stays in the Cleanup job's care.
    /// </summary>
    public Task RecordReleaseAsync(UnitOfWork uow, long id, HandbackRelease release, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(release);
        var stamp = TimestampColumns.Orm(now);
        return uow.ExecuteAsync(
            "UPDATE handbacks SET released_at = $released, settled_at = $settled, release_note = $note, updated_at = CURRENT_TIMESTAMP WHERE id = $id",
            ("$released", release.Removed ? stamp : null),
            ("$settled", release.Settles ? stamp : null),
            ("$note", WireStrings.Slice(release.Note, 2000)),
            ("$id", id));
    }

    /// <summary>
    /// The copy's path below its library's output folder, one part per folder and the name last; empty when it is not
    /// under that folder.
    /// </summary>
    public static IReadOnlyList<string> RelativeParts(HandbackRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(row.OutputFolder))
        {
            return [];
        }

        try
        {
            var relative = RemuxPassPaths.RelativeTo(RemuxPassPaths.Resolve(row.OutputPath), RemuxPassPaths.Resolve(row.OutputFolder.Trim()));
            return relative is null ? [] : [.. relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(part => part.Length > 0)];
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// The release rule (#652), shared by every caller that may remove a hand-back copy. Weir removes the copy only when all
    /// of these hold, and otherwise leaves it and says why:
    /// <list type="bullet">
    /// <item>it is inside its library's output folder, and not inside the watched folder (so a download is never touched);</item>
    /// <item>it is not the manager's own library file (<paramref name="libraryFilePath"/>, when the manager named one);</item>
    /// <item>it is still at the path Weir wrote, and still exactly the file Weir wrote: the same size and modification time.</item>
    /// </list>
    /// A copy that is already gone was moved by the manager, which is recorded and removes nothing. Nothing but that one
    /// file is ever removed: no folder, no sidecar, and never anything outside the output folder.
    /// </summary>
    public static HandbackRelease Release(HandbackRow row, string manager, string? libraryFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        string copy;
        string outputRoot;
        string? watchedRoot;
        try
        {
            if (string.IsNullOrWhiteSpace(row.OutputFolder))
            {
                return new HandbackRelease(HandbackReleaseKind.Kept, HandbackRules.OutsideNote);
            }

            copy = RemuxPassPaths.Resolve(row.OutputPath);
            outputRoot = RemuxPassPaths.Resolve(row.OutputFolder.Trim());
            watchedRoot = string.IsNullOrWhiteSpace(row.WatchedFolder) ? null : RemuxPassPaths.Resolve(row.WatchedFolder.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return new HandbackRelease(HandbackReleaseKind.Kept, HandbackRules.OutsideNote);
        }

        if (RemuxPassPaths.RelativeTo(copy, outputRoot) is not { Length: > 0 } || (watchedRoot is not null && RemuxPassPaths.IsUnder(copy, watchedRoot)))
        {
            return new HandbackRelease(HandbackReleaseKind.Kept, HandbackRules.OutsideNote);
        }

        if (libraryFilePath is not null && HandbackRules.SameManagerPath(libraryFilePath, copy))
        {
            return new HandbackRelease(HandbackReleaseKind.Kept, HandbackRules.SameAsLibraryNote);
        }

        FileInfo info;
        try
        {
            info = new FileInfo(copy);
            if (!info.Exists)
            {
                return Directory.Exists(copy)
                    ? new HandbackRelease(HandbackReleaseKind.Kept, HandbackRules.ChangedNote)
                    : new HandbackRelease(HandbackReleaseKind.AlreadyGone, HandbackRules.AlreadyGoneNote(manager));
            }

            var mtimeNs = (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100;
            if (info.Length != row.OutputSize || mtimeNs != row.OutputMtimeNs || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new HandbackRelease(HandbackReleaseKind.Kept, HandbackRules.ChangedNote);
            }

            info.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HandbackRelease(HandbackReleaseKind.InUse, HandbackRules.InUseNote(exception.Message.TrimEnd('.')));
        }

        return new HandbackRelease(HandbackReleaseKind.Removed, HandbackRules.RemovedNote(manager));
    }

    /// <summary>The row as the Files and Activity lists show it (<c>handback</c>), or null when Weir wrote no copy.</summary>
    public static WireValue ToOut(HandbackRow? row)
    {
        if (row is null)
        {
            return WireNull.Instance;
        }

        return new WireObject()
            .Set("output_path", row.OutputPath)
            .Set("written_at", Stamp(row.WrittenAt))
            .Set("outcome", row.Outcome)
            .Set("outcome_by", row.OutcomeBy)
            .Set("outcome_at", Stamp(row.OutcomeAt))
            .Set("imported_path", row.ImportedPath)
            .Set("outcome_reason", row.OutcomeReason)
            .Set("released_at", Stamp(row.ReleasedAt))
            .Set("settled_at", Stamp(row.SettledAt))
            .Set("release_note", row.ReleaseNote);
    }

    private static string? Stamp(DateTimeOffset? value) =>
        value is { } stamp ? Timestamp.FromDateTimeOffset(stamp.ToUniversalTime()).ToWireText() : null;

    private static HandbackRow Read(SqliteDataReader reader) => new(
        SqliteValues.GetInt64(reader, 0),
        SqliteValues.GetInt64(reader, 1),
        SqliteValues.GetString(reader, 2),
        SqliteValues.GetString(reader, 3),
        SqliteValues.GetInt64(reader, 4),
        SqliteValues.GetInt64(reader, 5),
        TimestampColumns.Parse(reader.GetValue(6)),
        SqliteValues.GetStringOrNull(reader, 7),
        SqliteValues.GetStringOrNull(reader, 8),
        TimestampColumns.Parse(reader.GetValue(9)),
        SqliteValues.GetStringOrNull(reader, 10),
        SqliteValues.GetStringOrNull(reader, 11),
        TimestampColumns.Parse(reader.GetValue(12)),
        TimestampColumns.Parse(reader.GetValue(13)),
        SqliteValues.GetStringOrNull(reader, 14),
        SqliteValues.GetString(reader, 15),
        SqliteValues.GetString(reader, 16),
        SqliteValues.GetString(reader, 17),
        SqliteValues.GetStringOrNull(reader, 18) is { } sourceKey
            ? new ManagerSpeaker(sourceKey, reader.IsDBNull(19) ? null : SqliteValues.GetInt64(reader, 19), SqliteValues.GetBool(reader, 20))
            : null);
}
