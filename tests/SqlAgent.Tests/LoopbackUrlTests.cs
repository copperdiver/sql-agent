using Microsoft.Extensions.Configuration;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class LoopbackUrlTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void Resolve_defaults_to_loopback_on_5099()
    {
        Assert.Equal("http://127.0.0.1:5099", LoopbackUrl.Resolve(Config()));
    }

    [Fact]
    public void Resolve_honors_the_configured_port()
    {
        Assert.Equal("http://127.0.0.1:8123", LoopbackUrl.Resolve(Config(("SqlAgent:Web:Port", "8123"))));
    }

    [Fact]
    public void Resolve_always_binds_the_loopback_address()
    {
        // The port is configurable; the host is not. Binding 0.0.0.0 would expose the configuration
        // surface to the network, which is phase 3 work behind TLS and real sessions.
        Assert.StartsWith("http://127.0.0.1:", LoopbackUrl.Resolve(Config(("SqlAgent:Web:Port", "1"))));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("not-a-port")]
    public void Resolve_rejects_a_port_outside_the_valid_range(string port)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoopbackUrl.Resolve(Config(("SqlAgent:Web:Port", port))));
        Assert.Contains("SqlAgent:Web:Port", ex.Message);
    }
}
