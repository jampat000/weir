using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>Where a hand-off stands once one of its files has a final result.</summary>
public enum HandoffTargetProgress
{
    /// <summary>The hand-off records no files of its own (it arrived before they were recorded): report the file alone.</summary>
    Untracked,

    /// <summary>The file is not one this hand-off covers, so the result says nothing about the hand-off.</summary>
    NotATarget,

    /// <summary>Other files of the hand-off have no final result yet.</summary>
    Waiting,

    /// <summary>Every file is finished and another pass has already reported this outcome.</summary>
    AlreadyReported,

    /// <summary>Every file is finished and this caller, and only this caller, reports the hand-off.</summary>
    Ready,
}

/// <summary>The answer to <see cref="HandoffTargetStore.FinishAsync"/>, with the hand-off and all its files once they matter.</summary>
public sealed record HandoffTargetFinish(HandoffTargetProgress Progress, HandoffLedgerRow? Row = null, IReadOnlyList<HandoffTarget>? Targets = null);

/// <summary>
/// The <c>media_manager_handoff_targets</c> table (migration 0019): every file a hand-off covers, and what each file's
/// pass came to. A hand-off of several files is reported once, when the last of them finishes, and a manager's
/// "imported" releases only the copies that report named.
/// </summary>
public sealed class HandoffTargetStore
{
    /// <summary>Record the files a hand-off covers. A resend of a hand-off still under way keeps what its files have already done.</summary>
    public async Task AddAsync(UnitOfWork uow, long handoffRowId, IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(relativePaths);
        foreach (var relativePath in relativePaths)
        {
            await uow.ExecuteAsync(
                "INSERT INTO media_manager_handoff_targets (handoff_row_id, relative_path) VALUES ($row, $path) " +
                "ON CONFLICT (handoff_row_id, relative_path) DO NOTHING",
                ("$row", handoffRowId),
                ("$path", relativePath)).ConfigureAwait(false);
        }
    }

    /// <summary>Forget a finished hand-off's files, so a resend of it starts over.</summary>
    public Task ClearAsync(UnitOfWork uow, long handoffRowId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.ExecuteAsync("DELETE FROM media_manager_handoff_targets WHERE handoff_row_id = $row", ("$row", handoffRowId));
    }

    /// <summary>The files a hand-off covers, in path order.</summary>
    public Task<List<HandoffTarget>> ListAsync(UnitOfWork uow, long handoffRowId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QueryAsync(
            "SELECT t.relative_path, t.result, t.output_file, t.message, t.output_written_at, " +
            "(SELECT NULLIF(f.size_bytes, 0) FROM files f WHERE f.library_id = h.library_id AND f.relative_path = t.relative_path) " +
            "FROM media_manager_handoff_targets t JOIN media_manager_handoffs h ON h.id = t.handoff_row_id " +
            "WHERE t.handoff_row_id = $row ORDER BY t.relative_path",
            reader => new HandoffTarget(
                SqliteValues.GetString(reader, 0),
                SqliteValues.GetStringOrNull(reader, 1),
                SqliteValues.GetStringOrNull(reader, 2),
                SqliteValues.GetStringOrNull(reader, 3),
                TimestampColumns.Parse(reader.GetValue(4)),
                reader.IsDBNull(5) ? null : reader.GetInt64(5)),
            ("$row", handoffRowId));
    }

    /// <summary>
    /// Record one file's final result, and say whether the caller now reports the whole hand-off. The result and the
    /// check run in one <c>BEGIN IMMEDIATE</c> transaction, so of two passes finishing together the second sees the
    /// first's result; and the report is claimed by a conditional update of <c>reported_status</c>, so exactly one caller
    /// gets <see cref="HandoffTargetProgress.Ready"/>. A hand-off already reported as failed may be reported once more
    /// when a retry turns it into a success, as a single file always could. <paramref name="row"/> is null for a job that
    /// carries no hand-off, or one whose hand-off is not recorded at all. The caller commits.
    /// </summary>
    public async Task<HandoffTargetFinish> FinishAsync(UnitOfWork uow, HandoffLedgerRow? row, string relativePath, WireObject result)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(result);
        if (row is null)
        {
            return new HandoffTargetFinish(HandoffTargetProgress.Untracked);
        }

