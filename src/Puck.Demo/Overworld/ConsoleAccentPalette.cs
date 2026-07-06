using System.Numerics;

namespace Puck.Demo.Overworld;

/// <summary>
/// The per-console accent colors, by costume — the DMG's grey-green shell, the CGB's berry purple, and the GBA's
/// indigo, matching the default document's dmg/cgb/agb console order. Presentation only; the single source both the
/// stand painter and the frame source read so the two never drift.
/// </summary>
internal static class ConsoleAccentPalette {
    /// <summary>Gets the DMG's grey-green shell accent.</summary>
    public static readonly Vector3 Dmg = new(0.72f, 0.73f, 0.66f);
    /// <summary>Gets the CGB's berry-purple shell accent (also the fallback for an unknown costume).</summary>
    public static readonly Vector3 Cgb = new(0.58f, 0.26f, 0.55f);
    /// <summary>Gets the GBA's indigo shell accent.</summary>
    public static readonly Vector3 Agb = new(0.33f, 0.30f, 0.71f);
}
