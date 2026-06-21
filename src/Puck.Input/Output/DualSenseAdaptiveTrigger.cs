namespace Puck.Input.Output;

/// <summary>
/// Builds DualSense adaptive-trigger output reports for the raw effect channel
/// (<see cref="IGamepadOutput.SendEffect"/>). Adaptive triggers have no portable shape across controller
/// families, so they ride the raw escape hatch rather than a typed cross-family capability. Each method returns
/// a complete USB output report (id <c>0x02</c>) that applies one effect to <b>both</b> the L2 and R2 triggers.
/// </summary>
/// <remarks>
/// <para>
/// The report asserts only the trigger-feedback flags in <c>valid_flag0</c> (<c>0x04</c> right, <c>0x08</c>
/// left), so it leaves the motor and light-bar sections — written separately by the rumble/LED path — untouched:
/// the DualSense firmware applies only the report sections whose flags are set. The effect persists in the
/// controller until a new trigger report (e.g. <see cref="Off"/>) replaces it. Bluetooth uses a different report
/// (<c>0x31</c>, +1 block shift, trailing CRC32) and is not built here, matching the USB-only rumble/LED paths.
/// </para>
/// <para>
/// Two families are offered. The <b>official zone-packed effects</b> — <see cref="Feedback"/> (<c>0x21</c>),
/// <see cref="Weapon"/> (<c>0x25</c>), <see cref="Vibration"/> (<c>0x26</c>) — pack a 10-zone strength array into
/// the trigger block and are the recommended, range-checked effects. The <b>legacy "simple" effects</b> —
/// <see cref="Resistance"/> (<c>0x01</c> <c>Simple_Feedback</c>) and <see cref="Section"/> (<c>0x02</c>
/// <c>Simple_Weapon</c>) — take raw <c>0..255</c> positions, are unvalidated firmware leftovers, but still
/// function and are the simplest to drive.
/// </para>
/// </remarks>
public static class DualSenseAdaptiveTrigger
{
    // Length of the USB output report (id byte + common block). Bluetooth's 0x31 report is larger.
    private const int UsbOutputReportLength = 64;
    private const byte UsbOutputReportId = 0x02;

    // valid_flag0: apply the right/left trigger force-feedback sections (disjoint from the vibration bits, so
    // motor state set by the rumble path survives a trigger write).
    private const byte FlagRightTriggerFfb = 0x04;
    private const byte FlagLeftTriggerFfb = 0x08;

    // Mode byte + parameter offsets of each trigger block within the report (right then left), matching the
    // DualSense output-report layout the light-bar offsets in DualSenseController come from. Each block is a mode
    // byte followed by up to 10 parameter bytes ([+1..+10]); the two blocks are disjoint.
    private const int RightTriggerOffset = 11;
    private const int LeftTriggerOffset = 22;
    private const int TriggerBlockParameterCount = 10;

    // Legacy "simple" effect modes (raw 0..255 parameters, no validation):
    //   0x00 off          — no parameters.
    //   0x01 Simple_Feedback — [+1] position, [+2] strength: constant resistance of `strength` from `position` on.
    //   0x02 Simple_Weapon   — [+1] start, [+2] end, [+3] strength: resistance of `strength` within [start, end].
    private const byte ModeOff = 0x00;
    private const byte ModeContinuousResistance = 0x01; // Simple_Feedback
    private const byte ModeSectionResistance = 0x02;    // Simple_Weapon

    // Official zone-packed effect modes. Zones index the trigger pull in 10 steps (0 = released .. 9 = pressed);
    // strength/amplitude is 1..8 (0 disables). Weapon's start/end are constrained to the firmware's valid band.
    private const byte ModeFeedback = 0x21;
    private const byte ModeWeapon = 0x25;
    private const byte ModeVibration = 0x26;
    private const int ZoneCount = 10;
    private const byte LastZone = 9;
    private const byte MaxStrength = 8;
    private const byte WeaponMinStart = 2;
    private const byte WeaponMaxStart = 7;
    private const byte WeaponMaxEnd = 8;

