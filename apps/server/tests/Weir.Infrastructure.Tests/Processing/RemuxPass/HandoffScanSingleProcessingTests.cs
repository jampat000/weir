using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Tests.Jobs;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// A media manager's hand-off and Weir's own folder detection can both find the same file. Whichever gets there first,
/// the file must be processed exactly once, and the hand-off must still report on — and be called back about — the one
/// job that processes it. Real database, real folders, the real intake, scan dispatch, remux-pass handler and worker
/// loop; only ffprobe/ffmpeg and the manager's HTTP are fakes.
/// </summary>
public sealed class HandoffScanSingleProcessingTests : IDisposable
{
    private const string EventsPath = "/api/integrations/processors/events";
    private const string Relative = "Film/film.mkv";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();
    private readonly FakeMediaRunner _media = new();
    private long _libraryId;

    public HandoffScanSingleProcessingTests()
    {
        _fixture.Store.Clock.Set(DateTimeOffset.UtcNow);
        _fixture.Store.Execute(
            "UPDATE operator_settings SET minimum_free_disk_space_mb = 0")
            .GetAwaiter().GetResult();
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
    }

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private async Task SetUpAsync()
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        // ready_after_seconds = 0: the scan does not wait for a second look before calling the file ready, so a single scan
        // is enough to reach the enqueue decision under test.
        _libraryId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, max_attempts, " +
            "rejected_file_action, retry_backoff_seconds, ready_after_seconds, min_file_size_mb, display_order) " +
            "VALUES ('Movies', 'movie', $w, $o, $k, 'pass_through', 3, 'leave', 60, 0, 0, 1) RETURNING id",
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work))), CultureInfo.InvariantCulture);
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _folders.Source(Path.Join("Film", "film.mkv"));
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.DefaultProbe = FakeMediaRunner.EnglishOnly;
    }

    private RemuxPassHandler RemuxHandler()
    {
        var data = new SqliteRemuxPassData(_fixture.Store.Database, _fixture.Connections, NullLogger<SqliteRemuxPassData>.Instance);
        var runner = new RemuxPassRunner(
            new MediaTools(_media, new FixedResolver(), new ListLogger<MediaTools>(), TimeProvider.System),
            new FixedResolver(),
            data,
            data,
            new SkippedTvSeasonFolderCleanup(),
            new FakeOriginalLanguage(),
            new RemuxPassSettings(),
            TimeProvider.System,
            NullLogger<RemuxPassRunner>.Instance);
        return new RemuxPassHandler(
            _fixture.Store.Database,
            _fixture.Store.Options,
            runner,
            new QueueingFailurePolicy(_fixture.Jobs),
            _fixture.OperatorSettings,
            _fixture.Handback,
            _fixture.Libraries,
            TimeProvider.System,
            NullLogger<RemuxPassHandler>.Instance,
            new DownloadedScanNotifier(_fixture.Connections, _fixture.ConnectionStore, _fixture.Libraries, _fixture.Http, NullLogger<DownloadedScanNotifier>.Instance),
            _fixture.Reporter);
    }

    /// <summary>The worker loop over the remux-pass kind only, with <paramref name="whileLeased"/> run once, mid-pass.</summary>
    private ProcessingJobProcessor Worker(Func<Task>? whileLeased = null)
    {
        var inner = RemuxHandler();
        var fired = false;
        var handler = new DelegateHandler(RemuxPassOutcomes.JobKind, async context =>
        {
            if (whileLeased is not null && !fired)
            {
                fired = true;
                await whileLeased();
            }

            await inner.HandleAsync(context, CancellationToken.None);
        });
        var registry = new JobHandlerRegistry([handler]);
        return new ProcessingJobProcessor(
            _fixture.Jobs,
            registry,
            new SqliteActivityWriter(_fixture.Store.Database),
            new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            _fixture.Store.Clock,
            NullLogger<ProcessingJobProcessor>.Instance);
    }

    private static async Task DrainAsync(ProcessingJobProcessor worker)
    {
        for (var i = 0; i < 20; i++)
        {
            if (await worker.ProcessOneAsync("test-worker") == JobProcessOutcome.Idle)
            {
                return;
            }
        }

        Assert.Fail("The worker never went idle.");
    }

    private async Task ScanAsync()
    {
        var handler = new ProcessingWatchedFolderScanDispatchJobHandler(
            _fixture.Store.Database, _fixture.Store.Clock, _fixture.Store.Options, _fixture.Jobs, _fixture.Connections,
            new SuiteSettingsStore(new AuthStore()), _fixture.Libraries, _fixture.Files, new FileSkipMarkerStore());
        var payload = new WireObject()
            .Set("enqueue_remux_jobs", true)
            .Set("scan_trigger", "watcher")
            .Set("media_scope", "movie")
            .Set("library_id", _libraryId);
        var job = await _fixture.Jobs.EnqueueOrGetAsync(
            $"scan-test-{Guid.NewGuid():N}",
            ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch,
            WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        await handler.HandleAsync(new JobWorkContext(job.Id, job.JobKind, job.PayloadJson, "scan-owner"), CancellationToken.None);
        // The scan ran here rather than through a worker, so finish its own row the way a worker would.
        await _fixture.Store.Execute($"UPDATE jobs SET status = 'completed' WHERE id = {job.Id}");
    }

    private MediaManagerImportEvent Handoff(string handoffId) => new()
    {
        SourceKey = "deluno",
        EventKind = "handoff",
        MediaScope = "movie",
        FilePath = Path.Join(_folders.Watched, "Film", "film.mkv"),
        HandoffId = handoffId,
        CallbackPath = EventsPath,
        ReleaseName = "Film.2001",
        LibraryId = "lib-1",
    };

    private Task<string> HandOffAsync(string handoffId = "h1") => _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, Handoff(handoffId)));

    private async Task<HandoffStatus> HandoffStatusAsync(string handoffId = "h1")
    {
        var row = (await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", handoffId)))!;
        return await _fixture.Db(uow => _fixture.Ledger.CurrentStatusAsync(uow, row));
    }

    private Task<long> RemuxJobCountAsync() =>
        _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{RemuxPassOutcomes.JobKind}'");

    private Task<long> ActiveRemuxJobCountAsync() =>
        _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{RemuxPassOutcomes.JobKind}' AND status IN ('pending', 'leased')");

    private async Task<string> ScalarText(string sql)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>One job ran, ffmpeg remuxed once, one completion was recorded, and the manager heard back exactly once.</summary>
    private async Task AssertProcessedExactlyOnceAndReportedAsync()
    {
        Assert.Equal(1, await RemuxJobCountAsync());
        Assert.Equal("completed", await ScalarText($"SELECT status FROM jobs WHERE job_kind = '{RemuxPassOutcomes.JobKind}'"));
        Assert.Single(_media.Remuxes);
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_remux_pass_completed'"));
        Assert.Equal("processed", await ScalarText($"SELECT status FROM files WHERE relative_path = '{Relative}'"));

        var post = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        var body = (WireObject)post.Json!;
        Assert.Equal(("h1", "completed"), (WireConvert.Str(body["handoffId"]), WireConvert.Str(body["status"])));
        Assert.Equal("completed", (await HandoffStatusAsync()).State);
    }

    [Fact]
    public async Task A_scan_after_a_hand_off_does_not_queue_the_file_again()
    {
        await SetUpAsync();
        await HandOffAsync();
        await ScanAsync();

        Assert.Equal(1, await RemuxJobCountAsync());
        Assert.Equal(IntakeRules.RemuxDedupeKey("deluno", "h1"), await ScalarText("SELECT dedupe_key FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));

        await DrainAsync(Worker());
        await AssertProcessedExactlyOnceAndReportedAsync();
    }

    [Fact]
    public async Task A_scan_while_the_hand_offs_pass_is_running_does_not_queue_the_file_again()
    {
        await SetUpAsync();
        await HandOffAsync();
        var activeDuringScan = -1L;

        await DrainAsync(Worker(async () =>
        {
            Assert.Equal("leased", await ScalarText("SELECT status FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
            await ScanAsync();
            activeDuringScan = await ActiveRemuxJobCountAsync();
        }));

        Assert.Equal(1, activeDuringScan);
        await AssertProcessedExactlyOnceAndReportedAsync();
    }

    [Fact]
    public async Task A_hand_off_after_a_scan_takes_over_the_queued_job_instead_of_adding_one()
    {
        await SetUpAsync();
        await ScanAsync();
        Assert.Equal(1, await RemuxJobCountAsync());

        await HandOffAsync();

        // Still one job, now keyed to the hand-off and carrying its origin, so the status route and the callback both
        // follow the job that will actually process the file.
        Assert.Equal(1, await RemuxJobCountAsync());
        Assert.Equal(IntakeRules.RemuxDedupeKey("deluno", "h1"), await ScalarText("SELECT dedupe_key FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        var payload = (WireObject)WireJsonParser.Parse(await ScalarText("SELECT payload_json FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        Assert.Equal(Relative, WireConvert.Str(payload["relative_media_path"]));
        Assert.Equal("h1", WireConvert.Str(((WireObject)payload["origin"]).Get("handoff_id")!));
        var queued = await HandoffStatusAsync();
        Assert.Equal(("queued", (long?)1), (queued.State, queued.QueuePosition));

        await DrainAsync(Worker());
        await AssertProcessedExactlyOnceAndReportedAsync();
    }

    [Fact]
    public async Task A_hand_off_while_the_scans_pass_is_running_reports_on_that_pass_and_is_called_back()
    {
        await SetUpAsync();
        await ScanAsync();
        HandoffStatus? during = null;

        await DrainAsync(Worker(async () =>
        {
            await HandOffAsync();
            during = await HandoffStatusAsync();
            Assert.Equal(1, await ActiveRemuxJobCountAsync());
        }));

        Assert.Equal("working", during!.State);
        await AssertProcessedExactlyOnceAndReportedAsync();
    }

    [Fact]
    public async Task A_due_automatic_retry_does_not_queue_a_second_pass_beside_a_hand_offs()
    {
        // The scan's automatic-retry branch for a failed file must check for an active pass before it enqueues.
        await SetUpAsync();
        var size = new FileInfo(Path.Join(_folders.Watched, "Film", "film.mkv")).Length;
        var due = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        await _fixture.Store.Execute(
            $"INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, failure_attempts, failure_class, next_retry_at) " +
            $"VALUES ({_libraryId}, '{Relative}', 'processing_failed', 'ffmpeg died.', {size}, 1, 'execution', '{due}')");
        await HandOffAsync();

        await ScanAsync();

        Assert.Equal(1, await RemuxJobCountAsync());
        Assert.Equal(IntakeRules.RemuxDedupeKey("deluno", "h1"), await ScalarText("SELECT dedupe_key FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
    }

    [Fact]
    public async Task Once_the_pass_has_finished_a_new_hand_off_or_a_scan_still_queues_the_file_again()
    {
        // The guard is about work already queued or running, not about history: a file handed over again, or found
        // again by a scan once nothing is queued for it, is still processed again. (Whether a scan skips a file whose
        // output is already complete is a separate, older check that this does not change.)
        await SetUpAsync();
        await HandOffAsync();
        await _fixture.Store.Execute("UPDATE jobs SET status = 'completed'");

        await HandOffAsync("h2");
        Assert.Equal(1, await ActiveRemuxJobCountAsync());
        Assert.Equal(IntakeRules.RemuxDedupeKey("deluno", "h2"), await ScalarText("SELECT dedupe_key FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1' AND status = 'pending'"));

        await _fixture.Store.Execute("UPDATE jobs SET status = 'completed'");
        await ScanAsync();
        Assert.Equal(1, await ActiveRemuxJobCountAsync());
        Assert.StartsWith("processing.file.remux_pass.v1:scan:", await ScalarText("SELECT dedupe_key FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1' AND status = 'pending'"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_hand_off_for_a_file_another_hand_off_already_queued_takes_it_over_and_the_first_is_closed_as_replaced()
    {
        await SetUpAsync();
        await HandOffAsync("h1");
        await HandOffAsync("h2");

        Assert.Equal(1, await RemuxJobCountAsync());
        Assert.Equal(IntakeRules.RemuxDedupeKey("deluno", "h2"), await ScalarText("SELECT dedupe_key FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'"));
        Assert.Equal("queued", (await HandoffStatusAsync("h2")).State);
        var replaced = await HandoffStatusAsync("h1");
        Assert.Equal("cancelled", replaced.State);
        Assert.Equal(CompletionReports.SupersededMessage, replaced.Message);
    }

    [Fact]
    public async Task Resending_the_same_hand_off_stays_one_job()
    {
        await SetUpAsync();
        await HandOffAsync();
        await HandOffAsync();

        Assert.Equal(1, await RemuxJobCountAsync());
    }
}
