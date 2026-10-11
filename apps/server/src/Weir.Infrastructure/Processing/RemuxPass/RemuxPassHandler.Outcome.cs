using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Time;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassHandler
{
    /// <summary>Keeps the durable Files row in step with the result shown in Activity.</summary>
    internal async Task ApplyFileOutcomeStateAsync(WireObject result, long? libraryId, string mediaScope, WireObject? origin)
    {
        if (result.Get("relative_media_path") is not WireString relValue || WireStrings.Strip(relValue.Value).Length == 0)
        {
            return;
        }

        var rel = WireStrings.Strip(relValue.Value);
        var updates = new WireObject();
        try
        {
            await LockedWrites.RunAsync(
                _database,
                async uow =>
                {
                    updates = new WireObject();
                    var library = await ResolveLibraryAsync(uow, _libraries, libraryId, mediaScope).ConfigureAwait(false);
                    if (library is null)
                    {
                        return;
                    }

                    var now = _time.GetUtcNow();
                    if (result.Get("rejection_kind") is { IsTruthy: true })
                    {
                        var rejectionReason = WireStrings.Slice(CollapseWhitespace(TextOr(result.Get("reason"), "Weir rejected this file before processing.")), 1200);
                        var cleanupDetail = TextOr(result.Get("rejected_cleanup_detail"), "The saved rejected-file action has not run yet.");
                        var rejectionKind = result.Get("rejection_kind") is WireString kind ? kind.Value : null;
                        if (await _failurePolicy.RejectBadReleaseAsync(uow, library, rel, rejectionReason, rejectionKind, origin).ConfigureAwait(false))
                        {
                            updates.Set("reject_queued", true);
                            cleanupDetail = "Weir is telling your media manager this release is bad so it can find a different one, " +
                                            "and removes the download only once the manager accepts.";
                        }

                        var reason = $"{rejectionReason} {cleanupDetail}";
                        if (WeirOnlyRejection.Applies(library, origin))
                        {
                            await RecordWeirOnlyRejectionAsync(uow, library, rel, reason, result).ConfigureAwait(false);
                        }
                        else if (await RemuxPassFileState.MarkFileStatusAsync(uow, library.Id, rel, ProcessingFileStatuses.Skipped, reason, now).ConfigureAwait(false))
                        {
                            await RemuxPassFileState.ClearFailureFieldsAsync(uow, library.Id, rel).ConfigureAwait(false);
                        }

                        updates.Set("retry_scheduled", false).Set("failure_next_retry_at", WireNull.Instance).Set("failure_operator_message", reason);
                        return;
                    }

                    if (result.Get("retryable_wait") is WireBool { Value: true })
                    {
                        var reason = WireStrings.Slice(CollapseWhitespace(TextOr(result.Get("reason"), "Weir is waiting until no other program is writing this file.")), 1200);
                        if (await RemuxPassFileState.MarkFileStatusAsync(uow, library.Id, rel, ProcessingFileStatuses.OnHold, reason, now).ConfigureAwait(false))
                        {
                            await RemuxPassFileState.ClearFailureFieldsAsync(uow, library.Id, rel).ConfigureAwait(false);
                            // Another look is booked (#646): the file is on hold until it, not ready for the next free lane.
                            if (result.Get("failure_next_retry_at") is WireString { Value.Length: > 0 } booked &&
                                TimestampColumns.Parse(booked.Value) is { } lookAgainAt)
                            {
                                await RemuxPassFileState.HoldUntilAsync(uow, library.Id, rel, lookAgainAt).ConfigureAwait(false);
                            }
                        }

                        updates.Set("retry_scheduled", false).Set("failure_next_retry_at", WireNull.Instance).Set("failure_operator_message", reason);
                        return;
                    }

                    if (result.Get("outcome") is WireString { Value: RemuxPassOutcomes.SourceGone })
                    {
                        if (result.Get(GoneLooksKey) is WireInteger { Value: var looks } && looks > 0)
                        {
                            await RemuxPassFileState.ForgetGoneAsync(uow, library.Id, rel).ConfigureAwait(false);
                        }
                        else
                        {
                            await RemuxPassFileState.HoldGoneAsync(uow, library.Id, rel, now + GoneSources.LookAgainAfter).ConfigureAwait(false);
                        }

                        updates.Set("retry_scheduled", false).Set("failure_next_retry_at", WireNull.Instance);
                        return;
                    }

                    if (result.Get("outcome") is WireString { Value: RemuxPassOutcomes.SkippedGuardrail })
                    {
                        await RecordGuardrailSkipAsync(uow, library, rel, result, updates, now).ConfigureAwait(false);
                        return;
                    }

                    if (result.Get("ok") is WireBool { Value: false })
                    {
                        var failureClass = ProcessingFailureClasses.Classify(TextOr(result.Get("outcome"), string.Empty));
                        var reason = WireStrings.Slice(CollapseWhitespace(TextOr(result.Get("reason"), "Weir returned an unsuccessful result.")), 1200);
                        var decision = await RemuxPassFileState.RecordFailureAsync(uow, library, rel, failureClass, reason, now).ConfigureAwait(false);
                        await HandoffRetries.QueueIfOwedAsync(uow, _failurePolicy, _libraries, library, rel, origin, decision).ConfigureAwait(false);
                        _scanWakeups?.RequestForRetry(library.Id, decision.NextRetryAt);
                        var followUp = await _failurePolicy.ApplyFailurePolicyAsync(
                            uow, library, rel, decision.WillRetry, origin, result.Get("content_unusable") is WireBool { Value: true }).ConfigureAwait(false);
                        updates
                            .Set("pass_through_queued", followUp == ProcessingFailurePolicies.PassThrough)
                            .Set("reject_queued", followUp == ProcessingFailurePolicies.Reject)
                            .Set("failure_class", failureClass)
                            .Set("retry_scheduled", decision.WillRetry)
                            .Set("failure_next_retry_at", decision.NextRetryAt is { } next ? Timestamp.FromDateTimeOffset(next).IsoFormat() : null)
                            .Set("failure_operator_message", decision.Reason);
                        return;
                    }

                    var processedReason = result.Get("pass_through_unchanged") is WireBool { Value: true }
                        ? "Weir passed this file through unchanged at the operator's request and placed it in the output folder."
                        : result.Get("outcome") is WireString { Value: RemuxPassOutcomes.LiveSkippedNotRequired }
                            ? "Checked this file and found that no changes were needed."
                            : "Finished processing this file.";
                    if (result.Get("source_kept_by_library_setting") is WireBool { Value: true })
                    {
                        processedReason += " The original download was kept in the watched folder, as this workflow asks.";
                    }
                    else if (result.Get("source_kept_by_manager_link") is WireBool { Value: true })
                    {
                        processedReason += " The original download was kept in the watched folder for your download client, because this workflow is linked to a media manager.";
                    }

                    if (await RemuxPassFileState.MarkFileStatusAsync(uow, library.Id, rel, ProcessingFileStatuses.Processed, processedReason, now).ConfigureAwait(false))
                    {
                        await RemuxPassFileState.ClearFailureFieldsAsync(uow, library.Id, rel).ConfigureAwait(false);
                    }

                    var sourceSize = result.Get("source_fingerprint_size") is WireInteger size ? (long)size.Value : (long?)null;
                    var sourceMtime = result.Get("source_fingerprint_mtime_ns") is WireInteger mtime ? (long)mtime.Value : (long?)null;
                    await RemuxPassFileState.RecordProcessedSourceAsync(uow, library.Id, rel, sourceSize, sourceMtime).ConfigureAwait(false);

                    // #652: exactly which copy this pass handed back, so it can be released safely once a manager has it, and the
                    // source it was cleaned from, so the same source is never cleaned again whatever becomes of the file's row.
                    // Only a copy this pass wrote itself: after a collision skip the file at that path is not Weir's.
                    if (result.Get("output_file") is WireString { Value.Length: > 0 } handedBack &&
                        result.Get("output_collision_action") is WireString { Value: "write" })
                    {
                        await _handback.RecordWrittenAsync(uow, library.Id, rel, handedBack.Value, now, sourceSize, sourceMtime).ConfigureAwait(false);
                    }
                },
                _logger,
                "file outcome").ConfigureAwait(false);
            foreach (var (key, value) in updates.Items)
            {
                result.Set(key, value);
            }
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            // The media pass remains the source of truth; a false failure over a completed file would be worse.
            _logger.LogWarning(exception, "Weir could not save the durable Files state; inspect the processing record.");
        }
    }

    /// <summary>
    /// A pass a guardrail stopped before it wrote anything (a file under the minimum size) is skipped, never done: nothing was
    /// written, and a scan looks at the file again once its size or the workflow's minimum changes.
    /// </summary>
    private static async Task RecordGuardrailSkipAsync(UnitOfWork uow, ProcessingLibraryRecord library, string relativePath, WireObject result, WireObject updates, DateTimeOffset now)
    {
        var reason = WireStrings.Slice(CollapseWhitespace(TextOr(result.Get("reason"), "Weir skipped this file.")), 1200);
        var skipKind = result.Get("skip_kind") is WireString { Value.Length: > 0 } kind ? kind.Value : null;
        if (await RemuxPassFileState.MarkFileStatusAsync(uow, library.Id, relativePath, ProcessingFileStatuses.Skipped, reason, now, skipKind).ConfigureAwait(false))
        {
            await RemuxPassFileState.ClearFailureFieldsAsync(uow, library.Id, relativePath).ConfigureAwait(false);
        }

        updates.Set("retry_scheduled", false).Set("failure_next_retry_at", WireNull.Instance).Set("failure_operator_message", reason);
    }

    /// <summary>Records a preflight failure through the same Files/Activity contract as a run result.</summary>
    private async Task RecordFailedResultAsync(WireObject payload, long? libraryId, string mediaScope, WireObject? origin)
    {
        await ApplyFileOutcomeStateAsync(payload, libraryId, mediaScope, origin).ConfigureAwait(false);
        await RecordAsync(payload).ConfigureAwait(false);
    }

    /// <summary><c>str(value or fallback)</c>.</summary>
    private static string TextOr(WireValue? value, string fallback) => value is { IsTruthy: true } ? WireConvert.Str(value) : fallback;

    /// <summary><c>" ".join(text.split())</c>.</summary>
    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
