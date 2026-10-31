using System.Text;
using CookieBridge.Core;
using Xunit;

namespace CookieBridge.Tests;

[Collection(BridgeEnvironmentCollection.Name)]
public sealed class CookieVaultTests : BridgeTestBase
{
    private static VaultMetadata NewMetadata() => new()
    {
        SourceBrowser = "chrome",
        ExtensionVersion = "1.2.0",
        SourceHost = "youtube.com",
    };

    [Fact]
    public void SaveThenLoad_RoundTripsExactCanonicalJar()
    {
        AssertIsolated();

        var jar = TestJars.Build(extraCookies: 4);
        var expected = NetscapeCookieFile.Canonicalize(jar);

        var saved = CookieVault.Save(jar, NewMetadata());
        var loaded = CookieVault.Load();

        Assert.True(saved.Success, saved.Error);
        Assert.True(loaded.Success, loaded.Error);
        Assert.Equal(expected, loaded.CookiesText);
        Assert.True(File.Exists(BridgePaths.VaultFile));

        var summary = NetscapeCookieFile.Summarize(expected);
        Assert.Equal(summary.CookieCount, loaded.Summary.CookieCount);
        Assert.True(loaded.Summary.LoginCookiePresent);
        Assert.Equal(summary.Sha256, loaded.Document!.PayloadSha256);
        Assert.Equal(CookieVault.CurrentFormatVersion, loaded.Document.FormatVersion);
        Assert.Equal("dpapi-current-user", loaded.Document.Protection);
        Assert.Equal("base64", loaded.Document.PayloadEncoding);
    }

