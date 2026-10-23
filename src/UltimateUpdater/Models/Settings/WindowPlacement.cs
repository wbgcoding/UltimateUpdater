namespace UltimateUpdater.Models.Settings;

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 700;
    public bool Maximized { get; set; }
}
