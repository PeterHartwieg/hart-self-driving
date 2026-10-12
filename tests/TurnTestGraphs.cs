using System;
using System.Collections.Generic;
using System.Linq;
using DrivenRides.Core.Routing;

namespace DrivenRides.Core.Tests;

internal sealed class TurnTestGraph
{
    private readonly List<Vec3> positions = new();
    private readonly List<int[]> edges = new();
    private readonly List<bool> intersections = new();
    private readonly Dictionary<int, int[]> lateral = new();
    public int Add(float x, float z, bool junction = false, float y = 0)
    {
        positions.Add(new Vec3(x, y, z));
        edges.Add(Array.Empty<int>());
        intersections.Add(junction);
        return positions.Count - 1;
    }
    public void Link(params int[] chain)
    {
        for (int k = 0; k + 1 < chain.Length; k++) edges[chain[k]] = new[] { chain[k + 1] };
    }
    public void Fork(int w, params int[] next) => edges[w] = next;
    public void Beside(int w, params int[] next) => lateral[w] = next;
    public LaneGraph Build(bool infer = false) => new(positions.ToArray(), edges.ToArray(), Enumerable.Repeat(1, positions.Count).ToArray(),
        intersections.ToArray(), new float[positions.Count], otherLanes: Enumerable.Range(0, positions.Count)
            .Select(i => lateral.TryGetValue(i, out var value) ? value : Array.Empty<int>()).ToArray(), inferTurns: infer);

    public static (LaneGraph Graph, int Entry, int Exit) Turn()
    {
        var b = new TurnTestGraph();
        int a = b.Add(0, -30), run = b.Add(0, -20), entry = b.Add(0, -10);
        int j1 = b.Add(0, -5, true), j2 = b.Add(0, 5, true), straight = b.Add(0, 10), end = b.Add(0, 20);
        b.Link(a, run, entry, j1, j2, straight, end);
        int west = b.Add(-20, -2), crossEntry = b.Add(-10, -2), cj1 = b.Add(-5, -2, true), cj2 = b.Add(5, -2, true);
        int exit = b.Add(10, -2), x2 = b.Add(20, -2), x3 = b.Add(30, -2), x4 = b.Add(40, -2), x5 = b.Add(50, -2);
        b.Link(west, crossEntry, cj1, cj2, exit, x2, x3, x4, x5);
        return (b.Build(true), entry, exit);
    }

    public static (LaneGraph Graph, int Entry, int Exit) UTurn()
    {
        var b = new TurnTestGraph();
        int a = b.Add(-30, 0), e = b.Add(-5, 0), j1 = b.Add(5, 0, true), j2 = b.Add(12, 0, true), j3 = b.Add(25, 0, true);
        int straight = b.Add(35, 0), end = b.Add(45, 0);
        b.Link(a, e, j1, j2, j3, straight, end);
        int w0 = b.Add(35, 15), wj1 = b.Add(20, 15, true), wj2 = b.Add(10, 15, true), wj3 = b.Add(0, 15, true), wj4 = b.Add(-10, 15, true);
        int exit = b.Add(-15, 15), w1 = b.Add(-30, 15), w2 = b.Add(-50, 15), w3 = b.Add(-70, 15);
        b.Link(w0, wj1, wj2, wj3, wj4, exit, w1, w2, w3);
        return (b.Build(true), e, exit);
    }
}