    /// <summary>Builds a report that clears both triggers' adaptive resistance.</summary>
    /// <returns>A raw USB output report for <see cref="IGamepadOutput.SendEffect"/>.</returns>
    public static byte[] Off() {
        return BuildEffect(mode: ModeOff, parameters: ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Builds an official <b>Feedback</b> effect (<c>0x21</c>): uniform resistance from
    /// <paramref name="position"/> to the end of the pull on both triggers (the trigger feels stiff once
    /// depressed past the start). This is the validated, recommended form of <see cref="Resistance"/>.
    /// </summary>
    /// <param name="position">The zone where resistance begins, 0 (released) to 9 (pressed).</param>
    /// <param name="strength">The resistance strength, 0 (off) to 8 (maximum).</param>
    /// <returns>A raw USB output report for <see cref="IGamepadOutput.SendEffect"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> &gt; 9 or <paramref name="strength"/> &gt; 8.</exception>
    public static byte[] Feedback(byte position, byte strength) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: position, other: LastZone);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: strength, other: MaxStrength);

        if (strength == 0) {
            return Off();
        }

        var (activeZones, valueZones) = PackZonesFromPosition(position: position, value3Bit: ((uint)((strength - 1) & 0x07)));

        Span<byte> parameters = stackalloc byte[6];

        WriteZoneParameters(parameters: parameters, activeZones: activeZones, valueZones: valueZones);

