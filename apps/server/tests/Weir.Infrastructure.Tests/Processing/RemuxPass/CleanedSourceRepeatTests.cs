using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Weir.Core.Activity;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Tests.Media;
using Weir.Infrastructure.Tests.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// One processing per source file: once Weir has cleaned a source (same path, size and modification time) and written its
/// copy, a resent hand-off, a requeue or a pass queued before the check existed settles as a skip with a reason, answers
/// the manager as a normal completion and never writes a second output. A different source is processed. Real database,
/// folders, intake, handler and worker loop; only ffprobe/ffmpeg and the manager's HTTP are fakes.
/// </summary>
public sealed class CleanedSourceRepeatTests : IDisposable
{
    private const string EventsPath = "/api/integrations/processors/events";
    private const string Relative = "Film/film.mkv";

    private readonly MediaManagerFixture _fixture = new();
    private readonly PassFolders _folders = new();
    private readonly FakeMediaRunner _media = new();
    private long _libraryId;

    public CleanedSourceRepeatTests()
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

    private string OutputFile => _folders.Out(Path.Join("Film", "film.mkv"));

    private string SourceFile => Path.Join(_folders.Watched, "Film", "film.mkv");

    private async Task SetUpAsync()
    {
        await _fixture.Store.Execute("DELETE FROM libraries");
        _libraryId = Convert.ToInt64(await _fixture.Db(uow => uow.ExecuteScalarWriteAsync(
            "INSERT INTO libraries (name, media_type, watched_folder, output_folder, work_folder, failure_policy, max_attempts, " +
            "rejected_file_action, retry_backoff_seconds, ready_after_seconds, min_file_size_mb, display_order, remove_original_after_success) " +
            "VALUES ('Movies', 'movie', $w, $o, $k, 'pass_through', 3, 'leave', 60, 0, 0, 1, 0) RETURNING id",
            ("$w", _folders.Watched),
            ("$o", _folders.Output),
            ("$k", _folders.Work))), CultureInfo.InvariantCulture);
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.30:5099", "k1");
        _folders.Source(Path.Join("Film", "film.mkv"));
        _folders.Source(Path.Join("Other", "other.mkv"));
        _media.Probes["film.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.Probes["other.mkv"] = FakeMediaRunner.EnglishAndJapanese;
        _media.DefaultProbe = FakeMediaRunner.EnglishOnly;
    }

    private ProcessingJobProcessor Worker()
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
        var handler = new RemuxPassHandler(
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
        return new ProcessingJobProcessor(
            _fixture.Jobs,
            new JobHandlerRegistry([handler]),
            new SqliteActivityWriter(_fixture.Store.Database),
            new NoUnhandledJobFailureRecorder(),
            new NoJobNotifications(),
            _fixture.Store.Clock,
            NullLogger<ProcessingJobProcessor>.Instance);
    }

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

    private MediaManagerImportEvent Handoff(string handoffId, string folder = "Film", string file = "film.mkv") => new()
    {
        SourceKey = "deluno",
        EventKind = "handoff",
        MediaScope = "movie",
        FilePath = Path.Join(_folders.Watched, folder, file),
        HandoffId = handoffId,
        CallbackPath = EventsPath,
        ReleaseName = "Film.2001",
        LibraryId = "lib-1",
    };

    private async Task HandOffAndDrainAsync(string handoffId, string folder = "Film", string file = "film.mkv")
    {
        await _fixture.Db(uow => _fixture.Intake.EnqueueRefineAsync(uow, Handoff(handoffId, folder, file)));
        await DrainAsync();
    }

    private Task<long> RemuxJobCountAsync() =>
        _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{RemuxPassOutcomes.JobKind}'");

    private Task<long> SkipLineCountAsync() =>
        _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{ActivityEventTypes.ProcessingFileSkippedRepeat}'");

    private async Task<string> ScalarText(string sql)
    {
        using var connection = _fixture.Store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private IReadOnlyList<WireObject> Reports() =>
        [.. _fixture.Http.RequestsTo(HttpMethod.Post, EventsPath).Select(request => (WireObject)request.Json!)];

    private static string Field(WireObject body, string name) => WireConvert.Str(body[name]);

    /// <summary>The skip is a normal completion naming the copy of the first run, never a failure and never a different path.</summary>
    private void AssertSecondReportIsTheFirstCompletion(string handoffId)
    {
        var reports = Reports();
        Assert.Equal(2, reports.Count);
        Assert.Equal("completed", Field(reports[0], "status"));
        Assert.Equal("completed", Field(reports[1], "status"));
        Assert.Equal(handoffId, Field(reports[1], "handoffId"));
        Assert.Equal(OutputFile, Field(reports[0], "outputPath"));
        Assert.Equal(Field(reports[0], "outputPath"), Field(reports[1], "outputPath"));
        Assert.Null(reports[1].Get("disposition"));
        Assert.Equal(CleanedEarlier.ReportMessage, Field(reports[1], "message"));
    }

    private async Task AssertNothingWasProcessedAgainAsync()
    {
        Assert.Equal(1, await RemuxJobCountAsync());
        Assert.Single(_media.Remuxes);
        Assert.Equal(1, await _fixture.Store.Scalar($"SELECT count(*) FROM activity_events WHERE event_type = '{ActivityEventTypes.ProcessingFileRemuxPassCompleted}'"));
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM handbacks"));
    }

    [Fact]
    public async Task The_same_hand_off_sent_again_for_the_same_source_is_skipped_and_answered_as_the_first_completion()
    {
        await SetUpAsync();
        await HandOffAndDrainAsync("h1");
        Assert.Single(_media.Remuxes);
        var writtenAt = new FileInfo(OutputFile).LastWriteTimeUtc;

        await HandOffAndDrainAsync("h1");

        await AssertNothingWasProcessedAgainAsync();
        Assert.Equal(writtenAt, new FileInfo(OutputFile).LastWriteTimeUtc);
        AssertSecondReportIsTheFirstCompletion("h1");
        Assert.Equal(1, await SkipLineCountAsync());
        var line = await ScalarText($"SELECT title FROM activity_events WHERE event_type = '{ActivityEventTypes.ProcessingFileSkippedRepeat}'");
        Assert.Equal("Skipped: already done (film.mkv)", line);
        Assert.Equal(
            ("skipped", Relative),
            (await ScalarText($"SELECT result FROM activity_events WHERE event_type = '{ActivityEventTypes.ProcessingFileSkippedRepeat}'"),
             await ScalarText($"SELECT relative_path FROM activity_events WHERE event_type = '{ActivityEventTypes.ProcessingFileSkippedRepeat}'")));
        var detail = (WireObject)WireJsonParser.Parse(await ScalarText($"SELECT detail FROM activity_events WHERE event_type = '{ActivityEventTypes.ProcessingFileSkippedRepeat}'"));
        Assert.Equal("skipped", Field(detail, "result"));
        Assert.StartsWith("Already done: cleaned on ", Field(detail, "user_message"), StringComparison.Ordinal);
        Assert.EndsWith($" into {OutputFile}", Field(detail, "user_message"), StringComparison.Ordinal);
        Assert.Equal("completed", ((await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "h1")))!).State);
        Assert.Equal("processed", await ScalarText($"SELECT status FROM files WHERE relative_path = '{Relative}'"));
    }

