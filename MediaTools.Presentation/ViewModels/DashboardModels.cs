namespace MediaTools.Presentation.ViewModels;

public enum DriveUsageSeverity
{
    Normal,
    Warning,
    Critical
}

/// <summary>Row for the compression activity bar chart.</summary>
public sealed class CompressionStatBarItem
{
    public required string Label { get; init; }
    public required string ValueText { get; init; }
    /// <summary>0–100 for progress bar fill.</summary>
    public double FillPercent { get; init; }
}

/// <summary>One fixed disk row for storage cards.</summary>
public sealed class DriveStorageDisplayItem
{
    public required string Name { get; init; }
    public required string TitleLine { get; init; }
    public required string DetailLine { get; init; }
    public double UsedPercent { get; init; }
    public DriveUsageSeverity Severity { get; init; }
}
