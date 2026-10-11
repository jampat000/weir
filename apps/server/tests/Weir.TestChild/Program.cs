using System.Diagnostics;
using System.Globalization;

// A stand-in for a slow external tool, for tests of the process runner (#806). It is a program of our own, not a system
// tool run through a shell: a console program that inherits its shell's console and outlives a kill of that shell can be
// left without a console, and where Windows Terminal is the default terminal Windows then opens an error window for it.
// The child started below gets a console of its own, so killing its parent at any moment cannot strand it.
const string Hold = "hold";
const string AnnounceAndHold = "announce-and-hold";
const string HoldThroughChild = "hold-through-child";
const string CloseStdoutThenLinger = "close-stdout-then-linger";
const string Chatter = "chatter";
const string ChatterThenStall = "chatter-then-stall";
const string TickLine = "tick";
const string ProgressStuck = "progress-stuck";
const string ProgressAdvancing = "progress-advancing";
const string FinishThenLinger = "finish-then-linger";
const string AnnouncementLine = "ready";
const string FinishedLine = "done";

// Bounds how long a process orphaned by a killed test run can linger.
var lifetime = TimeSpan.FromMinutes(1);

switch (args.FirstOrDefault())
{
    case Hold:
        await Task.Delay(lifetime);
        break;

    case AnnounceAndHold:
        Console.Out.WriteLine(AnnouncementLine);
        await Task.Delay(lifetime);
        break;

    case HoldThroughChild:
        // The child inherits this process's stdout and stderr, so it keeps those pipes open if only this process is killed.
        using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Hold) { UseShellExecute = false, CreateNoWindow = true }))
        {
            // A second argument names a file to receive the child's process id, so a test can check the child is gone. It
            // appears whole or not at all, because the test may read it at the moment this process is killed.
            if (args.Length > 1)
            {
                var partial = args[1] + ".partial";
                await File.WriteAllTextAsync(partial, child!.Id.ToString(CultureInfo.InvariantCulture));
                File.Move(partial, args[1]);
            }

            await child!.WaitForExitAsync();
        }

        Console.Out.WriteLine(FinishedLine);
        break;

    case CloseStdoutThenLinger:
        // Like an ffmpeg tearing down after its progress stream ends: stdout closes, the process takes its time to exit.
        // The last argument is the linger in seconds, because a caller that adds options of its own puts them before it.
        Console.Out.WriteLine(AnnouncementLine);
        Console.Out.Flush();
        StandardOutput.Close();
        await Task.Delay(TimeSpan.FromSeconds(double.Parse(args[^1], CultureInfo.InvariantCulture)));
        break;

    case Chatter:
    case ChatterThenStall:
        // Like an ffmpeg working through a slow read: a line of progress every <period> milliseconds, then either the end or
        // silence. The period is the second argument and the number of lines the last, because a caller that adds options of
        // its own puts them between.
        for (var tick = 0; tick < int.Parse(args[^1], CultureInfo.InvariantCulture); tick++)
        {
            Console.Out.WriteLine(TickLine);
            Console.Out.Flush();
            await Pause(args[1], tick + 1);
        }

        if (args[0] == ChatterThenStall)
        {
            await Task.Delay(lifetime);
        }

        break;

    case ProgressStuck:
    case ProgressAdvancing:
        // An ffmpeg's `-progress pipe:1` stream: a block every <period> milliseconds, <count> blocks. A stuck one reports the same
        // output time every time, as ffmpeg 9 does while it waits on a read that never returns; an advancing one moves on, and
        // ends with the block that says it has finished.
        for (var block = 0; block < int.Parse(args[^1], CultureInfo.InvariantCulture); block++)
        {
            WriteProgressBlock(args[0] == ProgressAdvancing ? block + 1 : 1, final: false);
            await Pause(args[1], block + 1);
        }

        if (args[0] == ProgressAdvancing)
        {
            WriteProgressBlock(int.Parse(args[^1], CultureInfo.InvariantCulture) + 1, final: true);
        }

        break;

    case FinishThenLinger:
        // An ffmpeg that has written its output and printed the end of its progress, and then does not exit. The last argument is
        // how many seconds it stays.
        WriteProgressBlock(1, final: true);
        await Task.Delay(TimeSpan.FromSeconds(double.Parse(args[^1], CultureInfo.InvariantCulture)));
        break;

    default:
        Console.Error.WriteLine($"Unknown mode. Use {Hold}, {AnnounceAndHold}, {HoldThroughChild}, {CloseStdoutThenLinger}, {Chatter}, {ChatterThenStall}, {ProgressStuck}, {ProgressAdvancing} or {FinishThenLinger}.");
        return 2;
}

return 0;

// The wait between two lines or blocks: a number of milliseconds, or "ack:" and a folder, in which case the next one is printed
// once the caller has made a file named for how many it has seen. A test that paces a child by the files it makes gives the child
// no clock of its own to fall behind on a busy machine.
static async Task Pause(string how, int seen)
{
    const string Ack = "ack:";
    if (!how.StartsWith(Ack, StringComparison.Ordinal))
    {
        await Task.Delay(TimeSpan.FromMilliseconds(double.Parse(how, CultureInfo.InvariantCulture)));
        return;
    }

    var file = Path.Join(how[Ack.Length..], seen.ToString(CultureInfo.InvariantCulture));
    var waited = System.Diagnostics.Stopwatch.StartNew();
    while (!File.Exists(file) && waited.Elapsed < TimeSpan.FromMinutes(1))
    {
        await Task.Delay(TimeSpan.FromMilliseconds(2));
    }
}

static void WriteProgressBlock(int step, bool final)
{
    Console.Out.WriteLine($"frame={step.ToString(CultureInfo.InvariantCulture)}");
    Console.Out.WriteLine("total_size=N/A");
    Console.Out.WriteLine($"out_time_us={(step * 500_000L).ToString(CultureInfo.InvariantCulture)}");
    Console.Out.WriteLine($"out_time_ms={(step * 500_000L).ToString(CultureInfo.InvariantCulture)}");
    Console.Out.WriteLine(final ? "progress=end" : "progress=continue");
    Console.Out.Flush();
}
