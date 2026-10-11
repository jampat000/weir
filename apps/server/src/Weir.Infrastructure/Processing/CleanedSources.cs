using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Observability;
using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>
/// One processing per source file. A source is the file at a path with a size and a modification time; once a pass has
/// cleaned it and written its copy, every later route that would clean the same source again (a resent or replayed
/// hand-off, a requeue, a scan, a pass queued before this check existed) settles here instead, as a skip with a reason.
/// A different source at the same path, or a different path, is a new opportunity and goes through.
/// </summary>
/// <remarks>
/// What was cleaned is read from the hand-back row (<c>handbacks</c>), which is keyed by path and holds the source beside the
/// copy and the manager's word on it. It does not depend on the file's row in the list: a file taken off the list and handed
/// over again, unchanged, is a repeat like any other, and the manager's "imported" is not lost to a second copy.
/// </remarks>
public static class CleanedSources
{
    /// <summary>
    /// What Weir already did with this source, or null when it should be processed: the file is not there to measure, it was
    /// not cleaned (or has changed since), or the copy it wrote is gone and nobody collected it. A file whose row says it was
    /// since rejected, failed or held is not a repeat: it is being dealt with again.
    /// </summary>
    public static Task<CleanedEarlier?> FindAsync(UnitOfWork uow, long libraryId, string watchedFolder, string relativePath) =>
        FindAsync(uow, libraryId, relativePath, recorded => SourceIsUnchanged(watchedFolder, relativePath, recorded));

    /// <summary>
    /// What Weir already did with a file whose original is no longer in the watched folder: there is no source to compare, and
    /// nothing left to process, so the earlier cleaning is the answer whenever its copy is still there or a manager collected it.
    /// </summary>
    public static Task<CleanedEarlier?> FindForMissingOriginalAsync(UnitOfWork uow, long libraryId, string relativePath) =>
        FindAsync(uow, libraryId, relativePath, _ => true);

    /// <summary>The source a copy was cleaned from, as the hand-back row recorded it; null when it did not.</summary>
    private sealed record RecordedSource(long? SizeBytes, long? ModifiedTimeNs);

