// The NWH cars measured in game so far, for the tools (CarSpec.Parse): from the game log of the NWH spike (13 October
// 2026, the "NWH car" lines and the rig's minRadius at 10 km/h), body boxes moved to the rear axle (the limo's is
// 2.075 m behind its origin, the Dash's 1.625 m). The front track is fitted to the limo's traces (tools/calibrate); the
// Dash's is its body width less the limo's difference, until its traces come. The game measures every car from its
// prefab (NwhShape).
static class Cars
{
    public static readonly Dictionary<string, string> Measured = new()
    {
        ["limo"] = "limo;3.75;50;0,0.86,1.835;1.055,0.74,3.075;0=40,10=38.41,25=31.9,50=20.8;1.42",
        ["mersaididash"] = "mersaididash;4.02;50;-0.01,1.44,1.625;1.13,1.165,3.405;0=35,10=33.30,25=26.4,50=14.7;1.57",
    };

    // A measured car by name (or the end of a vehicle type name), or a spec.
    public static DrivenRides.Core.Drive.CarSpec Get(string nameOrSpec)
    {
        foreach (var (name, spec) in Measured)
            if (nameOrSpec == name || nameOrSpec.EndsWith("_" + name, StringComparison.Ordinal)) return DrivenRides.Core.Drive.CarSpec.Parse(spec);
        return DrivenRides.Core.Drive.CarSpec.Parse(nameOrSpec);
    }
}
