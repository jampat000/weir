using System.Globalization;
using Microsoft.Data.Sqlite;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>Working a hand-off's live state out from the job queue and the Files rows, and keeping the ledger row in step with it.</summary>
public sealed partial class HandoffLedgerStore
{
    /// <summary>
    /// The files this hand-off is about: the ones it covers (<c>media_manager_handoff_targets</c>, recorded at intake from
    /// the file or from the folder's videos with samples left out). A sample or extra that merely sits in the same folder,
    /// and that a scan recorded in its own right (even as waiting while processing was paused), is not one of them, so it
    /// never holds the hand-off back. A hand-off that records no files of its own (one that arrived before they were
    /// recorded) falls back to everything under its path.
    /// Matching is an exact prefix compare in .NET, not SQL <c>LIKE</c> (#544 item 5): <c>LIKE</c> treats <c>_</c> and
    /// <c>%</c> in the path as wildcards, so a sibling folder whose name merely resembles this one would be folded into
    /// this hand-off's status, and it ignores case on every platform. Case follows OS path semantics, as elsewhere
    /// (<see cref="ReconciliationService.SafeUnlinkUnderRoots"/>, <see cref="HandoffCompletionReporter.TranslateOutputPath"/>):
    /// case-insensitive on Windows, case-sensitive everywhere else.
    /// </summary>
    public static async Task<List<HandoffFileRow>> FileRowsAsync(UnitOfWork uow, HandoffLedgerRow row)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(row);
        if (row.LibraryId is not { } libraryId)
        {
            return [];
        }

        var path = row.RelativePath.TrimEnd('/');
        var rows = await uow.QueryAsync(
            "SELECT id, relative_path, status, status_reason, next_retry_at, updated_at FROM files " +
            "WHERE library_id = $library",
            reader => new HandoffFileRow(
                SqliteValues.GetInt64(reader, 0),
                SqliteValues.GetString(reader, 1),
                SqliteValues.GetString(reader, 2),
                SqliteValues.GetString(reader, 3),
                TimestampColumns.Parse(reader.GetValue(4)),
                TimestampColumns.Parse(reader.GetValue(5))),
            ("$library", libraryId)).ConfigureAwait(false);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = path + "/";
        var underPath = rows.Where(file => string.Equals(file.RelativePath, path, comparison) || file.RelativePath.StartsWith(prefix, comparison)).ToList();

        var covered = await TargetPathsAsync(uow, row).ConfigureAwait(false);
        if (covered.Count == 0)
        {
            return underPath;
        }

        var coveredPaths = new HashSet<string>(covered, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return [.. underPath.Where(file => coveredPaths.Contains(file.RelativePath))];
    }

    /// <summary>
    /// The paths of the files this hand-off covers, whether or not each still has a row in the list: a file taken off the list
    /// is still one the manager was handed and may still answer for. A hand-off that records no files of its own covers its own
    /// path and every file listed under it.
    /// </summary>
    public static async Task<List<string>> CoveredPathsAsync(UnitOfWork uow, HandoffLedgerRow row)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(row);
        var targets = await TargetPathsAsync(uow, row).ConfigureAwait(false);
        if (targets.Count > 0)
        {
            return targets;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return [.. new[] { row.RelativePath.TrimEnd('/') }.Concat((await FileRowsAsync(uow, row).ConfigureAwait(false)).Select(file => file.RelativePath)).Distinct(comparer)];
    }

    private static Task<List<string>> TargetPathsAsync(UnitOfWork uow, HandoffLedgerRow row) =>
        uow.QueryAsync(
            "SELECT relative_path FROM media_manager_handoff_targets WHERE handoff_row_id = $row",
            reader => SqliteValues.GetString(reader, 0),
            ("$row", row.Id));