    private static async Task<CleanedEarlier?> FindAsync(UnitOfWork uow, long libraryId, string relativePath, Func<RecordedSource, bool> sourceIsUnchanged)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var earlier = await uow.QuerySingleAsync(
            "SELECT f.status, f.last_attempt_at, h.source_size, h.source_mtime_ns, h.output_path, h.written_at, h.outcome, h.outcome_by, h.outcome_at " +
            "FROM handbacks h LEFT JOIN files f ON f.library_id = h.library_id AND f.relative_path = h.relative_path " +
            "WHERE h.library_id = $library AND h.relative_path = $path",
            reader => new
            {
                RowStatus = SqliteValues.GetStringOrNull(reader, 0),
                RowWorkedOn = !reader.IsDBNull(1),
                Source = new RecordedSource(reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)),
                OutputPath = SqliteValues.GetString(reader, 4),
                WrittenAt = TimestampColumns.Parse(reader.GetValue(5)),
                Outcome = SqliteValues.GetStringOrNull(reader, 6),
                OutcomeBy = SqliteValues.GetStringOrNull(reader, 7),
                OutcomeAt = TimestampColumns.Parse(reader.GetValue(8)),
            },
            ("$library", libraryId),
            ("$path", relativePath)).ConfigureAwait(false);
        if (earlier is null || earlier.WrittenAt is not { } writtenAt ||
            !RowLeavesItToTheRecord(earlier.RowStatus, earlier.RowWorkedOn) || !sourceIsUnchanged(earlier.Source))
        {
            return null;
        }

        if (File.Exists(earlier.OutputPath))
        {
            return new CleanedEarlier(earlier.OutputPath, writtenAt, Collected: false, Manager: null, CollectedAt: null);
        }

        return earlier.Outcome == HandbackRules.Imported
            ? new CleanedEarlier(earlier.OutputPath, writtenAt, Collected: true, earlier.OutcomeBy, earlier.OutcomeAt)
            : null;
    }

    /// <summary>
    /// Whether the file's row leaves the question to the hand-back record. There is no row (the list lost it), it says the
    /// file is done, or it is a row Weir has never worked on, made afresh for the hand-off or scan that found the file again.
    /// A row that says a later attempt failed, was rejected or is held is not: the file is being dealt with again.
    /// </summary>
    private static bool RowLeavesItToTheRecord(string? rowStatus, bool workedOn) =>
        rowStatus is null or ProcessingFileStatuses.Processed || (rowStatus == ProcessingFileStatuses.Unprocessed && !workedOn);

    /// <summary>
    /// A repeat leaves the file's row saying what became of it: done, with the source its copy was cleaned from. A row the list
    /// lost is written again, as seen <paramref name="now"/>, and one made afresh for this repeat, which no pass will ever settle,
    /// stops waiting for one. Any other row is left as it is.
    /// </summary>
    public static Task SettleRowAsync(UnitOfWork uow, long libraryId, string relativePath, CleanedEarlier earlier, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(earlier);
        return uow.ExecuteAsync(
            "INSERT INTO files (library_id, relative_path, status, status_reason, processed_source_size, processed_source_mtime_ns, last_attempt_at, last_seen_at) " +
            "SELECT h.library_id, h.relative_path, $processed, $reason, h.source_size, h.source_mtime_ns, h.written_at, $now FROM handbacks h " +
            "WHERE h.library_id = $library AND h.relative_path = $path " +
            "ON CONFLICT (library_id, relative_path) DO UPDATE SET status = excluded.status, status_reason = excluded.status_reason, " +
            "processed_source_size = excluded.processed_source_size, processed_source_mtime_ns = excluded.processed_source_mtime_ns, " +
            "last_attempt_at = excluded.last_attempt_at, hold_until = NULL, blocked_by_connection = NULL, updated_at = CURRENT_TIMESTAMP " +
            "WHERE files.status = $unprocessed AND files.last_attempt_at IS NULL",
            ("$processed", ProcessingFileStatuses.Processed),
            ("$unprocessed", ProcessingFileStatuses.Unprocessed),
            ("$reason", earlier.Reason),
            ("$now", TimestampColumns.Orm(now)),
            ("$library", libraryId),
            ("$path", relativePath));
    }

    /// <summary>
    /// The one Activity line for a repeat that was left alone, grey ("skipped"): never a failure, and never something a
    /// person has to act on.
    /// </summary>
    public static Task<long> RecordSkipAsync(UnitOfWork uow, long libraryId, string relativePath, CleanedEarlier earlier, string trigger)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(earlier);
        var title = earlier.Title(MediaPathNames.Name(relativePath, OperatingSystem.IsWindows()));
        var detail = OperatorMessages.ActivityDetailEnvelope("processing", "skip_repeat", trigger, "skipped", userMessage: earlier.Reason)
            .Set("relative_media_path", relativePath)
            .Set("library_id", libraryId)
            .Set("output_file", earlier.OutputPath)
            .Set("cleaned_at", earlier.CleanedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Set("collected", earlier.Collected);
        return SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
            ActivityEventTypes.ProcessingFileSkippedRepeat,
            "processing",
            title,
            WireStrings.Slice(WireJsonWriter.Dumps(detail, WireJsonFormat.Compact), 10_000)));
    }

    private static bool SourceIsUnchanged(string watchedFolder, string relativePath, RecordedSource recorded)
    {
        try
        {
            var fingerprint = SourceFiles.Fingerprint(RemuxPassPaths.ResolveMediaFileUnderRoot(watchedFolder, relativePath));
            return recorded is { SizeBytes: { } size, ModifiedTimeNs: { } modified } && size == fingerprint.SizeBytes && modified == fingerprint.ModifiedTimeNs;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
