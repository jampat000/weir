using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weir.Core.Media;
using Weir.Core.Rules;
using Weir.Infrastructure.Media;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Tests.Media;

/// <summary>
/// How long the media tools are given: a bound sized to the file, and a limit on going nowhere for a tool that reports progress, so
/// a big file on a slow drive is left to finish and only a run that has stopped is cut off.
/// </summary>
public sealed class MediaToolTimeLimitTests
{
    private const long Gibibyte = 1024L * 1024 * 1024;

    private static readonly string StandInPath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Weir.TestChild.exe" : "Weir.TestChild");

    private static MediaTools Tools(
        IProcessRunner runner,
        long fileBytes = 100,
        TimeSpan? silence = null,
        TimeSpan? exitGrace = null,
        ListLogger<MediaTools>? logger = null) =>
        new(runner, new FixedResolver(), logger ?? new ListLogger<MediaTools>(), TimeProvider.System, path => new MediaFileState(path, true, true, fileBytes, 0))
        {
            SilenceLimit = silence ?? TimeSpan.FromSeconds(ToolTimeLimits.SilenceSeconds),
            FinishedExitGrace = exitGrace ?? TimeSpan.FromSeconds(ToolTimeLimits.FinishedExitSeconds),
        };

    private static ScriptedRunner Silent(ProcessTimeoutKind timeout = ProcessTimeoutKind.None, int exitCode = 0) =>
        new(_ => new ScriptedRun { Timeout = timeout, ExitCode = exitCode });

    private static RemuxWriteRequest WriteRequest(Action<FfmpegProgressUpdate>? progress) =>
        new(
            "source.mkv",
            "output.mkv",
            new RemuxPlan { VideoIndices = [0], Audio = [new PlannedTrack { InputIndex = 1, LangLabel = "eng", Default = true }], Subtitles = [] },
            JsonDocument.Parse("{}").RootElement,
            progress);

    private static string[] Block(int step, string progress = "continue") =>
        [$"frame={step}", "total_size=N/A", $"out_time_us={step * 500_000L}", $"out_time_ms={step * 500_000L}", $"progress={progress}"];

    // --- how long --------------------------------------------------------------------------

    [Fact]
    public void The_overall_bound_grows_with_the_size_of_the_file_from_the_least_it_is_ever_given()
    {
        Assert.Equal(FfmpegCommands.FfmpegTimeoutSeconds, ToolTimeLimits.OverallSeconds(0));
        Assert.Equal(FfmpegCommands.FfmpegTimeoutSeconds, ToolTimeLimits.OverallSeconds(-5));
        Assert.Equal(FfmpegCommands.FfmpegTimeoutSeconds + 80 * 1024, ToolTimeLimits.OverallSeconds(80 * Gibibyte));
        Assert.Equal(int.MaxValue, ToolTimeLimits.OverallSeconds(long.MaxValue));
    }

    [Fact]
    public void The_probe_bound_is_the_usual_one_until_the_operator_raises_the_probe_size()
    {
        Assert.Equal(FfmpegCommands.FfprobeTimeoutSeconds, ToolTimeLimits.ProbeSeconds(FfmpegCommands.DefaultProbeSizeMb));
        Assert.Equal(FfmpegCommands.FfprobeTimeoutSeconds, ToolTimeLimits.ProbeSeconds(1));
        Assert.Equal(FfmpegCommands.FfprobeTimeoutSeconds + 1014, ToolTimeLimits.ProbeSeconds(1024));
    }

    [Fact]
    public void The_time_between_two_whole_percents_covers_a_source_of_sixty_gigabytes_at_the_slowest_rate()
    {
        // One percent of 60 GiB is 614.4 MiB: 614 seconds at 1 MiB a second, so the usual ten minutes would cut it off.
        Assert.True(ToolTimeLimits.PercentStepSeconds(60 * Gibibyte) >= 614);
        Assert.Equal(0, ToolTimeLimits.PercentStepSeconds(0));
        Assert.Equal(int.MaxValue, ToolTimeLimits.PercentStepSeconds(long.MaxValue));
    }