        var outputFile = CompletionReports.IsSucceeded(result) && result.Get("output_file") is WireString { Value.Length: > 0 } written ? written.Value : null;
        var updated = await uow.ExecuteAsync(
            "UPDATE media_manager_handoff_targets SET result = $result, output_file = $output, message = $message, " +
            "output_written_at = CASE WHEN $output IS NULL THEN NULL " +
            "ELSE (SELECT written_at FROM handbacks WHERE library_id = $library AND relative_path = $path) END " +
            "WHERE handoff_row_id = $row AND relative_path = $path " +
            // A send that was replaced only ever fills a file that has no result yet, and a file whose send was replaced takes no later one:
            // that send has had its last answer.
            "AND (result IS NULL OR ($replacing = 0 AND coalesce(message, '') <> $replacedMessage))",
            ("$replacing", CompletionReports.IsSuperseded(result) ? 1 : 0),
            ("$replacedMessage", CompletionReports.SupersededMessage),
            ("$result", FolderHandoffReports.TargetResult(result)),
            ("$output", outputFile),
            ("$message", WireStrings.Slice(CompletionReports.MessageFor(result), 2000)),
            ("$library", row.LibraryId),
            ("$row", row.Id),
            ("$path", relativePath)).ConfigureAwait(false);
        if (updated == 0)
        {
            var targets = await ListAsync(uow, row.Id).ConfigureAwait(false);
            return new HandoffTargetFinish(targets.Count == 0 ? HandoffTargetProgress.Untracked : HandoffTargetProgress.NotATarget, row);
        }

        return await EvaluateAsync(uow, row).ConfigureAwait(false);
    }

    /// <summary>
    /// A file whose queued pass was cancelled in Weir is finished for the hand-off: nothing will be delivered for it.
    /// Runs the same readiness check and claim as <see cref="FinishAsync"/>, so a cancellation that turns out to be the
    /// hand-off's last unresolved file also reports it, instead of leaving the others' copies un-releasable forever.
    /// </summary>
    public async Task<HandoffTargetFinish> MarkCancelledAsync(UnitOfWork uow, HandoffLedgerRow row, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(row);
        await uow.ExecuteAsync(
            "UPDATE media_manager_handoff_targets SET result = $cancelled, message = $message " +
            "WHERE handoff_row_id = $row AND relative_path = $path AND result IS NULL",
            ("$cancelled", HandoffLedgerRules.Cancelled),
            ("$message", HandoffLedgerRules.CancelledInWeirMessage),
            ("$row", row.Id),
            ("$path", relativePath)).ConfigureAwait(false);
        return await EvaluateAsync(uow, row).ConfigureAwait(false);
    }

    /// <summary>Whether every target now has a final result and, if so, who claims reporting it.</summary>
    private async Task<HandoffTargetFinish> EvaluateAsync(UnitOfWork uow, HandoffLedgerRow row)
    {
        var targets = await ListAsync(uow, row.Id).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return new HandoffTargetFinish(HandoffTargetProgress.Untracked, row);
        }

        if (targets.Any(target => target.Result is null))
        {
            return new HandoffTargetFinish(HandoffTargetProgress.Waiting, row, targets);
        }

        var status = FolderHandoffReports.State(targets) == HandoffLedgerRules.Failed ? HandoffLedgerRules.Failed : HandoffLedgerRules.Completed;
        var claimed = await uow.ExecuteAsync(
            "UPDATE media_manager_handoffs SET reported_status = $status WHERE id = $row " +
            "AND (reported_status IS NULL OR (reported_status = $failed AND $status <> $failed))",
            ("$status", status),
            ("$failed", HandoffLedgerRules.Failed),
            ("$row", row.Id)).ConfigureAwait(false);
        return new HandoffTargetFinish(claimed == 1 ? HandoffTargetProgress.Ready : HandoffTargetProgress.AlreadyReported, row, targets);
    }

    /// <summary>
    /// Whether the manager was told about this copy: the hand-off reported the file, naming exactly this copy as Weir wrote
    /// it then. A copy Weir wrote for the same file after that report was never named to the manager, whatever its path.
    /// A hand-off that records no files of its own named only the one file it was for.
    /// </summary>
    public async Task<Func<HandbackRow, bool>> ReportedCopiesAsync(UnitOfWork uow, HandoffLedgerRow row)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(row);
        var targets = await ListAsync(uow, row.Id).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return copy => copy.RelativePath == row.RelativePath;
        }

        if (row.ReportedStatus is null)
        {
            return _ => false;
        }

        var reported = targets.Where(target => target.Delivered && target.OutputFile is not null).ToList();
        return copy => reported.Any(target =>
            target.RelativePath == copy.RelativePath && SameFile(target.OutputFile!, copy.OutputPath) &&
            (target.OutputWrittenAt is not { } writtenAt || writtenAt == copy.WrittenAt));
    }

    private static bool SameFile(string reported, string copy)
    {
        try
        {
            return RemuxPassPaths.SamePath(RemuxPassPaths.Resolve(reported), RemuxPassPaths.Resolve(copy));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