    /// <summary>
    /// Pending or leased jobs keyed to this hand-off, or the failure-policy jobs for its files.
    /// The hand-off's own <c>dedupe_key</c> prefix is matched with <c>substr(...) =</c>, not <c>LIKE</c> (#544 item 5), so a
    /// hand-off id containing <c>_</c> or <c>%</c> cannot match another hand-off's jobs. A dedupe key is an opaque
    /// identifier, not a path, so this match is always case-sensitive (SQLite's default <c>BINARY</c> collation for <c>=</c>).
    /// </summary>
    public static async Task<List<ProcessingJob>> JobsForAsync(UnitOfWork uow, HandoffLedgerRow row)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(row);
        var baseKey = IntakeRules.RemuxDedupeKey(row.SourceKey, row.HandoffId);
        var conditions = new List<string> { "dedupe_key = $base", "substr(dedupe_key, 1, length($base_prefix)) = $base_prefix" };
        var parameters = new List<(string, object?)> { ("$base", baseKey), ("$base_prefix", baseKey + ":") };
        if (row.LibraryId is { } libraryId)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal) { row.RelativePath };
            foreach (var file in await FileRowsAsync(uow, row).ConfigureAwait(false))
            {
                paths.Add(file.RelativePath);
            }

            var index = 0;
            foreach (var path in paths)
            {
                // #545 item 2: pass-through and reject dedupe keys carry the source's fingerprint as a trailing segment
                // (so a later failure of a since-replaced file queues again), so the ledger matches the base
                // "{kind}:{library}:{path}" either exactly (rows written by earlier releases) or as a prefix. The prefix is
                // compared as plain text, not a LIKE pattern, so "_" or "%" in a path never matches another file (#544 item 5).
                var passBase = $"{IntakeRules.PassThroughJobKind}:{libraryId.ToString(CultureInfo.InvariantCulture)}:{path}";
                var rejectBase = $"{IntakeRules.RejectJobKind}:{libraryId.ToString(CultureInfo.InvariantCulture)}:{path}";
                conditions.Add($"(dedupe_key = $pass_{index} OR substr(dedupe_key, 1, length($pass_prefix_{index})) = $pass_prefix_{index})");
                parameters.Add(($"$pass_{index}", passBase));
                parameters.Add(($"$pass_prefix_{index}", passBase + ":"));
                conditions.Add($"(dedupe_key = $reject_{index} OR substr(dedupe_key, 1, length($reject_prefix_{index})) = $reject_prefix_{index})");
                parameters.Add(($"$reject_{index}", rejectBase));
                parameters.Add(($"$reject_prefix_{index}", rejectBase + ":"));
                index++;
            }
        }

        parameters.Add(("$pending", ProcessingJobStatus.Pending));
        parameters.Add(("$leased", ProcessingJobStatus.Leased));
        parameters.Add(("$failed", ProcessingJobStatus.Failed));
        parameters.Add(("$pass_kind", IntakeRules.PassThroughJobKind));
        parameters.Add(("$reject_kind", IntakeRules.RejectJobKind));
        // #545 item 3: a pass-through or reject job that exhausted its own retries drops out of pending/leased, but it
        // is still an undelivered outcome the manager needs to hear about (as failed, with the reason) rather than
        // silently vanishing from the ledger's view.
        return await uow.QueryAsync(
            $"SELECT {ProcessingJobStore.JobColumns} FROM jobs WHERE ({string.Join(" OR ", conditions)}) AND " +
            "(status IN ($pending, $leased) OR (status = $failed AND job_kind IN ($pass_kind, $reject_kind)))",
            ProcessingJobStore.ReadJob,
            [.. parameters]).ConfigureAwait(false);
    }

    /// <summary>
    /// Files waiting ahead of this one, plus one. Only file work counts (another file's pass or a library clean), because
    /// that is what a manager is waiting behind. Background jobs (folder scans, the work file sweep) are quick and are not
    /// files; counting them would tell a manager it was third in line behind two sweeps.
    /// </summary>
    public static async Task<long> QueuePositionAsync(UnitOfWork uow, ProcessingJob job)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(job);
        var ahead = await uow.CountAsync(
            "SELECT count(id) FROM jobs WHERE status = $pending AND (priority > $priority OR (priority = $priority AND id < $id)) " +
            "AND (job_kind LIKE 'processing.file.%' OR job_kind = $library_clean)",
            ("$pending", ProcessingJobStatus.Pending),
            ("$priority", job.Priority),
            ("$id", job.Id),
            ("$library_clean", "processing.library.clean.v1")).ConfigureAwait(false);
        return ahead + 1;
    }

    /// <summary>Work the state out from what exists now, and keep the ledger row in step with it.</summary>
    public async Task<HandoffStatus> CurrentStatusAsync(UnitOfWork uow, HandoffLedgerRow row)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(row);
        var now = NowToMicroseconds();
        string? liveState = null;
        DateTimeOffset? changedAt = null;
        long? queuePosition = null;
        DateTimeOffset? scheduledFor = null;
        string? message = null;

        if (row.State != HandoffLedgerRules.Cancelled)
        {
            var jobs = await JobsForAsync(uow, row).ConfigureAwait(false);
            var files = await FileRowsAsync(uow, row).ConfigureAwait(false);
            var targets = await _targets.ListAsync(uow, row.Id).ConfigureAwait(false);
            var states = new List<string>();
            var stamps = new List<DateTimeOffset>();
            var busyPaths = new HashSet<string>(StringComparer.Ordinal);

            foreach (var job in jobs)
            {
                stamps.Add(job.UpdatedAt);
                if (PayloadRelativePath(job.PayloadJson) is { } busyPath)
                {
                    busyPaths.Add(busyPath);
                }

                var isOutcomeJob = job.JobKind is IntakeRules.PassThroughJobKind or IntakeRules.RejectJobKind;

                // #545 item 3: a pass-through or reject job that exhausted its own retries is a final, undelivered
                // outcome — report it as failed, with the reason the job itself recorded, rather than let it vanish
                // once it drops out of pending/leased (JobsForAsync still returns it for exactly this reason).
                if (job.Status == ProcessingJobStatus.Failed)
                {
                    // A failed pass-through or reject job is found by the file's path, not by the hand-off, so one left
                    // by an earlier hand-off of the same release belongs to that hand-off, not this one. Counting it
                    // would turn a hand-off Weir had just completed into "failed", and Weir would then refuse the
                    // manager's "imported" for it.
                    if (IsFromEarlierHandoff(row, job.CreatedAt))
                    {
                        continue;
                    }

                    states.Add(HandoffLedgerRules.Failed);
                    message ??= string.IsNullOrEmpty(job.LastError) ? "Weir could not hand this file back to your media manager." : job.LastError;
                    continue;
                }

                if (job.Status == ProcessingJobStatus.Leased)
                {
                    states.Add(HandoffLedgerRules.Working);
                    continue;
                }

                if (isOutcomeJob)
                {
                    // A pending pass-through or reject job is a decided disposition about to run, not a normal place
                    // in the remux queue, so it is reported as scheduled rather than queued (and carries no queue
                    // position — that field means something only for the remux queue itself).
                    states.Add(HandoffLedgerRules.Scheduled);
                    if (job.NotBefore is { } outcomeNotBefore && outcomeNotBefore > now)
                    {
                        scheduledFor = scheduledFor is { } currentOutcome ? Min(currentOutcome, outcomeNotBefore) : outcomeNotBefore;
                    }

                    continue;
                }

                if (job.NotBefore is { } notBefore && notBefore > now)
                {
                    states.Add(HandoffLedgerRules.Scheduled);
                    scheduledFor = scheduledFor is { } current ? Min(current, notBefore) : notBefore;
                }
                else
                {
                    states.Add(HandoffLedgerRules.Queued);
                    var position = await QueuePositionAsync(uow, job).ConfigureAwait(false);
                    queuePosition = queuePosition is { } currentPosition && currentPosition != 0 ? Math.Min(currentPosition, position) : position;
                }
            }

            foreach (var file in files)
            {
                if (file.UpdatedAt is { } stamp)
                {
                    stamps.Add(stamp);
                }

                if (busyPaths.Contains(file.RelativePath))
                {
                    if (file.StatusReason.Length > 0 && message is null)
                    {
                        message = file.StatusReason;
                    }

                    continue;
                }

                var (state, when) = HandoffLedgerRules.FileState(file.Status, file.NextRetryAt);
                if (LeftAloneByTheWorkflow(file, targets))
                {
                    state = HandoffLedgerRules.Skipped;
                }

                // The same for a file row: a failure recorded before this hand-off arrived, and not touched since, is
                // what became of an earlier hand-off of the path. A success from before still counts.
                if (state is HandoffLedgerRules.Failed or HandoffLedgerRules.Skipped or HandoffLedgerRules.Rejected or HandoffLedgerRules.Cancelled &&
                    IsFromEarlierHandoff(row, file.UpdatedAt))
                {
                    continue;
                }

                states.Add(state);
                if (when is { } whenValue)
                {
                    scheduledFor = scheduledFor is { } current ? Min(current, whenValue) : whenValue;
                }

                if (file.StatusReason.Length > 0 && (message is null || !(HandoffLedgerRules.TerminalStates.Contains(state) || state == HandoffLedgerRules.Skipped)))
                {
                    message = file.StatusReason;
                }
            }

            if (states.Count > 0)
            {
                liveState = HandoffLedgerRules.Combine(states);
                changedAt = stamps.Count > 0 ? stamps.Max() : null;
                if (liveState != HandoffLedgerRules.Queued)
                {
                    queuePosition = null;
                }

                if (liveState != HandoffLedgerRules.Scheduled)
                {
                    scheduledFor = null;
                }
            }
        }

        var answerState = row.State;
        var lastChanged = row.LastChangedAt;
        var storedMessage = row.Message;
        if (liveState is not null)
        {
            var changes = new List<(string, object?)>();
            var previous = row.LastChangedAt;
            if (liveState != row.State)
            {
                answerState = liveState;
                lastChanged = changedAt is { } changed && (previous is null || changed > previous) ? changedAt : now;
                changes.Add(("state", answerState));
                changes.Add(("last_changed_at", TimestampColumns.Orm(lastChanged!.Value)));
            }
            else if (changedAt is { } newer && previous is { } before && newer > before)
            {
                lastChanged = newer;
                changes.Add(("last_changed_at", TimestampColumns.Orm(newer)));
            }

            if (message is not null && !HandoffLedgerRules.TerminalStates.Contains(liveState))
            {
                var sliced = WireStrings.Slice(message, 2000);
                if (sliced != row.Message)
                {
                    storedMessage = sliced;
                    changes.Add(("message", sliced));
                }
            }

            if (changes.Count > 0)
            {
                var sets = changes.Select((change, index) => $"{change.Item1} = $v{index}");
                await uow.ExecuteAsync(
                    $"UPDATE media_manager_handoffs SET {string.Join(", ", sets)} WHERE id = $row",
                    [.. changes.Select((change, index) => ($"$v{index}", change.Item2)), ("$row", row.Id)]).ConfigureAwait(false);
            }
        }

        var delivered = answerState is HandoffLedgerRules.Completed or HandoffLedgerRules.PassedThrough;
        return new HandoffStatus(
            row.HandoffId,
            answerState,
            lastChanged ?? now,
            queuePosition,
            scheduledFor,
            delivered ? row.OutputPath : null,
            message ?? storedMessage,
            delivered ? ReportedOutputFiles(row) : null);
    }

    /// <summary>
    /// Whether the file is one the workflow's own rules left alone (a pass skipped it under the minimum size) and that may be
    /// left alone without failing the hand-off. The stored result of the file's target says so, never the file's status, because
    /// that status is also written for a rejected release, an unreadable file or a file with no video, which a person must see.
    /// </summary>
    private static bool LeftAloneByTheWorkflow(HandoffFileRow file, IReadOnlyList<HandoffTarget> targets)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return file.Status == ProcessingFileStatuses.Skipped &&
               targets.FirstOrDefault(target => string.Equals(target.RelativePath, file.RelativePath, comparison)) is { Result: HandoffLedgerRules.Skipped } target &&
               FolderHandoffReports.MayBeLeftAlone(target, targets);
    }

    /// <summary>The output files Weir reported; a hand-off reported before the list was kept named its one file.</summary>
    private static IReadOnlyList<string>? ReportedOutputFiles(HandoffLedgerRow row) =>
        row.OutputFiles ?? (row.OutputPath is { } single ? [single] : null);

    /// <summary>Whether Weir's report to the manager named at least one output file, whatever else became of the hand-off.</summary>
    public static bool HandedBackFile(HandoffLedgerRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return ReportedOutputFiles(row) is { Count: > 0 };
    }

    /// <summary>
    /// The <c>relative_media_path</c> a job payload names, as a string (empty when missing or falsy); null for unreadable
    /// JSON. A payload that is not a JSON object throws.
    /// </summary>
    private static string? PayloadRelativePath(string? payloadJson)
    {
        WireValue parsed;
        try
        {
            parsed = WireJsonParser.Parse(string.IsNullOrEmpty(payloadJson) ? "{}" : payloadJson);
        }
        catch (WireJsonDecodeException)
        {
            return null;
        }

        if (parsed is not WireObject dict)
        {
            throw new InvalidOperationException("A job's payload is not a JSON object.");
        }

        return dict.Get("relative_media_path") is { IsTruthy: true } value ? WireConvert.Str(value) : string.Empty;
    }

    private DateTimeOffset NowToMicroseconds() => Timestamp.TruncateToMicroseconds(_time.GetUtcNow());

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    /// <summary>
    /// How much older than the hand-off a record must be to belong to an earlier one. Receiving a hand-off writes its
    /// file's row in the same request, a moment before the hand-off's own row, so "older at all" would wrongly set
    /// aside a failure that happened to this hand-off. An earlier hand-off's leftovers are minutes to days old.
    /// </summary>
    private static readonly TimeSpan EarlierHandoffMargin = TimeSpan.FromMinutes(1);

    /// <summary>Whether a record last written at <paramref name="at"/> belongs to an earlier hand-off of the same path.</summary>
    internal static bool IsFromEarlierHandoff(HandoffLedgerRow row, DateTimeOffset? at) =>
        row.ReceivedAt is { } received && at is { } written && written < received - EarlierHandoffMargin;

    internal static ProcessingJob ReadJob(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        TimestampColumns.Parse(reader.GetValue(6)),
        (int)reader.GetInt64(7),
        (int)reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        TimestampColumns.Parse(reader.GetValue(10)),
        (int)reader.GetInt64(11),
        (int)reader.GetInt64(12),
        TimestampColumns.Parse(reader.GetValue(13)) ?? DateTimeOffset.MinValue,
        TimestampColumns.Parse(reader.GetValue(14)) ?? DateTimeOffset.MinValue);
}
