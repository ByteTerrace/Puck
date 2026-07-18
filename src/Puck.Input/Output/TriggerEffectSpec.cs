namespace Puck.Input.Output;

/// <summary>The adaptive-trigger effect a <see cref="TriggerEffectSpec"/> describes.</summary>
public enum TriggerEffectKind : byte {
    /// <summary>No effect — the trigger pulls freely (clears any resistance).</summary>
    Off = 0,
    /// <summary>Uniform resistance from a start zone to the end of the pull.</summary>
    Feedback,
    /// <summary>Resistance within a band that gives way (snaps) once pushed through.</summary>
    Weapon,
    /// <summary>Vibration from a start zone to the end of the pull, at a given amplitude and frequency.</summary>
    Vibration,
    /// <summary>A per-zone resistance curve along the pull (the general form of <see cref="Feedback"/>).</summary>
    ContinuousCurve,
}

/// <summary>
/// A typed, allocation-free description of one trigger's adaptive-resistance effect — the first-class replacement
/// for hand-building a raw output report. It is composed into the controller's normal output report by an
/// <see cref="Puck.Input.Devices.ITriggerEffectParser"/> (the DualSense), and a controller without adaptive
/// triggers simply has no such capability. Zones index the trigger pull in ten steps
/// (<c>0</c> = released … <c>9</c> = fully pressed); strength/amplitude runs <c>0</c> (off) to <c>8</c> (maximum).
/// Build one with a factory — invalid parameters throw — and apply the per-trigger pair via
/// <see cref="IGamepadOutput.SetTriggerEffect"/>.
/// </summary>
/// <remarks>The 10-zone curve packs one strength nibble per zone into <see cref="Curve"/>, so the whole spec stays
/// a small value with no backing array.</remarks>
public readonly record struct TriggerEffectSpec {
    /// <summary>The number of resistance zones along the trigger pull.</summary>
    public const int ZoneCount = 10;

    private const byte MaxStrength = 8;
    private const byte MaxZone = 9;
    private const byte WeaponMaxEnd = 8;
    private const byte WeaponMaxStart = 7;
    private const byte WeaponMinStart = 2;

    /// <summary>The effect kind; selects which other members apply.</summary>
    public TriggerEffectKind Kind { get; private init; }
    /// <summary>The zone an effect begins at (<see cref="TriggerEffectKind.Feedback"/>/<see cref="TriggerEffectKind.Vibration"/> start, <see cref="TriggerEffectKind.Weapon"/> band start).</summary>
    public byte StartZone { get; private init; }
    /// <summary>The <see cref="TriggerEffectKind.Weapon"/> band's end zone; unused otherwise.</summary>
    public byte EndZone { get; private init; }
    /// <summary>Feedback/Weapon resistance strength or Vibration amplitude, <c>1..8</c> (unused otherwise).</summary>
    public byte Strength { get; private init; }
    /// <summary>Vibration frequency in hertz, <c>1..255</c> (unused otherwise).</summary>
    public byte Frequency { get; private init; }
    /// <summary>For <see cref="TriggerEffectKind.ContinuousCurve"/>, each zone's strength (<c>0..8</c>) packed one nibble per zone, lowest zone first.</summary>
    public ulong Curve { get; private init; }

    /// <summary>The neutral "no effect" spec — the trigger pulls freely.</summary>
    public static TriggerEffectSpec Off => default;

    /// <summary>Uniform resistance from <paramref name="position"/> to the end of the pull.</summary>
    /// <param name="position">The zone resistance begins at, <c>0</c> (released) to <c>9</c> (pressed).</param>
    /// <param name="strength">The resistance strength, <c>0</c> (off) to <c>8</c> (maximum).</param>
    /// <returns>The effect, or <see cref="Off"/> when <paramref name="strength"/> is zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> &gt; 9 or <paramref name="strength"/> &gt; 8.</exception>
    public static TriggerEffectSpec Feedback(byte position, byte strength) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: position, other: MaxZone);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: strength, other: MaxStrength);

        return ((strength == 0)
            ? Off
            : new TriggerEffectSpec { Kind = TriggerEffectKind.Feedback, StartZone = position, Strength = strength, });
    }

    /// <summary>Resistance within a band that gives way once pushed through.</summary>
    /// <param name="startZone">Where the band begins, zone <c>2</c> to <c>7</c>.</param>
    /// <param name="endZone">Where the band ends, greater than <paramref name="startZone"/> up to zone <c>8</c>.</param>
    /// <param name="strength">The resistance strength, <c>0</c> (off) to <c>8</c> (maximum).</param>
    /// <returns>The effect, or <see cref="Off"/> when <paramref name="strength"/> is zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside the firmware's valid band.</exception>
    public static TriggerEffectSpec Weapon(byte startZone, byte endZone, byte strength) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: startZone, other: WeaponMinStart);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: startZone, other: WeaponMaxStart);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: endZone, other: WeaponMaxEnd);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: endZone, other: startZone);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: strength, other: MaxStrength);

        return ((strength == 0)
            ? Off
            : new TriggerEffectSpec { EndZone = endZone, Kind = TriggerEffectKind.Weapon, StartZone = startZone, Strength = strength, });
    }

    /// <summary>Vibration from <paramref name="position"/> to the end of the pull.</summary>
    /// <param name="position">The zone vibration begins at, <c>0</c> (released) to <c>9</c> (pressed).</param>
    /// <param name="amplitude">The vibration amplitude, <c>0</c> (off) to <c>8</c> (maximum).</param>
    /// <param name="frequency">The vibration frequency in hertz, <c>1</c> to <c>255</c> (<c>0</c> disables).</param>
    /// <returns>The effect, or <see cref="Off"/> when <paramref name="amplitude"/> or <paramref name="frequency"/> is zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> &gt; 9 or <paramref name="amplitude"/> &gt; 8.</exception>
    public static TriggerEffectSpec Vibration(byte position, byte amplitude, byte frequency) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: position, other: MaxZone);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: amplitude, other: MaxStrength);

        return (((amplitude == 0) || (frequency == 0))
            ? Off
            : new TriggerEffectSpec { Frequency = frequency, Kind = TriggerEffectKind.Vibration, StartZone = position, Strength = amplitude, });
    }

    /// <summary>A per-zone resistance curve: each zone's strength (<c>0..8</c>) along the pull.</summary>
    /// <param name="zoneStrengths">Up to <see cref="ZoneCount"/> strengths, lowest zone first; a strength of <c>0</c> leaves that zone free.</param>
    /// <returns>The effect, or <see cref="Off"/> when every zone is zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException">More than <see cref="ZoneCount"/> zones, or a strength &gt; 8.</exception>
    public static TriggerEffectSpec ContinuousCurve(ReadOnlySpan<byte> zoneStrengths) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: zoneStrengths.Length, other: ZoneCount);

        var curve = 0UL;
        var any = false;

        for (var zone = 0; (zone < zoneStrengths.Length); zone++) {
            var strength = zoneStrengths[zone];

            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: strength, other: MaxStrength);

            curve |= (((ulong)strength) << (zone * 4));
            any |= (strength != 0);
        }

        return (any
            ? new TriggerEffectSpec { Curve = curve, Kind = TriggerEffectKind.ContinuousCurve, }
            : Off);
    }

    /// <summary>Gets the strength (<c>0..8</c>) of a <see cref="TriggerEffectKind.ContinuousCurve"/> zone.</summary>
    /// <param name="zone">The zone index, <c>0</c> to <see cref="ZoneCount"/> − 1.</param>
    /// <returns>The zone's strength.</returns>
    public byte ZoneStrength(int zone) {
        return ((byte)((Curve >> (zone * 4)) & 0xFUL));
    }
}
