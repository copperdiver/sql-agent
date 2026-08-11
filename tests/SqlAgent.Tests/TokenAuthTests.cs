using System.Net;
using SqlAgent.Host.Web;

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
    public async Task Origin_matching_the_servers_own_scheme_and_authority_is_accepted()
    {
        // Pins the fixed behavior: an Origin that is exactly this server's own bind address is
        // accepted, not merely one that resolves to a loopback-family host name.
        var host = $"127.0.0.1:{LoopbackUrl.DefaultPort}";
        var request = new HttpRequestMessage(HttpMethod.Get, $"/?token={WebTestHost.Token}");
        request.Headers.Host = host;
        request.Headers.Add("Origin", $"http://{host}");

        var r = await _host.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Origin_with_a_different_port_than_the_servers_own_is_rejected()
    {
        // Same host, different port. This is exactly the gap a host-only Origin check misses:
        // SameSite cookie scoping ignores port, and a WebSocket handshake bypasses CORS entirely, so
        // a sibling local process on another port could otherwise ride the session cookie into a
        // circuit. Fails against the old host-only IsLocalOrigin implementation -- verified by
        // temporarily reverting the fix, see the fix report.
        var host = $"127.0.0.1:{LoopbackUrl.DefaultPort}";
        var request = new HttpRequestMessage(HttpMethod.Get, $"/?token={WebTestHost.Token}");
        request.Headers.Host = host;
        request.Headers.Add("Origin", "http://127.0.0.1:44444");

        var r = await _host.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Authenticated_request_with_a_realistic_host_and_port_succeeds()
    {
        // WebApplicationFactory's default client sends a bare "localhost" Host header with no port,
        // which every other test in this file relies on implicitly. That leaves a gap where the whole
        // suite could be green while a real browser -- which always sends Host: 127.0.0.1:<port> --
        // is rejected. Pin the realistic case explicitly.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/?token={WebTestHost.Token}");
        request.Headers.Host = $"127.0.0.1:{LoopbackUrl.DefaultPort}";

        var r = await _host.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Authenticated_negotiate_request_succeeds()
    {
        // Complements Blazor_circuit_negotiation_is_not_covered_by_the_framework_asset_exemption:
        // that test proves negotiate is refused when unauthenticated, this proves it actually works
        // once authenticated with a realistic host header, so the two together bound the behavior.
        var host = $"127.0.0.1:{LoopbackUrl.DefaultPort}";
        var client = _host.NewClient();

        var login = new HttpRequestMessage(HttpMethod.Get, $"/?token={WebTestHost.Token}");
        login.Headers.Host = host;
        await client.SendAsync(login);   // client keeps the cookie

        var negotiate = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        negotiate.Headers.Host = host;
        var r = await client.SendAsync(negotiate);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Framework_assets_are_reachable_without_a_token()
    {
        // Documents the positive side of the /_framework exemption: the browser must be able to fetch
        // blazor.web.js before it has a token to present. This test would fail (401) if the exemption
        // were ever narrowed or removed by mistake. It must also actually find the file: asserting only
        // "not 401" previously let a 404 (the file not being wired into the static file provider at all —
        // see Program.cs's builder.WebHost.UseStaticWebAssets() call) pass silently, which is exactly the
        // bug that shipped undetected. Asserting OK plus a non-empty body rules that out.
        var r = await _host.NewClient().GetAsync("/_framework/blazor.web.js");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(body);
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
