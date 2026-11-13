using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DrivenRides.Core.Drive;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

public class JunctionHullTests
{
    private static CarSpec Limo() => CarSpec.Parse("limo;3.748;80;0,0.86,1.831;1.056,0.74,3.076;0=40,10=38.4,25=31.94,50=20.77;1.534;140");

    private sealed class Fixture
    {
        public LaneGraph Graph = null!;
        public float[] Limits = null!;
        public Dictionary<int, int> Index = new();
        public Dictionary<string, List<Vec3>> Corners = new();
    }

    private static Fixture Load()
    {
        string? path = null;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(JunctionHullTests).Assembly.Location)!); directory != null && path == null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "feasibility-fixtures", "avenue-uturns.json");
            if (File.Exists(candidate)) path = candidate;
        }
        Assert.NotNull(path);
        var root = JsonDocument.Parse(File.ReadAllText(path!)).RootElement;
        var waypoints = root.GetProperty("waypoints");
        var n = waypoints.GetArrayLength();
        var positions = new Vec3[n];
        var next = new int[n][];
        var other = new int[n][];
        var mask = new int[n];
        var junction = new bool[n];
        var limits = new float[n];
        var fixture = new Fixture();
        var i = 0;
        foreach (var w in waypoints.EnumerateArray())
        {
            var p = w.GetProperty("p");
            positions[i] = new Vec3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
            next[i] = w.GetProperty("n").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            other[i] = w.TryGetProperty("o", out var o) ? o.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>();
            if (w.TryGetProperty("a", out var a)) foreach (var t in a.EnumerateArray()) mask[i] |= 1 << t.GetInt32();
            else mask[i] = 3;
            junction[i] = w.GetProperty("x").GetInt32() == 1;
            limits[i] = w.GetProperty("s").GetSingle();
            fixture.Index[w.GetProperty("src").GetInt32()] = i;
            i++;
        }
        fixture.Graph = new LaneGraph(positions, next, mask, junction, limits, otherLanes: other, inferLanes: true, inferTurns: true);
        fixture.Limits = limits;
        foreach (var c in root.GetProperty("corners").EnumerateObject())
            fixture.Corners[c.Name] = c.Value.EnumerateArray().Select(p => new Vec3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())).ToList();
        return fixture;
    }

    [Theory]
    [InlineData("17684-99")]
    [InlineData("7408-15092")]
    public void TheCornersOfAUTurnDrivenCleanlyAreCarriageway(string pair)
    {
        var f = Load();
        var corners = f.Corners[pair];
        Assert.NotEmpty(corners);
        Assert.All(corners, c => Assert.True(Pavement.OnALane(f.Graph, c), $"({c.X}, {c.Z}) is pavement, {Pavement.LaneDistance(f.Graph, c):0.0} m from a lane"));
        Assert.Contains(corners, c => Pavement.LaneDistance(f.Graph, c) > 4f);
    }

    [Theory]
    [InlineData(17684, 99)]
    [InlineData(7408, 15092)]
    public void AUTurnDrivenCleanlyIsFeasibleWhereverThePedestriansWalk(int entry, int exit)
    {
        var f = Load();
        var check = new Feasibility(f.Graph, Limo(), null, Feasibility.DefaultMargin, w => f.Limits[w]) { PavementAt = _ => true };
        var verdict = check.Step(f.Index[entry], f.Index[exit]);
        Assert.True(verdict.Feasible, verdict.ToString());
        Assert.Equal("u-curve", verdict.Form);
    }

    [Fact]
    public void ThePavementBesideTheAvenueAwayFromAJunctionIsNot()
    {
        var f = Load();
        foreach (var p in new[] { new Vec3(80f, 0f, -389.5f), new Vec3(80f, 0f, -348f) })
        {
            Assert.False(Pavement.OnALane(f.Graph, p), $"({p.X}, {p.Z})");
            Assert.False(f.Graph.Junctions.Contains(p));
        }
    }
}
