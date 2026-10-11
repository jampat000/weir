using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Weir.Core.Media;

namespace Weir.Infrastructure.Processes;

/// <summary>What a child process reads on stdin.</summary>
public enum ProcessInput
{
    /// <summary>End of file immediately.</summary>
    Null,

    /// <summary>This process's own stdin, inherited.</summary>
    Inherit,
}

/// <summary>What happens to a child's stdout or stderr.</summary>
public enum ProcessOutput
{
    /// <summary>Read and dropped, so the child never blocks on a full pipe.</summary>
    Discard,

    /// <summary>Kept in full.</summary>
    Capture,

    /// <summary>Only the last <see cref="ProcessRequest.TailBytes"/> bytes are kept.</summary>
    Tail,
}

/// <summary>How a run ended when it did not end by itself.</summary>
public enum ProcessTimeoutKind
{
    None,

    /// <summary><see cref="ProcessRequest.Timeout"/> elapsed.</summary>
    Overall,

    /// <summary>
    /// The process made no progress for <see cref="ProcessRequest.IdleTimeout"/>: it wrote nothing, or, with
    /// <see cref="ProcessRequest.MarksProgress"/>, nothing that counted.
    /// </summary>
    Idle,

    /// <summary>
    /// The process printed its <see cref="ProcessRequest.IsFinalLine"/> and was still running <see cref="ProcessRequest.ExitAfterFinalLine"/>
    /// later. Its work is done; it was killed only for not exiting.
    /// </summary>
    NotExitedAfterFinish,
}

/// <summary>One child process to run, argv token for token (no shell).</summary>
public sealed record ProcessRequest
{
    /// <summary>The executable, then its arguments.</summary>
    public required IReadOnlyList<string> Argv { get; init; }

    /// <summary>Wall-clock limit; the whole process tree is killed when it passes. See also <see cref="IdleTimeout"/>.</summary>
    public TimeSpan? Timeout { get; init; }

    public ProcessInput Stdin { get; init; } = ProcessInput.Null;

    public ProcessOutput Stdout { get; init; } = ProcessOutput.Capture;

    public ProcessOutput Stderr { get; init; } = ProcessOutput.Capture;

    public int TailBytes { get; init; } = 32 * 1024;

    /// <summary>
    /// Called for each stdout line as it arrives, decoded as UTF-8 with replacement and split with universal
    /// newlines (<c>\n</c>, <c>\r\n</c>, <c>\r</c>), without the line end. If it throws, the process tree is
    /// killed and the exception is rethrown from <see cref="IProcessRunner.RunAsync"/>. Stdout is not captured
    /// when this is set.
    /// </summary>
    public Action<string>? OnStdoutLine { get; init; }

    /// <summary>
    /// How long the process may make no progress before it is taken to have stopped and its process tree is killed. A liveness
    /// limit for a tool that reports progress, in place of guessing how long the work takes. Any output on stdout or stderr is
    /// progress unless <see cref="MarksProgress"/> says which lines are; it also bounds the wait for exit once the output has closed.
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>
    /// With <see cref="IdleTimeout"/> and <see cref="OnStdoutLine"/>: decides whether a stdout line is progress. Only such a line
    /// restarts the idle clock, and nothing on stderr does, for a tool that keeps reporting while it is stuck.
    /// </summary>
    public Func<string, bool>? MarksProgress { get; init; }

    /// <summary>With <see cref="ExitAfterFinalLine"/> and <see cref="OnStdoutLine"/>: whether a stdout line is the last the tool writes before it exits.</summary>
    public Func<string, bool>? IsFinalLine { get; init; }

    /// <summary>With <see cref="IsFinalLine"/>: how long the process is given to exit after that line before it is killed.</summary>
    public TimeSpan? ExitAfterFinalLine { get; init; }

    /// <summary>
    /// Called with the child's process id as soon as it has started, before anything is read from it. It must not throw: the
    /// child is already running.
    /// </summary>
    public Action<int>? OnStarted { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// The child's scheduling priority. Below normal by default (#716): a remux or a full read can keep every core busy
    /// for an hour, and the web app, the database and anything else on the machine should still get the CPU first.
    /// </summary>
    public ProcessPriorityClass Priority { get; init; } = ProcessPriorityClass.BelowNormal;
}

/// <summary>How a child process ended and what it wrote.</summary>
public sealed record ProcessResult
{
    /// <summary>The exit code; meaningless (the kill's code) when <see cref="TimedOut"/>.</summary>
    public required int ExitCode { get; init; }

