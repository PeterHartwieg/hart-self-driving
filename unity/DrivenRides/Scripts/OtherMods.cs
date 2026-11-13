#nullable enable
using System;
using System.Reflection;

namespace DrivenRides
{
    // What other mods are doing that the mod's own keys must stand aside for, read by reflection: none is referenced,
    // and without them every answer is false (docs/rlo-1.0.5-compatibility.md).
    internal static class OtherMods
    {
        private const string PhotoModeType = "RealLifeOverhaul.Leisure.PhotoMode";
        private static PropertyInfo? photoModeActive;

        // Real Life Overhaul's photo mode: a free camera over the paused game that rolls on L and J and changes its
        // filter on K. Looked up on a key press until found, so a mod loaded later is still seen.
        public static bool PhotoMode
        {
            get
            {
                try
                {
                    photoModeActive ??= Find(PhotoModeType)?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
                    return photoModeActive?.GetValue(null) is bool active && active;
                }
                catch (Exception ex)
                {
                    Log.Error("Real Life Overhaul's photo mode: " + ex.Message);
                    return false;
                }
            }
        }

        private static Type? Find(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