        return BuildEffect(mode: ModeFeedback, parameters: parameters);
    }

    /// <summary>
    /// Builds an official <b>Weapon</b> effect (<c>0x25</c>): resistance between <paramref name="startPosition"/>
    /// and <paramref name="endPosition"/> that gives way (snaps) once pushed through, on both triggers. This is
    /// the validated, recommended form of <see cref="Section"/>.
    /// </summary>
    /// <param name="startPosition">Where the resistant band begins, zone 2 to 7.</param>
    /// <param name="endPosition">Where the band ends, greater than <paramref name="startPosition"/> up to zone 8.</param>
    /// <param name="strength">The resistance strength, 0 (off) to 8 (maximum).</param>
    /// <returns>A raw USB output report for <see cref="IGamepadOutput.SendEffect"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside the firmware's valid band.</exception>
    public static byte[] Weapon(byte startPosition, byte endPosition, byte strength) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: startPosition, other: WeaponMinStart);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: startPosition, other: WeaponMaxStart);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: endPosition, other: WeaponMaxEnd);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: endPosition, other: startPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: strength, other: MaxStrength);

        if (strength == 0) {
            return Off();
        }

        var startAndStopZones = ((ushort)((1 << startPosition) | (1 << endPosition)));

        Span<byte> parameters = stackalloc byte[3];

        parameters[0] = ((byte)(startAndStopZones & 0xFF));
        parameters[1] = ((byte)((startAndStopZones >> 8) & 0xFF));
        parameters[2] = ((byte)(strength - 1));

        return BuildEffect(mode: ModeWeapon, parameters: parameters);
    }

    /// <summary>
    /// Builds an official <b>Vibration</b> effect (<c>0x26</c>): the triggers vibrate from
    /// <paramref name="position"/> to the end of the pull at the given amplitude and frequency.
    /// </summary>
    /// <param name="position">The zone where vibration begins, 0 (released) to 9 (pressed).</param>
    /// <param name="amplitude">The vibration amplitude, 0 (off) to 8 (maximum).</param>
    /// <param name="frequency">The vibration frequency in hertz, 1 to 255 (0 disables the effect).</param>
    /// <returns>A raw USB output report for <see cref="IGamepadOutput.SendEffect"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> &gt; 9 or <paramref name="amplitude"/> &gt; 8.</exception>
    public static byte[] Vibration(byte position, byte amplitude, byte frequency) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: position, other: LastZone);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: amplitude, other: MaxStrength);

        if ((amplitude == 0) || (frequency == 0)) {
            return Off();
        }

        var (activeZones, valueZones) = PackZonesFromPosition(position: position, value3Bit: ((uint)((amplitude - 1) & 0x07)));

        // Zone mask + amplitudes occupy [+1..+6]; the frequency byte sits at [+9] (parameter index 8). Clear first
        // so the gap bytes [6]/[7] are zero even if SkipLocalsInit is ever enabled (stackalloc would skip zeroing).
        Span<byte> parameters = stackalloc byte[9];

        parameters.Clear();
        WriteZoneParameters(parameters: parameters, activeZones: activeZones, valueZones: valueZones);
        parameters[8] = frequency;

        return BuildEffect(mode: ModeVibration, parameters: parameters);
    }

    /// <summary>
    /// Builds a <b>legacy</b> constant-resistance effect (<c>0x01</c> <c>Simple_Feedback</c>) from
    /// <paramref name="startPosition"/> to the end of the pull on both triggers. Prefer <see cref="Feedback"/>.
    /// </summary>
    /// <param name="startPosition">Where resistance begins, 0 (fully released) to 255 (fully pressed).</param>
    /// <param name="force">The resistance strength, 0 (none) to 255 (maximum).</param>
    /// <returns>A raw USB output report for <see cref="IGamepadOutput.SendEffect"/>.</returns>
    public static byte[] Resistance(byte startPosition, byte force) {
        Span<byte> parameters = [startPosition, force,];

        return BuildEffect(mode: ModeContinuousResistance, parameters: parameters);
    }

    /// <summary>
    /// Builds a <b>legacy</b> banded-resistance effect (<c>0x02</c> <c>Simple_Weapon</c>) between
    /// <paramref name="startPosition"/> and <paramref name="endPosition"/> on both triggers. Prefer
    /// <see cref="Weapon"/>.
    /// </summary>
    /// <param name="startPosition">Where the resistant band begins, 0 to 255.</param>
    /// <param name="endPosition">Where the resistant band ends, 0 to 255.</param>
    /// <param name="strength">
    /// The resistance strength within the band, 0 (none) to 255 (maximum). Mode <c>0x02</c>
    /// (<c>Simple_Weapon</c>) carries the strength in its third parameter byte; a zero value leaves the band
    /// exerting <b>no</b> force, so it must be non-zero to be felt.
    /// </param>
    /// <returns>A raw USB output report for <see cref="IGamepadOutput.SendEffect"/>.</returns>
    public static byte[] Section(byte startPosition, byte endPosition, byte strength = 255) {
        Span<byte> parameters = [startPosition, endPosition, strength,];

        return BuildEffect(mode: ModeSectionResistance, parameters: parameters);
    }

    // Packs a 3-bit value into every zone from `position` to the end, returning the active-zone mask and the
    // 32-bit packed value field. Shared by Feedback and Vibration.
    private static (ushort ActiveZones, uint ValueZones) PackZonesFromPosition(byte position, uint value3Bit) {
        uint valueZones = 0;
        ushort activeZones = 0;

        for (var zone = position; (zone < ZoneCount); ++zone) {
            valueZones |= (value3Bit << (3 * zone));
            activeZones |= ((ushort)(1 << zone));
        }

        return (activeZones, valueZones);
    }

    // Writes the 2-byte active-zone mask followed by the 4-byte packed value field into a trigger block's
    // parameter span ([+1..+6] within the block), little-endian.
    private static void WriteZoneParameters(Span<byte> parameters, ushort activeZones, uint valueZones) {
        parameters[0] = ((byte)(activeZones & 0xFF));
        parameters[1] = ((byte)((activeZones >> 8) & 0xFF));
        parameters[2] = ((byte)(valueZones & 0xFF));
        parameters[3] = ((byte)((valueZones >> 8) & 0xFF));
        parameters[4] = ((byte)((valueZones >> 16) & 0xFF));
        parameters[5] = ((byte)((valueZones >> 24) & 0xFF));
    }

    // Writes one effect (mode byte + up to 10 parameter bytes) into both the right and left trigger blocks of a
    // fresh 0x02 USB output report. The report is zero-initialized, so any parameter bytes the effect leaves
    // unset stay zero, and the two blocks never overlap (RightTriggerOffset + 1 + 10 == LeftTriggerOffset).
    private static byte[] BuildEffect(byte mode, ReadOnlySpan<byte> parameters) {
        var report = new byte[UsbOutputReportLength];

        report[0] = UsbOutputReportId;
        report[1] = (FlagRightTriggerFfb | FlagLeftTriggerFfb); // valid_flag0

        WriteTriggerBlock(report: report, blockOffset: RightTriggerOffset, mode: mode, parameters: parameters);
        WriteTriggerBlock(report: report, blockOffset: LeftTriggerOffset, mode: mode, parameters: parameters);

        return report;
    }
    private static void WriteTriggerBlock(byte[] report, int blockOffset, byte mode, ReadOnlySpan<byte> parameters) {
        report[blockOffset] = mode;
        parameters[..Math.Min(val1: parameters.Length, val2: TriggerBlockParameterCount)].CopyTo(destination: report.AsSpan(start: (blockOffset + 1)));
    }
}
