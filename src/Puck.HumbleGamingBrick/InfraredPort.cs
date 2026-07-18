using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The machine's one infrared transceiver: the shared IR LED (light out) and phototransistor (light in) that the CGB
/// infrared port (RP, <c>0xFF56</c>) and the HuC1/HuC3 cartridge IR windows are two register views of. It is NOT a
/// clocked component — light is a level, not a bit stream, so there is no per-cycle work and nothing on the hot path:
/// the state changes only on an RP or cart IR write (edge-driven), and the received line is derived from a linked peer
/// plus hardware self-sensing on read. All emulated state (the RP register byte, the cart IR LED latch) is captured for
/// the snapshot; the peer reference and the current model are host/composition wiring and are never serialized, exactly
/// like the serial cable's peer.
/// <para>
/// One physical medium. The light this machine emits is its RP LED bit (<c>RP</c> bit 0) OR-ed with the cart IR-mode LED
/// write — both views drive the same LED. The light it receives is a linked peer's emitted light (see
/// <c>IrLinkSession</c>) OR-ed with this machine's OWN emitted light when hardware self-sensing applies (see
/// <see cref="ReceivedLight"/>): a bare CGB with no cable reads its own lit LED back rather than reading dark.
/// </para>
/// <para>
/// RP semantics: a read returns the written data-enable bits 7-6 and LED bit 0, with the unused bits reading 1, and
/// received light clears bit 1 only while the data-read-enable bits 7-6 are both set. The analog receiver's warm-up/decay
/// curve is deliberately NOT reproduced here: it would require per-cycle ticking (against the zero-per-cycle-cost
/// constraint) and a digital light level is exact for the deterministic link and the synthetic gate. RP is Color-only;
/// the bus gates it on the model exactly as it gates KEY1. The transceiver itself is registered on every model because
/// HuC1/HuC3 cart IR works on monochrome hardware too.
/// </para>
/// </summary>
public sealed class InfraredPort : IInfrared, IInfraredPeer, ISnapshotable, IModeSwitchable {
    // RP read-back: keep the written bits 7-6 (data enable) and bit 0 (LED); force the unused bits (1-5) high.
    // 0x3E = 0b0011_1110 — bit 1 defaults high (no light), bits 2-5 are wired high. (Hardware also sets bit 4 except on
    // the CGB-E revision; Puck has one generic Color costume, so the common non-CGB-E read is used.)
    private const byte ReadBackKeepMask = 0xC1;
    private const byte ReadBackHighBits = 0x3E;
    // The data-read-enable gate: light is reported on bit 1 only when bits 7-6 are BOTH set.
    private const byte DataReadEnableMask = 0xC0;
    private const byte ReceivedLightBit = 0x02;
    private const byte LedOutBit = 0x01;

    // The last value written to RP. Only bits 7-6 and bit 0 survive a read-back, but the full byte is kept because that
    // matches how the register behaves on real hardware.
    private byte m_register;
    // The HuC1/HuC3 cart IR-mode LED latch: a second LED drive OR-ed with the RP LED bit.
    private bool m_cartLightOut;
    // The currently-emulated model, re-derived on a live device swap (IModeSwitchable) exactly like every other
    // capability gate; gates hardware self-sensing (see ReceivedLight). Not serialized — ModelState is the one snapshotted
    // authority and every gated component re-derives from it via Machine.Restore, same as m_supportsColor elsewhere.
    private ConsoleModel m_model;
    // The linked peer whose emitted light this transceiver receives; null is the no-cable default. Host wiring, never
    // serialized — attaching it cannot perturb determinism (see the class remarks and IrLinkSession).
    private IInfraredPeer? m_peer;

    /// <summary>Creates the transceiver seeded with the boot model's self-sensing gate.</summary>
    /// <param name="configuration">The machine configuration, whose <see cref="MachineConfiguration.Model"/> seeds the
    /// self-sensing gate before any live device swap re-derives it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public InfraredPort(MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: configuration);

