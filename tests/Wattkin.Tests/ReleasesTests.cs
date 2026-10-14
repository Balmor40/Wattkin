using System.Security.Cryptography;
using Wattkin.Features;
using Xunit;

namespace Wattkin.Tests;

public class ReleasesTests
{
    private static readonly byte[] Exe = [0x4D, 0x5A, 1, 2, 3, 4, 5];

    private static (string PublicKey, string Signature) Signed(Version version, byte[] exe)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = key.SignData(Releases.Statement(version, exe), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(signature));
    }

    [Theory]
    [InlineData("v0.2.0", 0, 2, 0)]
    [InlineData("0.2.1", 0, 2, 1)]
    [InlineData("1.4.0+3f9c2a1", 1, 4, 0)]
    [InlineData("v2.0", 2, 0, 0)]
    public void VersionsParseFromTagsAndFileVersions(string text, int major, int minor, int build)
    {
        Assert.True(Releases.TryParseVersion(text, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    public void NonVersionsAreRejected(string? text) => Assert.False(Releases.TryParseVersion(text, out _));

    [Fact]
    public void AGoodSignatureVerifies()
    {
        var version = new Version(0, 2, 0);
        var (publicKey, signature) = Signed(version, Exe);
        Assert.True(Releases.Verify(version, Exe, signature, publicKey));
    }

    [Fact]
    public void TamperedFilesWrongVersionsAndOtherKeysAreRejected()
    {
        var version = new Version(0, 2, 0);
        var (publicKey, signature) = Signed(version, Exe);
        Assert.False(Releases.Verify(version, [.. Exe, 0], signature, publicKey));           // different bytes
        Assert.False(Releases.Verify(new Version(0, 3, 0), Exe, signature, publicKey));   // replayed as a newer version
        var (otherKey, _) = Signed(version, Exe);
        Assert.False(Releases.Verify(version, Exe, signature, otherKey));                 // someone else's key
        Assert.False(Releases.Verify(version, Exe, "not base64!", publicKey));
    }

    private const string Release = """
        {
          "tag_name": "v0.2.0", "draft": false, "prerelease": false,
          "html_url": "https://github.com/CoppingEthan/Wattkin/releases/tag/v0.2.0",
          "body": "Notes",
          "assets": [
            { "name": "Wattkin.exe", "size": 950000, "browser_download_url": "https://github.com/CoppingEthan/Wattkin/releases/download/v0.2.0/Wattkin.exe" },
            { "name": "Wattkin.exe.sig", "size": 88, "browser_download_url": "https://github.com/CoppingEthan/Wattkin/releases/download/v0.2.0/Wattkin.exe.sig" }
          ]
        }
        """;

    [Fact]
    public void ReadsTheLatestReleaseAnswer()
    {
        var release = Releases.Parse(Release, out var problem);
        Assert.Null(problem);
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 0), release!.Version);
        Assert.Equal(950000, release.ExeSize);
        Assert.EndsWith("Wattkin.exe.sig", release.SignatureUrl.AbsolutePath);
    }

    [Fact]
    public void UnsignedDraftOrInsecureReleasesAreNotOffered()
    {
        Assert.Null(Releases.Parse(Release.Replace("\"Wattkin.exe.sig\"", "\"notes.txt\""), out var unsigned));
        Assert.Contains("signed", unsigned);
        Assert.Null(Releases.Parse(Release.Replace("\"draft\": false", "\"draft\": true"), out _));
        Assert.Null(Releases.Parse(Release.Replace("https://github.com/CoppingEthan/Wattkin/releases/download/v0.2.0/Wattkin.exe\"", "http://example.com/Wattkin.exe\""), out _));
        Assert.Null(Releases.Parse(Release.Replace("950000", "999999999999"), out _));
        Assert.Null(Releases.Parse("{ not json", out _));
    }
}
