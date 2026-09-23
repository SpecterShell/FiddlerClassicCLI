// Projects WebSocket frame metadata and copies bounded payload ranges on Fiddler's UI thread.
using System.Globalization;
using Fiddler;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeDispatcher
{
    /// <summary>
    /// Lists a stable page of WebSocket frame metadata without copying payload bytes.
    /// </summary>
    /// <param name="request">The tunnel session, zero-based offset, and result limit.</param>
    private static ListWebSocketMessagesResponse ListWebSocketMessages(ListWebSocketMessagesRequest request)
    {
        if (request.Offset < 0 || request.Limit < 1 || request.Limit > ProtocolConstants.MaxWebSocketMessageLimit)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"WebSocket offset must be non-negative and limit must be between 1 and {ProtocolConstants.MaxWebSocketMessageLimit}.");
        }

        return FiddlerThread.Invoke(() =>
        {
            var session = FindSession(request.SessionId);
            var messages = GetWebSocket(session).listMessages.ToArray();
            return new ListWebSocketMessagesResponse
            {
                SessionId = session.id,
                TotalMessages = messages.Length,
                Messages = messages.Skip(request.Offset).Take(request.Limit).Select(ToWebSocketSummary).ToList()
            };
        });
    }

    /// <summary>
    /// Copies one bounded byte range from a WebSocket frame payload.
    /// </summary>
    /// <param name="request">The session, message ID, payload offset, and bounded byte count.</param>
    private static WebSocketMessageChunk GetWebSocketMessage(GetWebSocketMessageRequest request)
    {
        if (request.Offset < 0 || request.Length < 1 || request.Length > ProtocolConstants.CliBodyChunkBytes)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"WebSocket payload offset must be non-negative and length must be between 1 and {ProtocolConstants.CliBodyChunkBytes}.");
        }

        return FiddlerThread.Invoke(() =>
        {
            var session = FindSession(request.SessionId);
            var message = GetWebSocket(session).listMessages.FirstOrDefault(candidate => candidate.ID == request.MessageId)
                ?? throw new BridgeOperationException(
                    ErrorCodes.NotFound,
                    $"WebSocket message {request.MessageId} was not found in session {request.SessionId}.");
            var payload = message.PayloadData ?? Array.Empty<byte>();
            if (request.Offset > payload.LongLength)
            {
                throw new BridgeOperationException(
                    ErrorCodes.InvalidRequest,
                    $"Payload offset {request.Offset} exceeds message length {payload.LongLength}.");
            }

            var count = (int)Math.Min(request.Length, payload.LongLength - request.Offset);
            var chunk = new byte[count];
            if (count > 0)
            {
                Buffer.BlockCopy(payload, checked((int)request.Offset), chunk, 0, count);
            }

            var summary = ToWebSocketSummary(message);
            return new WebSocketMessageChunk
            {
                SessionId = session.id,
                MessageId = message.ID,
                Direction = summary.Direction,
                Opcode = summary.Opcode,
                IsFinal = summary.IsFinal,
                Offset = request.Offset,
                BytesReturned = count,
                TotalBytes = payload.LongLength,
                EndOfMessage = request.Offset + count >= payload.LongLength,
                Base64Data = Convert.ToBase64String(chunk),
                TimestampUtc = summary.TimestampUtc
            };
        });
    }

    private static WebSocket GetWebSocket(Session session)
    {
        return session.__oTunnel as WebSocket
            ?? throw new BridgeOperationException(ErrorCodes.NotFound, $"Session {session.id} has no WebSocket messages.");
    }

    private static WebSocketMessageSummary ToWebSocketSummary(WebSocketMessage message)
    {
        var timestamp = message.IsOutbound ? message.Timers.dtBeginSend : message.Timers.dtDoneRead;
        return new WebSocketMessageSummary
        {
            MessageId = message.ID,
            Direction = message.IsOutbound ? WebSocketDirections.Sent : WebSocketDirections.Received,
            Opcode = message.FrameType.ToString().ToLowerInvariant(),
            IsFinal = message.IsFinalFrame,
            IsContinuation = message.FrameType == WebSocketFrameTypes.Continuation,
            WasAborted = message.WasAborted,
            CloseReason = message.FrameType == WebSocketFrameTypes.Close ? message.iCloseReason : (int?)null,
            PayloadLength = message.PayloadLength,
            TimestampUtc = timestamp.Year > 1900
                ? timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : null
        };
    }
}
