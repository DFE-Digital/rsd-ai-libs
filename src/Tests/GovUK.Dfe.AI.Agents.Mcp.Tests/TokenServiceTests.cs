using Azure.Core;
using Azure.Identity;
using Azure;
using GovUK.Dfe.AI.Agents.Mcp.Services;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Mcp.Tests;

/// <summary>MCP access tokens: cached until near expiry, refreshed once for concurrent callers, transient failures retried.</summary>
public sealed class TokenServiceTests
{
    private const string Scope = "api://school-performance/.default";
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    /// <summary>Hands out numbered tokens; fails the first <c>failures</c> calls with <c>failWith</c>.</summary>
    private sealed class FakeCredential(TimeSpan lifetime, int failures = 0, Exception? failWith = null, TimeSpan? delay = null) : TokenCredential
    {
        private int _calls;

        public int Calls => _calls;

        public List<string> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            lock (Scopes)
            {
                Scopes.AddRange(requestContext.Scopes);
            }

            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            if (call <= failures)
            {
                throw failWith!;
            }

            return new AccessToken($"token-{call}", DateTimeOffset.UtcNow + lifetime);
        }
    }

    [Fact]
    public async Task AsksTheAppsCredential_ForTheServersScope()
    {
        var credential = new FakeCredential(TimeSpan.FromHours(1));

        var token = await new TokenService(credential, Scope).GetAccessTokenAsync(cancellationToken);

        Assert.Equal("token-1", token);
        Assert.Equal([Scope], credential.Scopes);
    }

    [Fact]
    public async Task ReusesTheToken_WhileItHasMoreThanAMinuteLeft()
    {
        var credential = new FakeCredential(TimeSpan.FromHours(1));
        var sut = new TokenService(credential, Scope);

        await sut.GetAccessTokenAsync(cancellationToken);
        await sut.GetAccessTokenAsync(cancellationToken);

        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public async Task GetsANewToken_OnceItsWithinAMinuteOfExpiry()
    {
        var credential = new FakeCredential(TimeSpan.FromSeconds(30));
        var sut = new TokenService(credential, Scope);

        await sut.GetAccessTokenAsync(cancellationToken);
        var second = await sut.GetAccessTokenAsync(cancellationToken);

        Assert.Equal("token-2", second);
    }

    /// <summary>Outages and throttling are worth another try; a wrong secret or missing permission never succeeds on retry.</summary>
    public static TheoryData<Exception, bool> Failures => new()
    {
        { new RequestFailedException(503, "Service unavailable."), true },
        { new RequestFailedException(429, "Too many requests."), true },
        { new RequestFailedException(0, "Network unreachable."), true },
        { new HttpRequestException("Connection reset."), true },
        { new HttpRequestException("Bad gateway.", null, System.Net.HttpStatusCode.BadGateway), true },
        { new RequestFailedException(403, "Forbidden."), false },
        { new HttpRequestException("Unauthorized.", null, System.Net.HttpStatusCode.Unauthorized), false },
        { new AuthenticationFailedException("Wrong secret."), false },
        { new CredentialUnavailableException("No managed identity here."), false },
        { new InvalidOperationException("Unexpected."), false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task RetriesOnlyFailuresThatCanSucceedNextTime(Exception failure, bool retried)
    {
        var credential = new FakeCredential(TimeSpan.FromHours(1), failures: 1, failWith: failure);
        var sut = new TokenService(credential, Scope);

        if (retried)
        {
            Assert.Equal("token-2", await sut.GetAccessTokenAsync(cancellationToken));
        }
        else
        {
            Assert.Same(failure, await Record.ExceptionAsync(() => sut.GetAccessTokenAsync(cancellationToken)));
        }

        Assert.Equal(retried ? 2 : 1, credential.Calls);
    }

    [Fact]
    public async Task GivesUp_AfterThreeTransientFailures()
    {
        var credential = new FakeCredential(TimeSpan.FromHours(1), failures: 10, failWith: new RequestFailedException(503, "Unavailable"));

        await Assert.ThrowsAsync<RequestFailedException>(() => new TokenService(credential, Scope).GetAccessTokenAsync(cancellationToken));
        Assert.Equal(3, credential.Calls);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneRefresh()
    {
        var credential = new FakeCredential(TimeSpan.FromHours(1), delay: TimeSpan.FromMilliseconds(100));
        var sut = new TokenService(credential, Scope);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => sut.GetAccessTokenAsync(cancellationToken)));

        Assert.All(tokens, token => Assert.Equal("token-1", token));
        Assert.Equal(1, credential.Calls);
    }
}
