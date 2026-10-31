using CookieBridge.Core;
using Xunit;

namespace CookieBridge.Tests;

[Collection(BridgeEnvironmentCollection.Name)]
public sealed class SecretRedactorTests : BridgeTestBase
{
    [Fact]
    public void Redact_CutsTabSeparatedCookieRows()
    {
        var line = "failed to parse: .youtube.com\tTRUE\t/\tTRUE\t1790000000\tLOGIN_INFO\tSECRET_LOGIN_VALUE";

        var redacted = SecretRedactor.Redact(line);

        Assert.DoesNotContain("SECRET_LOGIN_VALUE", redacted, StringComparison.Ordinal);
        Assert.Contains("<redacted-cookie-row>", redacted, StringComparison.Ordinal);
        Assert.StartsWith("failed to parse: .youtube.com", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RemovesValueOfKnownCookieNames_EvenWithoutSevenFields()
    {
        var redacted = SecretRedactor.Redact("cookie LOGIN_INFO\tSECRET_LOGIN_VALUE\nnext line");

        Assert.DoesNotContain("SECRET_LOGIN_VALUE", redacted, StringComparison.Ordinal);
        Assert.Contains("LOGIN_INFO\t<redacted>", redacted, StringComparison.Ordinal);
        Assert.Contains("next line", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_KeepsOrdinaryDiagnostics()
    {
        const string message = "Wrote VRCVC cookie file (12 cookies, login=True).";

        Assert.Equal(message, SecretRedactor.Redact(message));
    }

    [Fact]
    public void Redact_HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
        Assert.Equal(string.Empty, SecretRedactor.Redact(""));
    }

    [Fact]
    public void BridgeLogFormat_RedactsCookieRows()
    {
        var line = BridgeLog.Format("ERROR", "target", ".youtube.com\tTRUE\t/\tTRUE\t1790000000\tLOGIN_INFO\tSECRET_LOGIN_VALUE");

        Assert.DoesNotContain("SECRET_LOGIN_VALUE", line, StringComparison.Ordinal);
        Assert.StartsWith("2", line, StringComparison.Ordinal);       // ISO-8601 timestamp
        Assert.Contains("[ERROR]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeLog_WriteAppendsRedactedLineInsideTheIsolatedRoot()
    {
        AssertIsolated();

        BridgeLog.Info("test", "cookie row .youtube.com\tTRUE\t/\tTRUE\t1790000000\tLOGIN_INFO\tSECRET_LOGIN_VALUE");

        var log = File.ReadAllText(BridgePaths.LogFile);
        Assert.DoesNotContain("SECRET_LOGIN_VALUE", log, StringComparison.Ordinal);
        Assert.Contains("[INFO ]", log, StringComparison.Ordinal);
    }
}
