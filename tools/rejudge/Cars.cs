// The NWH cars measured in game so far, for the tools (CarSpec.Parse): the game log's "NWH shape" lines (0.1.55, turn
// rig session 20261013-182118), read from the drivable prefabs; the wheel turns at 140°/s on all of them. The taxi is the dressed vordtiaravic.
static class Cars
{
    public static readonly Dictionary<string, string> Measured = new()
    {
        ["limo"] = "limo;3.748;80;0,0.86,1.831;1.056,0.74,3.076;0=40,10=38.4,25=31.94,50=20.77;1.534;140",
        ["mersaididash"] = "mersaididash;4.02;65;-0.005,1.436,1.624;1.128,1.164,3.404;0=35,10=33.28,25=26.39,50=14.73;1.7;140",
        ["vordtiaravic"] = "vordtiaravic;2.665;0;0,0.782,1.199;0.897,0.578,2.461;0=35,10=33.28,25=26.39,50=14.73;1.504;140",
        ["taxi"] = "taxi;2.665;0;0,0.782,1.199;0.897,0.578,2.461;0=35,10=33.28,25=26.39,50=14.73;1.504;140",
    };

    // A measured car by name (or the end of a vehicle type name), or a spec.
    public static DrivenRides.Core.Drive.CarSpec? Get(string nameOrSpec)
    {
        foreach (var (name, spec) in Measured)
            if (nameOrSpec == name || nameOrSpec.EndsWith("_" + name, StringComparison.Ordinal)) return DrivenRides.Core.Drive.CarSpec.Parse(spec);
        return nameOrSpec.Contains(';') ? DrivenRides.Core.Drive.CarSpec.Parse(nameOrSpec) : null;
    }
}
