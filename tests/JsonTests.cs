using System.Globalization;
using System.Text.Json;
using DrivenRides.Core.Testing;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class JsonTests
{
    [Fact]
    public void FixtureAndResultJsonEscapeStringsAndUseInvariantNumbers()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var fixture = new Fixture { Id = "quote\" slash\\ newline\n tab\t control\u0001", Kind = CaseKind.Control, Manoeuvre = CaseKind.UTurn,
                Route = new() { 1, 2 }, Path = new() { 1, 3 }, SlowPath = new() { 0 }, SharpnessDegrees = 12.5f, IsMutation = true };
            using var parsed = JsonDocument.Parse(fixture.ToJson());
            Assert.Equal(fixture.Id, parsed.RootElement.GetProperty("Id").GetString());
            Assert.Equal(12.5f, parsed.RootElement.GetProperty("SharpnessDegrees").GetSingle());
            Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("PairId").ValueKind);
            Assert.True(parsed.RootElement.GetProperty("IsMutation").GetBoolean());
            var result = new JudgeResult { Verdict = "fail", Reasons = new() { "contact\n\"" }, NearMiss = 0.1f };
            using var resultJson = JsonDocument.Parse(result.ToJson());
            Assert.Equal("contact\n\"", resultJson.RootElement.GetProperty("Reasons")[0].GetString());
            Assert.Equal(0.1f, resultJson.RootElement.GetProperty("NearMiss").GetSingle());
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
