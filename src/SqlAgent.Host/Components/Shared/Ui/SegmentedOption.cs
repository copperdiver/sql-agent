namespace SqlAgent.Host.Components.Shared.Ui;

/// <summary>One choice in a <c>Segmented</c> control. <paramref name="Icon"/> is an
/// <c>Icon</c> name; when labels are hidden it is the only visible content, and the label is still
/// rendered for assistive technology.</summary>
public record SegmentedOption(string Value, string Label, string? Icon = null);
