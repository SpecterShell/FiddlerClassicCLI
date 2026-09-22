// Keeps the CLI-only skill inventory aligned with the public non-MCP command tree and size limits.
using System.CommandLine;
using System.Text.RegularExpressions;
using FiddlerClassic.Host.Cli;
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Host.Services;

namespace FiddlerClassic.Tests;

public sealed class SkillDocumentationTests : IDisposable
{
    private readonly string _configDirectory = Path.Combine(
        Path.GetTempPath(),
        "FiddlerClassicTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CoversExactlyThePublicNonMcpLeafCommands()
    {
        var repositoryRoot = FindRepositoryRoot();
        var references = Directory.GetFiles(
            Path.Combine(repositoryRoot, "skills", "fiddler-classic-cli", "references"),
            "*.md");
        var documented = references
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "^## `(?<command>[^`]+)`$", RegexOptions.Multiline))
            .Select(match => Regex.Replace(match.Groups["command"].Value, "\\s*<[^>]+>", string.Empty))
            .ToHashSet(StringComparer.Ordinal);
        var commandTree = CreateRoot();
        var publicLeaves = commandTree.Subcommands
            .Where(command => command.Name is not "mcp" and not "config")
            .SelectMany(command => EnumerateLeaves(command, string.Empty))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(47, documented.Count);
        Assert.Equal(47, publicLeaves.Count);
        Assert.Equal(publicLeaves.OrderBy(value => value), documented.OrderBy(value => value));
    }

    [Fact]
    public void KeepsSkillFilesSmallAndFreeOfMcpGuidance()
    {
        var skillDirectory = Path.Combine(FindRepositoryRoot(), "skills", "fiddler-classic-cli");
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        var references = Directory.GetFiles(Path.Combine(skillDirectory, "references"), "*.md");

        Assert.True(File.ReadLines(skillPath).Count() <= 120);
        Assert.Equal(5, references.Length);
        Assert.All(references, path => Assert.True(File.ReadLines(path).Count() <= 150, path));
        Assert.DoesNotContain("mcp", File.ReadAllText(skillPath), StringComparison.OrdinalIgnoreCase);
        Assert.All(references, path =>
            Assert.DoesNotContain("mcp", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(Path.Combine(skillDirectory, "scripts")));
    }

    private RootCommand CreateRoot()
    {
        var bridge = new TestBridgeClient();
        var environment = new FiddlerEnvironment();
        var config = new ConfigStore(_configDirectory);
        var actions = new CliActions(
            bridge,
            new BridgeInstaller(environment),
            config,
            environment,
            new StatusService(bridge, environment));
        var daemon = new DaemonClient(
            "unused.exe",
            $"fiddler-classic-cli.skill-tests.{Guid.NewGuid():N}",
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100));
        return CommandFactory.Create(actions, daemon);
    }

    private static IEnumerable<string> EnumerateLeaves(Command command, string prefix)
    {
        var path = string.IsNullOrEmpty(prefix) ? command.Name : $"{prefix} {command.Name}";
        var children = command.Subcommands.Where(child => !child.Hidden).ToArray();
        if (children.Length == 0)
        {
            yield return path;
            yield break;
        }

        foreach (var leaf in children.SelectMany(child => EnumerateLeaves(child, path)))
        {
            yield return leaf;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FiddlerClassicCLI.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The Fiddler Classic CLI repository root was not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
    }
}
