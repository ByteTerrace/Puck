using Puck.Abstractions.Lighting;

namespace Puck.Input.Lighting;

/// <summary>
/// Computes per-lamp colors for a keyboard bind legend, layering (bottom to top):
/// <list type="number">
/// <item><description><b>Base</b> — a bound key takes its command's <see cref="BindCategory"/> color; an unbound key takes the palette's faint idle wash.</description></item>
/// <item><description><b>Availability</b> — a bound-but-unavailable key is dimmed by the palette's unavailable scale.</description></item>
/// <item><description><b>Chord modifier</b> — a held chord-modifier key is overpainted with the modifier highlight (the board itself recolors because the host feeds the active chord page's legend).</description></item>
/// <item><description><b>Activation flash</b> — a key whose command fired this tick flashes toward the flash color and decays over subsequent ticks.</description></item>
/// </list>
/// The composer is stateful only in the flash decay it carries between ticks; everything else comes from the
/// per-tick <see cref="LightLegendState"/>. It maps each lamp to a keyboard source through the lamp's declared
/// input binding and <see cref="KeyboardUsageMap"/>, so it never assumes a device's key layout.
/// </summary>
public sealed class LightLegendComposer {
    private readonly LightLegendPalette m_palette;
    private float[] m_flash = [];

    /// <summary>Initializes a new instance over a palette.</summary>
    /// <param name="palette">The palette that colors categories and layers; defaults to <see cref="LightLegendPalette.CreateDefault"/>.</param>
    public LightLegendComposer(LightLegendPalette? palette = null) {
        m_palette = (palette ?? LightLegendPalette.CreateDefault());
    }

    /// <summary>Gets the palette this composer paints with.</summary>
    public LightLegendPalette Palette => m_palette;

    /// <summary>Clears the carried activation-flash decay (e.g. on focus loss).</summary>
    public void ResetFlash() {
        Array.Clear(array: m_flash);
    }

    /// <summary>
    /// Composes one frame of lamp colors for a device into <paramref name="destination"/>. Writes exactly
    /// <c>device.LampCount</c> entries (indices align with lamp indices <c>0..LampCount-1</c>).
    /// </summary>
    /// <param name="device">The lamp array to compose for.</param>
    /// <param name="state">This tick's legend.</param>
    /// <param name="flashDecay">The fraction of activation flash to remove this frame (0 = hold, 1 = clear); a driver derives it from elapsed time.</param>
    /// <param name="destination">The span that receives the composed colors; must be at least <c>device.LampCount</c> long.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the device's lamp count.</exception>
    public void Compose(ILampArrayDevice device, LightLegendState state, float flashDecay, Span<LampColor> destination) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(state);

        var count = device.LampCount;

        if (destination.Length < count) {
            throw new ArgumentException(message: "destination is shorter than the device's lamp count.", paramName: nameof(destination));
        }

        EnsureFlashCapacity(count: count);

        var decay = Math.Clamp(value: flashDecay, min: 0f, max: 1f);

        for (var index = 0; (index < count); index++) {
            // Decay the carried flash for this lamp before this tick's events re-arm it.
            m_flash[index] = Math.Max(val1: 0f, val2: (m_flash[index] - decay));

            var color = ResolveBase(
                device: device,
                index: index,
                source: out var source,
                state: state
            );

            if ((source is not null) && state.WasFlashed(source: source)) {
                m_flash[index] = 1f;
            }

            var flash = m_flash[index];

            if (flash > 0f) {
                color = Lerp(a: color, b: m_palette.Flash, t: flash);
            }

            destination[index] = color;
        }
    }

    private LampColor ResolveBase(ILampArrayDevice device, int index, LightLegendState state, out string? source) {
        source = null;

        if (!device.TryGetLampInfo(index: index, info: out var info) || !info.HasInputBinding) {
            return m_palette.Idle;
        }

        if (!KeyboardUsageMap.TryGetSource(
            usagePage: info.InputBindingUsagePage,
            usage: info.InputBindingUsage,
            source: out var resolved
        )) {
            return m_palette.Idle;
        }

        source = resolved;

        // A held chord modifier always wins the base — its key marks the chord the board has switched to.
        if (state.IsHeldModifier(source: resolved)) {
            return m_palette.Modifier;
        }

        if (!state.TryGetBinding(source: resolved, entry: out var entry)) {
            return m_palette.Idle;
        }

        var color = m_palette.ColorFor(category: entry.Category);

        return (entry.IsAvailable ? color : color.Scale(scale: m_palette.UnavailableScale));
    }
    private void EnsureFlashCapacity(int count) {
        if (m_flash.Length < count) {
            Array.Resize(array: ref m_flash, newSize: count);
        }
    }
    private static LampColor Lerp(LampColor a, LampColor b, float t) {
        return new LampColor(
            Red: LerpChannel(a: a.Red, b: b.Red, t: t),
            Green: LerpChannel(a: a.Green, b: b.Green, t: t),
            Blue: LerpChannel(a: a.Blue, b: b.Blue, t: t),
            Intensity: LerpChannel(a: a.Intensity, b: b.Intensity, t: t)
        );
    }
    private static byte LerpChannel(byte a, byte b, float t) {
        return ((byte)(a + ((b - a) * t)));
    }
}
