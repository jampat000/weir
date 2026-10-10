using Microsoft.Extensions.Logging;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Observability;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// Worker handler for <c>processing.file.pass_through.v1</c>: hand
/// the unmodified original back to the output folder once retries are exhausted, then tell a waiting manager it is ready.
/// </summary>
/// <remarks>
/// Runs as its own durable job, and the copy holds no unit of work open (#465): failures are recorded inside a
/// transaction, and a pass-through can be a very large copy, so it only ever queues delivery. The worker reads the
/// library, closes its unit of work, copies, then opens a brief transaction for bookkeeping.
/// </remarks>
public sealed class ProcessingPassThroughHandler : IJobHandler
{
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _time;
    private readonly ILogger<ProcessingPassThroughHandler> _logger;
    private readonly HandbackStore _handback;
    private readonly LibraryStore _libraries;
    private readonly ProcessingJobStore _jobs;
    private readonly HandoffCompletionReporter? _reporter;
    private readonly IOutputOwnership? _ownership;

    public ProcessingPassThroughHandler(
        SqliteDatabase database,
        TimeProvider time,
        ILogger<ProcessingPassThroughHandler> logger,
        HandbackStore handback,
        LibraryStore libraries,
        ProcessingJobStore jobs,
        HandoffCompletionReporter? reporter = null,
        IOutputOwnership? ownership = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _handback = handback ?? throw new ArgumentNullException(nameof(handback));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _reporter = reporter;
        _ownership = ownership;
    }

    /// <summary>Test seam: how long a file that was not there is given to come back before Weir believes it is gone.</summary>
    internal TimeSpan GoneSettle { get; init; } = GoneSources.DefaultSettle;

    private const string GoneLooksKey = GoneLooks.Key;

    public string JobKind => IntakeRules.PassThroughJobKind;

    public async Task HandleAsync(JobWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = FollowUpJobPayload.Parse(context.PayloadJson);
        var relativePath = FollowUpJobPayload.RelativeMediaPath(payload);
        var libraryId = FollowUpJobPayload.LibraryId(payload);
        if (relativePath.Length == 0 || libraryId is null)
        {
            throw new InvalidOperationException("A pass-through job needs a file and a workflow.");
        }

        // 1. Read what the delivery needs, then close the unit of work before touching any file.
        var delivery = await LockedWrites.RunAsync(
            _database,
            async uow =>
            {
                var library = await RemuxPassHandler.ResolveLibraryAsync(uow, _libraries, libraryId, null).ConfigureAwait(false);
                if (library is null)
                {
                    throw new InvalidOperationException($"Workflow {libraryId} no longer exists, so there is nowhere to hand the file back to.");
                }

                return new PassThroughDeliverySettings(
                    library.Id, library.WatchedFolder, library.OutputFolder, library.OutputCollisionPolicy, library.MinimumFreeDiskSpaceMb);
            },
            _logger,
            "pass-through claim",
            cancellationToken).ConfigureAwait(false);

        // 2. The copy, with no transaction open — this can take minutes on a large file.
        PassThroughDeliveryResult? delivered;
        try
        {
            delivered = await DeliverUnlessGoneAsync(delivery, relativePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PassThroughIntegrityException or FileNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Pass-through could not deliver {Path}.", relativePath);
            await LockedWrites.RunAsync(
                _database,
                uow => SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
                    ActivityEventTypes.ProcessingFilePassThroughFailed,
                    "processing",
                    $"{MediaPathNames.Name(relativePath, OperatingSystem.IsWindows())} could not be handed back",
                    WireJsonWriter.Dumps(
                        new WireObject()
                            .Set("job_id", context.Id)
                            .Set("relative_media_path", relativePath)
                            .Set("message", WireStrings.Slice(exception.Message, 1200))
                            .Set("trigger", "worker")
                            .Set("result", "failed")
                            .Set("library_id", delivery.LibraryId)
                            .Set("source_kept", true)
                            .Set("next_action",
                                "The original is untouched in the watched folder. Check that the output folder exists and is writable."),
                        WireJsonFormat.Compact))),
                _logger,
                "pass-through failure record",
                cancellationToken).ConfigureAwait(false);

            // Recorded above in plain words; the worker still fails the job but does not say it twice (#488).
            throw new AlreadyRecordedFailureException(exception.Message, exception);
        }

