namespace Puck.Overlays;

/// <summary>
/// The live theme every CPU writer <see cref="UnifiedOverlayNode"/> constructs shares one instance of, read fresh at
/// each <c>Emit</c>/<c>EmitSeat</c> call rather than baked into the writer at construction — so one
/// <see cref="Publish"/> call retheme every surface on the very next produced frame. The composition root (never
/// this project) resolves the document's authored <c>theme</c> section against live state and calls
/// <see cref="UnifiedOverlayNode.UpdateTheme"/>, which republishes here and re-fills the GPU token slab. Not thread
/// hazardous: publish and read both happen on the render thread, the same single-writer/single-reader discipline
/// every other overlay store (<see cref="MarkerStore"/>, <see cref="HudStore"/>) already keeps.
/// </summary>
public sealed class OverlayThemeStore {
    /// <summary>Gets the current resolved theme — <see cref="OverlayThemeValues.Zero"/> (no chrome) until the first
    /// <see cref="Publish"/>.</summary>
    public OverlayThemeValues Current { get; private set; } = OverlayThemeValues.Zero;

    /// <summary>Publishes a newly resolved theme.</summary>
    /// <param name="theme">The resolved theme.</param>
    public void Publish(in OverlayThemeValues theme) => Current = theme;
}
