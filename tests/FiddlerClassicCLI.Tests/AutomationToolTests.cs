// Verifies AutoResponder and breakpoint MCP forwarding and safety annotations.
using System.Reflection;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Protocol;
using ModelContextProtocol.Server;

namespace FiddlerClassicCLI.Tests;

public sealed class AutomationToolTests
{
    [Fact]
    public void AutoResponderToolsExposeApprovedSafetyAnnotations()
    {
        var tools = GetTools(typeof(AutoResponderTools));
        Assert.Equal(
            new[]
            {
                "add_autoresponder_rule", "clear_autoresponder_rules", "configure_autoresponder",
                "get_autoresponder_status", "list_autoresponder_rules", "load_autoresponder_rules",
                "move_autoresponder_rule", "remove_autoresponder_rule", "save_autoresponder_rules",
                "update_autoresponder_rule"
            }.Order(),
            tools.Keys.Order());
        Assert.True(tools["get_autoresponder_status"].ReadOnly);
        Assert.True(tools["add_autoresponder_rule"].OpenWorld);
        Assert.True(tools["clear_autoresponder_rules"].Destructive);
        Assert.True(tools["load_autoresponder_rules"].Destructive);
        Assert.All(tools.Values, attribute => Assert.True(attribute.UseStructuredContent));
    }

    [Fact]
    public void BreakpointToolsExposeApprovedSafetyAnnotations()
    {
        var tools = GetTools(typeof(BreakpointTools));
        Assert.Equal(
            new[]
            {
                "abort_network_breakpoint", "arm_network_breakpoint", "disarm_network_breakpoint",
                "get_network_breakpoint", "list_network_breakpoint_arms", "list_pending_network_breakpoints",
                "resume_network_breakpoint", "update_network_breakpoint", "wait_for_network_breakpoint"
            }.Order(),
            tools.Keys.Order());
        Assert.True(tools["list_pending_network_breakpoints"].ReadOnly);
        Assert.True(tools["arm_network_breakpoint"].OpenWorld);
        Assert.True(tools["update_network_breakpoint"].Destructive);
        Assert.True(tools["abort_network_breakpoint"].Destructive);
        Assert.All(tools.Values, attribute => Assert.True(attribute.UseStructuredContent));
    }

    [Fact]
    public async Task AutoResponderRuleToolForwardsExactActionAndMetadata()
    {
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                Assert.Equal(Operations.AddAutoResponderRule, operation);
                var add = Assert.IsType<AddAutoResponderRuleRequest>(request);
                Assert.Equal("REGEX:(?i)example", add.Match);
                Assert.Equal("*drop", add.Action);
                Assert.Equal("test", add.Comment);
                Assert.True(add.DisableOnMatch);
                Assert.Equal(75, add.LatencyMilliseconds);
                return new AutoResponderMutationResponse { AffectedCount = 1 };
            }
        };

        var result = await new AutoResponderTools(bridge).AddRule(
            "REGEX:(?i)example",
            "*drop",
            comment: "test",
            disableOnMatch: true,
            latencyMs: 75,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.AffectedCount);
    }

    [Fact]
    public async Task AutoResponderUpdateAppliesASuppliedCommentWithoutAnExtraSwitch()
    {
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                Assert.Equal(Operations.UpdateAutoResponderRule, operation);
                var update = Assert.IsType<UpdateAutoResponderRuleRequest>(request);
                Assert.Equal("changed", update.Comment);
                Assert.True(update.SetComment);
                return new AutoResponderMutationResponse();
            }
        };

        await new AutoResponderTools(bridge).UpdateRule(
            "rule1",
            comment: "changed",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BreakpointArmToolForwardsStageFiltersAndTimeout()
    {
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                Assert.Equal(Operations.ArmBreakpoint, operation);
                var arm = Assert.IsType<ArmBreakpointRequest>(request);
                Assert.Equal(BreakpointStages.Response, arm.Stage);
                Assert.Equal("example.test", arm.Filter.Host);
                Assert.Equal(503, arm.Filter.StatusCode);
                Assert.False(arm.OneShot);
                Assert.Equal(45000, arm.HoldMilliseconds);
                return new BreakpointArmMutationResponse();
            }
        };

        await new BreakpointTools(bridge).Arm(
            BreakpointStages.Response,
            host: "example.test",
            statusCode: 503,
            oneShot: false,
            holdMs: 45000,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BreakpointUpdateToolValidatesAndForwardsExactMutation()
    {
        var body = Convert.ToBase64String(new byte[] { 0, 1, 2 });
        var bridge = new TestBridgeClient
        {
            Handler = (operation, request) =>
            {
                Assert.Equal(Operations.UpdateBreakpoint, operation);
                var update = Assert.IsType<UpdateBreakpointRequest>(request);
                Assert.Equal("bp1", update.BreakpointId);
                Assert.Equal("https://example.test/changed", update.Url);
                Assert.Equal("yes", Assert.Single(update.SetHeaders).Value);
                Assert.Equal("X-Old", Assert.Single(update.RemoveHeaders));
                Assert.Equal(body, update.BodyBase64);
                return new BreakpointDetailsResponse();
            }
        };

        await new BreakpointTools(bridge).Update(
            "bp1",
            url: "https://example.test/changed",
            setHeaders: new[] { "X-Test: yes" },
            removeHeaders: new[] { "X-Old" },
            bodyBase64: body,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, McpServerToolAttribute> GetTools(Type type)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute != null)
            .ToDictionary(attribute => attribute!.Name!, attribute => attribute!);
    }
}