    public byte[] Stdout { get; init; } = [];

    public byte[] Stderr { get; init; } = [];

    public ProcessTimeoutKind Timeout { get; init; }

    public bool TimedOut => Timeout != ProcessTimeoutKind.None;
}

/// <summary>Runs external tools. Behind an interface so callers can be tested without them.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="request"/> to completion. Cancellation and timeouts kill the whole process tree.
    /// A cancelled run throws <see cref="OperationCanceledException"/>; a timed-out run returns with
    /// <see cref="ProcessResult.Timeout"/> set. A missing executable throws <see cref="Win32Exception"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IProcessRunner"/> over <see cref="Process"/>, for Windows and Linux.</summary>
public sealed partial class ProcessRunner(ILogger<ProcessRunner>? logger = null, ToolProcessLedger? tools = null, TimeProvider? time = null) : IProcessRunner
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// How long to wait for pipes to drain after a kill before giving up on them. Internal (not private)
    /// so tests can size their own wall-clock assertions off the real allowance instead of guessing one.
    /// </summary>
    internal static readonly TimeSpan DrainAfterKill = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Argv.Count == 0)
        {
            throw new ArgumentException("A process needs an executable.", nameof(request));
        }

        if (request.OnStdoutLine is null && (request.MarksProgress is not null || request.IsFinalLine is not null))
        {
            throw new ArgumentException("Telling progress or the final line apart needs OnStdoutLine to read the lines.", nameof(request));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Argv[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = request.Stdin == ProcessInput.Null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in request.Argv.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var toolUse = tools?.Track(process);
        request.OnStarted?.Invoke(process.Id);
        SetPriority(process, request.Priority, request.Argv[0]);
        if (request.Stdin == ProcessInput.Null)
        {
            process.StandardInput.Close();
        }

        using var timeoutSource = request.Timeout is { } timeout ? new CancellationTokenSource(timeout) : new CancellationTokenSource();
        using var idle = new IdleWatch(request.IdleTimeout, _time);
        using var lateExit = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, idle.Token, lateExit.Token);
        var anyOutputIsProgress = request.MarksProgress is null;
        Action onOutput = anyOutputIsProgress ? idle.Activity : static () => { };

        Exception? callbackError = null;
        var stdoutSink = new OutputSink(request.OnStdoutLine is null ? request.Stdout : ProcessOutput.Discard, request.TailBytes);
        var stderrSink = new OutputSink(request.Stderr, request.TailBytes);
        UniversalNewlineSplitter? lines = request.OnStdoutLine is null
            ? null
            : new UniversalNewlineSplitter(line =>
            {
                if (callbackError is not null)
                {
                    return;
                }

                try
                {
                    request.OnStdoutLine(line);
                    if (request.MarksProgress?.Invoke(line) == true)
                    {
                        idle.Activity();
                    }

                    if (request.ExitAfterFinalLine is { } grace && request.IsFinalLine?.Invoke(line) == true)
                    {
                        lateExit.CancelAfter(grace);
                    }
                }
#pragma warning disable CA1031 // The callback's exception is rethrown to the caller after the kill.
                catch (Exception error)
#pragma warning restore CA1031
                {
                    callbackError = error;
                    KillTree(process);
                }
            });

        var stdoutTask = PumpAsync(process.StandardOutput.BaseStream, stdoutSink, lines, onOutput);
        var stderrTask = PumpAsync(process.StandardError.BaseStream, stderrSink, null, onOutput);
        var timedOut = ProcessTimeoutKind.None;

        try
        {
            if (lines is not null)
            {
                await stdoutTask.WaitAsync(linked.Token).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            KillTree(process);
            await DrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = timeoutSource.IsCancellationRequested ? ProcessTimeoutKind.Overall
                : lateExit.IsCancellationRequested ? ProcessTimeoutKind.NotExitedAfterFinish
                : ProcessTimeoutKind.Idle;
        }

        await DrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
        if (callbackError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackError).Throw();
        }

        return new ProcessResult
        {
            ExitCode = process.HasExited ? process.ExitCode : -1,
            Stdout = stdoutSink.ToArray(),
            Stderr = stderrSink.ToArray(),
            Timeout = timedOut,
        };
    }

    private static async Task DrainAsync(Process process, Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(DrainAfterKill).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A grandchild outside the tree kept a pipe open; what was read is what there is.
        }

        using var exit = new CancellationTokenSource(DrainAfterKill);
        try
        {
            await process.WaitForExitAsync(exit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Reported through HasExited.
        }
    }

    /// <summary>
    /// Set straight after start, since <see cref="ProcessStartInfo"/> has no priority. On Linux, BelowNormal is nice 10 on
    /// the child's main thread, which the threads a tool starts afterwards inherit.
    /// </summary>
    private void SetPriority(Process process, ProcessPriorityClass priority, string tool)
    {
        if (priority == ProcessPriorityClass.Normal)
        {
            return;
        }

        try
        {
            process.PriorityClass = priority;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // It has already exited, or this system does not allow it (a locked-down container, say). The tool then runs at
            // its normal priority, which only slows everything else down.
            if (logger is not null)
            {
                LogPriorityNotSet(logger, exception, tool, priority);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not lower the priority of {Tool} to {Priority}; it runs at normal priority.")]
    private static partial void LogPriorityNotSet(ILogger logger, Exception exception, string tool, ProcessPriorityClass priority);

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Exiting while being killed, or access denied to a descendant: nothing more to do.
        }
        catch (AggregateException)
        {
            // Some descendants could not be killed; the process itself was.
        }
    }

    private static async Task PumpAsync(Stream stream, OutputSink sink, UniversalNewlineSplitter? lines, Action onOutput)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            onOutput();
            sink.Write(buffer.AsSpan(0, read));
            lines?.Feed(buffer.AsSpan(0, read));
        }

        lines?.Finish();
    }

    /// <summary>
    /// Cancels its token once nothing has been written for the limit. Output pushes the deadline back, but only when a twentieth
    /// of the limit has passed since it was last pushed, so a chatty tool does not cost a timer reset per read; a run is therefore
    /// stopped no sooner than 95% of the limit after its last output.
    /// </summary>
    private sealed class IdleWatch : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private readonly TimeProvider _time;
        private readonly TimeSpan? _limit;
        private long _pushedAt;

        public IdleWatch(TimeSpan? limit, TimeProvider time)
        {
            _limit = limit;
            _time = time;
            _source = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
            if (limit is { } span)
            {
                _pushedAt = _time.GetTimestamp();
                _source.CancelAfter(span);
            }
        }

        public CancellationToken Token => _source.Token;

        public void Activity()
        {
            if (_limit is not { } limit)
            {
                return;
            }

            var now = _time.GetTimestamp();
            if (_time.GetElapsedTime(Volatile.Read(ref _pushedAt), now) < limit / 20)
            {
                return;
            }

            Volatile.Write(ref _pushedAt, now);
            try
            {
                _source.CancelAfter(limit);
            }
            catch (ObjectDisposedException)
            {
                // The run is over; a pipe still being read has nothing left to keep alive.
            }
        }

        public void Dispose() => _source.Dispose();
    }

    /// <summary>Keeps everything, the tail, or nothing.</summary>
    private sealed class OutputSink(ProcessOutput mode, int tailBytes)
    {
        private ArrayBufferWriter<byte> _buffer = new();

        public void Write(ReadOnlySpan<byte> data)
        {
            switch (mode)
            {
                case ProcessOutput.Capture:
                    _buffer.Write(data);
                    break;
                case ProcessOutput.Tail:
                    _buffer.Write(data);
                    if (_buffer.WrittenCount > tailBytes * 2L)
                    {
                        var kept = _buffer.WrittenSpan[^tailBytes..].ToArray();
                        _buffer = new ArrayBufferWriter<byte>(tailBytes * 2);
                        _buffer.Write(kept);
                    }

                    break;
                case ProcessOutput.Discard:
                default:
                    break;
            }
        }

        public byte[] ToArray()
        {
            var all = _buffer.WrittenSpan;
            return (mode == ProcessOutput.Tail && all.Length > tailBytes ? all[^tailBytes..] : all).ToArray();
        }
    }
}
