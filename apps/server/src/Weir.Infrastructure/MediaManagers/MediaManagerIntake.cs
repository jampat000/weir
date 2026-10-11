using Microsoft.Data.Sqlite;
using Weir.Core.Activity;
using Weir.Core.Configuration;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Artwork;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>An intake request refused with an HTTP status and a detail.</summary>
public sealed class IntakeRefusedException : Exception
{
    public IntakeRefusedException()
    {
    }

    public IntakeRefusedException(string message)
        : base(message)
    {
    }

    public IntakeRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public IntakeRefusedException(int statusCode, string detail)
        : base(detail)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; } = 400;
}

/// <summary>
/// Who an intake caller proved itself as. <see cref="ConnectionId"/> is the specific connection whose own secret
/// matched, when one did; it is <see langword="null"/> when the caller proved itself only through the instance-wide
/// secret (or, for a single unambiguous connection with no secret, is left unauthenticated but still attributable).
/// A hand-off route that reveals file paths must accept only the secret of the connection its own row names as
/// owner, never any other connection's secret of the same kind.
/// </summary>
public readonly record struct MediaManagerIntakeIdentity(bool Authenticated, long? ConnectionId);

/// <summary>
/// The intake webhook's work: who may post, which library a hand-off belongs to, which files
/// it means, and the remux jobs and ledger row it leaves behind.
/// </summary>
public sealed partial class MediaManagerIntake
{
    /// <summary>Why a kept target's hand-off report says it was not delivered (#786 review of #785).</summary>
    private const string KeptReason = "A person chose to keep this file without processing it again.";

    private readonly WeirOptions _options;
    private readonly MediaManagerConnectionService _connections;
    private readonly MediaManagerConnectionStore _connectionStore;
    private readonly HandoffLedgerStore _ledger;
    private readonly HandoffTargetStore _targets;
    private readonly ProcessingJobStore _jobs;
    private readonly FileSkipMarkerStore _skipMarkers;
    private readonly HandoffCompletionReporter _reporter;
    private readonly ArtworkSubjects _artwork;
    private readonly TimeProvider _time;
    private readonly ConnectionActivityHub? _activity;

    public MediaManagerIntake(
        WeirOptions options,
        MediaManagerConnectionService connections,
        MediaManagerConnectionStore connectionStore,
        HandoffLedgerStore ledger,
        HandoffTargetStore targets,
        ProcessingJobStore jobs,
        FileSkipMarkerStore skipMarkers,
        HandoffCompletionReporter reporter,
        ArtworkSubjects artwork,
        TimeProvider time,
        ConnectionActivityHub? activity = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _connectionStore = connectionStore ?? throw new ArgumentNullException(nameof(connectionStore));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _skipMarkers = skipMarkers ?? throw new ArgumentNullException(nameof(skipMarkers));
        _artwork = artwork ?? throw new ArgumentNullException(nameof(artwork));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _activity = activity;
    }

    /// <summary>For tests: where fresh dedupe ids come from when a hand-off names none.</summary>
    public Func<Guid> NewGuid { get; init; } = Guid.NewGuid;

    public HandoffLedgerStore Ledger => _ledger;

    public ProcessingJobStore Jobs => _jobs;

    /// <summary>
    /// Authorise a webhook: this source's own connection secret when it has one, else the instance-wide secret, else no
    /// check. The presented secret is matched against every enabled connection of the kind (#544 item 6), so a second
    /// connection of the same kind (a 4K Radarr next to a 1080p one, each with its own secret) can authenticate too, and
    /// the event is attributed to whichever one matches. The instance-wide secret is the fallback only when none of them
    /// has a secret configured; once any connection of this kind uses its own secret, its callers must present it.
    /// </summary>
    /// <remarks>
    /// An unchecked "imported" is still recorded, but it never removes a file (#652). The connection-less native
    /// source has no address of its own to prove who is calling, so it refuses a write once nobody has ever
    /// configured a secret for it, rather than accepting one unchecked as a real connection does. A kind with no
    /// connection at all is refused the same way: an existing connection that never rotated its secret keeps
    /// accepting unsigned webhooks (upgrades must not break), but there is nothing to attribute a webhook to
    /// when the kind was never set up in the first place.
    /// </remarks>
    public async Task<MediaManagerIntakeIdentity> AuthoriseAsync(UnitOfWork uow, string sourceKey, string? presented) =>
        Heard(await IdentifyWebhookCallerAsync(uow, sourceKey, presented).ConfigureAwait(false));

