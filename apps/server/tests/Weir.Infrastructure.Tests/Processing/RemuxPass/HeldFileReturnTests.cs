using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// A file held as gone that comes back is picked up again, and every hand-off for it gets an answer. The run that found it:
/// Deluno sent a release, the file was not there when Weir's pass started, Deluno sent the release again and the file was back
/// within the minute, and Weir left the file on hold, with both hand-offs unanswered, until the look booked for after the scan's
/// grace. Each case runs for a single file in the watched folder and for a file in a release folder. Real database, folders,
/// intake, handler, sweep and worker loop; only ffprobe/ffmpeg and the manager's HTTP are fakes.
/// </summary>
public sealed class HeldFileReturnTests : IDisposable
{
    private const string EventsPath = "/api/integrations/processors/events";
    private const string ReleaseName = "Busyfilm.2017.1080p.WEB-DL.x264-GOLDEN";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();
    private readonly FakeMediaRunner _media = new();
    private long _libraryId;
    private bool _releaseFolder;

    public HeldFileReturnTests()
    {
        _fixture.Store.Clock.Set(DateTimeOffset.UtcNow);
        _fixture.Store.Execute("UPDATE operator_settings SET minimum_free_disk_space_mb = 0").GetAwaiter().GetResult();
        _fixture.Http.Json(HttpMethod.Post, EventsPath, "{}", HttpStatusCode.Accepted);
    }

    public void Dispose()
    {
        _folders.Dispose();
        _fixture.Dispose();
    }

    private string Video => $"{ReleaseName}.mkv";

    private string Relative => _releaseFolder ? $"{ReleaseName}/{Video}" : Video;

    /// <summary>What is moved away and brought back: the release folder with the video in it, or the video itself.</summary>
    private string Moved => Path.Join(_folders.Watched, _releaseFolder ? ReleaseName : Video);

