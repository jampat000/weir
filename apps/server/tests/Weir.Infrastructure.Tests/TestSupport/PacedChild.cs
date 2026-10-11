using Microsoft.Extensions.Time.Testing;
using Weir.Infrastructure.Processes;

namespace Weir.Infrastructure.Tests;

/// <summary>
/// A stand-in child whose pace, and the clock the runner's idle limit reads, both belong to the test. The child prints its next line
/// only when the test has seen the last one (<see cref="Pace"/>), and the test moves the clock by hand (<see cref="Tick"/>), so how
/// long a busy machine takes to start the child, run its timers or schedule the reader decides nothing: the limit is measured in
/// the lines the test has seen, not in seconds that other work can eat.
/// </summary>
internal sealed class PacedChild : IDisposable
{
    private readonly TempDirectory _acks = new();

    public PacedChild() => Runner = new ProcessRunner(time: Time);

    /// <summary>The clock the runner's idle limit reads.</summary>
    public FakeTimeProvider Time { get; } = new();

    public ProcessRunner Runner { get; }

    /// <summary>The pause argument that makes the stand-in wait for <see cref="Tick"/> between lines.</summary>
    public string Pace => "ack:" + _acks.Path;

    /// <summary>Lets <paramref name="seen"/> lines' worth of time pass and tells the child that many have been seen, so it prints the next.</summary>
    public void Tick(TimeSpan elapsed, int seen)
    {
        Time.Advance(elapsed);
        File.WriteAllBytes(_acks.Join(seen.ToString(System.Globalization.CultureInfo.InvariantCulture)), []);
    }

    public void Dispose() => _acks.Dispose();
}
