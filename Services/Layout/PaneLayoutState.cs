namespace GitManager.Services.Layout;

public sealed class PaneLayoutState
{
    public bool IsExpanded { get; set; } = true;
    public double Size { get; set; }
    public bool IsStar { get; set; }
}
