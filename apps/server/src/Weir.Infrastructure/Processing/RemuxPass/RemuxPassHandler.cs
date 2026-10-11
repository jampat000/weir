using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// The worker handler for <c>processing.file.remux_pass.v1</c>: claim the file, run
/// the pass outside any transaction, keep the Files row and Activity in step with the result, apply the library's failure
/// policy, and report back to the manager that handed the file over.
/// </summary>
/// <remarks>
/// #531 item 2: when a retried or requeued payload has lost its hand-off origin, the origin of the most recent job for the
/// same file is carried forward (<see cref="HandoffOriginCarry"/>), so the final outcome is still reported with its output path.
/// </remarks>
public sealed partial class RemuxPassHandler : IJobHandler
{
    private readonly SqliteDatabase _database;
    private readonly WeirOptions _options;
    private readonly RemuxPassRunner _runner;
    private readonly IFailurePolicy _failurePolicy;
    private readonly HandoffCompletionReporter? _reporter;
    private readonly DownloadedScanNotifier _downloadedScan;
    private readonly ProcessingJobStore? _jobs;
    private readonly OperatorSettingsStore _operatorSettings;
    private readonly HandbackStore _handback;
    private readonly LibraryStore _libraries;
    private readonly TimeProvider _time;
    private readonly ILogger<RemuxPassHandler> _logger;
    private readonly LiveProgressStore _liveProgress;
    private readonly ScanWakeups? _scanWakeups;

    public RemuxPassHandler(
        SqliteDatabase database,
        WeirOptions options,
        RemuxPassRunner runner,
        IFailurePolicy failurePolicy,
        OperatorSettingsStore operatorSettings,
        HandbackStore handback,
        LibraryStore libraries,
        TimeProvider time,
        ILogger<RemuxPassHandler> logger,
        DownloadedScanNotifier downloadedScan,
        HandoffCompletionReporter? reporter = null,
        ProcessingJobStore? jobs = null,
        LiveProgressStore? liveProgress = null,
        ScanWakeups? scanWakeups = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _failurePolicy = failurePolicy ?? throw new ArgumentNullException(nameof(failurePolicy));
        _operatorSettings = operatorSettings ?? throw new ArgumentNullException(nameof(operatorSettings));
        _handback = handback ?? throw new ArgumentNullException(nameof(handback));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _downloadedScan = downloadedScan ?? throw new ArgumentNullException(nameof(downloadedScan));
        _reporter = reporter;
        _jobs = jobs;
        _liveProgress = liveProgress ?? new LiveProgressStore();
        _scanWakeups = scanWakeups;
    }

    /// <summary>Test seam: how long a file that was not there is given to come back before Weir believes it is gone.</summary>
    internal TimeSpan GoneSettle { get; init; } = GoneSources.DefaultSettle;

    /// <summary>Test seam: what happens while a pass waits for a file that was not there to come back, in place of waiting <see cref="GoneSettle"/>.</summary>
    internal Func<CancellationToken, Task>? GoneLookAgain { get; init; }

    /// <summary>Test seam: runs once the second look has found the file still gone, before the pass settles it.</summary>
    internal Func<Task>? GoneConfirmed { get; init; }

    /// <summary>Test seam: runs once the pass has decided its outcome, just before the manager is told.</summary>
    internal Func<Task>? BeforeReport { get; init; }

    /// <summary>
    /// How many times a file that is only waiting out the minimum file age is looked at again before Weir stops
    /// looking (#632). Each look is a minute or so apart, so this is about half an hour of a file that never stops
    /// changing, which is a copy that has gone wrong rather than one that is finishing.
    /// </summary>
    public const int MaxMinimumAgeWaits = 30;

    /// <summary>
    /// How long Weir waits between looks at a file it could not read from start to finish, and so how long it treats one as
    /// still arriving (#646). After the last of these, a file that has not changed at all is damaged rather than unfinished,
    /// and the library's failure policy decides what happens to it. About an hour of patience in all.
    /// </summary>
    public static readonly IReadOnlyList<int> UnreadableWaitMinutes = [5, 15, 45];

