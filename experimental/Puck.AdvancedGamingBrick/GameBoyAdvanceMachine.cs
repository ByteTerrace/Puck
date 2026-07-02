namespace Puck.AdvancedGamingBrick;

/// <summary>
/// A complete Game Boy Advance instance: the CPU bound to the system bus, resolved together from one DI scope
/// so a machine never shares stateful peripherals with another. Mirrors the public surface of the DMG/CGB
/// <c>GameBoyMachine</c> — construct, boot, then step — so the conformance harness drives both cores the same way.
/// </summary>
public sealed class GameBoyAdvanceMachine {
    /// <summary>The address a cartridge begins executing from on the GBA.</summary>
    public const uint CartridgeEntryPoint = 0x08000000u;

    /// <summary>Master cycles per frame (228 scanlines × 1232 dots/scanline).</summary>
    public const int CyclesPerFrame = 228 * 1232;

    private readonly IArmCpu m_cpu;
    private readonly IGbaBus m_bus;
    private readonly GbaBus? m_concreteBus;
    private readonly IGbaPpu m_ppu;
    private readonly IGbaApu m_apu;

    /// <summary>Creates the machine from its CPU, bus, PPU, and APU (all injected from the per-machine scope).</summary>
    /// <param name="cpu">The ARM7TDMI core, already bound to <paramref name="bus"/>.</param>
    /// <param name="bus">The system bus.</param>
    /// <param name="ppu">The picture-processing unit.</param>
    /// <param name="apu">The audio-processing unit.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GameBoyAdvanceMachine(IArmCpu cpu, IGbaBus bus, IGbaPpu ppu, IGbaApu apu) {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(ppu);
        ArgumentNullException.ThrowIfNull(apu);

        m_cpu = cpu;
        m_bus = bus;
        m_concreteBus = bus as GbaBus;
        m_ppu = ppu;
        m_apu = apu;
    }

    /// <summary>Gets the CPU core.</summary>
    public IArmCpu Cpu => m_cpu;

    /// <summary>Gets the system bus.</summary>
    public IGbaBus Bus => m_bus;

    /// <summary>Gets the picture-processing unit.</summary>
    public IGbaPpu Ppu => m_ppu;

    /// <summary>Gets the audio-processing unit.</summary>
    public IGbaApu Apu => m_apu;

    /// <summary>Gets the most recent 240×160 frame as packed 0xAARRGGBB pixels.</summary>
    public ReadOnlySpan<uint> Framebuffer => m_ppu.Framebuffer;

    /// <summary>Boots straight into the cartridge, skipping the BIOS, with the standard post-BIOS machine state.</summary>
    public void DirectBoot() {
        m_cpu.SetupDirectBoot(entryPoint: CartridgeEntryPoint);
    }

    /// <summary>Sets the KEYINPUT register (active-low: clear bit = pressed). Bit layout: 0=A, 1=B, 2=Select,
    /// 3=Start, 4=Right, 5=Left, 6=Up, 7=Down, 8=R, 9=L.</summary>
    public void SetKeyInput(ushort keys) {
        m_concreteBus?.SetKeyInput(keys: keys);
    }

    /// <summary>Executes one instruction (or a pending exception entry).</summary>
    public void Step() {
        m_cpu.Step();
    }

    /// <summary>Runs the machine for one full frame (~280,896 master cycles). Returns the number of
    /// instructions executed.</summary>
    public int RunFrame() {
        if (m_concreteBus is null) {
            return 0;
        }

        var target = m_concreteBus.Cycles + CyclesPerFrame;
        var steps = 0;

        while (m_concreteBus.Cycles < target) {
            m_cpu.Step();
            ++steps;
        }

        return steps;
    }
}