    private async Task SetUpAsync(bool releaseFolder, bool linkedToDeluno = true)
    {
        _releaseFolder = releaseFolder;
        await _fixture.Store.Execute("DELETE FROM libraries");
        _libraryId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, max_attempts, " +
            "rejected_file_action, retry_backoff_seconds, ready_after_seconds, min_file_size_mb, display_order, remove_original_after_success) " +
            "VALUES ('Movies', 'movie', $w, $o, $k, 'pass_through', 3, 'leave', 60, 0, 0, 1, 0) RETURNING id",
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work))), CultureInfo.InvariantCulture);
        var connection = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        if (linkedToDeluno)
        {
            await _fixture.Store.Execute($"INSERT INTO library_manager_links (library_id, connection_id) VALUES ({_libraryId}, {connection})");
        }

        _folders.Source(Relative);
        _media.Probes[Video] = FakeMediaRunner.EnglishAndJapanese;
        _media.DefaultProbe = FakeMediaRunner.EnglishOnly;
    }

    private ProcessingJobProcessor Worker(TimeSpan? goneSettle = null)
    {
        var data = new SqliteRemuxPassData(_fixture.Store.Database, _fixture.Connections, NullLogger<SqliteRemuxPassData>.Instance);
        var runner = new RemuxPassRunner(
            new MediaTools(_media, new FixedResolver(), new ListLogger<MediaTools>(), _fixture.Store.Clock),
            new FixedResolver(),
            data,
            data,
            new SkippedTvSeasonFolderCleanup(),
            new FakeOriginalLanguage(),
            new RemuxPassSettings(),
            _fixture.Store.Clock,
            NullLogger<RemuxPassRunner>.Instance);
        var handler = new RemuxPassHandler(
            _fixture.Store.Database,
            _fixture.Store.Options,
            runner,
            new QueueingFailurePolicy(_fixture.Jobs),
            _fixture.OperatorSettings,
            _fixture.Handback,
            _fixture.Libraries,
            _fixture.Store.Clock,
            NullLogger<RemuxPassHandler>.Instance,
            new DownloadedScanNotifier(_fixture.Connections, _fixture.ConnectionStore, _fixture.Libraries, _fixture.Http, NullLogger<DownloadedScanNotifier>.Instance),
            _fixture.Reporter,
            _fixture.Jobs)
        {
            GoneSettle = goneSettle ?? TimeSpan.Zero,
        };
        return new ProcessingJobProcessor(
            _fixture.Jobs,
            new JobHandlerRegistry([handler, PassThroughHandler()]),
            new SqliteActivityWriter(_fixture.Store.Database),
            new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            _fixture.Store.Clock,
            NullLogger<ProcessingJobProcessor>.Instance);
    }

    private ProcessingPassThroughHandler PassThroughHandler() =>
        new(_fixture.Store.Database, _fixture.Store.Clock, NullLogger<ProcessingPassThroughHandler>.Instance, _fixture.Handback, _fixture.Libraries, _fixture.Jobs, _fixture.Reporter)
        {
            GoneSettle = TimeSpan.Zero,
        };

    private async Task DrainAsync()
    {
        var worker = Worker();
        for (var i = 0; i < 20; i++)
        {
            if (await worker.ProcessOneAsync("test-worker") == JobProcessOutcome.Idle)
            {
                return;
            }
        }

        Assert.Fail("The worker never went idle.");
    }

    private Task SweepAsync() =>
        new VanishedFileSweepTask(_fixture.Store.Database, _fixture.Store.Options, _fixture.Libraries, _fixture.Store.Clock, NullLogger<VanishedFileSweepTask>.Instance)
        {
            GoneLookAgain = _ => Task.CompletedTask,
        }.RunOnceAsync(CancellationToken.None);

    private void Advance(TimeSpan by) => _fixture.Store.Clock.Set(_fixture.Store.Clock.GetUtcNow() + by);

    private void TakeAway()
    {
        if (_releaseFolder)
        {
            Directory.Move(Moved, Moved + ".away");
        }
        else
        {
            File.Move(Moved, Moved + ".away");
        }
    }

    private void BringBack()
    {
        if (_releaseFolder)
        {
            Directory.Move(Moved + ".away", Moved);
        }
        else
        {
            File.Move(Moved + ".away", Moved);
        }
    }

    private async Task HandOffAsync(string handoffId) =>
        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, new MediaManagerImportEvent
        {
            SourceKey = "deluno",
            EventKind = "handoff",
            MediaScope = "movie",
            FilePath = Moved,
            HandoffId = handoffId,
            CallbackPath = EventsPath,
            ReleaseName = ReleaseName,
            LibraryId = "lib-1",
        }));

    /// <summary>The pass runs while the file is not there, as Busyfilm's did: the file is held as gone and nothing is told to the manager.</summary>
    private async Task RunPassWhileGoneAsync()
    {
        TakeAway();
        await DrainAsync();
        Assert.Equal("on_hold", await StatusAsync());
        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync());
        Assert.Empty(Reports());
    }

    private Task<string> StatusAsync() => ScalarTextAsync($"SELECT status FROM files WHERE relative_path = '{Relative}'");

    private Task<string> ReasonAsync() => ScalarTextAsync($"SELECT status_reason FROM files WHERE relative_path = '{Relative}'");

    private async Task<string> ScalarTextAsync(string sql)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private IReadOnlyList<WireObject> Reports() =>
        [.. _fixture.Http.RequestsTo(HttpMethod.Post, EventsPath).Select(request => (WireObject)request.Json!)];

    private IReadOnlyList<WireObject> ReportsFor(string handoffId) =>
        [.. Reports().Where(report => WireConvert.Str(report["handoffId"]) == handoffId)];

    private async Task<HandoffStatus> HandoffStatusAsync(string handoffId) =>
        await _fixture.Db(async uow => await _fixture.Ledger.CurrentStatusAsync(uow, (await HandoffLedgerStore.FindAsync(uow, "deluno", handoffId))!));

    /// <summary>The file was cleaned once, from the one file, and the hand-off was told it was completed with the copy.</summary>
    private async Task AssertCleanedAndToldAsync(string handoffId)
    {
        Assert.Single(_media.Remuxes);
        Assert.Equal("processed", await StatusAsync());
        var report = Assert.Single(ReportsFor(handoffId), candidate => WireConvert.Str(candidate["status"]) == "completed");
        Assert.False(string.IsNullOrEmpty(WireConvert.Str(report["outputPath"])));
        Assert.DoesNotContain(Reports(), candidate => WireConvert.Str(candidate["status"]) == "failed" && !IsSupersededReport(candidate));
        Assert.True(File.Exists(Path.Join(_folders.Watched, Relative)), "the original is never touched");
    }

    private static bool IsSupersededReport(WireObject report) => report.Get("failureClass") is WireString { Value: "superseded" };

    /// <summary>The send the release was sent again for gets its own last answer: not a completion, no output, nothing held or rejected.</summary>
    private void AssertSuperseded(string handoffId)
    {
        var report = Assert.Single(ReportsFor(handoffId));
        Assert.Equal("failed", WireConvert.Str(report["status"]));
        Assert.Equal("superseded", WireConvert.Str(report["failureClass"]));
        Assert.False(((WireBool)report["sourceRemoved"]).Value);
        Assert.False(report.ContainsKey("disposition"));
        Assert.False(report.ContainsKey("outputPath"));
        Assert.False(string.IsNullOrEmpty(WireConvert.Str(report["message"])));
    }

    // --- the file is back during the grace ----------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_held_as_gone_that_is_back_when_the_release_is_sent_again_is_processed_and_both_sends_are_answered(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        await RunPassWhileGoneAsync();

        // Busyfilm: the file was back about 14 s after the look found it gone, and the resend arrived 17 s after that.
        Advance(TimeSpan.FromSeconds(14));
        BringBack();
        Advance(TimeSpan.FromSeconds(17));
        await HandOffAsync("second");
        await DrainAsync();

        await AssertCleanedAndToldAsync("second");
        AssertSuperseded("first");
        Assert.Equal("cancelled", (await HandoffStatusAsync("first")).State);
        var status = await HandoffStatusAsync("second");
        Assert.Equal("completed", status.State);
        Assert.Equal(WireConvert.Str(Assert.Single(ReportsFor("second"))["outputPath"]), status.OutputPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_held_as_gone_that_comes_back_on_its_own_is_processed_when_the_sweep_sees_it(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        await RunPassWhileGoneAsync();

        Advance(TimeSpan.FromMinutes(2));
        BringBack();
        await SweepAsync();
        await DrainAsync();

        await AssertCleanedAndToldAsync("first");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_held_as_gone_that_is_still_away_when_the_sweep_looks_is_left_waiting_for_the_booked_look(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        await RunPassWhileGoneAsync();

        Advance(TimeSpan.FromMinutes(2));
        await SweepAsync();
        await DrainAsync();

        Assert.Equal(GoneSourceText.HeldReason, await ReasonAsync());
        Assert.Empty(Reports());
        Assert.Empty(_media.Remuxes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_resend_that_takes_over_a_held_pass_nobody_owned_starts_it_at_once_and_is_answered(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({_libraryId}, '{Relative}', 'unprocessed', '')");
        var payload = new WireObject().Set("relative_media_path", Relative).Set("library_id", _libraryId).Set("media_scope", "movie").Set("trigger", "manual");
        await _fixture.Jobs.EnqueueOrGetAsync("scan-queued", RemuxPassOutcomes.JobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        await RunPassWhileGoneAsync();

        Advance(TimeSpan.FromSeconds(30));
        BringBack();
        await HandOffAsync("second");
        await DrainAsync();

        await AssertCleanedAndToldAsync("second");
    }

    // --- the file is back before the pass has settled ------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_that_is_back_before_the_pass_looks_again_is_processed_by_that_pass(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        TakeAway();

        var working = Worker(TimeSpan.FromSeconds(1.5)).ProcessOneAsync("test-worker");
        await Task.Delay(300);
        BringBack();
        await working;

        await AssertCleanedAndToldAsync("first");
        Assert.Equal(0, await _fixture.Store.Scalar("SELECT count(*) FROM activity_events WHERE event_type = 'processing.file_left_watched_folder'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_resend_after_a_file_came_back_before_the_pass_looked_again_is_answered_as_already_done(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        TakeAway();
        var working = Worker(TimeSpan.FromSeconds(1.5)).ProcessOneAsync("test-worker");
        await Task.Delay(300);
        BringBack();
        await working;

        await HandOffAsync("second");
        await DrainAsync();

        Assert.Single(_media.Remuxes);
        var report = Assert.Single(ReportsFor("second"));
        Assert.Equal("completed", WireConvert.Str(report["status"]));
        Assert.Equal(WireConvert.Str(ReportsFor("first")[0]["outputPath"]), WireConvert.Str(report["outputPath"]));
    }

    // --- the file is back after Weir forgot it -------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_that_comes_back_after_it_was_forgotten_is_processed_for_the_new_hand_off(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        await RunPassWhileGoneAsync();
        Advance(GoneSources.LookAgainAfter + TimeSpan.FromMinutes(1));
        await DrainAsync();

        var forgotten = Assert.Single(Reports());
        Assert.Equal("source_gone", WireConvert.Str(forgotten["failureClass"]));
        Assert.Equal("first", WireConvert.Str(forgotten["handoffId"]));
        Assert.Equal(string.Empty, await StatusAsync());

        BringBack();
        await HandOffAsync("second");
        await DrainAsync();

        Assert.Single(_media.Remuxes);
        Assert.Equal("processed", await StatusAsync());
        var report = Assert.Single(ReportsFor("second"));
        Assert.Equal("completed", WireConvert.Str(report["status"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_that_comes_back_after_it_was_forgotten_waits_for_its_manager_when_the_workflow_is_linked(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        await RunPassWhileGoneAsync();
        Advance(GoneSources.LookAgainAfter + TimeSpan.FromMinutes(1));
        await DrainAsync();

        BringBack();
        await SweepAsync();
        await DrainAsync();

        Assert.Empty(_media.Remuxes);
        Assert.Equal(string.Empty, await StatusAsync());
        Assert.True(File.Exists(Path.Join(_folders.Watched, Relative)));
    }

    // --- the release is sent again while the first send's pass is waiting or running -------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_release_sent_again_while_the_first_send_is_queued_is_answered_by_the_new_send_alone(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        await HandOffAsync("second");
        await DrainAsync();

        await AssertCleanedAndToldAsync("second");
        AssertSuperseded("first");
        Assert.Equal("cancelled", (await HandoffStatusAsync("first")).State);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{RemuxPassOutcomes.JobKind}'"));
        var status = await HandoffStatusAsync("second");
        Assert.Equal("completed", status.State);
        Assert.Equal(WireConvert.Str(Assert.Single(ReportsFor("second"))["outputPath"]), status.OutputPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_release_sent_again_while_the_first_send_is_being_cleaned_is_answered_by_the_new_send_alone(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        var sentAgain = false;
        _media.OnCall = () =>
        {
            string[] last;
            lock (_media.Calls)
            {
                last = [.. _media.Calls[^1]];
            }

            if (!sentAgain && last[0] == "ffmpeg" && last.Contains("-map") && !last.Contains("null"))
            {
                sentAgain = true;
                Task.Run(() => HandOffAsync("second")).GetAwaiter().GetResult();
            }
        };

        await DrainAsync();

        Assert.True(sentAgain);
        await AssertCleanedAndToldAsync("second");
        AssertSuperseded("first");
        Assert.Equal("completed", (await HandoffStatusAsync("second")).State);
    }

    // --- a pass-through look waiting for a file that is gone ---------------------------------------------------------------

    /// <summary>The original is to be handed back for the first send, but is not there: held as gone, with the look booked for after the grace.</summary>
    private async Task BookPassThroughLookAsync()
    {
        await _fixture.Db(async uow =>
        {
            var row = await _fixture.Ledger.RecordReceivedAsync(uow, "deluno", "first", _libraryId, Relative);
            await _fixture.Targets.AddAsync(uow, row, [Relative]);
            return 0;
        });
        await _fixture.Store.Execute($"INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ({_libraryId}, '{Relative}', 'processing_failed', 'failed')");
        var payload = new WireObject()
            .Set("relative_media_path", Relative)
            .Set("library_id", _libraryId)
            .Set("trigger", "worker")
            .Set("origin", new WireObject().Set("source_key", "deluno").Set("handoff_id", "first").Set("callback_path", EventsPath));
        await _fixture.Jobs.EnqueueOrGetAsync("pass-through-first", IntakeRules.PassThroughJobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));
        await RunPassWhileGoneAsync();
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{IntakeRules.PassThroughJobKind}' AND status = 'pending' AND not_before IS NOT NULL"));
    }

    private async Task AssertHandedBackAndToldAsync(string handoffId)
    {
        Assert.Empty(_media.Remuxes);
        Assert.Equal("passed_through", await StatusAsync());
        var report = Assert.Single(ReportsFor(handoffId), candidate => WireConvert.Str(candidate["status"]) == "completed");
        Assert.True(File.Exists(WireConvert.Str(report["outputPath"])));
        Assert.True(File.Exists(Path.Join(_folders.Watched, Relative)), "the original is never touched");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pass_through_look_at_a_file_that_comes_back_on_its_own_starts_when_the_sweep_sees_it(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await BookPassThroughLookAsync();

        Advance(TimeSpan.FromMinutes(2));
        BringBack();
        await SweepAsync();
        await DrainAsync();

        await AssertHandedBackAndToldAsync("first");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pass_through_look_at_a_file_that_is_back_when_the_release_is_sent_again_is_answered_to_the_new_send(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await BookPassThroughLookAsync();

        Advance(TimeSpan.FromSeconds(30));
        BringBack();
        await HandOffAsync("second");
        await DrainAsync();

        await AssertHandedBackAndToldAsync("second");
        AssertSuperseded("first");
    }
}
