namespace Puck.HumbleGamingBrick;

/// <summary>
/// The system bus: the memory map and the master clock. It decodes every CPU access, routes it to the cartridge,
/// RAM, or an I/O block, and advances the clocked peripherals by the cycles each access costs (the deferred-cycle
/// model of <see cref="ICpuBus"/>). Exposes the peripherals it owns for reset seeding and output.
/// </summary>
public interface ISystemBus : ICpuBus {
    /// <summary>Gets the picture processing unit, whose framebuffer is the machine's video output.</summary>
    IPpu Ppu { get; }
    /// <summary>Gets the audio processing unit.</summary>
    IApu Apu { get; }
    /// <summary>Gets the divider/timer block.</summary>
    ITimer Timer { get; }
    /// <summary>Gets the serial link port.</summary>
    ISerial Serial { get; }
    /// <summary>Gets the joypad, for the host to feed button input.</summary>
    IJoypad Joypad { get; }
    /// <summary>Gets the master clock cycles (T-cycles / PPU dots) elapsed since reset.</summary>
    ulong ElapsedDots { get; }
    /// <summary>Reads a byte with no timing effect (for reset seeding and inspection).</summary>
    byte ReadByte(ushort address);
    /// <summary>Writes a byte with no timing effect (for reset seeding).</summary>
    void WriteByte(ushort address, byte value);
}