    [Fact]
    public async Task A_big_file_is_given_longer_to_write_than_the_least_and_a_run_that_reports_progress_may_not_stand_still()
    {
        var runner = Silent();

        await Tools(runner, fileBytes: 80 * Gibibyte).WriteWithFfmpegAsync(WriteRequest(_ => { }));

        var request = Assert.Single(runner.Requests);
        Assert.Equal(TimeSpan.FromSeconds(FfmpegCommands.FfmpegTimeoutSeconds + 80 * 1024), request.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(ToolTimeLimits.SilenceSeconds), request.IdleTimeout);
        Assert.NotNull(request.MarksProgress);
        Assert.NotNull(request.IsFinalLine);
        Assert.Equal(TimeSpan.FromSeconds(ToolTimeLimits.FinishedExitSeconds), request.ExitAfterFinalLine);
    }

    [Fact]
    public async Task A_size_that_cannot_be_read_gives_the_least_time_and_says_so()
    {
        var logger = new ListLogger<MediaTools>();
        var runner = Silent();

        await Tools(runner, fileBytes: -1, logger: logger).WriteWithFfmpegAsync(WriteRequest(_ => { }));

        Assert.Equal(TimeSpan.FromSeconds(FfmpegCommands.FfmpegTimeoutSeconds), Assert.Single(runner.Requests).Timeout);
        var line = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("could not read the size of source.mkv", line.Message, StringComparison.Ordinal);
    }

    // --- an ffmpeg that stands still -------------------------------------------------------

    [Fact]
    public async Task A_write_that_stalls_is_reported_as_stalled_not_as_taking_too_long()
    {
        var error = await Assert.ThrowsAsync<MediaToolException>(
            () => Tools(Silent(ProcessTimeoutKind.Idle)).WriteWithFfmpegAsync(WriteRequest(_ => { })));

        Assert.Equal(ToolFailureText.Stalled, ToolFailureText.Plain(error));
    }

    [Fact]
    public async Task A_write_past_its_size_based_bound_is_reported_as_taking_too_long()
    {
        var error = await Assert.ThrowsAsync<MediaToolException>(
            () => Tools(Silent(ProcessTimeoutKind.Overall)).WriteWithFfmpegAsync(WriteRequest(_ => { })));

        Assert.Equal(ToolFailureText.TookTooLong, ToolFailureText.Plain(error));
    }

    [Fact]
    public async Task An_ffmpeg_whose_progress_moves_on_is_left_to_run_for_longer_than_the_silence_limit()
    {
        using var child = new PacedChild();
        var blocks = 0;

        // Forty blocks 100 ms apart run for four seconds of the limit's clock, over the three-second limit, and each moves on.
        var tools = Tools(child.Runner, silence: TimeSpan.FromSeconds(3));

        await tools.RunFfmpegAsync([StandInPath, "progress-advancing", child.Pace, "40"], progressCallback: _ => child.Tick(TimeSpan.FromMilliseconds(100), ++blocks));
    }

