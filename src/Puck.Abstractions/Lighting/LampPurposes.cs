namespace Puck.Abstractions.Lighting;

/// <summary>
/// The purposes a lamp declares, as the HID LampArray <c>LampPurposes</c> attribute reports them. A lamp may
/// serve several purposes at once, so these compose as flags. A bind legend cares mostly about
/// <see cref="Control"/> lamps (the ones sitting on a control, e.g. a key), leaving pure
/// <see cref="Accent"/> / <see cref="Branding"/> lamps to an ambient layer.
/// </summary>
[Flags]
public enum LampPurposes {
    /// <summary>No purpose was declared.</summary>
    None = 0,
    /// <summary>The lamp sits on a control (a key, button, or similar) — the lamps a bind legend color-codes.</summary>
    Control = 0x01,
    /// <summary>The lamp is decorative accent lighting.</summary>
    Accent = 0x02,
    /// <summary>The lamp lights a brand mark or logo.</summary>
    Branding = 0x04,
    /// <summary>The lamp is a status indicator.</summary>
    Status = 0x08,
    /// <summary>The lamp provides general illumination.</summary>
    Illumination = 0x10,
    /// <summary>The lamp is intended for presentation / effects use.</summary>
    Presentation = 0x20,
}