    private async Task<MediaManagerIntakeIdentity> IdentifyWebhookCallerAsync(UnitOfWork uow, string sourceKey, string? presented)
    {
        var connections = await _connectionStore.ListEnabledForKindAsync(uow, sourceKey).ConfigureAwait(false);
        var withSecret = connections.Where(connection => !string.IsNullOrEmpty(connection.WebhookSecretCiphertext)).ToList();
        if (withSecret.Count > 0)
        {
            var matched = withSecret.FirstOrDefault(connection => _connections.WebhookSecretMatches(connection, presented));
            if (matched is null)
            {
                throw new IntakeRefusedException(401, IntakeRules.MissingSecretDetail);
            }

            return new MediaManagerIntakeIdentity(Authenticated: true, matched.Id);
        }

        // Attributable even when unchecked: exactly one connection of the kind is the only one this event could be
        // for, whether or not it has bothered to rotate its own secret.
        var soleConnectionId = connections.Count == 1 ? connections[0].Id : (long?)null;
        var configured = _options.MediaManagerWebhookSecret;
        if (string.IsNullOrEmpty(configured))
        {
            if (sourceKey == MediaManagerKinds.Native)
            {
                throw new IntakeRefusedException(401, IntakeRules.NativeNeedsSecretDetail);
            }

            if (connections.Count == 0)
            {
                throw new IntakeRefusedException(401, IntakeRules.NoConnectionDetail(sourceKey));
            }

            return new MediaManagerIntakeIdentity(Authenticated: false, soleConnectionId);
        }

        var provided = WireStrings.Strip(presented ?? string.Empty);
        if (provided.Length == 0 || !MediaManagerConnectionService.CompareDigest(provided, configured))
        {
            throw new IntakeRefusedException(401, IntakeRules.MissingSecretDetail);
        }

        return new MediaManagerIntakeIdentity(Authenticated: true, soleConnectionId);
    }

    /// <summary>
    /// Require a secret: the hand-off routes reveal file paths, so unlike the webhook they never run unauthenticated.
    /// Returns who the secret proved, exactly as <see cref="AuthoriseAsync"/> does, so a caller with a specific
    /// hand-off in hand can refuse a secret that proves the wrong connection.
    /// </summary>
    public async Task<MediaManagerIntakeIdentity> RequireSecretAsync(UnitOfWork uow, string? presented, string? sourceKey) =>
        Heard(await IdentifySecretHolderAsync(uow, presented, sourceKey).ConfigureAwait(false));

    private async Task<MediaManagerIntakeIdentity> IdentifySecretHolderAsync(UnitOfWork uow, string? presented, string? sourceKey)
    {
        var provided = WireStrings.Strip(presented ?? string.Empty);
        var rows = await _connectionStore.ListEnabledWithWebhookSecretAsync(uow).ConfigureAwait(false);
        if (sourceKey is not null)
        {
            rows = [.. rows.Where(row => row.Kind == sourceKey)];
        }

        var configured = _options.MediaManagerWebhookSecret;
        if (rows.Count == 0 && string.IsNullOrEmpty(configured))
        {
            throw new IntakeRefusedException(403, IntakeRules.NeedsSecretDetail);
        }

        if (provided.Length > 0)
        {
            if (rows.FirstOrDefault(row => _connections.WebhookSecretMatches(row, provided)) is { } matched)
            {
                return new MediaManagerIntakeIdentity(Authenticated: true, matched.Id);
            }

            if (!string.IsNullOrEmpty(configured) && MediaManagerConnectionService.CompareDigest(provided, configured))
            {
                return new MediaManagerIntakeIdentity(Authenticated: true, ConnectionId: null);
            }
        }

        throw new IntakeRefusedException(401, IntakeRules.MissingSecretDetail);
    }

