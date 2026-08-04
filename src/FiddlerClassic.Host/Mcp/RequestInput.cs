// Validates and converts MCP request headers and bodies into bridge inputs.
using System.Text.RegularExpressions;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Mcp;

internal static partial class RequestInput
{
    /// <summary>
    /// Parses repeated CLI-style headers while rejecting malformed names and line-break injection.
    /// </summary>
    /// <param name="values">The raw <c>Name: value</c> inputs in caller order.</param>
    public static List<HeaderDto> ParseHeaders(IEnumerable<string> values)
    {
        var headers = new List<HeaderDto>();
        foreach (var value in values)
        {
            if (value.Contains('\r') || value.Contains('\n'))
            {
                throw new ArgumentException("Header values cannot contain CR or LF characters.");
            }

            var separator = value.IndexOf(':');
            if (separator <= 0)
            {
                throw new ArgumentException($"Header '{value}' must use the format 'Name: value'.");
            }

            var name = value[..separator].Trim();
            var headerValue = value[(separator + 1)..].TrimStart();
            if (!HeaderNamePattern().IsMatch(name))
            {
                throw new ArgumentException($"Header name '{name}' is invalid.");
            }

            headers.Add(new HeaderDto { Name = name, Value = headerValue });
        }

        return headers;
    }

    /// <summary>
    /// Verifies an optional body is valid base64 and within the compose size limit.
    /// </summary>
    /// <param name="bodyBase64">The optional base64-encoded request body.</param>
    public static void ValidateBody(string? bodyBase64)
    {
        if (bodyBase64 is null)
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(bodyBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The request body is not valid base64.", nameof(bodyBase64), exception);
        }

        if (bytes.Length > ProtocolConstants.MaxComposeBodyBytes)
        {
            throw new ArgumentException($"The request body cannot exceed {ProtocolConstants.MaxComposeBodyBytes} bytes.");
        }
    }

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderNamePattern();
}
