// Carries stable error codes for managed HTTP configuration and control failures.
namespace FiddlerClassicCLI.Host.Services;

internal sealed class HttpAdministrationException : Exception
{
    public HttpAdministrationException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
