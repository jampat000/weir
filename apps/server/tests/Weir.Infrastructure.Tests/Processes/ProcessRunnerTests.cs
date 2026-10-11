using System.ComponentModel;
using System.Globalization;
using System.Text;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Tests.Processes;

/// <summary>The real <see cref="ProcessRunner"/> on this OS's shell: capture, lines, timeouts, cancellation, tree kill.</summary>
public sealed class ProcessRunnerTests
{
    private static readonly ProcessRunner Runner = new();

    private const string StandInName = "Weir.TestChild";

    /// <summary>Long enough for the stand-in to have started its own child, unless the machine is starved, so a kill finds the whole tree.</summary>
    private static readonly TimeSpan KillAfter = TimeSpan.FromSeconds(5);

    private static string StandInPath { get; } = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? StandInName + ".exe" : StandInName);

    private static string[] Shell(string windows, string posix) =>
        OperatingSystem.IsWindows() ? ["cmd.exe", "/d", "/c", windows] : ["/bin/sh", "-c", posix];

    /// <summary>A slow tool that says something first and never finishes on its own.</summary>
    private static string[] TalkativeSlowTool() => [StandInPath, "announce-and-hold"];

    /// <summary>
    /// A tool that starts a slow child of its own and waits for it, so killing only the tool would leave the child
    /// holding the pipes. It prints a line only after that child ends, and writes the child's process id to
    /// <paramref name="childIdFile"/>.
    /// </summary>
    private static string[] ToolWithSlowChild(string childIdFile) => [StandInPath, "hold-through-child", childIdFile];

    /// <summary>
    /// Runs <see cref="ToolWithSlowChild"/> with <paramref name="configure"/> applied, remembering the tool and its child
    /// so the caller can check both are gone. Counting stand-ins by name would count the ones other tests run at the same time.
    /// A tool that a starved machine was too slow to let start its child before the limit has no child to leave behind.
    /// </summary>
    private static async Task<ProcessResult> RunWithSlowChildAsync(StartedProcesses started, Func<ProcessRequest, ProcessRequest> configure)
    {
        using var temp = new TempDirectory();
        var childIdFile = temp.Join("child.pid");

        var result = await Runner.RunAsync(configure(new ProcessRequest { Argv = ToolWithSlowChild(childIdFile), OnStarted = started.Remember }));

        if (File.Exists(childIdFile))
        {
            started.Remember(int.Parse(await File.ReadAllTextAsync(childIdFile), CultureInfo.InvariantCulture));
        }

        return result;
    }

    [Fact]
    public async Task Stdout_stderr_and_exit_code_are_captured()
    {
        var result = await Runner.RunAsync(new ProcessRequest { Argv = Shell("echo out& echo err 1>&2& exit /b 3", "echo out; echo err 1>&2; exit 3") });

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("out", Encoding.UTF8.GetString(result.Stdout).Trim());
        Assert.Equal("err", Encoding.UTF8.GetString(result.Stderr).Trim());
    }

    [Fact]
    public async Task A_tool_runs_below_normal_priority()
    {
        // The child reads stdin to its end before reporting, and the runner closes stdin only after it has set the
        // priority, so the child cannot look before it is set.
        string[] reportOwnPriority = OperatingSystem.IsWindows()
            ? ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "[Console]::In.ReadToEnd() | Out-Null; [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass"]
            : ["/bin/sh", "-c", "cat > /dev/null; awk '{ print $19 }' /proc/$$/stat"];

        var result = await Runner.RunAsync(new ProcessRequest { Argv = reportOwnPriority });

        Assert.Equal(OperatingSystem.IsWindows() ? "BelowNormal" : "10", Encoding.UTF8.GetString(result.Stdout).Trim());
    }

    [PosixFact("The shell's own argument handling is what this proves; dotnet itself echoes nothing useful on Windows.")]
    public async Task Arguments_reach_the_child_token_for_token()
    {
        var result = await Runner.RunAsync(new ProcessRequest { Argv = ["/bin/sh", "-c", "printf '%s|' \"$@\"", "sh", "a b", "'q'", "\"d\"", ""] });

        Assert.Equal("a b|'q'|\"d\"||", Encoding.UTF8.GetString(result.Stdout));
    }

    [Fact]
    public async Task Tail_keeps_only_the_last_bytes()
    {
        var result = await Runner.RunAsync(new ProcessRequest
        {
            Argv = Shell("for /l %i in (1,1,400) do @echo line %i", "i=1; while [ $i -le 400 ]; do echo line $i; i=$((i+1)); done"),
            Stdout = ProcessOutput.Tail,
            TailBytes = 64,
        });

        Assert.Equal(64, result.Stdout.Length);
        Assert.EndsWith("line 400", Encoding.UTF8.GetString(result.Stdout).TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lines_arrive_split_on_every_newline_style()
    {
        var lines = new List<string>();

        await Runner.RunAsync(new ProcessRequest
        {
            Argv = Shell("echo one& echo two", "printf 'one\\r\\ntwo\\rthree\\nfour'"),
            OnStdoutLine = lines.Add,
        });

        Assert.Equal(OperatingSystem.IsWindows() ? ["one", "two"] : ["one", "two", "three", "four"], lines);
    }

    [Fact]
    public async Task A_timeout_kills_the_whole_tree_and_returns_promptly()
    {
        using var started = new StartedProcesses();

        var result = await RunWithSlowChildAsync(started, request => request with { Timeout = KillAfter });

        Assert.Equal(ProcessTimeoutKind.Overall, result.Timeout);
        Assert.DoesNotContain("done", Encoding.UTF8.GetString(result.Stdout), StringComparison.Ordinal);
        Assert.NotEqual(0, started.Count);
        await started.AllHaveEndedAsync();
    }

    [Fact]
    public async Task A_progress_run_that_never_writes_a_line_is_still_stopped_by_the_timer()
    {
        // #539 item 4: a progress loop that only checks its timeout as a line arrives would hang forever on a
        // process that never writes one - stuck reading its input, for instance.
        // Here the tool writes nothing until its child ends, which is long after the timeout; the timeout is still
        // enforced, on the wall-clock timer alone.
        var lines = new List<string>();
        using var started = new StartedProcesses();

        var result = await RunWithSlowChildAsync(started, request => request with { OnStdoutLine = lines.Add, Timeout = KillAfter });

        Assert.Equal(ProcessTimeoutKind.Overall, result.Timeout);
        Assert.Empty(lines);
        Assert.NotEqual(0, started.Count);
        await started.AllHaveEndedAsync();
    }

    [Fact]
    public async Task Cancellation_kills_the_tree_and_throws()
    {
        using var temp = new TempDirectory();
        using var cancel = new CancellationTokenSource(KillAfter);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner.RunAsync(new ProcessRequest { Argv = ToolWithSlowChild(temp.Join("child.pid")) }, cancel.Token));
    }

    [Fact]
    public async Task A_throwing_line_callback_kills_the_process_and_rethrows()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Runner.RunAsync(new ProcessRequest
        {
            Argv = TalkativeSlowTool(),
            OnStdoutLine = _ => throw new InvalidOperationException("stop"),
        }));

        Assert.Equal("stop", error.Message);
    }

    [Fact]
    public async Task A_process_that_goes_silent_is_killed_once_it_has_said_nothing_for_the_idle_limit()
    {
        using var started = new StartedProcesses();

        var result = await Runner.RunAsync(new ProcessRequest
        {
            Argv = TalkativeSlowTool(),
            OnStarted = started.Remember,
            IdleTimeout = TimeSpan.FromMilliseconds(500),
            Timeout = TimeSpan.FromMinutes(5),
        });

        Assert.Equal(ProcessTimeoutKind.Idle, result.Timeout);
        Assert.True(result.TimedOut);
        Assert.Equal(1, started.Count);
        await started.AllHaveEndedAsync();
    }

    [Fact]
    public async Task A_process_that_keeps_talking_is_left_to_run_for_longer_than_the_idle_limit()
    {
        using var child = new PacedChild();
        var lines = new List<string>();

        // 40 lines 100 ms apart run for four seconds of the limit's clock, over the three-second limit, but never fall silent for it.
        var result = await child.Runner.RunAsync(new ProcessRequest
        {
            Argv = [StandInPath, "chatter", child.Pace, "40"],
            OnStdoutLine = line =>
            {
                lines.Add(line);
                child.Tick(TimeSpan.FromMilliseconds(100), lines.Count);
            },
            IdleTimeout = TimeSpan.FromSeconds(3),
            Timeout = TimeSpan.FromMinutes(5),
        });

        Assert.Equal(ProcessTimeoutKind.None, result.Timeout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(40, lines.Count);
    }

    [Fact]
    public async Task A_process_that_stops_talking_part_way_is_killed_by_the_idle_limit_and_what_it_said_is_kept()
    {
        using var child = new PacedChild();
        var lines = new List<string>();
        using var started = new StartedProcesses();

        // The fifth line is the last: once it is in, the limit's clock moves on past the limit with nothing more said.
        var result = await child.Runner.RunAsync(new ProcessRequest
        {
            Argv = [StandInPath, "chatter-then-stall", "50", "5"],
            OnStarted = started.Remember,
            OnStdoutLine = line =>
            {
                lines.Add(line);
                if (lines.Count == 5)
                {
                    child.Tick(TimeSpan.FromSeconds(3), lines.Count);
                }
            },
            IdleTimeout = TimeSpan.FromSeconds(2),
            Timeout = TimeSpan.FromMinutes(5),
        });

        Assert.Equal(ProcessTimeoutKind.Idle, result.Timeout);
        Assert.Equal(5, lines.Count);
        await started.AllHaveEndedAsync();
    }

    [Fact]
    public async Task A_process_that_keeps_printing_lines_that_are_not_progress_is_killed_by_the_idle_limit()
    {
        var lines = new List<string>();
        using var started = new StartedProcesses();

        // Lines arrive every 100 ms for a minute; none of them is progress, so the limit runs from the start.
        var result = await Runner.RunAsync(new ProcessRequest
        {
            Argv = [StandInPath, "chatter", "100", "600"],
            OnStarted = started.Remember,
            OnStdoutLine = lines.Add,
            MarksProgress = _ => false,
            IdleTimeout = TimeSpan.FromSeconds(1),
            Timeout = TimeSpan.FromMinutes(5),
        });

        Assert.Equal(ProcessTimeoutKind.Idle, result.Timeout);
        Assert.True(lines.Count < 600);
        await started.AllHaveEndedAsync();
    }

    [Fact]
    public async Task A_process_whose_progress_lines_advance_is_left_to_run_past_the_idle_limit()
    {
        using var child = new PacedChild();
        var advance = new Weir.Core.Media.FfmpegProgressAdvance();
        var blocks = 0;

        // Forty blocks 100 ms apart run for four seconds of the limit's clock, over the three-second limit, and each moves on.
        var result = await child.Runner.RunAsync(new ProcessRequest
        {
            Argv = [StandInPath, "progress-advancing", child.Pace, "40"],
            OnStdoutLine = line =>
            {
                if (line.StartsWith("progress=", StringComparison.Ordinal))
                {
                    child.Tick(TimeSpan.FromMilliseconds(100), ++blocks);
                }
            },
            MarksProgress = advance.Feed,
            IdleTimeout = TimeSpan.FromSeconds(3),
            Timeout = TimeSpan.FromMinutes(5),
        });

        Assert.Equal(ProcessTimeoutKind.None, result.Timeout);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_process_that_keeps_printing_the_same_progress_block_is_killed_by_the_idle_limit()
    {
        var advance = new Weir.Core.Media.FfmpegProgressAdvance();
        using var started = new StartedProcesses();

        var result = await Runner.RunAsync(new ProcessRequest
        {
            Argv = [StandInPath, "progress-stuck", "50", "1200"],
            OnStarted = started.Remember,
            OnStdoutLine = _ => { },
            MarksProgress = advance.Feed,
            IdleTimeout = TimeSpan.FromSeconds(1),
            Timeout = TimeSpan.FromMinutes(5),
        });

        Assert.Equal(ProcessTimeoutKind.Idle, result.Timeout);
        await started.AllHaveEndedAsync();
    }

    [Fact]
    public async Task A_process_that_does_not_exit_after_its_final_line_is_killed_and_says_so()
    {
        using var started = new StartedProcesses();

        var result = await Runner.RunAsync(new ProcessRequest
        {
            Argv = [StandInPath, "finish-then-linger", "60"],
            OnStarted = started.Remember,
            OnStdoutLine = _ => { },
            IsFinalLine = Weir.Core.Media.FfmpegProgressAdvance.IsEnd,
            ExitAfterFinalLine = TimeSpan.FromMilliseconds(300),
            Timeout = TimeSpan.FromMinutes(5),
        });

        Assert.Equal(ProcessTimeoutKind.NotExitedAfterFinish, result.Timeout);
        await started.AllHaveEndedAsync();
    }

    [Fact]
    public async Task A_process_that_exits_within_the_wait_after_its_final_line_is_not_killed()
    {
        var result = await Runner.RunAsync(new ProcessRequest
        {
            Argv = [StandInPath, "progress-advancing", "50", "3"],
            OnStdoutLine = _ => { },
            IsFinalLine = Weir.Core.Media.FfmpegProgressAdvance.IsEnd,
            ExitAfterFinalLine = TimeSpan.FromSeconds(30),
            Timeout = TimeSpan.FromMinutes(5),
        });

        Assert.Equal(ProcessTimeoutKind.None, result.Timeout);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Telling_progress_apart_needs_the_lines_to_be_read()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Runner.RunAsync(new ProcessRequest
        {
            Argv = [StandInPath, "chatter", "100", "1"],
            MarksProgress = _ => true,
        }));
    }

    [Fact]
    public async Task A_process_that_lingers_after_closing_stdout_is_killed_once_it_has_been_silent_for_the_idle_limit()
    {
        using var started = new StartedProcesses();

        var result = await Runner.RunAsync(new ProcessRequest
        {
            Argv = [StandInPath, "close-stdout-then-linger", "60"],
            OnStarted = started.Remember,
            OnStdoutLine = _ => { },
            IdleTimeout = TimeSpan.FromMilliseconds(500),
            Timeout = TimeSpan.FromMinutes(5),
        });

        Assert.Equal(ProcessTimeoutKind.Idle, result.Timeout);
        await started.AllHaveEndedAsync();
    }

    [Fact]
    public async Task A_process_that_takes_a_while_to_exit_after_closing_stdout_is_waited_for_within_the_idle_limit()
    {
        var lines = new List<string>();

        var result = await Runner.RunAsync(new ProcessRequest
        {
            Argv = [StandInPath, "close-stdout-then-linger", "1"],
            OnStdoutLine = lines.Add,
            IdleTimeout = TimeSpan.FromSeconds(30),
            Timeout = TimeSpan.FromSeconds(60),
        });

        Assert.Equal(ProcessTimeoutKind.None, result.Timeout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["ready"], lines);
    }

    [Fact]
    public async Task A_missing_executable_throws()
    {
        await Assert.ThrowsAsync<Win32Exception>(() => Runner.RunAsync(new ProcessRequest { Argv = ["weir-no-such-tool-" + Guid.NewGuid().ToString("N")] }));
    }
}
