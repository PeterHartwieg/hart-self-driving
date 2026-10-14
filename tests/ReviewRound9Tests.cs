using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Ride;
using DrivenRides.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

// Review round 9 (DD1: the near field swept where the model foresees the body).
public sealed class ReviewRound9Tests
{
    private readonly ITestOutputHelper output;
    public ReviewRound9Tests(ITestOutputHelper output) => this.output = output;

    // DD1. The runs that lost the stopping option avoidably before the near field was swept where the model foresees
    // the body (0.1.80: priority 50, lights 16, all in junction turns), each run again from nearby start poses, with
    // the wheel turned a little either way at the start, and with the decisions on the other physics step: no contact,
    // no stall, no unmakeable stop outside the emergency rule. The stopping option lost for a car without news of it,
    // by more than the decisions' discretization, is printed (not a hard invariant).
    private static readonly int[] PriorityLost = { 3, 5, 8, 56, 73, 81, 89, 101, 111, 121, 124, 144, 146, 156, 161, 176, 181, 182, 186, 206, 209, 234, 237, 257, 264, 269, 270, 280, 282, 284, 287, 290, 295, 334, 335, 345, 352, 357, 362, 382, 383, 400, 435, 448, 458, 460, 471, 481, 483, 496 };
    private static readonly int[] LightsLost = { 32, 44, 55, 96, 142, 154, 166, 176, 236, 266, 287, 377, 389, 393, 469, 477 };

    [Theory]
    [InlineData("priority")]
    [InlineData("lights")]
    public void TheTurnsThatLostTheOptionKeepIt(string template)
    {
        var seeds = template == "priority" ? PriorityLost : LightsLost;
        var variants = TrafficTests.Full
            ? new (float Shift, float Wheel, int Phase)[] { (0f, 0f, 1), (0.5f, 3f, 0), (-0.5f, -3f, 1), (0.25f, -2f, 0) }
            : new (float Shift, float Wheel, int Phase)[] { (0.5f, 3f, 1), (-0.5f, -3f, 0) };
        var bad = new System.Collections.Concurrent.ConcurrentBag<string>();
        var lost = new System.Collections.Concurrent.ConcurrentBag<string>();
        System.Threading.Tasks.Parallel.ForEach(seeds, seed =>
        {
            foreach (var (shift, wheel, phase) in variants)
            {
                var sc = TrafficTemplates.Build(template, seed);
                sc.Ride.X += (float)Math.Sin(sc.Ride.Heading) * shift;
                sc.Ride.Z += (float)Math.Cos(sc.Ride.Heading) * shift;
                sc.Ride.Steer = wheel;
                var r = new TrafficSim(sc, false, TrafficDriver.MaxPassRounds, phase).Run();
                if (r.OptionLostBeyond > 0f) lost.Add($"{template} {seed} ({shift:+0.00;-0.00} m, wheel {wheel:+0;-0}, phase {phase}): lost {r.OptionLostBeyond:0.00} m {r.LostKind}");
                if (r.Failure != null || !r.Arrived || r.NotEmergency != null)
                    bad.Add($"{template} {seed} ({shift:+0.00;-0.00} m, wheel {wheel:+0;-0}, phase {phase}): {r}; {r.NotEmergency}; lost {r.OptionLostBeyond:0.00} m {r.LostKind}");
            }
        });
        output.WriteLine($"{seeds.Length * variants.Length} runs, {bad.Count} bad, {lost.Count} losing the stopping option");
        foreach (var l in bad.OrderBy(l => l).Concat(lost.OrderBy(l => l)).Take(20)) output.WriteLine(l);
        Assert.Empty(bad);
    }
}
