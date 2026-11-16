namespace CanvasCountdown;

/// <summary>
/// Where the live countdown is drawn: on Windows the tray icon and the taskbar
/// badge, redrawn from a <see cref="DockTileRenderPlan"/>.
/// </summary>
/// <remarks>
/// Narrow on purpose, so the countdown's coordination can be tested without a
/// shell to draw into, and no test ever repaints the real tray icon of
/// whoever is running the suite.
/// </remarks>
public interface IDockRendering
{
    /// <summary>Renders the current countdown. Pass null when there is no eligible event.</summary>
    void Render(int? daysRemaining, string label);

    void Apply(DockAppearance appearance)
    {
    }
}
