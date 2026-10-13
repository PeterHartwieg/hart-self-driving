using System.Text.Json;
using DrivenRides.Core.Routing;

// The lane graph the game writes (GleyGraph.Dump): positions, successors, other lanes, vehicle types, junction flag
// and the road's limit (km/h, 0 for none).
static class GraphFile
{
    public static LaneGraph Load(string path, out float[] limitsKmh)
    {
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        int n = root.GetArrayLength();
        var pos = new Vec3[n]; var nb = new int[n][]; var ol = new int[n][]; var mask = new int[n]; var inter = new bool[n]; var spd = new float[n];
        var i = 0;
        foreach (var w in root.EnumerateArray())
        {
            var p = w.GetProperty("p"); pos[i] = new Vec3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
            nb[i] = w.GetProperty("n").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            ol[i] = w.GetProperty("o").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            foreach (var a in w.GetProperty("a").EnumerateArray()) mask[i] |= 1 << a.GetInt32();
            inter[i] = w.GetProperty("x").GetInt32() == 1; spd[i] = w.GetProperty("s").GetSingle(); i++;
        }
        limitsKmh = spd;
        return new LaneGraph(pos, nb, mask, inter, spd, otherLanes: ol, inferLanes: true, inferTurns: true);
    }
}
