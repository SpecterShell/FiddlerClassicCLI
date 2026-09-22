// Verifies pure dispatcher validation and evidence projections without constructing a live Fiddler dispatcher.
using System.Reflection;
using FiddlerClassic.Bridge;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Bridge.Tests;

public sealed class BridgeDispatcherProjectionTests
{
    [Fact]
    public void HeaderDiffPreservesDuplicateValueOrder()
    {
        var result = DiffHeaders(
            new[] { Header("Set-Cookie", "first=1"), Header("Set-Cookie", "second=2") },
            new[] { Header("Set-Cookie", "second=2"), Header("Set-Cookie", "first=1") });

        var difference = Assert.Single(result.Differences);
        Assert.Equal("response-header", difference.Area);
        Assert.Equal("Set-Cookie", difference.Name);
        Assert.Equal(new[] { "first=1", "second=2" }, difference.LeftValues);
        Assert.Equal(new[] { "second=2", "first=1" }, difference.RightValues);
    }

    [Fact]
    public void HeaderDiffReportsOrderAndNameCasingChanges()
    {
        var result = DiffHeaders(
            new[] { Header("X-First", "1"), Header("X-Second", "2") },
            new[] { Header("x-second", "2"), Header("X-First", "1") });

        var difference = Assert.Single(result.Differences);
        Assert.Equal("ordered-sequence", difference.Name);
        Assert.Equal(new[] { "X-First: 1", "X-Second: 2" }, difference.LeftValues);
        Assert.Equal(new[] { "x-second: 2", "X-First: 1" }, difference.RightValues);
    }

    [Fact]
    public void IdenticalOrderedHeadersHaveNoDifferences()
    {
        var headers = new[] { Header("X-Repeated", "  original  "), Header("X-Repeated", "second") };

        Assert.Empty(DiffHeaders(headers, headers).Differences);
    }

    [Fact]
    public void HeaderDiffPreservesMissingValues()
    {
        var result = DiffHeaders(new[] { Header("X-Removed", "original") }, Array.Empty<HeaderDto>());

        var difference = Assert.Single(result.Differences);
        Assert.Equal(new[] { "original" }, difference.LeftValues);
        Assert.Empty(difference.RightValues);
    }

    [Fact]
    public void BodyHashUsesExactBytesAndTreatsMissingBodyAsEmpty()
    {
        var hash = Helper<Func<byte[]?, string>>("ComputeSha256");

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash(new byte[] { 97, 98, 99 }));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash(null));
        Assert.Equal(hash(Array.Empty<byte>()), hash(null));
        Assert.NotEqual(hash(new byte[] { 97, 98, 99 }), hash(new byte[] { 97, 98, 99, 13, 10 }));
    }

    [Theory]
    [MemberData(nameof(InvalidFilters))]
    public void FilterValidationRejectsInvalidBoundsBeforeNativeAccess(ListSessionsRequest request)
    {
        var validate = Helper<Action<ListSessionsRequest>>("ValidateFilters");

        var error = Assert.Throws<BridgeOperationException>(() => validate(request));

        Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
    }

    [Fact]
    public void FilterValidationAcceptsInclusiveBoundsAndMaximumBodySearch()
    {
        var validate = Helper<Action<ListSessionsRequest>>("ValidateFilters");

        validate(new ListSessionsRequest
        {
            Limit = ProtocolConstants.MaxSessionLimit,
            MinId = 1,
            MaxId = 1,
            MinDurationMilliseconds = 0,
            MaxDurationMilliseconds = 0,
            MinBodyBytes = 0,
            MaxBodyBytes = 0,
            BodyContains = "synthetic",
            BodyDirection = "RESPONSE",
            BodySearchBytes = ProtocolConstants.MaxBodySearchBytes
        });
    }

    public static IEnumerable<object[]> InvalidFilters()
    {
        yield return new object[] { new ListSessionsRequest { Limit = 0 } };
        yield return new object[] { new ListSessionsRequest { Limit = ProtocolConstants.MaxSessionLimit + 1 } };
        yield return new object[] { new ListSessionsRequest { MinId = -1 } };
        yield return new object[] { new ListSessionsRequest { MinId = 2, MaxId = 1 } };
        yield return new object[] { new ListSessionsRequest { MinDurationMilliseconds = -1 } };
        yield return new object[] { new ListSessionsRequest { MinDurationMilliseconds = 2, MaxDurationMilliseconds = 1 } };
        yield return new object[] { new ListSessionsRequest { MinBodyBytes = -1 } };
        yield return new object[] { new ListSessionsRequest { MinBodyBytes = 2, MaxBodyBytes = 1 } };
        yield return new object[] { new ListSessionsRequest { BodyContains = "synthetic", BodySearchBytes = 0 } };
        yield return new object[] { new ListSessionsRequest { BodyContains = "synthetic", BodySearchBytes = ProtocolConstants.MaxBodySearchBytes + 1 } };
        yield return new object[] { new ListSessionsRequest { BodyContains = "synthetic", BodyDirection = "invalid" } };
    }

    private static SessionDiffResponse DiffHeaders(HeaderDto[] left, HeaderDto[] right)
    {
        var result = new SessionDiffResponse();
        Helper<Action<SessionDiffResponse, string, IEnumerable<HeaderDto>, IEnumerable<HeaderDto>>>("AddHeaderDifferences")(
            result, "response-header", left, right);
        return result;
    }

    private static HeaderDto Header(string name, string value)
    {
        return new HeaderDto { Name = name, Value = value };
    }

    // Bind only pure helpers; constructing the dispatcher would subscribe to Fiddler events.
    private static T Helper<T>(string name) where T : Delegate
    {
        var method = typeof(BridgeDispatcher).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (T)method.CreateDelegate(typeof(T));
    }
}
