using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// Building and durably persisting the report for a hand-off whose targets are all now final, so a crash or exception
/// between claiming the hand-off and sending its report never leaves the claim behind with nothing to send (#667):
/// see <see cref="ClaimAndDeliverAsync"/> for a pass finishing, <see cref="StageReportAfterCancellationAsync"/> for a
/// cancellation.
/// </summary>
public sealed partial class HandoffCompletionReporter
{
    /// <summary>
    /// A hand-off whose targets are all now final (a pass just made it so, or, for a cancellation, the target that
    /// cancellation just settled). Stages the report and persists it — the ledger state, and, when there is somewhere
    /// to send it, the report itself as owed — in one transaction, then commits and attempts delivery immediately.
    /// Never throws: a manager being unreachable must not fail a pass that succeeded on disk.
    /// </summary>
    private async Task<string> ClaimAndDeliverAsync(
        UnitOfWork uow, HandoffOrigin origin, HandoffTargetFinish finish, WireObject? result, long? libraryId, bool viaCancellation, bool deliverNow, CancellationToken cancellationToken)
    {
        var staged = await StageReadyReportAsync(uow, origin, finish, result, libraryId, viaCancellation, cancellationToken).ConfigureAwait(false);
        if (staged is not { } ready)
        {
            return "skipped: nothing was delivered, so there is nothing to report";
        }

        PendingReport? owed;
        try
        {
            owed = await PersistClaimedReportAsync(uow, origin, ready.Outcome, ready.Target, ready.KeepOwed).ConfigureAwait(false);
            await uow.CommitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or IOException)
        {
            await uow.RollbackAsync().ConfigureAwait(false);
            _logger.LogWarning(exception, "Could not record the hand-off outcome.");
            return "skipped: the outcome could not be recorded";
        }

        if (ready.Target is null)
        {
            return $"skipped: {ready.SkipReason}";
        }

        if (!deliverNow)
        {
            return "queued: the report is owed and goes out with the next delivery";
        }

        var delivery = await DeliverOwedReportAsync(uow, origin.SourceKey, origin.HandoffId!, ready.Target, owed!, cancellationToken).ConfigureAwait(false);
        return delivery.Status;
    }

    /// <summary>
    /// A target cancelled in Weir before its pass ran (#667). Runs the same readiness check a finished pass does; when
    /// that makes the hand-off ready and something was delivered, the report is staged and persisted as owed in
    /// <paramref name="uow"/> — the caller (<see cref="Processing.PendingJobCancellation.CancelAsync"/>) commits it
    /// together with the cancellation itself, and the heartbeat delivers it once the manager next answers, since this
    /// runs inside that caller's own transaction, before its commit. When nothing was delivered there is nothing to
    /// report: <see cref="HandoffLedgerStore.SettleAfterJobCancelledAsync"/> already covers a hand-off that ends with
    /// nothing to show for it.
    /// </summary>
    public async Task StageReportAfterCancellationAsync(UnitOfWork uow, HandoffOrigin origin, HandoffLedgerRow row, string relativePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(row);
        var finish = await _targets.MarkCancelledAsync(uow, row, relativePath).ConfigureAwait(false);
        if (finish.Progress != HandoffTargetProgress.Ready)
        {
            return;
        }

        var staged = await StageReadyReportAsync(uow, origin, finish, result: null, libraryId: null, viaCancellation: true, cancellationToken).ConfigureAwait(false);
        if (staged is { } ready)
        {
            await PersistClaimedReportAsync(uow, origin, ready.Outcome, ready.Target, ready.KeepOwed).ConfigureAwait(false);
        }
    }

    /// <summary>A report staged and ready to persist, once its target's readiness check claimed the hand-off.</summary>
    private sealed record StagedReport(ReportedOutcome Outcome, HandoffReportTarget? Target, string? SkipReason, bool KeepOwed = false);

