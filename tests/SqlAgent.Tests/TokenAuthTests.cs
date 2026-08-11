using System.Net;

namespace SqlAgent.Tests;

public class TokenAuthTests : IClassFixture<WebTestHost>
{
    private readonly WebTestHost _host;
    public TokenAuthTests(WebTestHost host) => _host = host;

    [Fact]
    public async Task Request_without_a_token_or_cookie_is_unauthorized()
    {
        var r = await _host.NewClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Wrong_token_is_unauthorized()
    {
        var r = await _host.NewClient().GetAsync("/?token=nope");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Valid_token_issues_a_session_cookie()
    {
        var r = await _host.NewClient().GetAsync($"/?token={WebTestHost.Token}");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // The response may also carry an antiforgery cookie set by Blazor's own CSRF protection
        // (app.UseAntiforgery(), registered in Task 1) — pick out the session cookie specifically.
        var cookie = Assert.Single(r.Headers.GetValues("Set-Cookie"), c => c.StartsWith("sqlagent_session="));
        // ASP.NET Core serializes cookie attributes in lowercase ("httponly", "samesite=strict"),
        // so the assertion is case-insensitive; the security property under test is the flag's
        // presence, not its casing on the wire.
        Assert.Contains("HttpOnly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_from_a_previous_exchange_is_accepted_without_the_token()
    {
        var client = _host.NewClient();
        await client.GetAsync($"/?token={WebTestHost.Token}");   // client keeps the cookie

        var r = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Foreign_origin_is_rejected_even_with_a_valid_cookie()
    {
        var client = _host.NewClient();
        await client.GetAsync($"/?token={WebTestHost.Token}");

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Origin", "https://evil.example");
        var r = await client.SendAsync(request);

        // Without this, any page the user has open could drive the configuration UI.
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Foreign_host_header_is_rejected()
    {
        // DNS rebinding: an attacker-controlled name resolving to 127.0.0.1.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/?token={WebTestHost.Token}");
        request.Headers.Host = "evil.example";
        var r = await _host.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Framework_assets_are_reachable_without_a_token()
    {
        // Documents the positive side of the /_framework exemption: the browser must be able to fetch
        // blazor.web.js before it has a token to present. This test would fail (401) if the exemption
        // were ever narrowed or removed by mistake.
        var r = await _host.NewClient().GetAsync("/_framework/blazor.web.js");
        Assert.NotEqual(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Blazor_circuit_negotiation_is_not_covered_by_the_framework_asset_exemption()
    {
        // The /_framework exemption exists only so the browser can fetch blazor.web.js before it can
        // authenticate. The circuit's own SignalR endpoint lives at /_blazor (not /_framework/*), so an
        // unauthenticated caller must still be refused here — otherwise the exemption would let any page
        // open a live Blazor circuit against the configuration UI without ever presenting the token.
        var r = await _host.NewClient().PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}
