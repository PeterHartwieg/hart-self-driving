using System.Collections.Generic;

namespace BAModAPI
{
    public interface IModLogger
    {
        void Info(string message);
        void Error(string message);
    }
}

namespace UnityEngine
{
    public static class Debug
    {
        public static readonly List<string> Messages = new();
        public static readonly List<string> Errors = new();
        public static void Log(string message) => Messages.Add(message);
        public static void LogError(string message) => Errors.Add(message);
    }
}

namespace DrivenRides
{
    internal static class TestMode { internal static bool On; }
    internal static class DrivenRidesMod { internal const string DisplayName = "HART. Self Driving"; }
}
