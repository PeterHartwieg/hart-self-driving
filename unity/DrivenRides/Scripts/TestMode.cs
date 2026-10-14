#nullable enable
using System;

namespace DrivenRides
{
    // Testing features run only when the game was started with -drivenrides-test, or with Food Trucks' test flag
    // (-foodtrucks-test), which the test PC's sessions already pass: the debug keys, the turn-test rig and its command
    // files, the ride trace, the lane graph and ride dumps, the slowdown dumps, the traffic cost records and the
    // detailed log (Log.Trace). A normal game gets none of them.
    internal static class TestMode
    {
        public const string Flag = "-drivenrides-test";
        public const string FoodTrucksFlag = "-foodtrucks-test";

        public static readonly bool On = Detect();

        private static bool Detect()
        {
            try
            {
                foreach (var arg in Environment.GetCommandLineArgs())
                {
                    if (string.Equals(arg, Flag, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(arg, FoodTrucksFlag, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }
    }
}
