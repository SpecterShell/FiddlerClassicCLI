// Validates archive paths, headers, URLs, bodies, and composed raw requests.
using System.Text;
using System.Text.RegularExpressions;

namespace FiddlerClassicCLI.Protocol;

public static class ArchivePaths
{
    /// <summary>
    /// Validates and normalizes an absolute SAZ path without changing the filesystem.
    /// </summary>
    /// <param name="path">The user-supplied archive path.</param>
    /// <param name="mustExist">Whether the archive file itself must already exist.</param>
    /// <param name="validatedPath">Receives the normalized absolute path on success.</param>
    /// <param name="errorCode">Receives a stable protocol error code on failure.</param>
    /// <param name="errorMessage">Receives an actionable validation message on failure.</param>
    public static bool TryValidate(
        string? path,
        bool mustExist,
        out string validatedPath,
        out string errorCode,
        out string errorMessage)
    {
        validatedPath = string.Empty;
        errorCode = string.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return Fail(ErrorCodes.InvalidRequest, "The archive path must be absolute.", out errorCode, out errorMessage);
        }

        try
        {
            validatedPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            return Fail(ErrorCodes.InvalidRequest, "The archive path is invalid.", out errorCode, out errorMessage);
        }

        if (!string.Equals(Path.GetExtension(validatedPath), ".saz", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(ErrorCodes.InvalidRequest, "The archive path must use the .saz extension.", out errorCode, out errorMessage);
        }

        if (mustExist && !File.Exists(validatedPath))
        {
            return Fail(ErrorCodes.NotFound, $"Archive not found: {validatedPath}", out errorCode, out errorMessage);
        }

        var directory = Path.GetDirectoryName(validatedPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return Fail(ErrorCodes.NotFound, $"Archive directory not found: {directory}", out errorCode, out errorMessage);
        }

        return true;
    }

    private static bool Fail(string code, string message, out string errorCode, out string errorMessage)
    {
        errorCode = code;
        errorMessage = message;
        return false;
    }
}

public static class AutoResponderPaths
{
    /// <summary>
    /// Validates and normalizes an absolute FARX path without changing the filesystem.
    /// </summary>
    /// <param name="path">The user-supplied rule file path.</param>
    /// <param name="mustExist">Whether the rule file itself must already exist.</param>
    /// <param name="validatedPath">Receives the normalized absolute path on success.</param>
    /// <param name="errorCode">Receives a stable protocol error code on failure.</param>
    /// <param name="errorMessage">Receives an actionable validation message on failure.</param>
    public static bool TryValidate(
        string? path,
        bool mustExist,
        out string validatedPath,
        out string errorCode,
        out string errorMessage)
    {
        validatedPath = string.Empty;
        errorCode = string.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return Fail(ErrorCodes.InvalidRequest, "The AutoResponder path must be absolute.", out errorCode, out errorMessage);
        }

        try
        {
            validatedPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            return Fail(ErrorCodes.InvalidRequest, "The AutoResponder path is invalid.", out errorCode, out errorMessage);
        }

        if (!string.Equals(Path.GetExtension(validatedPath), ".farx", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(ErrorCodes.InvalidRequest, "The AutoResponder path must use the .farx extension.", out errorCode, out errorMessage);
        }

        if (mustExist && !File.Exists(validatedPath))
        {
            return Fail(ErrorCodes.NotFound, $"AutoResponder file not found: {validatedPath}", out errorCode, out errorMessage);
        }

        var directory = Path.GetDirectoryName(validatedPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return Fail(ErrorCodes.NotFound, $"AutoResponder directory not found: {directory}", out errorCode, out errorMessage);
        }

        return true;
    }

    private static bool Fail(string code, string message, out string errorCode, out string errorMessage)
    {
        errorCode = code;
        errorMessage = message;
        return false;
    }
}

