using Weir.Core.Activity;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Observability;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// A row still waiting, held, failed or cancelled whose file is not on disk. The download client removed it, a person deleted it, or
/// the manager took it. The scan walks only files on disk, so nothing else would ever judge that row again, and it would stay
/// listed, with its old label, for ever (#645). Activity says once, plainly, that the file is no longer there.
/// </summary>
/// <remarks>
/// <para>A row a scan has seen waiting (to settle, outside its hours, for a manager) is held as soon as its file is found gone, so
/// its label is replaced at once: the file is looked for again after a few seconds first, as a share that drops for a moment would
/// leave its mount point behind, empty. It stays listed until it has been gone for <see cref="Grace"/> since a scan last saw it, and
/// is then forgotten, as Forget does, without saying so a second time. Any other row whose file is gone for that long is forgotten,
/// with the one entry. No row is held or forgotten on a single look: every change waits for the second.</para>
/// <para>A held row whose file is back is released to unprocessed, so a workflow whose files are never scanned (a manager hands them
/// over) does not keep telling a file that is there that it is gone. A held row whose file has a pass booked to look at it again
/// (<see cref="GoneLooks"/>) has that look started now instead, which processes the file.</para>
/// <para>A file with a pass queued or running is left to that pass, and a path that is now a folder on disk is kept. A watched folder
/// that cannot be read is never taken to mean its files left (<see cref="GoneSources.HasLeft"/>). Outcomes Weir reached (processed,
/// passed through, rejected, skipped) stay as history. A row carrying the source Weir last cleaned is never forgotten, because it is
/// what stops that source being cleaned twice if the file comes back unchanged: it goes back to processed and stops waiting on
/// anyone.</para>
/// <para>Rows no scan has seen are forgotten and never held, and so are failed and cancelled ones: a media manager's hand-off records
/// its file on receipt, before any scan sees it, and a cancelled hand-off is one Weir never started. Such a row counts from when it was
/// last written, so it gets the same grace before it is judged.</para>
/// <para>Every file is looked for with no transaction open; only the rows whose files are gone are then changed, a batch per short
/// transaction (#708). A row that changed in between (seen again, or moved on by a hand-off, a pass or another look) is kept, so two
/// looks at once change a row once.</para>
/// </remarks>
public static class VanishedFiles
{
    /// <summary>How long a file must have been gone, since a scan last saw it, before Weir stops listing it. Longer than a
    /// download client takes to move a file, and than a share takes to come back from a blip.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    /// <summary>Rows changed per transaction.</summary>
    internal const int BatchSize = 250;

    private const string Unchanged = "id = @id AND status = @status AND coalesce(last_seen_at, updated_at, created_at) = @seen";

    private static readonly string[] WaitingStatuses =
    [
        ProcessingFileStatuses.Unprocessed, ProcessingFileStatuses.OnHold, ProcessingFileStatuses.OutOfSchedule,
        ProcessingFileStatuses.BlockedUpstream, ProcessingFileStatuses.ProcessingFailed, ProcessingFileStatuses.Cancelled,
    ];

    /// <summary>The waits a scan records for a file it has seen, which no pass of its own will ever end.</summary>
    private static readonly string[] HoldableStatuses =
    [
        ProcessingFileStatuses.Unprocessed, ProcessingFileStatuses.OnHold, ProcessingFileStatuses.OutOfSchedule, ProcessingFileStatuses.BlockedUpstream,
    ];

    private enum Step
    {
        None,
        Hold,
        Forget,
        ForgetQuietly,
        Release,
        StartLook,
    }

    private sealed record WaitingRow(
        long Id, string RelativePath, string Status, string Reason, object LastSeenStored, DateTimeOffset? LastSeen, bool SeenByScan);

    /// <summary>The relative paths of the files held for being gone, forgotten, and released because they are back.</summary>
    public sealed record Changes(IReadOnlyList<string> Held, IReadOnlyList<string> Forgotten, IReadOnlyList<string> Released)
    {
        public bool Any => Held.Count + Forgotten.Count + Released.Count > 0;
    }