    [Fact]
    public async Task An_ffmpeg_that_keeps_printing_the_same_progress_block_is_stopped_as_stalled()
    {
        // What ffmpeg 9 does while it waits on a read that never returns: a block every half second, the same numbers in each.
        var tools = Tools(new ProcessRunner(), silence: TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<MediaToolException>(
            () => tools.RunFfmpegAsync([StandInPath, "progress-stuck", "50", "1200"], progressCallback: _ => { }));

        Assert.Equal(ToolFailureText.Stalled, ToolFailureText.Plain(error));
    }

    [Fact]
    public async Task An_ffmpeg_that_stops_reporting_part_way_is_stopped_for_the_silence()
    {
        var tools = Tools(new ProcessRunner(), silence: TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<MediaToolException>(
            () => tools.RunFfmpegAsync([StandInPath, "chatter-then-stall", "50", "5"], progressCallback: _ => { }));

        Assert.Equal(ToolFailureText.Stalled, ToolFailureText.Plain(error));
    }

    // --- an ffmpeg that has finished and stays -----------------------------------------------

    [Fact]
    public async Task An_ffmpeg_that_has_printed_the_end_of_its_progress_and_does_not_exit_is_stopped_and_the_run_is_a_success()
    {
        var logger = new ListLogger<MediaTools>();
        var tools = Tools(new ProcessRunner(), exitGrace: TimeSpan.FromMilliseconds(300), logger: logger);

        await tools.RunFfmpegAsync([StandInPath, "finish-then-linger", "60"], progressCallback: _ => { });

        var line = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.StartsWith("ffmpeg finished but did not exit", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_full_read_whose_ffmpeg_finished_but_did_not_exit_is_not_failed_for_it()
    {
        var logger = new ListLogger<MediaTools>();
        var runner = new ScriptedRunner(_ => new ScriptedRun
        {
            // The kill's exit code, after the whole file was read.
            ExitCode = -1,
            Timeout = ProcessTimeoutKind.NotExitedAfterFinish,
            Lines = [.. Block(1), .. Block(2, "end")],
        });

        await Tools(runner, logger: logger).ValidateMediaIntegrityAsync("movie.mkv", expectedDurationSeconds: 1.0);

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("ffmpeg finished but did not exit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_full_read_whose_ffmpeg_finished_but_did_not_exit_still_has_what_it_read_checked_against_the_duration()
    {
        var runner = new ScriptedRunner(_ => new ScriptedRun
        {
            ExitCode = -1,
            Timeout = ProcessTimeoutKind.NotExitedAfterFinish,
            Lines = [.. Block(1), .. Block(2, "end")],
        });

        // The read reached one second of a two-minute file.
        await Assert.ThrowsAsync<MediaCompletenessException>(() => Tools(runner).ValidateMediaIntegrityAsync("movie.mkv", expectedDurationSeconds: 120.0));
    }

    [Fact]
    public async Task Measuring_the_kept_streams_still_gives_the_time_reached_when_ffmpeg_does_not_exit_after_finishing()
    {
        var runner = new ScriptedRunner(_ => new ScriptedRun
        {
            ExitCode = -1,
            Timeout = ProcessTimeoutKind.NotExitedAfterFinish,
            Lines = [.. Block(1), .. Block(8, "end")],
        });

        var measured = await Tools(runner).MeasureKeptStreamsDurationAsync("movie.mkv", new RemuxPlan { VideoIndices = [0], Audio = [], Subtitles = [] });

        Assert.Equal(4.0, measured);
    }

    // --- mkvmerge ----------------------------------------------------------------------------

    [Fact]
    public async Task An_mkvmerge_write_is_sized_to_the_file_and_may_not_stand_still()
    {
        var runner = Silent();

        await Tools(runner).RunMkvmergeAsync(["mkvmerge"], progressCallback: null, sourceBytes: 10 * Gibibyte);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(TimeSpan.FromSeconds(MkvmergeCommands.MkvmergeTimeoutSeconds + 10 * 1024), request.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(ToolTimeLimits.SilenceSeconds), request.IdleTimeout);
    }

    [Fact]
    public async Task An_mkvmerge_write_of_a_very_large_source_is_given_longer_between_percents()
    {
        var runner = Silent();

        await Tools(runner).RunMkvmergeAsync(["mkvmerge"], progressCallback: null, sourceBytes: 60 * Gibibyte);

        var request = Assert.Single(runner.Requests);
        Assert.True(request.IdleTimeout > TimeSpan.FromSeconds(614), request.IdleTimeout.ToString());
        Assert.Equal(TimeSpan.FromSeconds(ToolTimeLimits.PercentStepSeconds(60 * Gibibyte)), request.IdleTimeout);
    }

    [Fact]
    public async Task Only_a_percent_further_than_the_last_is_mkvmerge_progress()
    {
        var runner = Silent();

        await Tools(runner).RunMkvmergeAsync(["mkvmerge"], progressCallback: null);

        var marks = Assert.Single(runner.Requests).MarksProgress!;
        Assert.True(marks("#GUI#progress 5%"));
        Assert.False(marks("#GUI#progress 5%"));
        Assert.False(marks("#GUI#progress 4%"));
        Assert.False(marks("Warning: something else"));
        Assert.True(marks("#GUI#progress 6%"));
    }

    [Fact]
    public async Task An_mkvmerge_write_that_stalls_is_reported_as_stalled()
    {
        var error = await Assert.ThrowsAsync<MediaToolException>(
            () => Tools(Silent(ProcessTimeoutKind.Idle)).RunMkvmergeAsync(["mkvmerge"], progressCallback: null));

        Assert.Equal(ToolFailureText.Stalled, ToolFailureText.Plain(error));
    }

    [Fact]
    public async Task An_mkvmerge_that_goes_quiet_after_a_few_percents_is_stopped_as_stalled()
    {
        var tools = Tools(new ProcessRunner(), silence: TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<MediaToolException>(
            () => tools.RunMkvmergeAsync([StandInPath, "chatter-then-stall", "50", "5"], progressCallback: null));

        Assert.Equal(ToolFailureText.Stalled, ToolFailureText.Plain(error));
    }

    [Fact]
    public async Task An_mkvmerge_failure_still_names_what_it_said_on_stdout()
    {
        var runner = new ScriptedRunner(_ => new ScriptedRun
        {
            ExitCode = 2,
            Stdout = Encoding.UTF8.GetBytes("#GUI#progress 10%\nError: the file is broken\n"),
        });

        var error = await Assert.ThrowsAsync<MediaToolException>(() => Tools(runner).RunMkvmergeAsync(["mkvmerge"], progressCallback: null));

        Assert.Contains("the file is broken", error.Message, StringComparison.Ordinal);
    }

    // --- the full read and the measure ---------------------------------------------------------

    [Fact]
    public async Task A_full_read_with_progress_is_sized_to_the_file_and_may_not_stand_still()
    {
        var runner = Silent();

        await Tools(runner, fileBytes: 20 * Gibibyte).ValidateMediaIntegrityAsync("movie.mkv", expectedDurationSeconds: null);
        await Tools(runner, fileBytes: 20 * Gibibyte).ValidateMediaIntegrityAsync("movie.mkv", expectedDurationSeconds: 600);

        Assert.Equal(2, runner.Requests.Count);
        Assert.All(runner.Requests, request => Assert.Equal(TimeSpan.FromSeconds(FfmpegCommands.FfmpegTimeoutSeconds + 20 * 1024), request.Timeout));
        Assert.Null(runner.Requests[0].IdleTimeout);
        Assert.Null(runner.Requests[0].MarksProgress);
        Assert.Equal(TimeSpan.FromSeconds(ToolTimeLimits.SilenceSeconds), runner.Requests[1].IdleTimeout);
        Assert.NotNull(runner.Requests[1].MarksProgress);
    }

    [Fact]
    public async Task A_full_read_that_stalls_is_reported_as_stalled()
    {
        var error = await Assert.ThrowsAsync<MediaToolException>(
            () => Tools(Silent(ProcessTimeoutKind.Idle)).ValidateMediaIntegrityAsync("movie.mkv", expectedDurationSeconds: 600));

        Assert.Equal(ToolFailureText.Stalled, ToolFailureText.Plain(error));
    }

    [Fact]
    public async Task Measuring_the_kept_streams_is_sized_to_the_file_and_may_not_stand_still()
    {
        var runner = Silent();
        var plan = new RemuxPlan { VideoIndices = [0], Audio = [], Subtitles = [] };

        await Tools(runner, fileBytes: 4 * Gibibyte).MeasureKeptStreamsDurationAsync("movie.mkv", plan);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(TimeSpan.FromSeconds(FfmpegCommands.FfmpegTimeoutSeconds + 4 * 1024), request.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(ToolTimeLimits.SilenceSeconds), request.IdleTimeout);
        Assert.NotNull(request.MarksProgress);
    }

    [Fact]
    public async Task The_full_read_and_the_measure_count_only_a_block_that_moves_on_as_progress()
    {
        var runner = Silent();
        var plan = new RemuxPlan { VideoIndices = [0], Audio = [], Subtitles = [] };

        await Tools(runner).ValidateMediaIntegrityAsync("movie.mkv", expectedDurationSeconds: 600);
        await Tools(runner).MeasureKeptStreamsDurationAsync("movie.mkv", plan);

        foreach (var request in runner.Requests)
        {
            var marks = request.MarksProgress!;
            var stuck = Block(3).Select(marks).ToList();
            var again = Block(3).Select(marks).ToList();
            var moved = Block(4).Select(marks).ToList();
            Assert.True(stuck[^1]);
            Assert.DoesNotContain(true, again);
            Assert.True(moved[^1]);
        }
    }

    [Fact]
    public async Task A_stalled_measure_is_a_measure_that_could_not_be_taken()
    {
        var plan = new RemuxPlan { VideoIndices = [0], Audio = [], Subtitles = [] };

        Assert.Null(await Tools(Silent(ProcessTimeoutKind.Idle)).MeasureKeptStreamsDurationAsync("movie.mkv", plan));
    }

    [Fact]
    public async Task A_raised_probe_size_is_given_the_time_to_be_read()
    {
        var runner = new ScriptedRunner(_ => new ScriptedRun { Stdout = """{"streams":[]}"""u8.ToArray() });

        await Tools(runner).FfprobeJsonAsync("movie.mkv", probeSizeMb: 1024);
        await Tools(runner).FfprobeJsonAsync("movie.mkv");

        Assert.Equal(TimeSpan.FromSeconds(FfmpegCommands.FfprobeTimeoutSeconds + 1014), runner.Requests[0].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(FfmpegCommands.FfprobeTimeoutSeconds), runner.Requests[1].Timeout);
    }
}
