using System.Globalization;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Jobs;

/// <summary>Which scan queued a pass, and how it was started.</summary>
internal sealed record ScanPassRequest(long ScanJobId, string ScanTrigger);

/// <summary>
/// One watched-folder scan over the files its walk found: each file is decided in memory, the writes go out in short batches,
/// and passes are queued after the batch that recorded their file commits.
/// </summary>
internal sealed class WatchedFolderScanRun
{
    private readonly SqliteDatabase _database;
    private readonly ProcessingJobStore _jobStore;
    private readonly FileStateStore _files;
    private readonly WatchedFolderScan _scan;
    private readonly WatchedFolderScanLookups _lookups;
    private readonly UnitOfWork _reads;
    private readonly ScanPassRequest _passes;
    private readonly WatchedFileDecider _decider;
    private readonly WatchedFolderScanBatch _batch;
    private readonly List<RejectedFileRemoval> _removals = [];

    public WatchedFolderScanRun(
        SqliteDatabase database, ProcessingJobStore jobStore, FileStateStore files, WatchedFolderScan scan, WatchedFolderScanLookups lookups, UnitOfWork reads,
        ScanPassRequest passes)
    {
        _database = database;
        _jobStore = jobStore;
        _files = files;
        _scan = scan;
        _lookups = lookups;
        _reads = reads;
        _passes = passes;
        _decider = new WatchedFileDecider(scan, lookups, reads);
        _batch = new WatchedFolderScanBatch(database, files, scan.Library.Id, scan.Now);
    }

    /// <summary>The earliest moment a file this scan held stops being held; the next look is booked for then.</summary>
    public DateTimeOffset? EarliestHoldEnds { get; private set; }

