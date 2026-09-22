// Defines reusable session filter options and their shared protocol mapping.
using System.CommandLine;
using FiddlerClassic.Protocol;
using static FiddlerClassic.Host.Cli.CommandHelpers;

namespace FiddlerClassic.Host.Cli;

/// <summary>
/// Owns the reusable CLI filter options and maps them into the shared protocol request.
/// </summary>
internal sealed class SessionFilterOptions
{
    private readonly bool _includeIdBounds;
    private readonly Option<int?> _minId = NullableIntOption("--min-id", "Minimum session ID, inclusive.");
    private readonly Option<int?> _maxId = NullableIntOption("--max-id", "Maximum session ID, inclusive.");
    private readonly Option<string?> _method = NullableStringOption("--method", "Exact HTTP method.");
    private readonly Option<string?> _host = NullableStringOption("--host", "Host substring.");
    private readonly Option<string?> _url = NullableStringOption("--url", "Full URL substring.");
    private readonly Option<int?> _status = NullableIntOption("--status", "Exact status code.");
    private readonly Option<string?> _contentType = NullableStringOption("--content-type", "Response content-type substring.");
    private readonly Option<string?> _process = NullableStringOption("--process", "Client process substring.");
    private readonly Option<string?> _headerName = NullableStringOption("--header-name", "Exact request or response header name.");
    private readonly Option<string?> _headerValue = NullableStringOption("--header-value", "Request or response header value substring.");
    private readonly Option<double?> _minDuration = new("--min-duration-ms") { Description = "Minimum duration in milliseconds." };
    private readonly Option<double?> _maxDuration = new("--max-duration-ms") { Description = "Maximum duration in milliseconds." };
    private readonly Option<string?> _protocol = NullableStringOption("--protocol", "Exact HTTP protocol, such as HTTP/1.1.");
    private readonly Option<long?> _minBodyBytes = new("--min-body-bytes") { Description = "Minimum combined body bytes." };
    private readonly Option<long?> _maxBodyBytes = new("--max-body-bytes") { Description = "Maximum combined body bytes." };
    private readonly Option<bool> _errorsOnly = new("--errors-only") { Description = "Include only aborted or HTTP 400+ sessions." };
    private readonly Option<bool> _successfulOnly = new("--successful-only") { Description = "Exclude aborted and HTTP 400+ sessions." };
    private readonly Option<string?> _bodyContains = NullableStringOption("--body-contains", "Exact UTF-8 bytes in a bounded body prefix.");
    private readonly Option<string> _bodyDirection = new("--body-direction")
    {
        Description = "Body search direction: request or response.",
        DefaultValueFactory = _ => BodyDirections.Response
    };
    private readonly Option<int> _bodySearchBytes = new("--body-search-bytes")
    {
        Description = "Maximum body prefix bytes searched per session (1-1048576).",
        DefaultValueFactory = _ => ProtocolConstants.DefaultBodySearchBytes
    };

    /// <summary>
    /// Creates a filter set for bounded inspection or an exclusive-cursor session watch.
    /// </summary>
    /// <param name="includeIdBounds">Whether to expose inclusive ID bounds; watch supplies its own cursor.</param>
    public SessionFilterOptions(bool includeIdBounds = true)
    {
        _includeIdBounds = includeIdBounds;
        _bodyDirection.AcceptOnlyFromAmong(BodyDirections.Request, BodyDirections.Response);
    }

    /// <summary>
    /// Adds the complete filter option set and its mutually exclusive error-mode validator.
    /// </summary>
    /// <param name="command">The list, watch, or HAR export command.</param>
    public void AddTo(Command command)
    {
        if (_includeIdBounds)
        {
            AddOptions(command, _minId, _maxId);
        }

        AddOptions(
            command,
            _method,
            _host,
            _url,
            _status,
            _contentType,
            _process,
            _headerName,
            _headerValue,
            _minDuration,
            _maxDuration,
            _protocol,
            _minBodyBytes,
            _maxBodyBytes,
            _errorsOnly,
            _successfulOnly,
            _bodyContains,
            _bodyDirection,
            _bodySearchBytes);
        command.Validators.Add(result =>
        {
            if (result.GetValue(_errorsOnly) && result.GetValue(_successfulOnly))
            {
                result.AddError("--errors-only and --successful-only cannot be combined.");
            }

            var minId = result.GetValue(_minId);
            var maxId = result.GetValue(_maxId);
            if (_includeIdBounds && (minId < 0 || maxId < 0 || (minId.HasValue && maxId.HasValue && minId > maxId)))
            {
                result.AddError("Session ID bounds are invalid.");
            }

            var minDuration = result.GetValue(_minDuration);
            var maxDuration = result.GetValue(_maxDuration);
            if (minDuration < 0 || maxDuration < 0
                || (minDuration.HasValue && maxDuration.HasValue && minDuration > maxDuration))
            {
                result.AddError("Duration bounds are invalid.");
            }

            var minBody = result.GetValue(_minBodyBytes);
            var maxBody = result.GetValue(_maxBodyBytes);
            if (minBody < 0 || maxBody < 0 || (minBody.HasValue && maxBody.HasValue && minBody > maxBody))
            {
                result.AddError("Body-size bounds are invalid.");
            }

            var bodySearchBytes = result.GetValue(_bodySearchBytes);
            if (bodySearchBytes != 0
                && (bodySearchBytes < 1 || bodySearchBytes > ProtocolConstants.MaxBodySearchBytes))
            {
                result.AddError($"Body search bytes must be between 1 and {ProtocolConstants.MaxBodySearchBytes}.");
            }
        });
    }

    /// <summary>
    /// Maps parsed CLI values to the bridge's reusable filter contract.
    /// </summary>
    /// <param name="parseResult">The command parse result containing this option set.</param>
    public ListSessionsRequest CreateRequest(System.CommandLine.ParseResult parseResult)
    {
        var errorsOnly = parseResult.GetValue(_errorsOnly);
        var successfulOnly = parseResult.GetValue(_successfulOnly);
        return new ListSessionsRequest
        {
            MinId = _includeIdBounds ? parseResult.GetValue(_minId) : null,
            MaxId = _includeIdBounds ? parseResult.GetValue(_maxId) : null,
            Method = parseResult.GetValue(_method),
            Host = parseResult.GetValue(_host),
            UrlContains = parseResult.GetValue(_url),
            StatusCode = parseResult.GetValue(_status),
            ContentType = parseResult.GetValue(_contentType),
            Process = parseResult.GetValue(_process),
            HeaderName = parseResult.GetValue(_headerName),
            HeaderValue = parseResult.GetValue(_headerValue),
            MinDurationMilliseconds = parseResult.GetValue(_minDuration),
            MaxDurationMilliseconds = parseResult.GetValue(_maxDuration),
            Protocol = parseResult.GetValue(_protocol),
            MinBodyBytes = parseResult.GetValue(_minBodyBytes),
            MaxBodyBytes = parseResult.GetValue(_maxBodyBytes),
            IsError = errorsOnly ? true : successfulOnly ? false : null,
            BodyContains = parseResult.GetValue(_bodyContains),
            BodyDirection = parseResult.GetRequiredValue(_bodyDirection),
            BodySearchBytes = parseResult.GetValue(_bodySearchBytes)
        };
    }
}
