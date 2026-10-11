using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.Processing;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassHandler
{
    /// <summary>
    /// The last line of "one processing per source file" (<see cref="CleanedSources"/>): a pass queued before the other checks
    /// existed, or by a route that has none, still finds out here, before it claims or touches anything, that Weir already
    /// cleaned this exact source. It ends as a skip, Activity says so, and the manager that handed the file over is answered
    /// as for any finished file. An operator's own choice of tracks or of passing the file through unchanged is a different
    /// piece of work and is never a repeat. True when the pass was settled this way.
    /// </summary>
    private async Task<bool> SettleRepeatAsync(
        WireObject data, WireObject provenance, string rel, string mediaScope, long? libraryId, string? payloadJson, CancellationToken cancellationToken)
    {
        if (data.Get("pass_through_unchanged") is WireBool { Value: true } || ManualPlanJson.FromPyJson(data.Get("manual_plan")) is not null)
        {
            return false;
        }

        CleanedEarlier? earlier = null;
        string? outputFolder = null;
        long? workflowId = null;
        var trigger = provenance.Get("trigger") is WireString { Value.Length: > 0 } named ? named.Value : "worker";
        await LockedWrites.RunAsync(
            _database,
            async uow =>
            {
                earlier = null;
                if (await ResolveLibraryAsync(uow, _libraries, libraryId, mediaScope).ConfigureAwait(false) is not { } library)
                {
                    return;
                }

                earlier = await CleanedSources.FindAsync(uow, library.Id, library.WatchedFolder, rel).ConfigureAwait(false);
                if (earlier is not null)
                {
                    await CleanedSources.RecordSkipAsync(uow, library.Id, rel, earlier, trigger).ConfigureAwait(false);
                    await CleanedSources.SettleRowAsync(uow, library.Id, rel, earlier, _time.GetUtcNow()).ConfigureAwait(false);
                    workflowId = library.Id;
                    outputFolder = string.IsNullOrWhiteSpace(library.OutputFolder) ? null : RemuxPassPaths.Resolve(library.OutputFolder.Trim());
                }
            },
            _logger,
            "repeat of a cleaned source",
            cancellationToken).ConfigureAwait(false);
        if (earlier is null)
        {
            return false;
        }

        _logger.LogInformation("Left {Path} alone: {Reason}", rel, earlier.Reason);
        await ReportBackAsync(payloadJson, earlier.ToResult(rel, workflowId, outputFolder)).ConfigureAwait(false);
        return true;
    }
}
