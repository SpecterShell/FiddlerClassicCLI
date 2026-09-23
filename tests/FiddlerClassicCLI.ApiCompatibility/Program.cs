// Resolves shipped bridge IL references against native metadata without executing any Fiddler or bridge code.
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;

namespace FiddlerClassicCLI.ApiCompatibility;

internal static class Program
{
    private static readonly Dictionary<short, OpCode> Instructions = typeof(OpCodes).GetFields()
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    /// <summary>Checks a release DLL's identity and resolves all metadata-bearing IL operands without invocation.</summary>
    /// <param name="args">Bridge DLL path, Fiddler EXE path, exact native version, and expected bridge SHA-256.</param>
    private static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Expected bridge DLL, native EXE, exact native version, and bridge SHA-256.");
            return 2;
        }
        var stage = "validate inputs";
        try
        {
            var bridgePath = Path.GetFullPath(args[0]);
            var nativePath = Path.GetFullPath(args[1]);
            if (FileVersionInfo.GetVersionInfo(nativePath).FileVersion != args[2])
                throw new InvalidOperationException("Native version mismatch.");
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(bridgePath))
                if (!BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty)
                        .Equals(args[3], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Bridge digest mismatch.");

            stage = "reflection-only assembly loading";
            // Reflection-only loading never runs assembly initializers, constructors, or methods.
            var native = Assembly.ReflectionOnlyLoadFrom(nativePath);
            var directories = new[] { Path.GetDirectoryName(bridgePath)!, Path.GetDirectoryName(nativePath)! };
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += (_, request) =>
            {
                var name = new AssemblyName(request.Name);
                if (name.Name == native.GetName().Name) return native;
                foreach (var directory in directories)
                {
                    var path = Path.Combine(directory, name.Name + ".dll");
                    if (File.Exists(path)) return Assembly.ReflectionOnlyLoadFrom(path);
                }
                return Assembly.ReflectionOnlyLoad(request.Name);
            };
            var bridge = Assembly.ReflectionOnlyLoadFrom(bridgePath);
            var members = 0;
            var nativeMembers = 0;
            stage = "resolve direct API references";
            foreach (var type in bridge.GetTypes())
            {
                var typeArguments = type.GetGenericArguments();
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly;
                foreach (var method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
                {
                    var code = method.GetMethodBody()?.GetILAsByteArray();
                    if (code is null) continue;
                    var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
                    foreach (var token in ReadMemberTokens(code))
                    {
                        var member = method.Module.ResolveMember(token, typeArguments, methodArguments);
                        if (member is null) throw new MissingMemberException();
                        members++;
                        if (member.Module.Assembly == native) nativeMembers++;
                    }
                }
            }
            if (nativeMembers == 0) throw new InvalidOperationException("No native member references were checked.");
            Console.WriteLine("PASS reflection-only API linkage on Fiddler " + args[2] + ": " + members +
                " IL member operands, including " + nativeMembers + " native operands. Shipped SHA-256 " + args[3]);
            Console.WriteLine("Native code was not executed. Binding policy, reflection-by-name, UI behavior, and lifecycle require the opt-in native probe.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + stage + ": " + exception.GetType().Name);
            return 1;
        }
    }

    /// <summary>Parses managed IL operand widths and returns metadata tokens which refer to types or members.</summary>
    /// <param name="code">One method body from the shipped bridge, never executed by this process.</param>
    private static IEnumerable<int> ReadMemberTokens(byte[] code)
    {
        var position = 0;
        while (position < code.Length)
        {
            var first = code[position++];
            var value = first == 0xfe ? unchecked((short)(0xfe00 | code[position++])) : (short)first;
            var instruction = Instructions[value];
            int length;
            switch (instruction.OperandType)
            {
                case OperandType.InlineNone: length = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: length = 1; break;
                case OperandType.InlineVar: length = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: length = 8; break;
                case OperandType.InlineSwitch:
                    length = checked(4 + 4 * BitConverter.ToInt32(code, position));
                    break;
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    yield return BitConverter.ToInt32(code, position);
                    length = 4;
                    break;
                default: length = 4; break;
            }
            if (length < 0 || length > code.Length - position) throw new BadImageFormatException();
            position += length;
        }
    }
}
