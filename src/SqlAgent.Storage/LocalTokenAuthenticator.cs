using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SqlAgent.Storage;

/// <summary>Outcome of validating a presented token (CD-51 Story 1.7).</summary>
public enum AuthOutcome
{
    /// <summary>No token is configured — local access is open (single-user trust boundary, ADR-0003).</summary>
    Disabled,
    /// <summary>A token is configured and the presented one matches.</summary>
    Authenticated,
    /// <summary>A token is configured but the request presented none.</summary>
    MissingToken,
    /// <summary>A token is configured and the presented one is wrong.</summary>
    InvalidToken,
}

/// <summary>
/// Validates the optional local-access token shared by the MCP and local-API surfaces (CD-51 Story 1.7).
/// The expected token is held in the <see cref="ISecretStore"/> under <see cref="TokenSecretReference"/>,
/// so callers reference it by key and never hold the raw value — and it is never logged or returned.
/// When no token is configured the surfaces are open (local single-user trust boundary, ADR-0003);
/// configure a token to require authentication.
/// </summary>
public sealed class LocalTokenAuthenticator(ISecretStore secrets, ILogger<LocalTokenAuthenticator> logger)
{
    /// <summary>Secret-store key under which the expected local-access token is stored.</summary>
    public const string TokenSecretReference = "local-auth-token";

    /// <summary>True for outcomes that may proceed; false for outcomes that must be rejected.</summary>
    public static bool IsAllowed(AuthOutcome outcome) =>
        outcome is AuthOutcome.Disabled or AuthOutcome.Authenticated;

    /// <summary>Validates <paramref name="presentedToken"/> against the configured token. Never throws.
    /// Rejections are logged without the token values.</summary>
    public async Task<AuthOutcome> AuthenticateAsync(string? presentedToken, CancellationToken ct = default)
    {
        var expected = await secrets.GetAsync(TokenSecretReference, ct);
        if (string.IsNullOrEmpty(expected))
            return AuthOutcome.Disabled;

        if (string.IsNullOrEmpty(presentedToken))
        {
            logger.LogWarning("Local access denied: authentication is enabled but the request presented no token.");
            return AuthOutcome.MissingToken;
        }

        // Fixed-time compare so a wrong token can't be discovered byte-by-byte via timing.
        // ponytail: differing lengths short-circuit (leaks length only) — acceptable on a local trust boundary.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presentedToken), Encoding.UTF8.GetBytes(expected)))
        {
            logger.LogWarning("Local access denied: the request presented an invalid token.");
            return AuthOutcome.InvalidToken;
        }

        return AuthOutcome.Authenticated;
    }

    /// <summary>Bridges admin configuration into the secret store: stores <paramref name="configuredToken"/>
    /// (encrypted, never logged) so it becomes the expected token, or leaves auth disabled when it is blank.
    /// Called once at host startup so the token can be set via Core settings (Story 1.7).</summary>
    public Task ConfigureFromSettingAsync(string? configuredToken, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(configuredToken)
            ? Task.CompletedTask
            : secrets.SetAsync(TokenSecretReference, configuredToken, ct);
}