        m_model = configuration.Model;
    }

    /// <summary>Gets or sets whether a HuC1/HuC3 cartridge's IR window is wired to this transceiver. Set once by the
    /// component factory when such a cartridge is loaded (see <see cref="IInfraredCartridge"/>); widens hardware
    /// self-sensing to the Advanced costume, exactly as it does on real hardware (see <see cref="ReceivedLight"/>). Not
    /// serialized: the attached cartridge's mapper kind is immutable machine composition, not emulated state, the same
    /// category as <see cref="ModelState"/>'s boot wiring.</summary>
    internal bool HasHuCCartridge { get; set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Hardware self-sensing (SameBoy oracle, verified against the checked-out source): <c>Core/memory.c</c>'s
    /// <c>GB_IO_RP</c> read case (~line 723) states "You will read your own IR LED if it's on" and clears RP bit 1 from
    /// <c>gb-&gt;effective_ir_input</c>, which <c>Core/timing.c</c>'s <c>ir_run</c> (~line 140) computes as
    /// <c>peer_input || cart_ir || (RP &amp; 1)</c> — peer light OR the local cart LED latch OR the local RP LED-output
    /// bit. <c>ir_run</c>'s own gate (same function, ~line 136) skips that computation entirely — freezing effective
    /// input toward false — unless the model is CGB-E-or-earlier in CGB mode, OR the cartridge is HuC1/HuC3; the RP
    /// register's own display additionally re-checks <c>model &lt;= GB_MODEL_CGB_E</c> before showing self-sensed light
    /// (memory.c ~line 728), a per-view split SameBoy's own comment flags as an interaction inaccuracy ("the way this
    /// thing works makes the CGB IR port behave inaccurately when used together with HUC1/3 IR ports", timing.c ~line
    /// 133). Puck's one shared transceiver deliberately does not fork that per-view split: RP, HuC1, and HuC3 all read
    /// the SAME <see cref="ReceivedLight"/> value, so the CGB costume (our stand-in for CGB-E-and-earlier) senses peer
    /// light plus its own RP LED bit plus its own cart LED latch unconditionally, and the Agb costume (newer-than-CGB-E)
    /// senses peer light plus its own emitted light only when a HuC1/HuC3 cartridge is present — matching Puck's
    /// existing correct AGB-RP-no-self-sense behavior when no HuC cartridge is loaded.
    /// </remarks>
    public bool ReceivedLight =>
        ((m_peer?.EmittedLight ?? false) || (SelfSensingEnabled && ((IInfraredPeer)this).EmittedLight));
    /// <inheritdoc/>
    public bool CartLightOut {
        get => m_cartLightOut;
        set => m_cartLightOut = value;
    }
    /// <inheritdoc/>
    bool IInfraredPeer.EmittedLight =>
        (((m_register & LedOutBit) != 0) || m_cartLightOut);

    // Whether this machine's own emitted light feeds back into ReceivedLight: always for the CGB costume; only with a
    // HuC1/HuC3 cartridge present for the Agb costume (or a bare Dmg — HuC IR works on monochrome hardware too). See the
    // ReceivedLight remarks for the SameBoy citation.
    private bool SelfSensingEnabled =>
        ((m_model == ConsoleModel.Cgb) || HasHuCCartridge);

    /// <inheritdoc/>
    public void ApplyModel(ConsoleModel model) =>
        m_model = model;

    /// <inheritdoc/>
    public byte ReadRegister() {
        var value = (byte)((m_register & ReadBackKeepMask) | ReadBackHighBits);

        // Bit 1 reads 0 (light detected) only while the data-read-enable bits 7-6 are both set AND a peer LED is lit.
        if (((m_register & DataReadEnableMask) == DataReadEnableMask) && ReceivedLight) {
            value &= unchecked((byte)~ReceivedLightBit);
        }

        return value;
    }
    /// <inheritdoc/>
    public void WriteRegister(byte value) =>
        m_register = value;

    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteByte(value: m_register);
        writer.WriteBoolean(value: m_cartLightOut);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_register = reader.ReadByte();
        m_cartLightOut = reader.ReadBoolean();
    }

    // Wires two transceivers as IR peers. Internal on purpose: IrLinkSession is the one blessed connect seam, because a
    // connected pair must also be STEPPED as a pair (the interleave keeps their light levels coherent) — the session owns
    // both halves. Guarded against double-linking exactly like the serial cable.
    internal static void Connect(InfraredPort first, InfraredPort second) {
        if (ReferenceEquals(objA: first, objB: second)) {
            throw new ArgumentException(message: "An infrared port cannot be linked to itself.", paramName: nameof(second));
        }

        if ((first.m_peer is not null) || (second.m_peer is not null)) {
            throw new InvalidOperationException(message: "An infrared port is already linked; disconnect its session first.");
        }

        first.m_peer = second;
        second.m_peer = first;
    }
    // Severs a port's link, clearing both ends; a no-op for an unlinked port.
    internal static void Disconnect(InfraredPort port) {
        if (port.m_peer is InfraredPort peer) {
            peer.m_peer = null;
            port.m_peer = null;
        }
    }
}
