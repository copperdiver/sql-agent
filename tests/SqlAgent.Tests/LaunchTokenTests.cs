using Microsoft.Extensions.Configuration;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class LaunchTokenTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void Configured_token_is_used_verbatim()
    {
        var token = new LaunchToken(Config(("SqlAgent:LocalAuth:Token", "operator-supplied-token")));

        Assert.Equal("operator-supplied-token", token.Value);
        Assert.False(token.IsGenerated);
    }

    [Fact]
    public void No_configuration_generates_a_token()
    {
        var token = new LaunchToken(Config());

        Assert.True(token.IsGenerated);
        Assert.False(string.IsNullOrEmpty(token.Value));
    }

    [Fact]
    public void Whitespace_only_configured_value_is_treated_as_absent()
    {
        var token = new LaunchToken(Config(("SqlAgent:LocalAuth:Token", "   ")));

        Assert.True(token.IsGenerated);
        Assert.NotEqual("   ", token.Value);
    }

    [Fact]
    public void Unconfigured_tokens_are_not_fixed_or_seeded()
    {
        var first = new LaunchToken(Config());
        var second = new LaunchToken(Config());

        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public void Generated_token_is_drawn_from_32_random_bytes()
    {
        var token = new LaunchToken(Config());

        // Convert.ToHexString of 32 bytes yields 64 hex characters. Asserting on the length ties
        // this test to the entropy the implementation draws, so shrinking it below 32 bytes fails.
        Assert.Equal(64, token.Value.Length);
    }
}