        if (delivered is not { } result)
        {
            await SettleGoneAsync(context, payload, delivery, relativePath, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A drive short of room holds the file rather than fail it: nothing was copied, and it is looked at again later.
        if (result.WaitingForSpace is not null)
        {
            await HoldForSpaceAsync(context, payload, delivery, relativePath, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 3. Brief bookkeeping.
        var now = _time.GetUtcNow();
        await LockedWrites.RunAsync(
            _database,
            uow => RecordDeliveryAsync(uow, delivery, relativePath, result, context.Id, now),
            _logger,
            "pass-through delivery record",
            cancellationToken).ConfigureAwait(false);

        // 4. Tell a waiting manager the file is ready. Only when something was actually delivered: after a collision
        // skip the file at that path is not this one, and asking the manager to import it would be wrong. Reporting
        // never raises; the delivery already succeeded.
        var origin = FollowUpJobPayload.Origin(payload);
        if (origin is { IsTruthy: true } && result.Delivered && _reporter is not null)
        {
            var reportResult = new WireObject()
                .Set("ok", true)
                .Set("outcome", "live_output_written")
                .Set("relative_media_path", relativePath)
                .Set("output_file", result.Destination)
                .Set("processing_output_folder_resolved", RemuxPassPaths.Resolve(delivery.OutputFolder))
                .Set("passed_through_after_failure", true);
            await ReportToManagerAsync(origin, delivery.LibraryId, reportResult, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Tells the manager that handed the file over how it ended. Reporting never raises.</summary>
    private async Task ReportToManagerAsync(WireObject origin, long libraryId, WireObject reportResult, CancellationToken cancellationToken)
    {
        if (_reporter is null)
        {
            return;
        }

        var reportPayload = WireJsonWriter.Dumps(new WireObject().Set("origin", origin).Set("library_id", libraryId), WireJsonFormat.Compact);
        var uow = await UnitOfWork.OpenAsync(_database, cancellationToken).ConfigureAwait(false);
        string status;
        await using (uow.ConfigureAwait(false))
        {
            status = await _reporter.ReportHandoffCompletionAsync(uow, reportPayload, reportResult, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Pass-through hand-off report: {Status}", status);
    }

    /// <summary>
    /// The copy, or null when the original is gone. A file that was not there is given a few seconds to come back, as a share that
    /// dropped for a moment would; one that is back is copied then.
    /// </summary>
    private async Task<PassThroughDeliveryResult?> DeliverUnlessGoneAsync(PassThroughDeliverySettings delivery, string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            return await PassThroughDelivery.DeliverUnchangedAsync(delivery, relativePath, _ownership).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever the copy raised, a file that is gone has nothing left to deliver.
        catch (Exception) when (GoneSources.HasLeft(delivery.WatchedFolder, relativePath))
#pragma warning restore CA1031
        {
            await Task.Delay(GoneSettle, cancellationToken).ConfigureAwait(false);
        }

        return GoneSources.HasLeft(delivery.WatchedFolder, relativePath)
            ? null
            : await PassThroughDelivery.DeliverUnchangedAsync(delivery, relativePath, _ownership).ConfigureAwait(false);
    }

    /// <summary>
    /// A file deleted before it could be handed back. The first time it is held, not forgotten, with a look booked for after the
    /// scan's grace for a vanished file; Activity says once, plainly, that there is nothing to do, unless the scan already did. That
    /// look finds the file back, and hands it back, or still gone, and forgets it and tells the manager that handed it over.
    /// </summary>
    private async Task SettleGoneAsync(
        JobWorkContext context, WireObject payload, PassThroughDeliverySettings delivery, string relativePath, CancellationToken cancellationToken)
    {
        var looks = payload.Get(GoneLooksKey) is WireInteger counted ? (long)counted.Value : 0;
        var final = looks > 0;
        var now = _time.GetUtcNow();
        var lookAgainAt = now + GoneSources.LookAgainAfter;
        var name = MediaPathNames.Name(relativePath, OperatingSystem.IsWindows());
        await LockedWrites.RunAsync(
            _database,
            async uow =>
            {
                if (final)
                {
                    await RemuxPassFileState.ForgetGoneAsync(uow, delivery.LibraryId, relativePath).ConfigureAwait(false);
                    return;
                }

                if (!await RemuxPassFileState.HoldGoneAsync(uow, delivery.LibraryId, relativePath, lookAgainAt).ConfigureAwait(false))
                {
                    return;
                }

                var detail = OperatorMessages.ActivityDetailEnvelope("processing", "pass_through", "worker", "skipped", userMessage: GoneSourceText.Reason)
                    .Set("job_id", context.Id)
                    .Set("relative_media_path", relativePath)
                    .Set("library_id", delivery.LibraryId);
                await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
                    ActivityEventTypes.ProcessingFileLeftWatchedFolder,
                    "processing",
                    GoneSourceText.Title(name),
                    WireJsonWriter.Dumps(detail, WireJsonFormat.Compact))).ConfigureAwait(false);
            },
            _logger,
            "pass-through of a file that is gone",
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Path} is no longer in the watched folder, so there is nothing to hand back.", relativePath);

        if (!final)
        {
            await _jobs.EnqueueOrGetAsync(
                $"{IntakeRules.PassThroughJobKind}:gone-wait:{context.Id}",
                IntakeRules.PassThroughJobKind,
                WireJsonWriter.Dumps(payload.Copy().Set(GoneLooksKey, 1), WireJsonFormat.Compact),
                notBefore: lookAgainAt,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else if (FollowUpJobPayload.Origin(payload) is { IsTruthy: true } origin)
        {
            var reportResult = new WireObject()
                .Set("ok", true)
                .Set("outcome", RemuxPassOutcomes.SourceGone)
                .Set("relative_media_path", relativePath);
            await ReportToManagerAsync(origin, delivery.LibraryId, reportResult, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Puts the file on hold with the reason and books this same hand-back for later, so it is never recorded as handed back
    /// while the copy has not been made. The hold ends when the next look is due.
    /// </summary>
    private async Task HoldForSpaceAsync(
        JobWorkContext context, WireObject payload, PassThroughDeliverySettings delivery, string relativePath, PassThroughDeliveryResult result, CancellationToken cancellationToken)
    {
        var looks = payload.Get("disk_space_looks") is WireInteger counted ? (long)counted.Value : 0;
        var lookAgainAt = _time.GetUtcNow() + DiskSpaceWaits.LookAfter(looks);
        await LockedWrites.RunAsync(
            _database,
            async uow =>
            {
                if (await RemuxPassFileState.MarkFileStatusAsync(uow, delivery.LibraryId, relativePath, ProcessingFileStatuses.OnHold, result.Sentence, _time.GetUtcNow()).ConfigureAwait(false))
                {
                    await RemuxPassFileState.HoldUntilAsync(uow, delivery.LibraryId, relativePath, lookAgainAt).ConfigureAwait(false);
                }
            },
            _logger,
            "pass-through space hold",
            cancellationToken).ConfigureAwait(false);
        await _jobs.EnqueueOrGetAsync(
            $"{IntakeRules.PassThroughJobKind}:disk-space-wait:{context.Id}",
            IntakeRules.PassThroughJobKind,
            WireJsonWriter.Dumps(payload.Copy().Set("disk_space_looks", looks + 1), WireJsonFormat.Compact),
            notBefore: lookAgainAt,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Pass-through of {Path} is waiting for room on the output drive.", relativePath);
    }

    /// <summary>The short bookkeeping transaction after a delivery.</summary>
    private async Task RecordDeliveryAsync(UnitOfWork uow, PassThroughDeliverySettings settings, string relativePath, PassThroughDeliveryResult result, long jobId, DateTimeOffset now)
    {
        await RemuxPassFileState.RecordOutputCollisionAsync(uow, relativePath, result.Collision, settings.LibraryId).ConfigureAwait(false);
        await RemuxPassFileState.MarkFileStatusAsync(uow, settings.LibraryId, relativePath, ProcessingFileStatuses.PassedThrough, result.Sentence, now).ConfigureAwait(false);
        if (result.Delivered)
        {
            // #652: the copy handed back, so it can be released safely once a manager has it.
            await _handback.RecordWrittenAsync(uow, settings.LibraryId, relativePath, result.Destination, now).ConfigureAwait(false);
        }

        var detail = new WireObject()
            .Set("job_id", jobId)
            .Set("relative_media_path", relativePath)
            .Set("library_id", settings.LibraryId)
            .Set("delivered", result.Delivered)
            .Set("destination", result.Destination)
            .Set("collision_action", result.Collision.Action)
            .Set("source_kept", true)
            .Set("message", result.Sentence)
            .Set("trigger", "worker")
            .Set("result", result.Delivered ? "success" : "skipped");
        await SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
            ActivityEventTypes.ProcessingFilePassedThrough,
            "processing",
            $"{MediaPathNames.Name(relativePath, OperatingSystem.IsWindows())} was handed back unchanged",
            WireStrings.Slice(WireJsonWriter.Dumps(detail, WireJsonFormat.Compact), 10_000))).ConfigureAwait(false);
    }
}