    /// <summary>A caller that is attributed to a connection has just called Weir, so that connection lights up; one that cannot be attributed lights nothing.</summary>
    private MediaManagerIntakeIdentity Heard(MediaManagerIntakeIdentity identity)
    {
        if (identity.ConnectionId is { } id)
        {
            _activity?.Publish(new ConnectionRef(ConnectionKind.MediaManager, id), ConnectionPhase.Answered, ConnectionDirection.Inbound, milliseconds: null);
        }

        return identity;
    }

    /// <summary>Every library's id, media type and watched folder, in display order.</summary>
    public static Task<List<IntakeLibrary>> ListLibrariesAsync(UnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        return uow.QueryAsync(
            "SELECT id, media_type, watched_folder FROM libraries ORDER BY display_order, id",
            reader => new IntakeLibrary(SqliteValues.GetInt64(reader, 0), SqliteValues.GetString(reader, 1), SqliteValues.GetString(reader, 2)));
    }

    /// <summary>
    /// The library a hand-off belongs to, chosen by folder. A file that no workflow's folder contains is refused
    /// with a list of the workflows; a hand-off that names no file is explained against the scope's seeded library.
    /// </summary>
    public static async Task<(IntakeLibrary? Library, HandoffPathResult Resolved)> LibraryForHandoffAsync(UnitOfWork uow, MediaManagerImportEvent importEvent)
    {
        ArgumentNullException.ThrowIfNull(importEvent);
        var libraries = await ListLibrariesAsync(uow).ConfigureAwait(false);
        if (IntakeRules.ChooseLibrary(libraries, importEvent) is { } chosen)
        {
            return (chosen.Library, chosen.Resolved);
        }

        if (WireStrings.Strip(importEvent.FilePath).Length > 0)
        {
            var workflows = await uow.QueryAsync(
                "SELECT name, media_type, watched_folder FROM libraries ORDER BY display_order, id",
                reader => new WorkflowFolder(SqliteValues.GetString(reader, 0), SqliteValues.GetString(reader, 1), SqliteValues.GetString(reader, 2))).ConfigureAwait(false);
            return (null, new HandoffPathResult(null, NoWorkflowWatches.Detail(workflows, importEvent.FilePath)));
        }

        var scope = ProcessingMediaScopes.Normalize(importEvent.MediaScope);
        var fallback = libraries.FirstOrDefault(library => library.MediaType == scope);
        return (fallback, HandoffPaths.RelativeMediaPathForHandoff(fallback?.WatchedFolder ?? string.Empty, importEvent.FilePath));
    }

