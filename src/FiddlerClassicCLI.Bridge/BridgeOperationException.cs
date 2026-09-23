// Represents a stable operation failure returned by the in-process Fiddler bridge.
namespace FiddlerClassicCLI.Bridge;

internal sealed class BridgeOperationException : Exception
{
    public BridgeOperationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
