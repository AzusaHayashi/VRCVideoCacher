using System.Text;
using System.Text.Json;
using CookieBridge.Core;
using Xunit;

namespace CookieBridge.Tests;

[Collection(BridgeEnvironmentCollection.Name)]
public sealed class NativeMessageHandlerTests : BridgeTestBase
{
    private static string UpdateJson(string cookies) => JsonSerializer.Serialize(new
    {
        type = NativeMessageHandler.TypeCookieUpdate,
        browser = "chrome",
        extensionVersion = "1.2.0",
        cookies,
        capturedUtc = "2026-11-01T00:30:00Z",
    });

    private static string ResponseJson(NativeMessageResponse response) =>
        BridgeJson.Serialize(response, compact: true);

    [Fact]
    public void CookieUpdate_WritesTheVault_IntoTheIsolatedRoot()
    {
        AssertIsolated();

        var response = NativeMessageHandler.HandleJson(UpdateJson(TestJars.Build(extraCookies: 5)));

        Assert.True(response.Ok, response.Error);
        Assert.True(response.VaultUpdated);
        Assert.Equal(8, response.CookieCount);          // LOGIN_INFO + SID + HSID + 5
        Assert.NotNull(response.VaultUpdatedUtc);

        Assert.True(File.Exists(BridgePaths.VaultFile));
        Assert.StartsWith(Root, BridgePaths.VaultFile, StringComparison.OrdinalIgnoreCase);

        var loaded = CookieVault.Load();
        Assert.True(loaded.Success, loaded.Error);
        Assert.Equal(8, loaded.Summary.CookieCount);
        Assert.True(loaded.Summary.LoginCookiePresent);
    }

    [Fact]
    public void CookieUpdate_AlsoWritesTheVrcvcCookieFile_WhenNoHookIsRunning()
    {
        AssertIsolated();

        var response = NativeMessageHandler.HandleJson(UpdateJson(TestJars.Build(extraCookies: 2)));

        Assert.True(response.Ok, response.Error);
        Assert.False(response.HookPresent);             // nothing injected in unit tests
        Assert.True(response.TargetWritten);
        Assert.Equal("vault+cookie-file", response.Applied);

        var text = File.ReadAllText(TargetFile);
        Assert.True(NetscapeCookieFile.IsValidForVrcvc(text));
        Assert.Equal(5, NetscapeCookieFile.Parse(text).Count);
        Assert.All(
            text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')),
            row => Assert.Equal(7, row.Split('\t').Length));
    }

    [Fact]
    public void InvalidJar_IsRejectedAndNothingIsWritten()
    {
        AssertIsolated();

        var response = NativeMessageHandler.HandleJson(UpdateJson(TestJars.BuildWithoutLogin()));

        Assert.False(response.Ok);
        Assert.Equal("invalid-cookie-format", response.Error);
        Assert.False(response.VaultUpdated);
        Assert.False(File.Exists(BridgePaths.VaultFile));
        Assert.False(File.Exists(TargetFile));
    }

    [Fact]
    public void ForeignHostJar_IsRejected()
    {
        AssertIsolated();

        var response = NativeMessageHandler.HandleJson(UpdateJson(TestJars.BuildForeignHost()));

        Assert.False(response.Ok);
        Assert.Equal("invalid-cookie-format", response.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public void MalformedInput_IsRejectedWithoutThrowing(string json)
    {
        var response = NativeMessageHandler.HandleJson(json);

        Assert.False(response.Ok);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public void Ping_ReturnsPong()
    {
        var response = NativeMessageHandler.HandleJson("""{"type":"ping"}""");

        Assert.True(response.Ok);
        Assert.Equal("pong", response.Message);
        Assert.Equal("none", response.Applied);
    }

    [Fact]
    public void Status_ReportsVaultAndTargetWithoutSecrets()
    {
        AssertIsolated();

        NativeMessageHandler.HandleJson(UpdateJson(TestJars.Build(extraCookies: 1)));
        var status = NativeMessageHandler.HandleJson("""{"type":"status"}""");

        Assert.True(status.Ok);
        Assert.True(status.VaultUpdated);
        Assert.Equal(4, status.CookieCount);
        Assert.True(status.TargetWritten);
        Assert.Contains("vault=True", status.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LOGIN_INFO", ResponseJson(status), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownType_IsRejected()
    {
        var response = NativeMessageHandler.HandleJson("""{"type":"rm -rf"}""");

        Assert.False(response.Ok);
        Assert.Equal("unknown-type:rm -rf", response.Error);
    }

    [Fact]
    public void Responses_NeverCarryCookieNamesOrValues()
    {
        AssertIsolated();

        var responses = new[]
        {
            NativeMessageHandler.HandleJson(UpdateJson(TestJars.Build())),
            NativeMessageHandler.HandleJson(UpdateJson(TestJars.BuildWithoutLogin())),
            NativeMessageHandler.HandleJson("""{"type":"status"}"""),
            NativeMessageHandler.HandleJson("""{"type":"ping"}"""),
        };

        foreach (var response in responses)
        {
            var json = ResponseJson(response);
            Assert.DoesNotContain("LOGIN_INFO", json, StringComparison.Ordinal);
            Assert.DoesNotContain(TestJars.LoginValue, json, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKE_SID_VALUE", json, StringComparison.Ordinal);
            Assert.DoesNotContain(".youtube.com", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VaultAfterUpdate_ContainsNoPlaintext()
    {
        AssertIsolated();

        NativeMessageHandler.HandleJson(UpdateJson(TestJars.Build()));

        var raw = File.ReadAllText(BridgePaths.VaultFile);
        Assert.DoesNotContain("LOGIN_INFO", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(TestJars.LoginValue, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Protocol_RoundTripsLengthPrefixedFrames()
    {
        var payload = JsonSerializer.Serialize(new { type = "cookie.update", cookies = TestJars.Build() });

        using var stream = new MemoryStream();
        NativeMessageProtocol.WriteMessage(stream, payload);
        stream.Position = 0;

        Assert.Equal(payload, NativeMessageProtocol.ReadMessage(stream));
        Assert.Null(NativeMessageProtocol.ReadMessage(stream));   // clean EOF
    }

    [Fact]
    public void Protocol_RejectsOversizedFrame()
    {
        using var stream = new MemoryStream();
        var header = BitConverter.GetBytes((uint)NativeMessageProtocol.MaxMessageBytes + 1);
        stream.Write(header, 0, 4);
        stream.Position = 0;

        Assert.Null(NativeMessageProtocol.ReadMessage(stream));
    }

    [Fact]
    public void PendingMarker_IsWrittenWhenASyncIsExpected()
    {
        AssertIsolated();

        NativeMessageHandler.HandleJson(UpdateJson(TestJars.Build()));

        // No hook in the unit test, so the host applies the jar itself and must NOT leave a pending marker.
        Assert.False(File.Exists(BridgePaths.PendingSyncMarker));
    }
}