    [Fact]
    public async Task A_different_hand_off_for_the_same_source_is_skipped_and_answered_under_its_own_id()
    {
        await SetUpAsync();
        await HandOffAndDrainAsync("h1");

        await HandOffAndDrainAsync("h2");

        await AssertNothingWasProcessedAgainAsync();
        AssertSecondReportIsTheFirstCompletion("h2");
        Assert.Equal("completed", ((await _fixture.Db(uow => HandoffLedgerStore.FindAsync(uow, "deluno", "h2")))!).State);
    }

    [Fact]
    public async Task A_requeue_of_a_cleaned_source_is_skipped_with_the_reason_and_queues_nothing()
    {
        await SetUpAsync();
        await HandOffAndDrainAsync("h1");
        var row = (await _fixture.Db(uow => _fixture.Files.ListAsync(uow, new ProcessingFileListFilter { LibraryId = _libraryId }))).Single();
        var requeue = new RequeueStore(_fixture.Jobs, _fixture.Libraries);

        var result = await _fixture.Db(uow => requeue.RequeueFileAsync(uow, row));

        Assert.Equal((0, 1, 1), (result.Requeued, result.Skipped, result.AlreadyCleaned));
        Assert.StartsWith("Weir already cleaned this file, so it skipped it. Already done: cleaned on ", result.Detail, StringComparison.Ordinal);
        Assert.False(await _fixture.Db(uow => requeue.CanRequeueAsync(uow, row)));
        await AssertNothingWasProcessedAgainAsync();
        Assert.Single(Reports());
        Assert.Equal(1, await SkipLineCountAsync());
        Assert.Equal("processed", await ScalarText($"SELECT status FROM files WHERE relative_path = '{Relative}'"));
    }

    [Fact]
    public async Task A_bulk_requeue_counts_the_cleaned_sources_it_left_alone()
    {
        await SetUpAsync();
        await HandOffAndDrainAsync("h1");
        var rows = await _fixture.Db(uow => _fixture.Files.ListAsync(uow, new ProcessingFileListFilter { LibraryId = _libraryId }));

        var result = await _fixture.Db(uow => new RequeueStore(_fixture.Jobs, _fixture.Libraries).RequeueFilesAsync(uow, rows));

        Assert.Equal((0, 1, 1), (result.Requeued, result.Skipped, result.AlreadyCleaned));
        Assert.Equal("Nothing was queued: 1 file was already cleaned, so Weir left it alone.", result.Detail);
    }

