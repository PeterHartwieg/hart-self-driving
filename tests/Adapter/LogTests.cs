using System;
using System.Collections.Generic;
using System.Linq;
using BAModAPI;
using Xunit;

namespace DrivenRides.Adapter.Tests;

[Collection("Game adapters")]
public sealed class LogTests : IDisposable
{
    private sealed class Logger : IModLogger
    {
        public readonly List<string> Messages = new();
        public readonly List<string> Errors = new();
        public void Info(string message) => Messages.Add(message);
        public void Error(string message) => Errors.Add(message);
    }

    public LogTests()
    {
        Log.Init(null!);
        TestMode.On = false;
        UnityEngine.Debug.Messages.Clear();
        UnityEngine.Debug.Errors.Clear();
    }

    public void Dispose()
    {
        Log.Init(null!);
        TestMode.On = false;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DiagnosticsKeepTheirVisibilityAndSink(bool testMode, bool useModLogger)
    {
        TestMode.On = testMode;
        var logger = new Logger();
        if (useModLogger) Log.Init(logger);

        Log.Info("Ride accepted.");
        Log.Trace("Detailed ride state.");
        foreach (var prefix in new[] { "NWH drive: ", "NWH planner: ", "NWH shape for " })
        {
            // Old Info callers and the explicit Trace callers keep identical visibility.
            Log.Info(prefix + "legacy");
            Log.Trace(prefix + "explicit");
        }
        Log.Info("NWH drive without the exact diagnostic prefix");
        Log.Error("NWH drive: failure");
        Log.Error(new InvalidOperationException("broken"));

        var expected = new List<string> { "Ride accepted." };
        if (testMode)
        {
            expected.Add("Detailed ride state.");
            foreach (var prefix in new[] { "NWH drive: ", "NWH planner: ", "NWH shape for " })
            {
                expected.Add(prefix + "legacy");
                expected.Add(prefix + "explicit");
            }
        }
        expected.Add("NWH drive without the exact diagnostic prefix");
        var errors = new[] { "NWH drive: failure", "System.InvalidOperationException: broken" };
        if (useModLogger)
        {
            Assert.Equal(expected, logger.Messages);
            Assert.Equal(errors, logger.Errors);
            Assert.Empty(UnityEngine.Debug.Messages);
            Assert.Empty(UnityEngine.Debug.Errors);
        }
        else
        {
            Assert.Equal(expected.Select(m => "[HART. Self Driving] " + m), UnityEngine.Debug.Messages);
            Assert.Equal(errors.Select(m => "[HART. Self Driving] " + m), UnityEngine.Debug.Errors);
        }
    }
}