    public async Task RunAsync(IReadOnlyList<WatchedMediaFile> listed, CancellationToken cancellationToken)
    {
        foreach (var entry in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = WatchedFolderScanOps.RelativePosixPathUnderWatched(_scan.Paths.WatchedFolder, entry.FullPath);
            var previous = _lookups.Rows.GetValueOrDefault(rel);
            var settled = _decider.Settled(entry, rel, previous);
            if (settled is { TouchRowId: { } settledRow, Enqueue: false })
            {
                NoteHoldEnds(settled);
                _batch.Touch(settledRow);
            }
            else if (WatchedMediaFile.Stat(entry.FullPath) is { } file)
            {
                // A directory entry's size and time can lag behind a file another program still has open (NTFS updates them
                // when the writer closes it), so every file that could be queued, held or changed is read from the file
                // itself. A file gone since the walk (a movie cleanup can remove a release folder mid-scan) is left alone.
                await ApplyAsync(await _decider.DecideAsync(file, rel, previous).ConfigureAwait(false), file, cancellationToken).ConfigureAwait(false);
            }

            if (_batch.IsFull)
            {
                await _batch.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await _batch.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the rejected files this library is set to delete. Their skipped state was committed first, so if the process
    /// stops in between, the source remains and the next scan safely retries.
    /// </summary>
    public async Task RemoveRejectedFilesAsync(CancellationToken cancellationToken)
    {
        foreach (var removal in _removals)
        {
            var (deleted, detail) = RemuxPassPaths.CleanupRejectedFile(_scan.Paths.WatchedFolder, removal.FilePath, removal.Action);
            var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
            await using (uow.ConfigureAwait(false))
            {
                await _files.MarkFileStatusAsync(uow, _scan.Library.Id, removal.RelativePath, ProcessingFileStatuses.Skipped, $"{removal.Reason} {detail}", deleted ? SkipKinds.AfterRemoval(removal.SkipKind) : removal.SkipKind)
                    .ConfigureAwait(false);
                await uow.CommitAsync().ConfigureAwait(false);
            }
        }
    }

    private void NoteHoldEnds(WatchedFileDecision decision)
    {
        if (decision.HoldEnds is { } holdEnds && (EarliestHoldEnds is null || holdEnds < EarliestHoldEnds))
        {
            EarliestHoldEnds = holdEnds;
        }
    }

    private async Task ApplyAsync(WatchedFileDecision decision, WatchedMediaFile file, CancellationToken cancellationToken)
    {
        NoteHoldEnds(decision);

        // While processing is paused a scan only looks: a removal waits for the next scan after the pause ends.
        if (decision.FinishMovieRemoval && !_scan.Window.Paused && !_scan.KeepsOriginals && await NoPassOwnsAsync(decision.RelativePath).ConfigureAwait(false))
        {
            // Earlier files' writes land first, and the removal itself runs with no transaction open.
            await _batch.FlushAsync(cancellationToken).ConfigureAwait(false);
            await CompletedMovieRemoval.FinishAsync(_database, _files, _scan, decision, file, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (decision.TouchRowId is { } rowId)
        {
            _batch.Touch(rowId);
        }

        if (decision.Write is { } write)
        {
            _batch.Record(write, AfterRecorded(decision));
        }
        else if (decision.Enqueue)
        {
            _batch.AfterCommit(() => EnqueueAsync(decision));
        }
    }

    /// <summary>What follows once a file's state is recorded: its pass, or its removal once the batch is committed.</summary>
    private Func<Task>? AfterRecorded(WatchedFileDecision decision)
    {
        if (decision.Enqueue)
        {
            return () => EnqueueAsync(decision);
        }

        if (decision.Removal is { } removal)
        {
            return () =>
            {
                _removals.Add(removal);
                return Task.CompletedTask;
            };
        }

        return null;
    }

    /// <summary>
    /// A source is never removed while a pass is queued or running for it. The scan's own list was read when it started, so
    /// this asks again, through the index, right before anything is deleted.
    /// </summary>
    private async Task<bool> NoPassOwnsAsync(string rel) =>
        !await ActiveRemuxPasses.ExistsForRelativePathAsync(_reads, rel, _scan.MediaScope, _scan.Library.Id).ConfigureAwait(false);

    /// <summary>The row this scan just made for a file Weir already cleaned says so, instead of waiting for a pass that will not come.</summary>
    private Task SettleRowAsync(string rel, CleanedEarlier cleaned) =>
        WriteLockTurns.TakeAsync(
            async () =>
            {
                var uow = await UnitOfWork.OpenAsync(_database, CancellationToken.None).ConfigureAwait(false);
                await using (uow.ConfigureAwait(false))
                {
                    await CleanedSources.SettleRowAsync(uow, _scan.Library.Id, rel, cleaned, _scan.Now).ConfigureAwait(false);
                    await uow.CommitAsync().ConfigureAwait(false);
                }
            },
            CancellationToken.None);

    private async Task EnqueueAsync(WatchedFileDecision decision)
    {
        var rel = decision.RelativePath;

        // A source a pass already cleaned is never queued again. The scan's own rules recognise one for the libraries that keep
        // originals; this is the same last check every other route makes, for the rest. Silent: a scan would repeat the line.
        if (await CleanedSources.FindAsync(_reads, _scan.Library.Id, _scan.Paths.WatchedFolder, rel).ConfigureAwait(false) is { } cleaned)
        {
            await SettleRowAsync(rel, cleaned).ConfigureAwait(false);
            return;
        }

        var payload = new WireObject()
            .Set("relative_media_path", rel)
            .Set("media_scope", _scan.MediaScope)
            .Set("trigger", ActivityProvenance.ScanTriggerToTrigger.GetValueOrDefault(_passes.ScanTrigger, "manual"))
            .Set("run_id", $"scan-{_passes.ScanJobId.ToString(CultureInfo.InvariantCulture)}")
            .Set("library_id", _scan.Library.Id);
        // A scan-driven retry (also how a failed hand-off's file is requeued automatically) keeps the hand-off's origin, so a
        // pass-through or reject reached after the retry still has an output path to report and a callback to send, as
        // RequeueStore.RequeueFileAsync does for a manual requeue (#531 item 2).
        if (await HandoffOriginCarry.FindAsync(_reads, _scan.Library.Id, rel).ConfigureAwait(false) is { } origin)
        {
            payload.Set("origin", origin);
        }

        var dedupe = $"{RequeueStore.RemuxPassJobKind}:scan:{Guid.NewGuid():N}";
        var payloadJson = WireJsonWriter.Dumps(payload, WireJsonFormat.Compact);

        // The dedupe key is random, so it cannot stop a second pass for the same file. The file's identity does: look for a
        // pending or leased pass for this file and insert only when there is none, both inside one short BEGIN IMMEDIATE.
        // The scan's own list of passes was read when it started, so a hand-off or another scan may have queued one since.
        await _jobStore.InTransactionAsync(
            (connection, transaction) =>
                ActiveRemuxPasses.ForRelativePath(connection, transaction, rel, _scan.MediaScope, _scan.Library.Id)
                ?? _jobStore.EnqueueOrGet(
                    connection, transaction, dedupe, RequeueStore.RemuxPassJobKind, payloadJson, JobQueueRules.DefaultMaxAttempts, runnerCost: null,
                    (int)_scan.Library.Priority)).ConfigureAwait(false);
    }
}
