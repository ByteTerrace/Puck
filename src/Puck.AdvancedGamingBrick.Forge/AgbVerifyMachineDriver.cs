namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The forge's verify-machine driver: boots a freshly-forged ROM on a real Advanced GamingBrick (a zeroed
/// replacement BIOS, direct boot at the cartridge entry) and exposes the observation surface a verify battery
/// asserts against — EWRAM bytes, framebuffer pixels, CPU registers, and scripted keypad input. The settle
/// discipline is frame-counted: <see cref="Press"/> holds keys for <see cref="FramesPerPress"/> frames and
/// releases for <see cref="FramesPerRelease"/>, so a polling kernel sees exactly one pressed edge per press.
/// Memory reads go through the bus's debug path, which never advances the machine clock.
/// </summary>
public sealed class AgbVerifyMachineDriver : IDisposable {
    /// <summary>Frames a key set is held per <see cref="Press"/> (enough for at least one polled tick).</summary>
    public const int FramesPerPress = 4;
    /// <summary>Frames the pad is released per <see cref="Press"/> (enough to re-arm the edge detector).</summary>
    public const int FramesPerRelease = 4;

    private readonly AgbBus m_bus;
    private readonly AgbMachineInstance m_instance;
    private readonly string m_label;
    private readonly AdvancedGamingBrickMachine m_machine;

    /// <summary>Creates the driver: builds an isolated machine around <paramref name="rom"/> with a zeroed
    /// 16 KiB replacement BIOS and direct-boots it.</summary>
    /// <param name="rom">The ROM image to verify.</param>
    /// <param name="label">The battery's label for assertion diagnostics.</param>
    public AgbVerifyMachineDriver(byte[] rom, string label) {
        ArgumentNullException.ThrowIfNull(argument: rom);
        ArgumentException.ThrowIfNullOrEmpty(argument: label);

        m_label = label;
        m_instance = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(
            bios: new byte[ReplacementBios.ImageSize],
            rom: rom
        ));
        m_machine = m_instance.Machine;
        m_bus = ((m_machine.Bus as AgbBus)
            ?? throw new InvalidOperationException(message: "The verify driver needs the standard bus composition (debug reads observe memory without advancing the clock)."));

        m_machine.DirectBoot();
    }

    /// <summary>Reads one byte through the bus's clock-free debug path.</summary>
    /// <param name="address">The 32-bit bus address.</param>
    /// <returns>The byte at <paramref name="address"/>.</returns>
    public byte ReadByte(uint address) => m_bus.DebugRead8(address: address);
    /// <summary>Reads a little-endian halfword through the clock-free debug path.</summary>
    /// <param name="address">The 32-bit bus address.</param>
    /// <returns>The halfword at <paramref name="address"/>.</returns>
    public ushort ReadHalf(uint address) =>
        ((ushort)(ReadByte(address: address) | (ReadByte(address: (address + 1u)) << 8)));
    /// <summary>Reads a little-endian word through the clock-free debug path.</summary>
    /// <param name="address">The 32-bit bus address.</param>
    /// <returns>The word at <paramref name="address"/>.</returns>
    public uint ReadWord(uint address) =>
        ((uint)ReadHalf(address: address)) | (((uint)ReadHalf(address: (address + 2u))) << 16);
    /// <summary>Reads one framebuffer pixel as the PPU's packed 0xAABBGGRR value.</summary>
    /// <param name="x">The pixel column (0..239).</param>
    /// <param name="y">The pixel row (0..159).</param>
    /// <returns>The packed pixel.</returns>
    public uint ReadPixel(int x, int y) => m_machine.Framebuffer[((y * AgbHw.ScreenWidth) + x)];
    /// <summary>Reads a CPU general-purpose register as the currently visible bank sees it.</summary>
    /// <param name="index">The register number, 0–15.</param>
    /// <returns>The register value.</returns>
    public uint ReadRegister(int index) => m_machine.Cpu.GetRegister(index: index);
    /// <summary>Runs whole frames with a key set held (the KEYINPUT register is refreshed before every frame).</summary>
    /// <param name="keys">The active-high keys to hold.</param>
    /// <param name="frames">The number of frames to run.</param>
    public void RunFrames(AgbKeys keys, int frames) {
        for (var frame = 0; (frame < frames); frame++) {
            m_machine.SetKeyInput(keys: ((ushort)(AgbHw.KeyMask & ~((ushort)keys))));
            _ = m_machine.RunFrame();
        }
    }
    /// <summary>Presses a key set: hold <see cref="FramesPerPress"/> frames, release <see cref="FramesPerRelease"/>.</summary>
    /// <param name="keys">The active-high keys to press.</param>
    public void Press(AgbKeys keys) {
        RunFrames(frames: FramesPerPress, keys: keys);
        RunFrames(frames: FramesPerRelease, keys: AgbKeys.None);
    }
    /// <summary>Executes single instructions (for emitter probes that assert per-instruction effects).</summary>
    /// <param name="count">The number of instructions to step.</param>
    public void StepInstructions(int count) {
        for (var step = 0; (step < count); step++) {
            m_machine.Step();
        }
    }
    /// <inheritdoc/>
    public void Dispose() => m_instance.Dispose();
    /// <summary>Throws when a verify condition does not hold.</summary>
    /// <param name="condition">The condition that must hold.</param>
    /// <param name="message">What failed, in observable terms.</param>
    /// <param name="label">The battery's label.</param>
    public static void Assert(bool condition, string message, string label) {
        if (!condition) {
            throw new InvalidOperationException(message: $"{label} ROM verification failed: {message}");
        }
    }
    /// <summary>Throws when a verify condition does not hold, using this driver's label.</summary>
    /// <param name="condition">The condition that must hold.</param>
    /// <param name="message">What failed, in observable terms.</param>
    public void Require(bool condition, string message) => Assert(condition: condition, label: m_label, message: message);
}