    public string JobKind => RemuxPassOutcomes.JobKind;

    public async Task HandleAsync(JobWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var raw = WireStrings.Strip(context.PayloadJson ?? string.Empty);
        if (raw.Length == 0)
        {
            await RecordAsync(FailedPayload(context.Id, "missing payload_json")).ConfigureAwait(false);
            return;
        }

        WireValue parsed;
        try
        {
            parsed = WireJsonParser.Parse(raw);
        }
        catch (WireJsonDecodeException exception)
        {
            await RecordAsync(FailedPayload(context.Id, $"invalid json: {exception.Message}")).ConfigureAwait(false);
            return;
        }

        if (parsed is not WireObject data)
        {
            await RecordAsync(FailedPayload(context.Id, "payload must be a JSON object")).ConfigureAwait(false);
            return;
        }

        var provenance = ActivityProvenance.JobProvenance(data);
        if (data.Get("relative_media_path") is not WireString relValue || WireStrings.Strip(relValue.Value).Length == 0)
        {
            await RecordAsync(Merge(FailedPayload(context.Id, "relative_media_path is required"), provenance)).ConfigureAwait(false);
            return;
        }

        var rel = WireStrings.Strip(relValue.Value);
        if (data.Get("dry_run") is { } dryRun && dryRun is not WireNull)
        {
            var legacy = FailedPayload(
                    context.Id,
                    "This job payload uses legacy Weir dry_run, which is no longer supported. Re-enqueue without dry_run.")
                .Set("relative_media_path", rel);
            await RecordFailedResultAsync(Merge(legacy, provenance), null, "movie", null).ConfigureAwait(false);
            return;
        }

        var mediaScope = data.Get("media_scope") is WireString { Value: "movie" or "tv" } scopeValue ? scopeValue.Value : "movie";
        long? libraryId = data.Get("library_id") is WireInteger libraryValue ? (long)libraryValue.Value : null;
        var passThrough = data.Get("pass_through_unchanged") is WireBool { Value: true };
        var manualPlan = ManualPlanJson.FromPyJson(data.Get("manual_plan"));
        var manualPlanFingerprint = ManualPlanJson.FingerprintFromPyJson(data.Get("source_fingerprint"));

        var origin = data.Get("origin") as WireObject;
        var payloadJson = context.PayloadJson;
        if (origin is null)
        {
            origin = await CarriedOriginAsync(context.Id, libraryId, rel, mediaScope, cancellationToken).ConfigureAwait(false);
            if (origin is not null)
            {
                var carried = data.Copy().Set("origin", origin);
                payloadJson = WireJsonWriter.Dumps(carried, WireJsonFormat.Compact);
            }
        }

        if (await SettleRepeatAsync(data, provenance, rel, mediaScope, libraryId, payloadJson, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var claim = await ClaimAsync(context, rel, mediaScope, libraryId, data.Get(HandoffRetries.PayloadMarker) is WireBool { Value: true }, cancellationToken).ConfigureAwait(false);
        if (claim.Superseded)
        {
            _logger.LogInformation("Dropped the queued retry of {Path}: the file was given up on, finished or started again after it was queued.", rel);
            return;
        }

        if (claim.Failure is { } failure)
        {
            Merge(failure, provenance);
            await RecordFailedResultAsync(failure, libraryId, mediaScope, origin).ConfigureAwait(false);
            await ReportBackAsync(payloadJson, failure).ConfigureAwait(false);
            return;
        }

        var progress = new ActivityProgressReporter(_database, context.Id, provenance, _logger, _time, _liveProgress);
        var performance = claim.Operator!;
        var workflow = claim.Library!;
        var request = new RemuxPassRequest
        {
            Runtime = claim.Runtime!,
            RelativeMediaPath = rel,
            LibraryId = claim.Library?.Id ?? libraryId,
            RulesConfig = claim.Rules,
            RulesProfileName = claim.RulesProfileName,
            MinFileAgeSeconds = workflow.ReadyAfterSeconds,
            MinInputFileSizeMb = workflow.MinFileSizeMb,
            MinimumFreeDiskSpaceMb = workflow.MinimumFreeDiskSpaceMb,
            KeepFailedWorkFiles = performance.KeepFailedWorkFiles,
            MediaScope = mediaScope,
            CurrentJobId = context.Id,
            ProgressReporter = progress.Report,
            PassThroughUnchanged = passThrough,
            Origin = HandoffOrigin.FromPayload(origin is null ? null : new WireObject().Set("origin", origin)),
            ManualPlan = manualPlan,
            ManualPlanFingerprint = manualPlanFingerprint,
        };
        WireObject result;
        try
        {
            result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            result = await SettleVanishedAsync(request, result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Before RecordAsync turns the progress row into the completed row, so no late progress save overwrites it.
            await progress.CompleteAsync().ConfigureAwait(false);
        }

        // A hand-off that arrived while this pass was running took the pass over (MediaManagerIntake.AdoptActivePass)
        // and wrote its origin onto this job's row, in place of the one the pass started with when the release was sent again;
        // pick it up now so the outcome is recorded and called back for the hand-off that owns the pass.
        if (await AdoptedOriginAsync(context.Id, cancellationToken).ConfigureAwait(false) is { } adopted && !IsSameHandoff(origin, adopted))
        {
            origin = adopted;
            payloadJson = WireJsonWriter.Dumps(data.Copy().Set("origin", adopted), WireJsonFormat.Compact);
        }

        if (result.Get("outcome") is WireString { Value: RemuxPassOutcomes.SourceGone })
        {
            result.Set(GoneLooksKey, data.Get(GoneLooksKey) is WireInteger looked ? (long)looked.Value : 0);
            _logger.LogInformation("{Path} is no longer in the watched folder, so there is nothing to do.", rel);
        }

        result.Set("job_id", context.Id);
        result.Set("library_id", claim.Library?.Id ?? libraryId);
        // Before the rejection is acted on: a file that stays unreadable through the looks is refused here, and only then.
        await SettleUnreadableSourceAsync(context.Id, data, origin, result, cancellationToken).ConfigureAwait(false);
        if (result.Get("rejection_kind") is { IsTruthy: true } && claim.Library is { } library)
        {
            ApplyRejectedFileAction(result, library, claim.Runtime?.ManagerLinks ?? WorkflowManagerLinks.None, origin);
        }

        await DeferUntilDiskSpaceAsync(context.Id, data, origin, result, cancellationToken).ConfigureAwait(false);
        Merge(result, provenance);
        await ApplyFileOutcomeStateAsync(result, libraryId, mediaScope, origin).ConfigureAwait(false);
        await DeferUntilOldEnoughAsync(context.Id, data, origin, result, cancellationToken).ConfigureAwait(false);
        await LookAgainForGoneFileAsync(context.Id, data, origin, result, cancellationToken).ConfigureAwait(false);
        await RecordAsync(result, progress.ActivityId, newEntry: AddsGoneEntry(result, claim.GoneListed)).ConfigureAwait(false);

        await FinishRejectedInputCleanupAsync(result, claim.Library, libraryId, mediaScope, origin).ConfigureAwait(false);
        if (BeforeReport is { } beforeReport)
        {
            await beforeReport().ConfigureAwait(false);
        }

        await ReportBackAsync(payloadJson, result).ConfigureAwait(false);
        await DownloadedScanAsync(result, mediaScope, origin).ConfigureAwait(false);
    }

    private static WireObject FailedPayload(long jobId, string reason) => new WireObject()
        .Set("job_id", jobId)
        .Set("ok", false)
        .Set("outcome", RemuxPassOutcomes.FailedBeforeExecution)
        .Set("reason", reason);

    /// <summary><c>d.update(other)</c>.</summary>
    private static WireObject Merge(WireObject target, WireObject other)
    {
        foreach (var (key, value) in other.Items)
        {
            target.Set(key, value);
        }

        return target;
    }
}
