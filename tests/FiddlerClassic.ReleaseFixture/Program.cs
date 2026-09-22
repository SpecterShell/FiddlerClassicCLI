// Supplies a version-only prior-install fixture without loading host services or user configuration.
using System.Reflection;

if (args.Length != 1 || args[0] != "--version")
{
    Console.Error.WriteLine("The installer fixture only accepts --version.");
    return 1;
}

Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
    .InformationalVersion);
return 0;