    /// <summary>
    /// Holds the library's rows whose files left the watched folder, forgets those that have been gone long enough, and releases
    /// those held whose files are back. <paramref name="lookAgain"/> waits between the first look at a file and the look that
    /// acts on it; it is only called when some file needs one.
    /// </summary>
    public static async Task<Changes> SettleAsync(
        SqliteDatabase database,
        long libraryId,
        string watchedRoot,
        string mediaScope,
        DateTimeOffset now,
        string trigger,
        Func<CancellationToken, Task> lookAgain,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(lookAgain);
        var cutoff = now - Grace;
        var (rows, queued) = await ReadAsync(database, libraryId, mediaScope, cancellationToken).ConfigureAwait(false);
        Step Judge(WaitingRow row) => queued.Contains(row.RelativePath) ? DecideQueued(row, watchedRoot) : Decide(row, cutoff, watchedRoot);
        var pending = rows
            .Select(row => (Row: row, Step: Judge(row)))
            .Where(item => item.Step != Step.None)
            .ToList();
        if (pending.Count > 0)
        {
            await lookAgain(cancellationToken).ConfigureAwait(false);
            pending = [.. pending.Where(item => Judge(item.Row) == item.Step)];
        }

        var held = new List<string>();
        var forgotten = new List<string>();
        var released = new List<string>();
        foreach (var batch in pending.Chunk(BatchSize))
        {
            await WriteLockTurns.TakeAsync(
                async () =>
                {
                    var uow = await UnitOfWork.OpenAsync(database, cancellationToken).ConfigureAwait(false);
                    await using (uow.ConfigureAwait(false))
                    {
                        foreach (var (row, step) in batch)
                        {
                            switch (step)
                            {
                                case Step.Hold when await HoldOneAsync(uow, libraryId, mediaScope, row, trigger).ConfigureAwait(false):
                                    held.Add(row.RelativePath);
                                    break;
                                case Step.Release when await ReleaseOneAsync(uow, mediaScope, libraryId, row).ConfigureAwait(false):
                                    released.Add(row.RelativePath);
                                    break;
                                case Step.StartLook when await GoneLooks.StartForFileAsync(uow, row.RelativePath, mediaScope, libraryId).ConfigureAwait(false):
                                    released.Add(row.RelativePath);
                                    break;
                                case Step.Forget or Step.ForgetQuietly
                                    when await ForgetOneAsync(uow, libraryId, mediaScope, row, trigger, record: step == Step.Forget).ConfigureAwait(false):
                                    forgotten.Add(row.RelativePath);
                                    break;
                            }
                        }

                        await uow.CommitAsync().ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new Changes(held, forgotten, released);
    }

    /// <summary>What to do with a row, by whether its file is gone, how long it has been and what the row already says.</summary>
    private static Step Decide(WaitingRow row, DateTimeOffset cutoff, string watchedRoot)
    {
        var held = GoneSourceText.IsHeld(row.Status, row.Reason);
        if (held && GoneSources.IsBack(watchedRoot, row.RelativePath))
        {
            return Step.Release;
        }

        if (row.LastSeen is not { } seen || !GoneSources.HasLeft(watchedRoot, row.RelativePath))
        {
            return Step.None;
        }

        if (seen <= cutoff)
        {
            return held ? Step.ForgetQuietly : Step.Forget;
        }

        return !held && row.SeenByScan && HoldableStatuses.Contains(row.Status) ? Step.Hold : Step.None;
    }

    /// <summary>What to do with a row whose file has a pass pending or leased: only a held row whose file is back, which starts the pass booked to look at it.</summary>
    private static Step DecideQueued(WaitingRow row, string watchedRoot) =>
        GoneSourceText.IsHeld(row.Status, row.Reason) && GoneSources.IsBack(watchedRoot, row.RelativePath) ? Step.StartLook : Step.None;

    /// <summary>The library's waiting rows, and the files that have a pass pending or leased, or a pass-through look waiting, which are left to it.</summary>
    private static async Task<(List<WaitingRow> Rows, HashSet<string> Queued)> ReadAsync(
        SqliteDatabase database, long libraryId, string mediaScope, CancellationToken cancellationToken)
    {
        var uow = await UnitOfWork.OpenAsync(database, cancellationToken).ConfigureAwait(false);
        await using (uow.ConfigureAwait(false))
        {
            var statuses = WaitingStatuses.Select((status, index) => ($"@s{index}", (object?)status));
            var rows = await uow.QueryAsync(
                "SELECT id, relative_path, status, status_reason, coalesce(last_seen_at, updated_at, created_at), last_seen_at IS NOT NULL FROM files " +
                $"WHERE library_id = @lib AND status IN ({string.Join(", ", WaitingStatuses.Select((_, index) => $"@s{index}"))})",
                reader => new WaitingRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    SqliteValues.GetString(reader, 3),
                    reader.GetValue(4),
                    TimestampColumns.Parse(reader.GetValue(4)),
                    reader.GetInt64(5) != 0),
                [("@lib", libraryId), .. statuses]).ConfigureAwait(false);
            var queued = await ActiveRemuxPasses.PathsAsync(uow, mediaScope, libraryId).ConfigureAwait(false);
            queued.UnionWith(await GoneLooks.WaitingPassThroughPathsAsync(uow, libraryId).ConfigureAwait(false));
            return (rows, queued);
        }
    }

    private static (string, object?)[] UnchangedParameters(WaitingRow row) =>
        [("@id", row.Id), ("@status", row.Status), ("@seen", row.LastSeenStored)];

    /// <summary>Holds one row for being gone, unless a pass now owns the file, the row changed since it was read, or another look held it first.</summary>
    private static async Task<bool> HoldOneAsync(UnitOfWork uow, long libraryId, string mediaScope, WaitingRow row, string trigger)
    {
        if (await ActiveRemuxPasses.ExistsForRelativePathAsync(uow, row.RelativePath, mediaScope, libraryId).ConfigureAwait(false))
        {
            return false;
        }

        // The hold ends when the row is forgotten, so the file shows a clock and does not read as a wait on a person.
        var held = await uow.ExecuteAsync(
            "UPDATE files SET status = @held, status_reason = @reason, blocked_by_connection = NULL, hold_until = @until, updated_at = CURRENT_TIMESTAMP " +
            $"WHERE {Unchanged} AND coalesce(status_reason, '') <> @reason",
            [
                ("@held", ProcessingFileStatuses.OnHold),
                ("@reason", GoneSourceText.HeldReason),
                ("@until", TimestampColumns.Orm(row.LastSeen!.Value + Grace)),
                .. UnchangedParameters(row),
            ]).ConfigureAwait(false);
        if (held == 0)
        {
            return false;
        }

        await RecordGoneAsync(uow, libraryId, row, trigger).ConfigureAwait(false);
        return true;
    }

    /// <summary>Releases one held row whose file is back, unless a pass now owns the file or the row changed since it was read.</summary>
    private static async Task<bool> ReleaseOneAsync(UnitOfWork uow, string mediaScope, long libraryId, WaitingRow row)
    {
        if (await ActiveRemuxPasses.ExistsForRelativePathAsync(uow, row.RelativePath, mediaScope, libraryId).ConfigureAwait(false))
        {
            return false;
        }

        var released = await uow.ExecuteAsync(
            "UPDATE files SET status = @unprocessed, status_reason = @back, hold_until = NULL, updated_at = CURRENT_TIMESTAMP " +
            $"WHERE {Unchanged} AND status_reason = @held",
            [
                ("@unprocessed", ProcessingFileStatuses.Unprocessed),
                ("@back", GoneSourceText.BackReason),
                ("@held", GoneSourceText.HeldReason),
                .. UnchangedParameters(row),
            ]).ConfigureAwait(false);
        return released > 0;
    }

    /// <summary>Forgets one row, unless a pass now owns the file or the row changed since it was read.</summary>
    private static async Task<bool> ForgetOneAsync(UnitOfWork uow, long libraryId, string mediaScope, WaitingRow row, string trigger, bool record)
    {
        if (await ActiveRemuxPasses.ExistsForRelativePathAsync(uow, row.RelativePath, mediaScope, libraryId).ConfigureAwait(false))
        {
            return false;
        }

        var parameters = UnchangedParameters(row);
        var forgotten = await uow.ExecuteAsync(
            $"DELETE FROM files WHERE {Unchanged} AND NOT {RemuxPassFileState.CarriesCleanedSource}", parameters).ConfigureAwait(false);
        // A row that carries the source Weir last cleaned is what stops that source being cleaned twice, so it is not deleted: it goes
        // back to finished, as history, and no longer waits on anyone.
        var restored = forgotten == 0
            ? await uow.ExecuteAsync($"UPDATE files SET {RemuxPassFileState.RestoreProcessed} WHERE {Unchanged} AND {RemuxPassFileState.CarriesCleanedSource}", parameters).ConfigureAwait(false)
            : 0;
        if (forgotten + restored == 0)
        {
            return false;
        }

        if (record)
        {
            await RecordGoneAsync(uow, libraryId, row, trigger).ConfigureAwait(false);
        }

        return true;
    }

    private static Task<long> RecordGoneAsync(UnitOfWork uow, long libraryId, WaitingRow row, string trigger) =>
        SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
            ActivityEventTypes.ProcessingFileLeftWatchedFolder,
            "processing",
            GoneSourceText.Title(MediaPathNames.Name(row.RelativePath, OperatingSystem.IsWindows())),
            WireJsonWriter.Dumps(
                OperatorMessages.ActivityDetailEnvelope("processing", "scan", trigger, "skipped", userMessage: GoneSourceText.Reason)
                    .Set("relative_media_path", row.RelativePath)
                    .Set("library_id", libraryId)
                    .Set("last_status", row.Status),
                WireJsonFormat.Compact)));
}
