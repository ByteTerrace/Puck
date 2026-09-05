namespace Puck.HumbleGamingBrick;

/// <summary>
/// The single authority for whether Color silicon is currently running its DMG-compatibility mode — the hardware fact
/// KEY0 (<c>0xFF4C</c>) carries: a real Color boot ROM writes it once, right before it locks and hands off, for a
/// cartridge whose header does not declare Color support. Every Color-only register a compatibility-mode cartridge
/// cannot reach (KEY1, RP, the palette DATA ports, SVBK, VBK, the HDMA registers, OPRI) asks this ONE question instead
/// of re-deriving <c>(model.SupportsColor() &amp;&amp; !header.SupportsColor)</c> itself, so the fact is computed in
/// exactly one place.
/// </summary>
/// <remarks>Seeded at construction from the boot model and the immutable cartridge header — exactly the answer a real
/// boot ROM's own header inspection produces — and re-derived on a live model swap or a snapshot restore (see
/// <see cref="ApplyModel"/>, called by <see cref="Machine.ApplyModel"/> alongside every other capability gate). A real
/// boot ROM run confirms (or, for a hand-authored header a boot ROM disagrees with, corrects) the same fact through
/// <see cref="ApplyKey0"/> when it executes the write; the value is not itself snapshotted, since it is fully
/// re-derivable from the (snapshotted) model and the (immutable) header exactly like every other component's cached
/// capability flag.</remarks>
public sealed class DmgCompatibilityState : IModeSwitchable {
    /// <summary>The KEY0 bit the Color boot ROM sets to hand off in DMG-compatibility mode.</summary>
    public const byte Key0CompatibilityBit = 0x04;

    private readonly CartridgeHeader m_header;

    private bool m_isActive;

    /// <summary>Creates the authority seeded from the boot model and the cartridge header.</summary>
    /// <param name="configuration">The machine configuration, whose <see cref="MachineConfiguration.Model"/> seeds the mode.</param>
    /// <param name="header">The cartridge header, whose <see cref="CartridgeHeader.SupportsColor"/> is the other half of the fact.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public DmgCompatibilityState(MachineConfiguration configuration, CartridgeHeader header) {
        ArgumentNullException.ThrowIfNull(argument: configuration);
        ArgumentNullException.ThrowIfNull(argument: header);

        m_header = header;
        m_isActive = Derive(
            header: header,
            model: configuration.Model
        );
    }

    /// <summary>Gets whether Color silicon is currently running its DMG-compatibility mode: the Color-only registers
    /// (KEY1, RP, the palette data ports, SVBK/VBK, HDMA, OPRI) read sealed rather than live. Always
    /// <see langword="false"/> on hardware without Color silicon.</summary>
    public bool IsActive =>
        m_isActive;

    /// <inheritdoc/>
    public void ApplyModel(ConsoleModel model) =>
        m_isActive = Derive(
            header: m_header,
            model: model
        );
    /// <summary>Applies a real boot ROM's write to KEY0 (<c>0xFF4C</c>) — the hardware event that hands the mode to
    /// the cartridge. Bit 2 is the documented DMG-compatibility flag; the undocumented PGB bits are not modeled.</summary>
    /// <param name="value">The byte written to FF4C.</param>
    public void ApplyKey0(byte value) =>
        m_isActive = ((value & Key0CompatibilityBit) != 0);

    private static bool Derive(ConsoleModel model, CartridgeHeader header) =>
        (model.SupportsColor() && !header.SupportsColor);
}