public static class HttpInputValidation
{
    private static readonly Regex HttpTokenPattern = new Regex(
        "^[!#$%&'*+.^_`|~0-9A-Za-z-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Validates an HTTP method token.
    /// </summary>
    /// <param name="method">The method token to validate.</param>
    public static bool IsMethod(string? method)
    {
        return !string.IsNullOrWhiteSpace(method) && HttpTokenPattern.IsMatch(method);
    }

    /// <summary>
    /// Validates an absolute HTTP or HTTPS URL and rejects line breaks.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    public static bool IsHttpUrl(string? url)
    {
        return url != null
            && url.IndexOfAny(new[] { '\r', '\n' }) < 0
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Validates one exact header name and value pair.
    /// </summary>
    /// <param name="header">The header pair to validate.</param>
    /// <param name="errorMessage">Receives a validation error on failure.</param>
    public static bool TryValidateHeader(HeaderDto? header, out string errorMessage)
    {
        if (header == null || string.IsNullOrWhiteSpace(header.Name) || !HttpTokenPattern.IsMatch(header.Name))
        {
            errorMessage = $"Header name '{header?.Name}' is invalid.";
            return false;
        }

        if (header.Value == null || header.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            errorMessage = $"Header '{header.Name}' contains a line break.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates one header name used for removal.
    /// </summary>
    /// <param name="name">The header name to validate.</param>
    public static bool IsHeaderName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && HttpTokenPattern.IsMatch(name);
    }

    /// <summary>
    /// Decodes a bounded optional base64 body, preserving null as "not supplied."
    /// </summary>
    /// <param name="bodyBase64">The optional encoded body.</param>
    /// <param name="body">Receives decoded bytes when supplied.</param>
    /// <param name="errorMessage">Receives a validation error on failure.</param>
    public static bool TryDecodeBody(string? bodyBase64, out byte[]? body, out string errorMessage)
    {
        body = null;
        errorMessage = string.Empty;
        if (bodyBase64 == null)
        {
            return true;
        }

        try
        {
            body = Convert.FromBase64String(bodyBase64);
        }
        catch (FormatException)
        {
            errorMessage = "The body is not valid base64.";
            return false;
        }

        if (body.Length > ProtocolConstants.MaxComposeBodyBytes)
        {
            errorMessage = $"Bodies are limited to {ProtocolConstants.MaxComposeBodyBytes} bytes.";
            body = null;
            return false;
        }

        return true;
    }
}

public static class RawRequestBuilder
{
    private static readonly Regex HttpTokenPattern = new Regex(
        "^[!#$%&'*+.^_`|~0-9A-Za-z-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Validates a composed request and builds the raw request format accepted by Fiddler.
    /// </summary>
    /// <param name="request">The method, URL, headers, and optional base64 body to compose.</param>
    /// <param name="rawRequest">Receives the complete raw request on success.</param>
    /// <param name="errorMessage">Receives a validation error on failure.</param>
    public static bool TryBuild(
        SendRequestRequest request,
        out string rawRequest,
        out string errorMessage)
    {
        rawRequest = string.Empty;
        errorMessage = string.Empty;

        if (request == null)
        {
            errorMessage = "The request is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Method) || !HttpTokenPattern.IsMatch(request.Method))
        {
            errorMessage = "The HTTP method is invalid.";
            return false;
        }

        Uri? uri;
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errorMessage = "The URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        var headers = request.Headers ?? new List<HeaderDto>();
        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name) || !HttpTokenPattern.IsMatch(header.Name))
            {
                errorMessage = $"Header name '{header.Name}' is invalid.";
                return false;
            }

            if (header.Value == null || header.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                errorMessage = $"Header '{header.Name}' contains a line break.";
                return false;
            }

            if (string.Equals(header.Name, "Fiddler-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "Fiddler-Encoding is reserved by the bridge.";
                return false;
            }
        }

        byte[] body;
        try
        {
            body = request.BodyBase64 == null ? Array.Empty<byte>() : Convert.FromBase64String(request.BodyBase64);
        }
        catch (FormatException)
        {
            errorMessage = "The request body is not valid base64.";
            return false;
        }

        if (body.Length > ProtocolConstants.MaxComposeBodyBytes)
        {
            errorMessage = $"Request bodies are limited to {ProtocolConstants.MaxComposeBodyBytes} bytes.";
            return false;
        }

        var builder = new StringBuilder();
        builder.Append(request.Method.ToUpperInvariant())
            .Append(' ')
            .Append(uri.AbsoluteUri)
            .Append(" HTTP/1.1\r\n");

        if (!headers.Any(header => string.Equals(header.Name, "Host", StringComparison.OrdinalIgnoreCase)))
        {
            builder.Append("Host: ").Append(uri.Authority).Append("\r\n");
        }

        foreach (var header in headers)
        {
            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        if (request.BodyBase64 != null)
        {
            if (!headers.Any(header => string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase)))
            {
                builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            }

            builder.Append("Fiddler-Encoding: base64\r\n");
        }

        builder.Append("\r\n");
        if (request.BodyBase64 != null)
        {
            builder.Append(request.BodyBase64);
        }

        rawRequest = builder.ToString();
        return true;
    }
}
