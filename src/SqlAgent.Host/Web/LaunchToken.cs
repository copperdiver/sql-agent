using System.Security.Cryptography;

namespace SqlAgent.Host.Web;

/// <summary>
/// The token a browser must present once to obtain a session cookie.
///
/// When the operator configured SqlAgent:LocalAuth:Token, that value is used and keeps its existing
/// persisted behavior. Otherwise a random token is generated for this process only and is deliberately
/// NOT written to the secret store: LocalTokenAuthenticator.ConfigureFromSettingAsync persists whatever
/// it is handed, and a blank setting does not clear a stored value — so persisting a per-start token
/// would silently switch authentication on for the MCP server too, with a value that changes on every
/// restart.
/// </summary>
public sealed class LaunchToken
{
    public string Value { get; }
    public bool IsGenerated { get; }

    public LaunchToken(IConfiguration configuration)
    {
        var configured = configuration["SqlAgent:LocalAuth:Token"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Value = configured;
            IsGenerated = false;
        }
        else
        {
            Value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            IsGenerated = true;
        }
    }
}