    /// <summary>
    /// Build the report for a hand-off whose targets are all now final: a folder-style report naming every delivered
    /// file for a hand-off of several files or a cancellation (a cancellation can settle several targets at once, so it
    /// always reports the same way a pass finishing the last of several would); the classic single-file report
    /// otherwise. A cancellation that delivered nothing has no report to stage — a hand-off never delivered anything is
    /// reported by <see cref="HandoffLedgerStore.SettleAfterJobCancelledAsync"/> instead. Read-only and network-only:
    /// no write happens here, so it never needs the write lock a report's eventual persistence takes.
    /// </summary>
    private async Task<StagedReport?> StageReadyReportAsync(
        UnitOfWork uow, HandoffOrigin origin, HandoffTargetFinish finish, WireObject? result, long? libraryId, bool viaCancellation, CancellationToken cancellationToken)
    {
        var row = finish.Row!;
        var targets = finish.Targets!;
        var resolution = await ResolveAsync(uow, origin).ConfigureAwait(false);
        var (target, reason) = (resolution.Target, resolution.Reason);
        if (!viaCancellation && targets.Count <= 1)
        {
            ArgumentNullException.ThrowIfNull(result);
            var outputPath = target is not null && CompletionReports.IsSucceeded(result)
                ? await ManagerOutputPathAsync(target.Connection, origin, result, cancellationToken).ConfigureAwait(false)
                : null;
            var body = CompletionReports.BuildCompletionBody(origin, result, outputPath);
            var relative = targets.Count == 1 ? targets[0].RelativePath : null;
            var outcome = new ReportedOutcome(FileReportState(body), body, relative, relative is null ? [] : [relative], libraryId);
            return new StagedReport(outcome, target, target is null ? reason : null, resolution.KeepOwed);
        }

        var state = FolderHandoffReports.State(targets);
        if (viaCancellation && state == HandoffLedgerRules.Cancelled)
        {
            return null;
        }

        var localFolder = await LocalOutputFolderAsync(uow, row, result).ConfigureAwait(false);
        var managerFolder = target is not null && localFolder is not null
            ? await ManagerOutputFolderAsync(target.Connection, origin, cancellationToken).ConfigureAwait(false)
            : null;
        string AsManagerSees(string local) =>
            managerFolder is not null && TranslateOutputPath(local, localFolder!, managerFolder) is { } translated ? translated : local;

        var outputFiles = targets.Where(file => file.Delivered && file.OutputFile is not null).Select(file => AsManagerSees(file.OutputFile!)).ToList();
        var handBackFolder = localFolder is null ? null : AsManagerSees(Path.Join(localFolder, row.RelativePath));
        var folderBody = FolderHandoffReports.BuildBody(origin, targets, handBackFolder, outputFiles);
        var delivered = targets.Where(file => file.Delivered).Select(file => file.RelativePath).ToList();
        var folderOutcome = new ReportedOutcome(IsSupersededReport(folderBody) ? HandoffLedgerRules.Cancelled : state, folderBody, row.RelativePath, delivered, libraryId ?? row.LibraryId);
        return new StagedReport(folderOutcome, target, target is null ? reason : null, resolution.KeepOwed);
    }

    /// <summary>The library's output folder as Weir sees it: from the pass that just finished, else from the library itself.</summary>
    private async Task<string?> LocalOutputFolderAsync(UnitOfWork uow, HandoffLedgerRow row, WireObject? result)
    {
        if (result?.Get("processing_output_folder_resolved") is WireString { Value.Length: > 0 } resolved)
        {
            return resolved.Value;
        }

        return row.LibraryId is { } libraryId && await _libraries.GetAsync(uow, libraryId).ConfigureAwait(false) is { OutputFolder.Length: > 0 } library
            ? RemuxPassPaths.Resolve(library.OutputFolder.Trim())
            : null;
    }

    /// <summary>A report that ends a send Weir will not do: one the manager replaced by sending the same release again, or one for a file Weir is working on for another manager. Such a send reads as cancelled.</summary>
    private static bool IsSupersededReport(WireObject body) =>
        body.Get("failureClass") is WireString { Value: CompletionReports.SupersededFailureClass or CompletionReports.NotTakenFailureClass };

    /// <summary>The ledger state a one-file report means.</summary>
    private static string FileReportState(WireObject body)
    {
        if (IsSupersededReport(body))
        {
            return HandoffLedgerRules.Cancelled;
        }

        if (body.Get("status") is not WireString { Value: "completed" })
        {
            return HandoffLedgerRules.Failed;
        }

        return body.Get("message") is WireString { Value: CompletionReports.PassThroughAfterFailureMessage }
            ? HandoffLedgerRules.PassedThrough
            : HandoffLedgerRules.Completed;
    }

    /// <summary>
    /// Persist a staged report's ledger state and, when there is somewhere to send it, the report itself as owed —
    /// unconditionally, before delivery is even attempted (#667), so a crash or exception between claiming and sending
    /// never leaves the claim behind with nothing for the heartbeat to find. Does not commit: the caller does, in the
    /// same transaction as the claim that produced <paramref name="outcome"/>.
    /// </summary>
    private async Task<PendingReport?> PersistClaimedReportAsync(UnitOfWork uow, HandoffOrigin origin, ReportedOutcome outcome, HandoffReportTarget? target, bool keepOwed = false)
    {
        var body = outcome.Body;
        await _ledger.RecordOutcomeAsync(
            uow,
            origin.SourceKey,
            origin.HandoffId,
            outcome.State,
            body.Get("outputPath") is WireString output ? output.Value : null,
            body.Get("message") is WireString message ? message.Value : null,
            body.Get("outputFiles") is WireArray files ? [.. files.Items.OfType<WireString>().Select(file => file.Value)] : null).ConfigureAwait(false);
        if ((target is null && !keepOwed) || string.IsNullOrEmpty(origin.HandoffId))
        {
            return null;
        }

        var owed = new PendingReport(origin.CallbackPath, origin.ReleaseName, origin.LibraryId, body, outcome.LibraryId, outcome.Subject, outcome.Files);
        await HandoffLedgerStore.SetPendingReportAsync(uow, origin.SourceKey, origin.HandoffId, owed.ToJson()).ConfigureAwait(false);
        return owed;
    }
}
