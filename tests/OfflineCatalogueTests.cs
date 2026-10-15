using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DrivenRides.Core.Routing;
using DrivenRides.Core.Testing;
using Xunit;
using Xunit.Abstractions;

namespace DrivenRides.Core.Tests;

public sealed class OfflineGraphFactAttribute : FactAttribute
{
    public OfflineGraphFactAttribute()
    {
        if (OfflineCatalogueTests.GraphFile() == null) Skip = "The offline lane graph dump is missing.";
    }
}

public sealed class OfflineCatalogueTests
{
    private readonly ITestOutputHelper output;
    public OfflineCatalogueTests(ITestOutputHelper output) => this.output = output;
    internal static string? GraphFile()
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(OfflineCatalogueTests).Assembly.Location)!); directory != null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "build", "sol-tests", "lane-graph.json");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    internal static LaneGraph LoadGraph()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GraphFile()!));
        var graphJson = document.RootElement;
        int n = graphJson.GetArrayLength();
        var pos = new Vec3[n]; var nb = new int[n][]; var ol = new int[n][]; var mask = new int[n]; var inter = new bool[n]; var spd = new float[n];
        int i = 0;
        foreach (var w in graphJson.EnumerateArray())
        {
            var p = w.GetProperty("p"); pos[i] = new Vec3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
            nb[i] = w.GetProperty("n").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            ol[i] = w.GetProperty("o").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            foreach (var a in w.GetProperty("a").EnumerateArray()) mask[i] |= 1 << a.GetInt32();
            inter[i] = w.GetProperty("x").GetInt32() == 1; spd[i] = w.GetProperty("s").GetSingle(); i++;
        }
        return new LaneGraph(pos, nb, mask, inter, spd, otherLanes: ol, inferLanes: true, inferTurns: true);
    }

    [OfflineGraphFact]
    public void OceanCrestPocketNeedsANearbyStartBeforeWideningTheDestination()
    {
        var graph = LoadGraph();
        // Coordinates and start from the rc4 failure log, 15 October 2026, at 6 Ocean Crest Road.
        var target = new Vec3(-3021.61f, 1.02f, -1049.14f);
        const int pocket = 9314;
        var close = GoalPicker.Pick(graph, target, 0);
        Assert.NotEmpty(close);
        var direct = RoutePlanner.Plan(graph, new RouteRequest { Start = pocket, Goals = close, WideUTurns = true }, int.MaxValue);
        Assert.Equal(PlanStatus.NoPath, direct.Status);

        var wide = GoalPicker.Pick(graph, target, 0, new GoalOptions { Radius = 150, MinStraightMetres = 0, MaxHeightDiff = 6, AnyVehicleType = true });
        var early = RoutePlanner.Plan(graph, new RouteRequest { Start = pocket, Goals = wide, WideUTurns = true }, int.MaxValue);
        Assert.Equal(PlanStatus.Found, early.Status);
        Assert.Single(early.Result!.Waypoints);
        Assert.True(early.Result.WalkSeconds > 80f);

        // The real connected lane is only five metres from the pocket. It reaches the original nearby goals.
        const int lane = 15533;
        Assert.InRange(Vec3.HorizontalDistance(graph.Position(pocket), graph.Position(lane)), 0f, 6f);
        var fromLane = RoutePlanner.Plan(graph, new RouteRequest { Start = lane, Goals = close, WideUTurns = true }, int.MaxValue);
        Assert.Equal(PlanStatus.Found, fromLane.Status);
        Assert.True(fromLane.Result!.Waypoints.Count > 1);
        Assert.InRange(fromLane.Result.WalkSeconds, 0f, 15f);
    }

    [OfflineGraphFact]
    public void RealGraphBuildsAllManoeuvresAndFourControls()
    {
        var graph = LoadGraph();
        var catalogue = Catalogue.Build(graph);
        foreach (CaseKind kind in Enum.GetValues<CaseKind>()) output.WriteLine($"{kind}: {catalogue.Count(f => f.Kind == kind)}");
        Assert.Contains(catalogue, f => f.Kind == CaseKind.Turn);
        Assert.Contains(catalogue, f => f.Kind == CaseKind.UTurn);
        Assert.Contains(catalogue, f => f.Kind == CaseKind.LaneChange);
        Assert.Equal(catalogue.Count, catalogue.Select(f => f.Id).Distinct().Count());
        Assert.True(catalogue.Zip(catalogue.Skip(1), (a, b) => a.SharpnessDegrees >= b.SharpnessDegrees).All(x => x));
        var controls = Controls.Build(graph, catalogue);
        output.WriteLine($"Controls: {controls.Count}");
        Assert.Equal(4, controls.Count);
        foreach (var pair in controls.GroupBy(f => f.PairId))
        {
            var ok = Assert.Single(pair, f => !f.IsMutation);
            var bad = Assert.Single(pair, f => f.IsMutation);
            Assert.Null(ok.Path); Assert.Null(ok.SlowPath);
            Assert.Equal(ok.Route, bad.Route);
            Assert.Equal(CaseKind.Control, bad.Kind);
            Assert.EndsWith("-ok", ok.Id); Assert.EndsWith("-bad", bad.Id);
            var expanded = DrivenRides.Core.Ride.PathExpander.Expand(graph, ok.Route);
            Assert.Equal(expanded.SlowPath.OrderBy(x => x), bad.SlowPath);
            if (bad.Manoeuvre == CaseKind.UTurn)
            {
                Assert.Equal("far-side-early", bad.ExpectedFailure); Assert.Equal(bad.Route, bad.Path);
            }
            else
            {
                Assert.Equal("intrusion", bad.ExpectedFailure); Assert.NotEqual(expanded.Path.ToArray(), bad.Path!.ToArray());
                Assert.Equal(expanded.Path.Count, bad.Path.Count);
            }
            var correctGeometry = CaseGeometry.Build(graph, ok);
            var badGeometry = CaseGeometry.Build(graph, bad);
            Assert.Equal(correctGeometry.ExpandedPolyline, badGeometry.ExpandedPolyline);
        }
    }
}
