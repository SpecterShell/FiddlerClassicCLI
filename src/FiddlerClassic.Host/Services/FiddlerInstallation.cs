// Describes an existing Fiddler executable and its file-version compatibility.
namespace FiddlerClassic.Host.Services;

internal sealed record FiddlerInstallation(string ExecutablePath, string? Version, bool Supported);
