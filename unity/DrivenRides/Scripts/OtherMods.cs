#nullable enable
using System;
using System.Reflection;

namespace DrivenRides
{
    internal static class OtherMods
    {
        private const string PhotoModeType = "RealLifeOverhaul.Leisure.PhotoMode";
        private static PropertyInfo? photoModeActive;

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
