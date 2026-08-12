namespace SqlAgent.Host.Components.Shared.Ui;

/// <summary>Visual weight of a <c>Badge</c>. Maps to token-driven surface/text pairs, never to
/// literal colors, so both themes stay consistent.</summary>
public enum BadgeTone
{
    Neutral,
    Primary,
    Success,
    Warning,
    Danger,
}
