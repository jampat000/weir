using Microsoft.Data.Sqlite;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing;

/// <summary>
/// The remux passes still to run for a file: pending or leased <c>processing.file.remux_pass.v1</c> jobs, matched on the
/// identity every automatic enqueue path agrees on (library, relative path, scope).
/// </summary>
/// <remarks>
/// A lookup for one file goes through <c>ix_jobs_active_remux_pass_path</c> (migration <c>0020</c>), an expression index over
/// the payload's path that holds only pending and leased passes; its predicate is repeated here word for word because SQLite
/// uses a partial index only for a query that states the same condition. A backlog of thousands of queued passes made the
/// unindexed lookup parse every one of them, under the write lock, for every enqueue (#708).
/// </remarks>
public static class ActiveRemuxPasses
{
    private const string ActivePassFilter =
        "job_kind = '" + RemuxPassOutcomes.JobKind + "' AND status IN ('" + ProcessingJobStatus.Pending + "', '" + ProcessingJobStatus.Leased + "')";

    /// <summary>The pending or leased passes naming one path, oldest first; <c>@path</c> is the path.</summary>
    internal const string ForPathSql =
        $"SELECT {ProcessingJobStore.JobColumns} FROM jobs WHERE {ActivePassFilter} " +
        "AND json_extract(payload_json, '$.relative_media_path') = @path ORDER BY id";

    /// <summary>The oldest pending or leased pass for this file, read inside the caller's write transaction.</summary>
    internal static ProcessingJob? ForRelativePath(SqliteConnection connection, SqliteTransaction transaction, string relativePosix, string mediaScope, long? libraryId)
    {
        var wantScope = ProcessingMediaScopes.Normalize(mediaScope);
        return ProcessingJobStore.Query(connection, transaction, ForPathSql, ("@path", relativePosix))
            .FirstOrDefault(job => PayloadNamesFile(job.PayloadJson, relativePosix, wantScope, libraryId));
    }

    /// <summary>Whether a pending or leased pass other than <paramref name="excludeJobId"/> names this file.</summary>
    public static async Task<bool> ExistsForRelativePathAsync(UnitOfWork uow, string relativePosix, string mediaScope, long? libraryId, long? excludeJobId = null) =>
        (await ForRelativePathAsync(uow, relativePosix, mediaScope, libraryId).ConfigureAwait(false)).Any(job => job.Id != excludeJobId);

    /// <summary>The pending or leased passes for this file, oldest first.</summary>
    internal static async Task<IReadOnlyList<ProcessingJob>> ForRelativePathAsync(UnitOfWork uow, string relativePosix, string mediaScope, long? libraryId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var wantScope = ProcessingMediaScopes.Normalize(mediaScope);
        var jobs = await uow.QueryAsync(ForPathSql, ProcessingJobStore.ReadJob, ("@path", relativePosix)).ConfigureAwait(false);
        return [.. jobs.Where(job => PayloadNamesFile(job.PayloadJson, relativePosix, wantScope, libraryId))];
    }

    /// <summary>The relative path of every file in the library and scope with a pass pending or leased, read once for a whole scan.</summary>
    public static async Task<HashSet<string>> PathsAsync(UnitOfWork uow, string mediaScope, long libraryId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var wantScope = ProcessingMediaScopes.Normalize(mediaScope);
        var payloads = await uow.QueryAsync(
            $"SELECT payload_json FROM jobs WHERE {ActivePassFilter}",
            reader => reader.IsDBNull(0) ? null : reader.GetString(0)).ConfigureAwait(false);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            if (FileOf(payload) is { } file && file.Scope == wantScope && file.LibraryId == libraryId)
            {
                paths.Add(file.RelativePath);
            }
        }

        return paths;
    }

    /// <summary>
    /// For each file in the library and scope whose pending pass is held back to a later time, the earliest such time: another
    /// look at a file that would not read to the end (#646), or a hand-off waiting out its minimum age (#632).
    /// </summary>
    public static async Task<Dictionary<string, DateTimeOffset>> HeldBackStartsAsync(UnitOfWork uow, string mediaScope, long libraryId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var wantScope = ProcessingMediaScopes.Normalize(mediaScope);
        var rows = await uow.QueryAsync(
            "SELECT payload_json, not_before FROM jobs WHERE job_kind = @kind AND status = @pending " +
            "AND not_before IS NOT NULL AND julianday(not_before) > julianday(@now)",
            reader => (PayloadJson: reader.IsDBNull(0) ? null : reader.GetString(0), StartsAt: TimestampColumns.Parse(reader.GetValue(1))),
            ("@kind", RemuxPassOutcomes.JobKind),
            ("@pending", ProcessingJobStatus.Pending),
            ("@now", TimestampColumns.Orm(now))).ConfigureAwait(false);
        var earliest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var (payloadJson, startsAt) in rows)
        {
            if (startsAt is { } at && FileOf(payloadJson) is { } file && file.Scope == wantScope && file.LibraryId == libraryId &&
                (!earliest.TryGetValue(file.RelativePath, out var known) || at < known))
            {
                earliest[file.RelativePath] = at;
            }
        }

        return earliest;
    }

    /// <summary>Whether a pass payload names this file: same relative path, same scope, and same library when one is given.</summary>
    private static bool PayloadNamesFile(string? payloadJson, string relativePosix, string wantScope, long? libraryId) =>
        FileOf(payloadJson) is { } file && file.RelativePath == relativePosix && file.Scope == wantScope &&
        (libraryId is null || file.LibraryId == libraryId);

    private sealed record PassFile(string RelativePath, string Scope, long? LibraryId);

    private static PassFile? FileOf(string? payloadJson)
    {
        var raw = (payloadJson ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        WireValue data;
        try
        {
            data = WireJsonParser.Parse(raw);
        }
        catch (WireJsonDecodeException)
        {
            return null;
        }

        if (data is not WireObject dict || dict.Get("relative_media_path") is not WireString relative)
        {
            return null;
        }

        var scope = dict.Get("media_scope") is WireString scopeText ? ProcessingMediaScopes.Normalize(scopeText.Value) : ProcessingMediaScopes.Movie;
        long? libraryId = dict.Get("library_id") is WireInteger libraryValue ? (long)libraryValue.Value : null;
        return new PassFile(relative.Value.Trim(), scope, libraryId);
    }
}