    /// <summary>
    /// The media files a hand-off means: a file names itself; a folder means the videos inside it with samples left out.
    /// </summary>
    public static List<string> HandoffMediaFiles(IntakeLibrary? library, string relativePath)
    {
        if (library is null || relativePath.Length == 0)
        {
            return [relativePath];
        }

        var folder = Path.Join(library.WatchedFolder, relativePath);
        List<IReadOnlyList<string>> videos;
        try
        {
            if (!Directory.Exists(folder))
            {
                return [relativePath];
            }

            videos = [];
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                if (IntakeRules.IsMediaCandidateName(Path.GetFileName(file)))
                {
                    videos.Add(Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [relativePath];
        }

        return ChosenVideos(relativePath, videos, IntakeRules.NoVideoInFolderDetail(relativePath));
    }

    /// <summary>
    /// Which of a folder's videos a hand-off means (samples left out, unless every video is one), as paths in the library.
    /// <paramref name="videos"/> are parts relative to the folder; <paramref name="noVideoDetail"/> is the refusal when none is left.
    /// </summary>
    private static List<string> ChosenVideos(string relativePath, List<IReadOnlyList<string>> videos, string noVideoDetail)
    {
        var chosen = IntakeRules.ChooseFolderVideos(videos, OperatingSystem.IsWindows());
        if (chosen.Count == 0)
        {
            throw new IntakeRefusedException(400, noVideoDetail);
        }

        var prefix = relativePath.Replace('\\', '/').TrimEnd('/');
        return [.. chosen.Select(parts => string.Join('/', new[] { prefix }.Concat(parts).Where(part => part.Length > 0 && part != ".")))];
    }

    /// <summary>
    /// Take in a hand-off: its remux jobs, its ledger row, and (#531) the fingerprint of each file.
    /// <paramref name="ownerConnectionId"/> is who <see cref="AuthoriseAsync"/> attributed the event to, recorded on
    /// the ledger row so a later hand-off route can require that connection's own secret, not any same-kind one.
    /// </summary>
    public async Task<string> EnqueueRefineAsync(UnitOfWork uow, MediaManagerImportEvent importEvent, long? ownerConnectionId = null)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(importEvent);
        if (!string.IsNullOrEmpty(importEvent.CallbackPath) && !IntakeRules.IsValidCallbackPath(importEvent.CallbackPath))
        {
            throw new IntakeRefusedException(422, IntakeRules.InvalidCallbackPathDetail(importEvent.CallbackPath));
        }

        var (library, resolved) = await LibraryForHandoffAsync(uow, importEvent).ConfigureAwait(false);
        if (!resolved.Ok)
        {
            throw new IntakeRefusedException(400, resolved.Problem!);
        }

        var relativePath = resolved.RelativeMediaPath!;
        var targets = importEvent.SourceFiles is { Count: > 0 } && library is not null
            ? HandoffListedFiles(library, relativePath, importEvent)
            : HandoffMediaFiles(library, relativePath);
        var baseKey = IntakeRules.BaseDedupeKey(importEvent, NewGuid);
        var covered = new List<string>(targets.Count);
        // #786 review of #785: a target someone chose to keep must not be reprocessed just because a manager
        // handed it over again — the marker is scan-only otherwise, and this is the same hand-off path a folder
        // detection or a resend both use. Read once per hand-off, not per target.
        var keptMarkers = library is not null
            ? await _skipMarkers.ForLibraryAsync(uow, library.Id).ConfigureAwait(false)
            : new Dictionary<string, FileSkipMarker>(StringComparer.Ordinal);
        var kept = new List<string>();
        var repeats = new List<(string Target, CleanedEarlier Earlier)>();
        var superseded = new List<(string PayloadJson, string Target)>();
        foreach (var target in targets)
        {
            if (library is not null && IsKept(keptMarkers, library, target))
            {
                // Tracked as one of this hand-off's own targets below (covered), exactly like any other file, so a
                // folder hand-off with a mix of kept and new files still waits for all of them before it reports.
                kept.Add(target);
                covered.Add(target);
                continue;
            }

            // The same source again (a resend, a replay) is never cleaned twice. It is covered like any other target, so
            // it counts toward the hand-off's report, and is settled below once the hand-off is on the ledger.
            if (library is not null && await CleanedSources.FindAsync(uow, library.Id, library.WatchedFolder, target).ConfigureAwait(false) is { } cleaned)
            {
                repeats.Add((target, cleaned));
                covered.Add(target);
                continue;
            }

            var dedupeKey = IntakeRules.DedupeKeyFor(baseKey, targets, target, relativePath);
            var payload = IntakeRules.Payload(importEvent, library, relativePath, target);
            var connection = uow.Connection;
            var transaction = await uow.WriteTransactionAsync().ConfigureAwait(false);

            // Folder detection may already have queued (or started) this very file under its own random key. One file
            // gets one pass: the hand-off takes over that pass rather than adding a second one. A resend of this same
            // hand-off still lands on its own row through EnqueueOrGet below.
            var active = library is null
                ? null
                : ActiveRemuxPasses.ForRelativePath(connection, transaction, target, library.MediaType, library.Id) ?? GoneLooks.WaitingPassThrough(connection, transaction, library.Id, target);
            if (library is not null && active is not null && GoneSources.IsBack(library.WatchedFolder, target) && GoneLooks.StartIfWaiting(connection, transaction, active))
            {
                // A file that was gone and is here again: the pass waiting out the grace has nothing left to wait for.
                _jobs.AnnounceQueueChange(transaction, active.JobKind);
            }

            if (library is not null &&
                active is not null &&
                ProcessingJobStore.GetByDedupeKey(connection, transaction, dedupeKey) is null)
            {
                if (AdoptActivePass(connection, transaction, active, importEvent, dedupeKey, payload, target, superseded))
                {
                    covered.Add(target);
                }

                continue;
            }

            await _jobs.EnqueueOrGetAsync(uow, dedupeKey, IntakeRules.RemuxPassJobKind, IntakeRules.PayloadJson(payload), JobQueueRules.DefaultMaxAttempts, runnerCost: null, priority: 0).ConfigureAwait(false);
            covered.Add(target);
        }

        if (!string.IsNullOrEmpty(importEvent.HandoffId))
        {
            var rowId = await _ledger.RecordReceivedAsync(
                uow, importEvent.SourceKey, importEvent.HandoffId, library?.Id, relativePath, ownerConnectionId, importEvent.DownloadId).ConfigureAwait(false);
            await _targets.AddAsync(uow, rowId, covered).ConfigureAwait(false);

            // #786 review of #785: answered the same way HistoryFileRemovalService's "keep" answers a live hand-off
            // — a failed/held completion report, through the exact owed-report/ledger machinery a real pass's
            // outcome uses (ReportHandoffCompletionAsync), so a restart between here and delivery still finds it,
            // and the hand-off ends in the ledger instead of waiting forever on a file nobody is going to process.
            foreach (var target in kept)
            {
                var keptPayload = IntakeRules.Payload(importEvent, library, relativePath, target);
                var keptResult = new WireObject().Set("ok", false).Set("outcome", "failed").Set("relative_media_path", target).Set("reason", KeptReason);
                await _reporter.ReportHandoffCompletionAsync(uow, IntakeRules.PayloadJson(keptPayload), keptResult).ConfigureAwait(false);
            }

            // The sends this one replaced are closed now, so the manager is told the answer is coming from this one.
            foreach (var (replacedPayload, target) in superseded)
            {
                await _reporter.ReportHandoffCompletionAsync(uow, replacedPayload, CompletionReports.SupersededResult(target)).ConfigureAwait(false);
            }
        }
        else
        {
            // No hand-off to answer through (a plain webhook import names no id): at least Activity says why
            // nothing was queued, rather than the file silently vanishing.
            foreach (var target in kept)
            {
                await RecordKeptHandoffAsync(uow, importEvent.SourceKey, target).ConfigureAwait(false);
            }
        }

        if (library is not null)
        {
            await SettleRepeatsAsync(uow, importEvent, library, relativePath, repeats).ConfigureAwait(false);
            await _artwork.LinkHandoffAsync(uow, library.Id, library.MediaType, targets, importEvent.ReleaseName, importEvent.Artwork).ConfigureAwait(false);
            foreach (var target in targets)
            {
                await RecordFingerprintAsync(uow, library, target).ConfigureAwait(false);
            }
        }

        return IntakeRules.RemuxPassJobKind;
    }

    /// <summary>
    /// A hand-off for a file that already has a pending or leased pass (queued by folder detection, an automatic retry
    /// or a user) makes that pass its own instead of queuing a second one: the job is re-keyed to the hand-off's dedupe
    /// key, so <c>GET /api/v1/intake/handoffs/{kind}/{id}</c> finds it (queue position, working), and it takes the
    /// hand-off's origin, so the outcome is called back to the manager. A pass that is already running picks the origin
    /// up when it finishes (<see cref="Processing.RemuxPass.RemuxPassHandler"/>). A pass that already belongs to
    /// another send of the same manager (the release sent again) is taken over by this one: the replaced send is added to
    /// <paramref name="superseded"/> to be told so, and this hand-off is the one the pass reports to. A pass that belongs to a
    /// hand-off of a different manager is left with it: this hand-off is still recorded and answers from the file's own state,
    /// and the file is not one this hand-off waits for before it reports. True when the hand-off took the pass over.
    /// </summary>
    private bool AdoptActivePass(
        SqliteConnection connection, SqliteTransaction transaction, ProcessingJob active, MediaManagerImportEvent importEvent, string dedupeKey, WireObject handoffPayload,
        string target, List<(string PayloadJson, string Target)> superseded)
    {
        WireObject existing;
        try
        {
            existing = WireJsonParser.Parse(string.IsNullOrEmpty(active.PayloadJson) ? "{}" : active.PayloadJson) as WireObject ?? new WireObject();
        }
        catch (WireJsonDecodeException)
        {
            existing = new WireObject();
        }

        if (HandoffOrigin.FromPayload(existing) is { HandoffId: { } ownerId } owner &&
            (owner.SourceKey != importEvent.SourceKey || ownerId != importEvent.HandoffId))
        {
            if (owner.SourceKey != importEvent.SourceKey || string.IsNullOrEmpty(importEvent.HandoffId))
            {
                return false;
            }

            superseded.Add((IntakeRules.PayloadJson(existing), target));
        }

        if (handoffPayload.Get("origin") is WireObject origin)
        {
            existing.Set("origin", origin);
        }

        existing.Set("trigger", "webhook");
        var newKey = string.IsNullOrEmpty(importEvent.HandoffId) || active.JobKind != IntakeRules.RemuxPassJobKind ? active.DedupeKey : dedupeKey;
        ProcessingJobStore.Execute(
            connection,
            transaction,
            "UPDATE jobs SET dedupe_key = @dedupe, payload_json = @payload, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
            ("@dedupe", newKey),
            ("@payload", IntakeRules.PayloadJson(existing)),
            ("@id", active.Id));
        _jobs.AnnounceQueueChange(transaction, active.JobKind);
        return true;
    }

    /// <summary>Whether <paramref name="relativePath"/>'s current bytes still match a "keep" marker recorded for it (#786 review of #785).</summary>
    private static bool IsKept(Dictionary<string, FileSkipMarker> markers, IntakeLibrary library, string relativePath)
    {
        if (!markers.TryGetValue(relativePath, out var marker))
        {
            return false;
        }

        try
        {
            var fingerprint = SourceFiles.Fingerprint(Path.Join(library.WatchedFolder, relativePath));
            return fingerprint.SizeBytes == marker.SizeBytes && fingerprint.ModifiedTimeNs == marker.MtimeNs;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// One Activity line for a hand-off target Weir did not queue because it is kept (#786 review of #785): the
    /// manager is not told anything back here — its own hand-off protocol has no answer for "already decided
    /// against, by hand, before you sent this" — but the file is never reprocessed just because it arrived again.
    /// </summary>
    private static Task<long> RecordKeptHandoffAsync(UnitOfWork uow, string sourceKey, string relativePath)
    {
        var manager = MediaManagerKinds.LabelForConnection(sourceKey, null);
        var fileName = MediaPathNames.Name(relativePath, OperatingSystem.IsWindows());
        var detail = new WireObject()
            .Set("relative_media_path", relativePath)
            .Set("manager", manager)
            .Set("trigger", "webhook")
            .Set("result", "skipped");
        return SqliteActivityWriter.RecordAsync(uow, new ActivityEventDraft(
            ActivityEventTypes.ProcessingFileRemovalKept,
            "processing",
            $"{manager} handed {fileName} to Weir again, but it is kept, so Weir left it alone",
            WireStrings.Slice(WireJsonWriter.Dumps(detail, WireJsonFormat.Compact), 10_000)));
    }

    /// <summary>
    /// Record a handed-over file's size when it arrives (#531). Otherwise the next watched-folder scan would read the zero
    /// a failure left on the row as a change and reset the failure count, so the retry limit and the hold after repeated
    /// failures would never apply. A file that is missing, or a row that already has a size, is left alone; a later scan
    /// still sees a genuinely changed file as changed.
    /// </summary>
    private async Task RecordFingerprintAsync(UnitOfWork uow, IntakeLibrary library, string relativePath)
    {
        long size;
        try
        {
            var info = new FileInfo(Path.Join(library.WatchedFolder, relativePath));
            if (!info.Exists)
            {
                return;
            }

            size = info.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return;
        }

        // No status reason: the queued job speaks for the file until a pass or scan records one, and a reason here
        // would change what the hand-off status tells the manager while the file waits.
        await uow.ExecuteAsync(
            "INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, last_seen_at) " +
            "VALUES ($library, $path, 'unprocessed', '', $size, $now) " +
            "ON CONFLICT (library_id, relative_path) DO UPDATE SET size_bytes = excluded.size_bytes, updated_at = CURRENT_TIMESTAMP " +
            "WHERE files.size_bytes = 0 AND excluded.size_bytes <> 0",
            ("$library", library.Id),
            ("$path", relativePath),
            ("$size", size),
            ("$now", TimestampColumns.Orm(_time.GetUtcNow()))).ConfigureAwait(false);
    }
}
