// Verifies installation discovery, path validation, and supported Fiddler Classic versions.
using FiddlerClassic.Host.Services;

namespace FiddlerClassic.Tests;

public sealed class FiddlerEnvironmentTests
{
    [Theory]
    [InlineData("5.0.20262.6151", true)]
    [InlineData("6.0.20261.7291", true)]
    [InlineData("4.6.20171.26113", false)]
    [InlineData("7.0.0.0", false)]
    [InlineData("invalid", false)]
    [InlineData(null, false)]
    public void RecognizesSupportedMajorVersions(string? version, bool expected)
    {
        Assert.Equal(expected, FiddlerEnvironment.IsSupportedVersion(version));
    }

    [Fact]
    public void NormalizesDefaultAndQuotedRegistryCandidatesAndPreservesOrder()
    {
        var paths = new[]
        {
            @"C:\Users\Test\AppData\Local\Programs\Fiddler\Fiddler.exe",
            @"C:\Program Files\Fiddler2\Fiddler.exe",
            @"C:\Program Files (x86)\Fiddler2\Fiddler.exe",
            "  \"D:/Tools/Old/../Fiddler/Fiddler.exe\"  "
        };
        var expected = new[] { paths[0], paths[1], paths[2], @"D:\Tools\Fiddler\Fiddler.exe" };
        var probes = new List<string>();

        var installations = FiddlerEnvironment.FindInstallationsFromCandidates(paths, path =>
        {
            probes.Add(path);
            return true;
        }, _ => "6.0.20261.7291");

        Assert.Equal(expected, probes);
        Assert.Equal(expected, installations.Select(installation => installation.ExecutablePath));
        Assert.All(installations, installation =>
        {
            Assert.Equal("6.0.20261.7291", installation.Version);
            Assert.True(installation.Supported);
        });
    }

    [Fact]
    public void DeduplicatesNormalizedPathsCaseInsensitivelyBeforeReadingFiles()
    {
        var probes = new List<string>();
        var reads = new List<string>();
        var installations = FiddlerEnvironment.FindInstallationsFromCandidates(
            [@"C:\Tools\Fiddler.exe", @"c:\TOOLS\.\FIDDLER.EXE", "\"C:/Tools/Fiddler.exe\""],
            path =>
            {
                probes.Add(path);
                return true;
            },
            path =>
            {
                reads.Add(path);
                return "5.0.20262.6151";
            });

        Assert.Equal(@"C:\Tools\Fiddler.exe", Assert.Single(installations).ExecutablePath);
        Assert.Single(probes);
        Assert.Single(reads);
    }

    [Theory]
    [InlineData("4.6.20171.26113")]
    [InlineData("7.0.0.0")]
    [InlineData("invalid")]
    [InlineData(null)]
    public void RetainsExistingUnsupportedInstallationsForDiagnostics(string? version)
    {
        var installations = FiddlerEnvironment.FindInstallationsFromCandidates(
            [@"C:\Tools\Fiddler.exe"], _ => true, _ => version);

        var installation = Assert.Single(installations);
        Assert.Equal(version, installation.Version);
        Assert.False(installation.Supported);
    }

    [Fact]
    public void SkipsMissingFilesWithoutReadingTheirVersions()
    {
        var installations = FiddlerEnvironment.FindInstallationsFromCandidates(
            [@"C:\Missing\Fiddler.exe"], _ => false,
            _ => throw new InvalidOperationException("Missing files must not be read."));

        Assert.Empty(installations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitPathNeverEnumeratesFallbackCandidates(bool exists)
    {
        var probes = new List<string>();
        var installations = FiddlerEnvironment.FindInstallationsFromCandidates(
            UnexpectedCandidates(),
            path =>
            {
                probes.Add(path);
                return exists;
            },
            _ => exists ? "5.0.20262.6151" : throw new InvalidOperationException("Missing files must not be read."),
            @"D:\Custom\Old\..\FIDDLER.EXE");

        Assert.Equal(new[] { @"D:\Custom\FIDDLER.EXE" }, probes);
        if (exists)
        {
            Assert.True(Assert.Single(installations).Supported);
        }
        else
        {
            Assert.Empty(installations);
        }
    }

    [Fact]
    public void MissingExplicitPathReturnsEmpty()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Fiddler.exe");

        Assert.Empty(new FiddlerEnvironment().FindInstallations(missingPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Fiddler.exe")]
    [InlineData(@"tools\Fiddler.exe")]
    [InlineData(@"C:Fiddler.exe")]
    [InlineData(@"\Tools\Fiddler.exe")]
    [InlineData(@"C:\Tools\Other.exe")]
    [InlineData(@"C:\Tools\Fiddler.exe\")]
    [InlineData("\"C:\\Tools\\Fiddler.exe\"")]
    [InlineData(@"C:\Bad*\Fiddler.exe")]
    [InlineData(@"C:\Bad:Folder\Fiddler.exe")]
    [InlineData("C:\\Bad\0\\Fiddler.exe")]
    public void RejectsMalformedExplicitPaths(string path)
    {
        var exception = Assert.Throws<ArgumentException>(() => new FiddlerEnvironment().FindInstallations(path));

        Assert.Equal("explicitPath", exception.ParamName);
    }

    [Fact]
    public void IgnoresMalformedDiscoveryCandidates()
    {
        var installations = FiddlerEnvironment.FindInstallationsFromCandidates(
            [null, "", " ", "Fiddler.exe", @"C:\Tools\Other.exe", "\"C:\\Tools\\Fiddler.exe\" --argument",
                @"C:\Good\Fiddler.exe"],
            path =>
            {
                Assert.Equal(@"C:\Good\Fiddler.exe", path);
                return true;
            },
            _ => "6.0.20261.7291");

        Assert.Single(installations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableCandidateDoesNotPreventFurtherDiscovery(bool failsDuringVersionRead)
    {
        var installations = FiddlerEnvironment.FindInstallationsFromCandidates(
            [@"C:\Unreadable\Fiddler.exe", @"C:\Good\Fiddler.exe"],
            path => failsDuringVersionRead || path == @"C:\Good\Fiddler.exe"
                ? true : throw new UnauthorizedAccessException(),
            path => path == @"C:\Good\Fiddler.exe" ? "6.0.20261.7291" : throw new IOException());

        Assert.Equal(@"C:\Good\Fiddler.exe", Assert.Single(installations).ExecutablePath);
    }

    private static IEnumerable<string?> UnexpectedCandidates()
    {
        return Enumerable.Range(0, 1).Select<int, string?>(
            _ => throw new InvalidOperationException("Explicit paths must not enumerate discovery candidates."));
    }
}