    [Fact]
    public void Save_WritesNoPlaintextIntoTheVaultFile()
    {
        AssertIsolated();

        var jar = TestJars.Build();
        Assert.True(CookieVault.Save(jar, NewMetadata()).Success);

        var rawJson = File.ReadAllText(BridgePaths.VaultFile);
        var document = CookieVault.ReadDocument()!;

        // 1. the base64 payload itself must not contain a cookie name
        Assert.DoesNotContain("LOGIN_INFO", document.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE_SID_VALUE", document.Payload, StringComparison.Ordinal);

        // 2. nor may the *decoded ciphertext* contain one (DPAPI output must be opaque)
        var cipherBytes = Convert.FromBase64String(document.Payload);
        var cipherAscii = Encoding.Latin1.GetString(cipherBytes);
        Assert.DoesNotContain("LOGIN_INFO", cipherAscii, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE_SID_VALUE", cipherAscii, StringComparison.Ordinal);

        // 3. the whole on-disk document carries only non-sensitive metadata
        Assert.DoesNotContain("LOGIN_INFO", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKE_SID_VALUE", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(".youtube.com", rawJson, StringComparison.Ordinal);

        // 4. but metadata is still useful
        Assert.Equal("chrome", document.Metadata.SourceBrowser);
        Assert.True(document.Metadata.CookieCount > 0);
        Assert.True(document.Metadata.LoginCookiePresent);
    }

    [Fact]
    public void TamperedPayloadSha256_IsRejectedAsHashMismatch()
    {
        AssertIsolated();

        Assert.True(CookieVault.Save(TestJars.Build(), NewMetadata()).Success);

        var document = CookieVault.ReadDocument()!;
        document.PayloadSha256 = new string('0', 64);
        AtomicFile.WriteAllText(BridgePaths.VaultFile, BridgeJson.Serialize(document));

        var loaded = CookieVault.Load();

        Assert.False(loaded.Success);
        Assert.Equal("hash-mismatch", loaded.Error);
        Assert.Null(loaded.CookiesText);
    }

    [Fact]
    public void TamperedCiphertext_IsRejected()
    {
        AssertIsolated();

        Assert.True(CookieVault.Save(TestJars.Build(), NewMetadata()).Success);

        var document = CookieVault.ReadDocument()!;
        var bytes = Convert.FromBase64String(document.Payload);
        bytes[bytes.Length / 2] ^= 0xFF;                       // corrupt the DPAPI blob
        document.Payload = Convert.ToBase64String(bytes);
        AtomicFile.WriteAllText(BridgePaths.VaultFile, BridgeJson.Serialize(document));

        var loaded = CookieVault.Load();

        Assert.False(loaded.Success);
        Assert.NotNull(loaded.Error);
        // A DPAPI blob carries an integrity MAC, so the expected failure is dpapi-failed; a hash mismatch
        // would be equally acceptable proof that the corruption was detected.
        Assert.Contains(loaded.Error!, new[] { "dpapi-failed", "hash-mismatch" });
    }

    [Fact]
    public void WrongEntropy_CannotDecrypt_SecretProtector()
    {
        var plaintext = Encoding.UTF8.GetBytes("LOGIN_INFO\tFAKE\n");
        var blob = SecretProtector.Protect(plaintext, BridgePaths.VaultEntropy);

        // Sanity: the right entropy works.
        Assert.Equal(plaintext, SecretProtector.Unprotect(blob, BridgePaths.VaultEntropy));

        // Any other entropy must not.
        var ex = Record.Exception(() => SecretProtector.Unprotect(blob, "VRCVideoCacher-CookieBridge/vault/v2"));
        Assert.NotNull(ex);
    }

    [Fact]
    public void WrongEntropyVault_CannotBeLoaded()
    {
        AssertIsolated();

        Assert.True(CookieVault.Save(TestJars.Build(), NewMetadata()).Success);

        // Re-encrypt the canonical jar with a wrong entropy and plant it as the vault payload.
        var document = CookieVault.ReadDocument()!;
        var canonical = NetscapeCookieFile.Canonicalize(TestJars.Build());
        document.Payload = Convert.ToBase64String(
            SecretProtector.Protect(Encoding.UTF8.GetBytes(canonical), "not-the-vault-entropy"));
        AtomicFile.WriteAllText(BridgePaths.VaultFile, BridgeJson.Serialize(document));

        var loaded = CookieVault.Load();

        Assert.False(loaded.Success);
        Assert.Equal("dpapi-failed", loaded.Error);
    }

    [Fact]
    public void UnsupportedFormatVersion_IsReported()
    {
        AssertIsolated();

        Assert.True(CookieVault.Save(TestJars.Build(), NewMetadata()).Success);

        var document = CookieVault.ReadDocument()!;
        document.FormatVersion = 2;
        AtomicFile.WriteAllText(BridgePaths.VaultFile, BridgeJson.Serialize(document));

        var loaded = CookieVault.Load();

        Assert.False(loaded.Success);
        Assert.NotNull(loaded.Error);
        Assert.StartsWith("unsupported-format", loaded.Error!, StringComparison.Ordinal);
        Assert.Equal("unsupported-format-2", loaded.Error);
    }

    [Fact]
    public void Load_WithoutVault_ReportsVaultMissing()
    {
        AssertIsolated();

        var loaded = CookieVault.Load();

        Assert.False(loaded.Success);
        Assert.Equal("vault-missing", loaded.Error);
    }

    [Fact]
    public void Save_RejectsJarWithoutLoginCookie()
    {
        AssertIsolated();

        var result = CookieVault.Save(TestJars.BuildWithoutLogin(), NewMetadata());

        Assert.False(result.Success);
        Assert.Equal("invalid-cookie-format", result.Error);
        Assert.False(File.Exists(BridgePaths.VaultFile));
    }

    [Fact]
    public void Save_RejectsJarForForeignHost()
    {
        AssertIsolated();

        var result = CookieVault.Save(TestJars.BuildForeignHost(), NewMetadata());

        Assert.False(result.Success);
        Assert.Equal("invalid-cookie-format", result.Error);
    }

    [Fact]
    public void Save_PreservesCreatedUtc_AndAdvancesUpdatedUtc()
    {
        AssertIsolated();

        var first = CookieVault.Save(TestJars.Build(), NewMetadata());
        var createdUtc = first.Document!.CreatedUtc;

        Thread.Sleep(15);
        var second = CookieVault.Save(TestJars.Build(extraCookies: 6), NewMetadata());

        Assert.Equal(createdUtc, second.Document!.CreatedUtc);
        Assert.True(second.Document.UpdatedUtc >= first.Document.UpdatedUtc);
        Assert.NotEqual(first.Document.PayloadSha256, second.Document.PayloadSha256);
    }

    [Fact]
    public void ReadDocument_DoesNotDecrypt_AndLastUpdatedReflectsFile()
    {
        AssertIsolated();

        Assert.True(CookieVault.Save(TestJars.Build(), NewMetadata()).Success);

        var document = CookieVault.ReadDocument();
        Assert.NotNull(document);
        Assert.NotNull(CookieVault.LastUpdatedUtc());

        CookieVault.Delete();
        Assert.Null(CookieVault.ReadDocument());
        Assert.Null(CookieVault.LastUpdatedUtc());
    }

    [Fact]
    public void MetadataIsFilledFromTheJar_NotFromTheCaller()
    {
        AssertIsolated();

        var metadata = new VaultMetadata { SourceBrowser = "firefox", CookieCount = 999, LoginCookiePresent = false };
        var result = CookieVault.Save(TestJars.Build(extraCookies: 1), metadata);

        Assert.True(result.Success, result.Error);
        Assert.Equal(4, result.Document!.Metadata.CookieCount);   // LOGIN_INFO + SID + HSID + 1
        Assert.True(result.Document.Metadata.LoginCookiePresent);
        Assert.Equal("firefox", result.Document.Metadata.SourceBrowser);
    }
}
