using CookieBridge.Core;
using Xunit;

namespace CookieBridge.Tests;

[Collection(BridgeEnvironmentCollection.Name)]
public sealed class CookieTargetWriterTests : BridgeTestBase
{
    [Fact]
    public void TargetPath_UsesTheOverride()
    {
        AssertIsolated();
        Assert.Equal(TargetFile, CookieTargetWriter.TargetPath);
        Assert.Equal(TargetFile + ".cookiebridge.bak", CookieTargetWriter.DefaultBackupPath);
    }

    [Fact]
    public void ReadState_ReportsMissingBeforeTheFirstWrite()
    {
        var state = CookieTargetWriter.ReadState();

        Assert.False(state.Exists);
        Assert.False(state.Readable);
        Assert.False(state.Valid);
        Assert.Equal(0, state.CookieCount);
        Assert.Null(state.LastWriteUtc);
    }

    [Fact]
    public void Write_InstallsACanonicalSevenFieldJar()
    {
        AssertIsolated();

        var outcome = CookieTargetWriter.Write(TestJars.Build(extraCookies: 3), out var detail);

        Assert.Equal(TargetWriteOutcome.Written, outcome);
        Assert.True(File.Exists(TargetFile));
        Assert.Contains("cookies", detail, StringComparison.OrdinalIgnoreCase);

        var written = File.ReadAllText(TargetFile);
        Assert.Equal(NetscapeCookieFile.Canonicalize(TestJars.Build(extraCookies: 3)), written);
        Assert.True(NetscapeCookieFile.IsValidForVrcvc(written));
        Assert.All(
            written.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')),
            row => Assert.Equal(7, row.Split('\t').Length));
    }

    [Fact]
    public void Write_SkipsIdenticalContent()
    {
        AssertIsolated();

        Assert.Equal(TargetWriteOutcome.Written, CookieTargetWriter.Write(TestJars.Build(), out _));

        var outcome = CookieTargetWriter.Write(TestJars.Build(), out var detail);

        Assert.Equal(TargetWriteOutcome.Unchanged, outcome);
        Assert.Equal("identical", detail);
    }

    [Fact]
    public void Write_RejectsInvalidInput_AndLeavesTheOldFileAlone()
    {
        AssertIsolated();

        Assert.Equal(TargetWriteOutcome.Written, CookieTargetWriter.Write(TestJars.Build(), out _));
        var before = File.ReadAllText(TargetFile);

        var outcome = CookieTargetWriter.Write(TestJars.BuildWithoutLogin(), out var detail);

        Assert.Equal(TargetWriteOutcome.Invalid, outcome);
        Assert.Equal("cookies-not-usable", detail);
        Assert.Equal(before, File.ReadAllText(TargetFile));
    }

    [Fact]
    public void Write_RejectsEmptyInput()
    {
        Assert.Equal(TargetWriteOutcome.Invalid, CookieTargetWriter.Write(string.Empty, out _));
        Assert.False(File.Exists(TargetFile));
    }

    [Fact]
    public void ReadState_DescribesAValidJar()
    {
        AssertIsolated();
        Assert.Equal(TargetWriteOutcome.Written, CookieTargetWriter.Write(TestJars.Build(extraCookies: 2), out _));

        var state = CookieTargetWriter.ReadState();

        Assert.True(state.Exists);
        Assert.True(state.Readable);
        Assert.True(state.Valid);
        Assert.Equal(5, state.CookieCount);
        Assert.True(state.LoginCookiePresent);
        Assert.NotNull(state.LastWriteUtc);
        Assert.True(state.SizeBytes > 0);
        Assert.Null(state.Error);
    }

    [Fact]
    public void ReadState_ReportsAnInvalidJarAsNotValid()
    {
        AssertIsolated();
        Directory.CreateDirectory(Path.GetDirectoryName(TargetFile)!);
        File.WriteAllText(TargetFile, "not a cookie jar at all\n");

        var state = CookieTargetWriter.ReadState();

        Assert.True(state.Exists);
        Assert.True(state.Readable);
        Assert.False(state.Valid);
        Assert.Equal(0, state.CookieCount);
    }

    [Fact]
    public void Write_WithBackupPath_KeepsThePreviousJar()
    {
        AssertIsolated();

        Assert.Equal(TargetWriteOutcome.Written, CookieTargetWriter.Write(TestJars.Build(extraCookies: 1), out _));
        var first = File.ReadAllText(TargetFile);

        var outcome = CookieTargetWriter.Write(
            TestJars.Build(extraCookies: 4), out _, backupPath: CookieTargetWriter.DefaultBackupPath);

        Assert.Equal(TargetWriteOutcome.Written, outcome);
        Assert.True(File.Exists(CookieTargetWriter.DefaultBackupPath));
        Assert.Equal(first, File.ReadAllText(CookieTargetWriter.DefaultBackupPath));
        Assert.NotEqual(first, File.ReadAllText(TargetFile));
    }

    [Theory]
    [InlineData(TargetWriteOutcome.Written, "已写入")]
    [InlineData(TargetWriteOutcome.Unchanged, "内容相同，跳过")]
    [InlineData(TargetWriteOutcome.Invalid, "Cookie 无效")]
    [InlineData(TargetWriteOutcome.Locked, "等待写入锁超时")]
    [InlineData(TargetWriteOutcome.Failed, "写入失败")]
    public void Describe_ReturnsAUserFacingLabel(TargetWriteOutcome outcome, string expected)
    {
        Assert.Equal(expected, CookieTargetWriter.Describe(outcome));
    }

    [Fact]
    public void WriteWithAlreadyLocked_SkipsTheCrossProcessMutex()
    {
        AssertIsolated();

        var outcome = CookieTargetWriter.Write(TestJars.Build(), out _, alreadyLocked: true);

        Assert.Equal(TargetWriteOutcome.Written, outcome);
        Assert.True(File.Exists(TargetFile));
    }
}
