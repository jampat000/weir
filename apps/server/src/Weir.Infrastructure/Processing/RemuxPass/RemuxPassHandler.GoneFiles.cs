using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassHandler
{
    private const string GoneLooksKey = GoneLooks.Key;

    /// <summary>
    /// A pass that failed because its file was not there is looked at again after a few seconds, the way a hand-off is told a share
    /// that dropped for a moment is not a deleted download. A file that is back is processed normally; one that is still gone settles
    /// as nothing to do, and is held (<see cref="LookAgainForGoneFileAsync"/>) before Weir forgets it.
    /// </summary>
    private async Task<WireObject> SettleVanishedAsync(RemuxPassRequest request, WireObject result, CancellationToken cancellationToken)
    {
        var watchedFolder = request.Runtime.WatchedFolder;
        if (result.Get("ok") is not WireBool { Value: false } || !GoneSources.HasLeft(watchedFolder, request.RelativeMediaPath))
        {
            return result;
        }

        await (GoneLookAgain?.Invoke(cancellationToken) ?? Task.Delay(GoneSettle, cancellationToken)).ConfigureAwait(false);
        if (!GoneSources.HasLeft(watchedFolder, request.RelativeMediaPath))
        {
            return await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (GoneConfirmed is { } confirmed)
        {
            await confirmed().ConfigureAwait(false);
        }

        return RemuxPassRunner.SourceGone(request.RelativeMediaPath, result.Get("inspected_source_path") is WireString inspected ? inspected.Value : null);
    }

    /// <summary>
    /// Whether this look adds an entry to Activity. A file is recorded as gone once: not by the look that forgets it, which is the
    /// second, and not by a look at a file that was already held as gone when the pass claimed it, which something else recorded.
    /// The manager that handed the file over is still told at the final look.
    /// </summary>
    private static bool AddsGoneEntry(WireObject result, bool listedWhenClaimed)
    {
        if (result.Get("outcome") is not WireString { Value: RemuxPassOutcomes.SourceGone })
        {
            return true;
        }

        return !listedWhenClaimed && !(result.Get(GoneLooksKey) is WireInteger { Value: var looks } && looks > 0);
    }

    /// <summary>
    /// The first time a file is found gone it is held, not forgotten: its attempts, reason and any hand-picked plan stay until it is
    /// plain that it is not coming back. A look is booked for after the scan's own grace for a vanished file
    /// (<see cref="GoneSources.LookAgainAfter"/>). That look finds the file back, and processes it, or still gone, and forgets it and
    /// tells the manager that handed it over. The first look is not final, so nothing is reported yet.
    /// </summary>
    private async Task LookAgainForGoneFileAsync(
        long jobId, WireObject data, WireObject? origin, WireObject result, (string WatchedFolder, string RelativePath, string MediaScope, long? LibraryId) file, CancellationToken cancellationToken)
    {
        if (_jobs is null || result.Get("outcome") is not WireString { Value: RemuxPassOutcomes.SourceGone } || result.Get(GoneLooksKey) is not WireInteger { Value: var looks } || looks != 0)
        {
            return;
        }

        await BookAnotherLookAsync(
            data.Copy().Set(GoneLooksKey, 1),
            origin,
            $"{RemuxPassOutcomes.JobKind}:gone-wait:{jobId}",
            _time.GetUtcNow() + GoneSources.LookAgainAfter,
            result,
            cancellationToken).ConfigureAwait(false);

        // The file can come back, and a send for it arrive, in the time it took to get here; nothing would start the look until the sweep.
        if (file.LibraryId is { } libraryId && GoneSources.IsBack(file.WatchedFolder, file.RelativePath))
        {
            await LockedWrites.RunAsync(
                _database,
                uow => GoneLooks.StartForFileAsync(uow, file.RelativePath, file.MediaScope, libraryId),
                _logger,
                "start the look at a file that is back",
                cancellationToken).ConfigureAwait(false);
        }
    }
}