    [Fact]
    public async Task A_pass_queued_before_the_check_existed_is_caught_when_it_starts_and_the_manager_is_answered()
    {
        await SetUpAsync();
        await HandOffAndDrainAsync("h1");
        var payload = new WireObject()
            .Set("relative_media_path", Relative)
            .Set("media_scope", "movie")
            .Set("library_id", _libraryId)
            .Set("trigger", "manual");
        var job = await _fixture.Jobs.EnqueueOrGetAsync(
            $"{RemuxPassOutcomes.JobKind}:requeue:{Guid.NewGuid():N}", RemuxPassOutcomes.JobKind, WireJsonWriter.Dumps(payload, WireJsonFormat.Compact));

        await DrainAsync();

        Assert.Equal("completed", await ScalarText($"SELECT status FROM jobs WHERE id = {job.Id}"));
        Assert.Single(_media.Remuxes);
        Assert.Equal(1, await SkipLineCountAsync());
        Assert.Equal("processed", await ScalarText($"SELECT status FROM files WHERE relative_path = '{Relative}'"));
        // The hand-off was already answered, so nothing is sent a second time for it.
        Assert.Single(Reports());
    }

    [Fact]
    public async Task A_source_that_changed_in_size_is_processed_again()
    {
        await SetUpAsync();
        await HandOffAndDrainAsync("h1");

        _folders.Source(Path.Join("Film", "film.mkv"), bytes: 3000);
        await HandOffAndDrainAsync("h2");

        Assert.Equal(2, await RemuxJobCountAsync());
        Assert.Equal(2, _media.Remuxes.Count());
        Assert.Equal(0, await SkipLineCountAsync());
        Assert.Equal(["completed", "completed"], Reports().Select(report => Field(report, "status")).ToArray());
    }

    [Fact]
    public async Task A_source_that_changed_only_in_modification_time_is_processed_again()
    {
        await SetUpAsync();
        await HandOffAndDrainAsync("h1");

        File.SetLastWriteTimeUtc(SourceFile, DateTime.UtcNow.AddMinutes(5));
        await HandOffAndDrainAsync("h2");

        Assert.Equal(2, await RemuxJobCountAsync());
        Assert.Equal(2, _media.Remuxes.Count());
        Assert.Equal(0, await SkipLineCountAsync());
    }

    [Fact]
    public async Task A_hand_off_for_a_different_release_is_processed()
    {
        await SetUpAsync();
        await HandOffAndDrainAsync("h1");

        await HandOffAndDrainAsync("h2", "Other", "other.mkv");

        Assert.Equal(2, await RemuxJobCountAsync());
        Assert.Equal(2, _media.Remuxes.Count());
        Assert.Equal(0, await SkipLineCountAsync());
        Assert.True(File.Exists(_folders.Out("Other/other.mkv")));
    }

    [Fact]
    public async Task A_repeat_after_the_manager_collected_the_copy_is_skipped_as_already_imported_and_names_the_same_path()
    {
        await SetUpAsync();
        await HandOffAndDrainAsync("h1");
        var handback = await _fixture.Db(uow => _fixture.Handback.FindAsync(uow, _libraryId, Relative));
        var collectedAt = DateTimeOffset.Parse("2026-10-07T04:30:00Z", CultureInfo.InvariantCulture);
        await _fixture.Db(async uow =>
        {
            await _fixture.Handback.RecordOutcomeAsync(uow, handback!.Id, HandbackRules.Imported, "Deluno", new ManagerSpeaker("deluno", 1, true), collectedAt, "/movies/Film/film.mkv", null);
            return 0;
        });
        File.Delete(OutputFile);

        await HandOffAndDrainAsync("h1");

        await AssertNothingWasProcessedAgainAsync();
        Assert.False(File.Exists(OutputFile));
        AssertSecondReportIsTheFirstCompletion("h1");
        Assert.Equal("Skipped: already imported (film.mkv)", await ScalarText($"SELECT title FROM activity_events WHERE event_type = '{ActivityEventTypes.ProcessingFileSkippedRepeat}'"));
        var detail = (WireObject)WireJsonParser.Parse(await ScalarText($"SELECT detail FROM activity_events WHERE event_type = '{ActivityEventTypes.ProcessingFileSkippedRepeat}'"));
        Assert.Equal("Already imported: Deluno collected the cleaned copy on 2026-10-07", Field(detail, "user_message"));
    }

    [Fact]
    public async Task A_copy_that_is_gone_with_nobody_saying_why_is_processed_again_so_there_is_something_to_hand_over()
    {
        await SetUpAsync();
        await HandOffAndDrainAsync("h1");
        File.Delete(OutputFile);

        await HandOffAndDrainAsync("h2");

        Assert.Equal(2, await RemuxJobCountAsync());
        Assert.Equal(2, _media.Remuxes.Count());
        Assert.True(File.Exists(OutputFile));
        Assert.Equal(0, await SkipLineCountAsync());
    }

    [Fact]
    public async Task A_hand_off_for_a_file_that_has_not_been_cleaned_is_queued_as_before()
    {
        await SetUpAsync();

        await HandOffAndDrainAsync("h1");

        Assert.Equal(1, await RemuxJobCountAsync());
        Assert.Single(_media.Remuxes);
        Assert.Equal(0, await SkipLineCountAsync());
        Assert.Equal("completed", Field(Assert.Single(Reports()), "status"));
    }
}
