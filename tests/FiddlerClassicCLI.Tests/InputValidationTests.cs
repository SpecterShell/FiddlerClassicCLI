// Verifies archive, header, URL, body, and raw-request validation rules.
using System.Text;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class InputValidationTests
{
    [Fact]
    public void ArchivePathRequiresAbsoluteSazPath()
    {
        Assert.False(ArchivePaths.TryValidate("capture.saz", false, out _, out var relativeCode, out _));
        Assert.Equal(ErrorCodes.InvalidRequest, relativeCode);

        var wrongExtension = Path.Combine(Path.GetTempPath(), "capture.zip");
        Assert.False(ArchivePaths.TryValidate(wrongExtension, false, out _, out var extensionCode, out _));
        Assert.Equal(ErrorCodes.InvalidRequest, extensionCode);
    }

    [Fact]
    public void ArchivePathAcceptsExistingDirectoryAndSazExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), "capture.saz");
        Assert.True(ArchivePaths.TryValidate(path, false, out var validated, out _, out _));
        Assert.Equal(Path.GetFullPath(path), validated);
    }

    [Fact]
    public void AutoResponderPathRequiresAbsoluteFarxPath()
    {
        Assert.False(AutoResponderPaths.TryValidate("rules.farx", false, out _, out var relativeCode, out _));
        Assert.Equal(ErrorCodes.InvalidRequest, relativeCode);

        var wrongExtension = Path.Combine(Path.GetTempPath(), "rules.xml");
        Assert.False(AutoResponderPaths.TryValidate(wrongExtension, false, out _, out var extensionCode, out _));
        Assert.Equal(ErrorCodes.InvalidRequest, extensionCode);

        var valid = Path.Combine(Path.GetTempPath(), "rules.farx");
        Assert.True(AutoResponderPaths.TryValidate(valid, false, out var normalized, out _, out _));
        Assert.Equal(Path.GetFullPath(valid), normalized);
    }

    [Fact]
    public void BreakpointMutationInputRejectsInjectionAndOversizedBodies()
    {
        Assert.False(
            HttpInputValidation.TryValidateHeader(
                new HeaderDto { Name = "X-Test", Value = "ok\r\nInjected: yes" },
                out _));
        Assert.False(HttpInputValidation.IsHttpUrl("https://example.test/\r\nInjected"));

        var oversized = Convert.ToBase64String(new byte[ProtocolConstants.MaxComposeBodyBytes + 1]);
        Assert.False(HttpInputValidation.TryDecodeBody(oversized, out _, out _));
        Assert.True(HttpInputValidation.TryDecodeBody(string.Empty, out var empty, out _));
        Assert.Empty(empty!);
    }

    [Fact]
    public void HeaderParserPreservesValueAndRejectsInjection()
    {
        var headers = RequestInput.ParseHeaders(new[] { "Authorization: Bearer secret:part" });
        Assert.Single(headers);
        Assert.Equal("Authorization", headers[0].Name);
        Assert.Equal("Bearer secret:part", headers[0].Value);

        Assert.Throws<ArgumentException>(() => RequestInput.ParseHeaders(new[] { "X-Test: ok\r\nInjected: yes" }));
    }

    /// <summary>
    /// Verifies raw request construction adds host, length, and Fiddler body-encoding headers.
    /// </summary>
    [Fact]
    public void RawRequestBuilderAddsRequiredFiddlerHeaders()
    {
        var body = Encoding.UTF8.GetBytes("hello");
        var request = new SendRequestRequest
        {
            Method = "post",
            Url = "https://example.test/path?q=1",
            Headers = new List<HeaderDto> { new() { Name = "Content-Type", Value = "text/plain" } },
            BodyBase64 = Convert.ToBase64String(body)
        };

        Assert.True(RawRequestBuilder.TryBuild(request, out var raw, out var error), error);
        Assert.StartsWith("POST https://example.test/path?q=1 HTTP/1.1\r\n", raw);
        Assert.Contains("Host: example.test\r\n", raw);
        Assert.Contains("Content-Length: 5\r\n", raw);
        Assert.Contains("Fiddler-Encoding: base64\r\n\r\n", raw);
        Assert.EndsWith(Convert.ToBase64String(body), raw);
    }

    [Theory]
    [InlineData("ftp://example.test/")]
    [InlineData("not a url")]
    public void RawRequestBuilderRejectsUnsupportedUrls(string url)
    {
        var request = new SendRequestRequest { Url = url, Method = "GET" };
        Assert.False(RawRequestBuilder.TryBuild(request, out _, out _));
    }

    /// <summary>
    /// Verifies reserved bridge headers and line-break injection are both rejected.
    /// </summary>
    [Fact]
    public void RawRequestBuilderRejectsReservedAndInjectedHeaders()
    {
        var reserved = new SendRequestRequest
        {
            Url = "https://example.test/",
            Headers = new List<HeaderDto> { new() { Name = "Fiddler-Encoding", Value = "base64" } }
        };
        var injected = new SendRequestRequest
        {
            Url = "https://example.test/",
            Headers = new List<HeaderDto> { new() { Name = "X-Test", Value = "ok\r\nBad: yes" } }
        };

        Assert.False(RawRequestBuilder.TryBuild(reserved, out _, out _));
        Assert.False(RawRequestBuilder.TryBuild(injected, out _, out _));
    }

    [Fact]
    public void BodyValidationEnforcesComposeLimit()
    {
        var oversized = new byte[ProtocolConstants.MaxComposeBodyBytes + 1];
        Assert.Throws<ArgumentException>(() => RequestInput.ValidateBody(Convert.ToBase64String(oversized)));
    }
}
