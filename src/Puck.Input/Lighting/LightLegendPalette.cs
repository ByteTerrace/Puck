using Puck.Abstractions.Lighting;

namespace Puck.Input.Lighting;

/// <summary>
/// Maps each <see cref="BindCategory"/> to the lamp color that represents it, plus the incidental colors the
/// composer layers use (the faint idle wash on unbound keys, the held-modifier highlight, the activation flash,
/// and the availability dim factor). Data-tunable: start from <see cref="CreateDefault"/> and override any entry
/// with <see cref="WithCategory"/> — a host can drive the whole palette from a run-document field.
/// </summary>
public sealed class LightLegendPalette {
    private readonly LampColor[] m_byCategory;

    private LightLegendPalette(
        LampColor[] byCategory,
        LampColor flash,
        LampColor idle,
        LampColor modifier,
        float unavailableScale
    ) {
        m_byCategory = byCategory;
        Flash = flash;
        Idle = idle;
        Modifier = modifier;
        UnavailableScale = unavailableScale;
    }

    /// <summary>Gets the color a key's command flashes toward when it fires this tick (the activation layer).</summary>
    public LampColor Flash { get; }

    /// <summary>Gets the very faint wash painted on a key that is not bound (or off, if its intensity is zero).</summary>
    public LampColor Idle { get; }

    /// <summary>Gets the highlight painted on a held chord-modifier key.</summary>
    public LampColor Modifier { get; }

    /// <summary>Gets the intensity multiplier (0..1) applied to a bound-but-unavailable key.</summary>
    public float UnavailableScale { get; }

    /// <summary>Gets the color for a category.</summary>
    /// <param name="category">The category to look up.</param>
    /// <returns>The category's lamp color.</returns>
    public LampColor ColorFor(BindCategory category) {
        var index = ((int)category);

        return (((index >= 0) && (index < m_byCategory.Length)) ? m_byCategory[index] : Idle);
    }

    /// <summary>Returns a copy of this palette with one category's color replaced.</summary>
    /// <param name="category">The category to override.</param>
    /// <param name="color">The new color for that category.</param>
    /// <returns>A new palette; this instance is unchanged.</returns>
    public LightLegendPalette WithCategory(BindCategory category, LampColor color) {
        var byCategory = (LampColor[])m_byCategory.Clone();
        var index = ((int)category);

        if ((index >= 0) && (index < byCategory.Length)) {
            byCategory[index] = color;
        }

        return new LightLegendPalette(
            byCategory: byCategory,
            flash: Flash,
            idle: Idle,
            modifier: Modifier,
            unavailableScale: UnavailableScale
        );
    }

    /// <summary>
    /// Builds the default palette: movement cyan, camera violet, interact green, console and meta amber, bench
    /// and debug in the red family, system a cool blue — chosen to stay legible across the keyboard at once.
    /// </summary>
    /// <returns>A fresh default palette.</returns>
    public static LightLegendPalette CreateDefault() {
        var byCategory = new LampColor[Enum.GetValues<BindCategory>().Length];

        byCategory[(int)BindCategory.None] = LampColor.Off;
        byCategory[(int)BindCategory.Movement] = LampColor.Rgb(red: 0, green: 200, blue: 255); // cyan
        byCategory[(int)BindCategory.Camera] = LampColor.Rgb(red: 150, green: 80, blue: 255); // violet
        byCategory[(int)BindCategory.Interact] = LampColor.Rgb(red: 40, green: 220, blue: 90); // green
        byCategory[(int)BindCategory.Console] = LampColor.Rgb(red: 255, green: 170, blue: 30); // amber
        byCategory[(int)BindCategory.Meta] = LampColor.Rgb(red: 255, green: 120, blue: 0); // deep amber
        byCategory[(int)BindCategory.Bench] = LampColor.Rgb(red: 255, green: 70, blue: 40); // ember red
        byCategory[(int)BindCategory.Debug] = LampColor.Rgb(red: 220, green: 40, blue: 80); // crimson
        byCategory[(int)BindCategory.System] = LampColor.Rgb(red: 70, green: 130, blue: 255); // cool blue

        return new LightLegendPalette(
            byCategory: byCategory,
            flash: LampColor.Rgb(red: 255, green: 255, blue: 255),
            // A faint cool wash on unbound keys. Brightness rides RGB (a keyboard lamp has only one intensity
            // level), so this stays dim on every device rather than depending on the intensity channel.
            idle: new LampColor(Red: 6, Green: 8, Blue: 12, Intensity: 255),
            modifier: LampColor.Rgb(red: 255, green: 255, blue: 255),
            unavailableScale: 0.18f
        );
    }
}
