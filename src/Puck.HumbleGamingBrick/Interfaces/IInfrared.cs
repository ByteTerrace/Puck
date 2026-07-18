namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The machine's single infrared transceiver — the one physical IR LED + phototransistor the CGB infrared port (RP at
/// <c>0xFF56</c>) and the HuC1/HuC3 cartridge IR windows are two register views of. There is exactly one transceiver per
/// machine: whatever drives light out (RP bit 0, or a cart IR-mode write) is the SAME LED, and whatever reads light in
/// (RP bit 1, or a cart IR-mode read) is the SAME receiver, so the bus routes RP here and the IR mappers route their
/// window here rather than each modelling a private line.
/// <para>
/// The received-light line (<see cref="ReceivedLight"/>) reflects a linked peer transceiver's emitted light (see
/// <c>IrLinkSession</c>); with no peer it is always dark, so an unpaired machine reads exactly its lone-hardware value.
/// </para>
/// </summary>
public interface IInfrared {
    /// <summary>Reads the RP register (<c>0xFF56</c>): the last-written data-enable bits 7-6 and LED bit 0 read back, the
    /// unused bits read 1, and bit 1 (received light, 0 = light detected) is only meaningful — and only ever driven to 0
    /// by a lit peer — while the data-read-enable bits 7-6 are both set. The bus gates this to Color machines.</summary>
    /// <returns>The RP register value.</returns>
    byte ReadRegister();
    /// <summary>Writes the RP register (<c>0xFF56</c>): bit 0 drives this machine's IR LED and bits 7-6 arm the light
    /// receiver. The bus gates this to Color machines.</summary>
    /// <param name="value">The value being written.</param>
    void WriteRegister(byte value);

    /// <summary>Gets whether a linked peer's IR LED is currently lit, OR-ed with this machine's own emitted light when
    /// hardware self-sensing applies for the current model/cartridge — the received-light line the RP and cart IR read
    /// windows both report identically. <see langword="false"/> with no cable attached and self-sensing not applicable
    /// (the lone-hardware dark reading); see <c>InfraredPort.ReceivedLight</c> for the exact self-sensing gate.</summary>
    bool ReceivedLight { get; }
    /// <summary>Gets or sets whether a HuC1/HuC3 cartridge IR window is currently driving the shared IR LED (its bit 0).
    /// OR-ed with the RP LED bit into the light this machine emits to a peer, exactly as the two register views share the
    /// one physical LED on hardware.</summary>
    bool CartLightOut { get; set; }
}
