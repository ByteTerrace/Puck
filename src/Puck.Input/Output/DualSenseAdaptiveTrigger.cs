namespace Puck.Input.Output;

/// <summary>
/// Encodes a <see cref="TriggerEffectSpec"/> into one DualSense trigger block — the mode byte plus its ten
/// parameter bytes — within a <c>0x02</c> output report. The <see cref="Devices.DualSenseController"/> composes
/// the right (L2/R2) blocks into the same report as rumble and the light bar (the trigger feedback sections are
/// disjoint from the motor and light-bar sections, so they never clobber one another), rather than writing a
/// separate raw report.
/// </summary>
/// <remarks>
/// The official zone-packed modes are emitted: <see cref="TriggerEffectKind.Feedback"/> and the per-zone
/// <see cref="TriggerEffectKind.ContinuousCurve"/> use the resistance mode <c>0x21</c>,
/// <see cref="TriggerEffectKind.Weapon"/> uses <c>0x25</c>, and <see cref="TriggerEffectKind.Vibration"/> uses
/// <c>0x26</c>. Zones index the pull in ten steps; each zone's 3-bit strength field is the strength minus one.
/// </remarks>
public static class DualSenseAdaptiveTrigger {
    /// <summary>The length of one trigger block: the mode byte plus ten parameter bytes.</summary>
    public const int TriggerBlockLength = 11;

    private const byte ModeFeedback = 0x21;
    private const byte ModeOff = 0x00;
    private const byte ModeVibration = 0x26;
    private const byte ModeWeapon = 0x25;
    private const int FrequencyParameterIndex = 8; // Vibration's frequency byte, after the 6-byte zone field

    /// <summary>Encodes <paramref name="spec"/> into <paramref name="block"/> (cleared first); the block must be at least <see cref="TriggerBlockLength"/> bytes.</summary>
    /// <param name="block">The destination trigger block within the output report.</param>
    /// <param name="spec">The effect to encode.</param>
    public static void WriteTriggerBlock(Span<byte> block, in TriggerEffectSpec spec) {
        block[..TriggerBlockLength].Clear();

        switch (spec.Kind) {
            case TriggerEffectKind.Feedback: {
                    var (activeZones, valueZones) = PackUniform(startZone: spec.StartZone, strength: spec.Strength);

                    block[0] = ModeFeedback;
                    WriteZones(parameters: block[1..], activeZones: activeZones, valueZones: valueZones);

                    break;
                }
            case TriggerEffectKind.Weapon: {
                    var bandZones = ((ushort)((1 << spec.StartZone) | (1 << spec.EndZone)));

                    block[0] = ModeWeapon;
                    block[1] = ((byte)(bandZones & 0xFF));
                    block[2] = ((byte)((bandZones >> 8) & 0xFF));
                    block[3] = ((byte)(spec.Strength - 1));

                    break;
                }
            case TriggerEffectKind.Vibration: {
                    var (activeZones, valueZones) = PackUniform(startZone: spec.StartZone, strength: spec.Strength);

                    block[0] = ModeVibration;
                    WriteZones(parameters: block[1..], activeZones: activeZones, valueZones: valueZones);
                    block[(1 + FrequencyParameterIndex)] = spec.Frequency;

                    break;
                }
            case TriggerEffectKind.ContinuousCurve: {
                    var (activeZones, valueZones) = PackCurve(spec: in spec);

                    block[0] = ModeFeedback;
                    WriteZones(parameters: block[1..], activeZones: activeZones, valueZones: valueZones);

                    break;
                }
            default: {
                    block[0] = ModeOff;

                    break;
                }
        }
    }

    // Sets the same 3-bit strength in every zone from `startZone` to the end of the pull (the uniform Feedback /
    // Vibration packing). Strength is 1..8 here (a zero-strength effect resolves to Off before reaching this).
    private static (ushort ActiveZones, uint ValueZones) PackUniform(byte startZone, byte strength) {
        var value3Bit = ((uint)(strength - 1)) & 0x07u;
        var valueZones = 0u;
        var activeZones = ((ushort)0);

        for (var zone = startZone; (zone < TriggerEffectSpec.ZoneCount); ++zone) {
            valueZones |= (value3Bit << (3 * zone));
            activeZones |= ((ushort)(1 << zone));
        }

        return (activeZones, valueZones);
    }

    // Packs an independent strength into each zone from the spec's curve: an active bit per non-zero zone and that
    // zone's (strength - 1) in its 3-bit field — the general form the uniform packing is a special case of.
    private static (ushort ActiveZones, uint ValueZones) PackCurve(in TriggerEffectSpec spec) {
        var valueZones = 0u;
        var activeZones = ((ushort)0);

        for (var zone = 0; (zone < TriggerEffectSpec.ZoneCount); ++zone) {
            var strength = spec.ZoneStrength(zone: zone);

            if (strength == 0) {
                continue;
            }

            valueZones |= ((((uint)(strength - 1)) & 0x07u) << (3 * zone));
            activeZones |= ((ushort)(1 << zone));
        }

        return (activeZones, valueZones);
    }

    // Writes the 2-byte active-zone mask followed by the 4-byte packed 3-bit-per-zone value field, little-endian.
    private static void WriteZones(Span<byte> parameters, ushort activeZones, uint valueZones) {
        parameters[0] = ((byte)(activeZones & 0xFF));
        parameters[1] = ((byte)((activeZones >> 8) & 0xFF));
        parameters[2] = ((byte)(valueZones & 0xFF));
        parameters[3] = ((byte)((valueZones >> 8) & 0xFF));
        parameters[4] = ((byte)((valueZones >> 16) & 0xFF));
        parameters[5] = ((byte)((valueZones >> 24) & 0xFF));
    }
}
