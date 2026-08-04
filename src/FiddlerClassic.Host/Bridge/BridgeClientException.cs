// Represents a stable transport or operation failure observed by bridge clients.
namespace FiddlerClassic.Host.Bridge;

internal sealed class BridgeClientException : Exception
{
    public BridgeClientException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
