using CookieBridge.Core;
using Xunit;

namespace CookieBridge.Tests;

[Collection(BridgeEnvironmentCollection.Name)]
public sealed class CompatibilityMatrixTests : BridgeTestBase
{
    [Theory]
    [InlineData("2026.10.29", CompatLevel.Verified)]
    [InlineData("2027.1.1", CompatLevel.UntestedNewer)]
    [InlineData("2025.6.1", CompatLevel.Supported)]
    [InlineData("2024.2.1", CompatLevel.UntestedOlder)]
    public void Evaluate_ClassifiesKnownVersions(string version, CompatLevel expected)
    {
        var report = CompatibilityMatrix.Evaluate(version);

        Assert.Equal(expected, report.Level);
        Assert.Equal(version, report.Version);
        Assert.NotEmpty(report.Headline);
        Assert.True(report.HookAllowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("unknown")]
    [InlineData("1.2.3.4")]
    [InlineData("v2026.10.29")]
    public void Evaluate_UnknownShapes_AreUnknownButStillAllowed(string? version)
    {
        var report = CompatibilityMatrix.Evaluate(version);

        Assert.Equal(CompatLevel.Unknown, report.Level);
        Assert.True(report.HookAllowed);
        Assert.Equal("未知版本", report.LevelLabel);
        Assert.NotEmpty(report.Notes);
    }

    [Fact]
    public void HookAllowed_IsAlwaysTrue_AcrossTheWholeMatrix()
    {
        string?[] versions =
        [
            null, "", "garbage", "1.2.3", "2020.1.1", "2024.2.1", "2025.1.1", "2025.6.1",
            "2026.10.28", "2026.10.29", "2026.10.30", "2027.1.1", "2099.12.31",
        ];

        foreach (var version in versions)
        {
            var report = CompatibilityMatrix.Evaluate(version);
            Assert.True(report.HookAllowed, $"HookAllowed was false for '{version ?? "<null>"}'");
        }
    }

    [Fact]
    public void VerifiedVersion_MatchesTheInstalledRelease()
    {
        Assert.Equal(new Version(2026, 10, 29), CompatibilityMatrix.VerifiedVersion);
        Assert.Equal(new Version(2025, 1, 1), CompatibilityMatrix.MinimumSupported);
        Assert.Equal(CompatLevel.Verified, CompatibilityMatrix.Evaluate("2026.10.29").Level);
    }

    [Theory]
    [InlineData("2026.10.29", 2026, 10, 29)]
    [InlineData("2026.10", 2026, 10, 0)]
    [InlineData("2026.1.2", 2026, 1, 2)]
    [InlineData("2026.10.29-sabr", 2026, 10, 29)]
    [InlineData("2026.10.29+abcdef", 2026, 10, 29)]
    [InlineData("  2026.10.29  ", 2026, 10, 29)]
    public void Parse_HandlesRealWorldShapes(string input, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), CompatibilityMatrix.Parse(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("26.10")]
    [InlineData("2026")]
    public void Parse_RejectsUnusableInput(string input)
    {
        Assert.Null(CompatibilityMatrix.Parse(input));
    }
}
