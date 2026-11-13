#nullable enable
using System;

namespace DrivenRides
{
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
