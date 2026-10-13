using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DrivenRides.Core.Testing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

public sealed class TrafficTests
{
    private readonly ITestOutputHelper output;
    public TrafficTests(ITestOutputHelper output) => this.output = output;

    internal static SimResult Run(TrafficScenario s, bool trace = false) => new TrafficSim(s, trace).Run();

    [Theory]
    [InlineData("lights")]
    [InlineData("priority")]
    [InlineData("merge")]
    [InlineData("uturn")]
    [InlineData("headon")]
    public void Soak(string template)
    {
        var seeds = int.TryParse(Environment.GetEnvironmentVariable("FT_TRAFFIC_SEEDS"), out var n) ? n : 500;
        var failures = new ConcurrentBag<(int Seed, string Why)>();
        var watch = Stopwatch.StartNew();
        var arrived = 0;
        Parallel.For(0, seeds, seed =>
        {
            var r = Run(TrafficTemplates.Build(template, seed));
            if (r.Failure != null) failures.Add((seed, r.Failure));
            else System.Threading.Interlocked.Increment(ref arrived);
        });
        output.WriteLine($"{template}: {seeds} seeds in {watch.ElapsedMilliseconds} ms, {failures.Count} failed");
        foreach (var (seed, why) in failures.OrderBy(f => f.Seed).Take(20)) output.WriteLine($"  seed {seed}: {why}");
        Assert.Empty(failures);
    }

    // FT_TRAFFIC_TRACE=lights:12 prints one seed's run, every half second.
    [Fact]
    public void TraceOneSeed()
    {
        var which = Environment.GetEnvironmentVariable("FT_TRAFFIC_TRACE");
        if (string.IsNullOrEmpty(which)) return;
        var parts = which.Split(':');
        var r = Run(TrafficTemplates.Build(parts[0], int.Parse(parts[1])), true);
        output.WriteLine(r.ToString());
        output.WriteLine(r.Trace.ToString());
    }

    // The NWH spike's three traffic failures (docs/nwh-drive-spike.md, "Traffic on").

    [Fact]
    public void SpikeUTurnAcrossOncomingOnASharedGreen()
    {
        var r = Run(TrafficTemplates.SpikeUTurnSharedGreen());
        Assert.True(r.Failure == null, r.Failure);
        Assert.True(r.Arrived);
        // It waited for the oncoming car instead of turning in front of it.
        Assert.Contains(1, r.GaveWayTo);
    }

    [Fact]
    public void SpikeGoesOnceTheOncomingCarsHavePassed()
    {
        var r = Run(TrafficTemplates.SpikeWaitAfterOncoming());
        Assert.True(r.Failure == null, r.Failure);
        // The last oncoming car is past the turn at about 11 s; the turn and 50 m on take under 8 s more.
        Assert.True(r.Time < 20f, $"arrived at {r.Time:0.0} s");
    }

    [Fact]
    public void SpikeNoseToNoseWithACarThatWaitsForIt()
    {
        var r = Run(TrafficTemplates.SpikeNoseToNose());
        Assert.True(r.Failure == null, r.Failure);
        Assert.True(r.BackOffs >= 1);
    }

    // A scenario survives the JSON round trip unchanged: it runs to the same end.
    [Fact]
    public void ScenarioJsonRoundTrip()
    {
        foreach (var s in new[] { TrafficTemplates.Build("lights", 7), TrafficTemplates.SpikeNoseToNose() })
        {
            var again = TrafficScenarioJson.Load(s.ToJson());
            Assert.Equal(s.Waypoints.Count, again.Waypoints.Count);
            Assert.Equal(s.Cars.Count, again.Cars.Count);
            var a = Run(s);
            var b = Run(again);
            Assert.Equal(a.ToString(), b.ToString());
            Assert.InRange(b.Time, a.Time - 0.05f, a.Time + 0.05f);
        }
    }

    // Every scenario in tests/traffic-scenarios/ (dumps from the game's failed traffic runs, turned into tests).
    [Fact]
    public void DumpedScenarios()
    {
        var folder = TrafficScenarioJson.Folder();
        Assert.NotNull(folder);
        var files = System.IO.Directory.GetFiles(folder!, "*.json");
        foreach (var file in files)
        {
            var r = Run(TrafficScenarioJson.Load(System.IO.File.ReadAllText(file)));
            output.WriteLine($"{System.IO.Path.GetFileName(file)}: {r}");
            Assert.True(r.Failure == null, $"{System.IO.Path.GetFileName(file)}: {r.Failure}");
        }
    }
}
