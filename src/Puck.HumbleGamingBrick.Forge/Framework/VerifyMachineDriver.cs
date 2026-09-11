using Puck.HumbleGamingBrick.Interfaces;
using MachineInstance = Puck.GamingBricks.MachineInstance<Puck.HumbleGamingBrick.Machine, Puck.HumbleGamingBrick.MachineConfiguration>;

namespace Puck.HumbleGamingBrick.Forge.Framework;

public sealed class VerifyMachineDriver : IDisposable {
    private const ulong TCyclesPerFrame = 70224UL;

    private readonly ICpu m_cpu;
    private readonly IJoypad m_joypad;
    private readonly string m_label;
    private readonly MachineInstance m_machine;
    private readonly ISystemBus m_bus;
    private readonly IFramebuffer m_framebuffer;

    public VerifyMachineDriver(byte[] rom, string label) {
        m_label = label;
        m_machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.CgbE, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );
        m_bus = m_machine.GetRequiredService<ISystemBus>();
        m_framebuffer = m_machine.GetRequiredService<IFramebuffer>();
        m_cpu = m_machine.GetRequiredService<ICpu>();
        m_joypad = m_machine.GetRequiredService<IJoypad>();
    }

    public byte Read(ushort address) => m_bus.ReadByte(address: address);
    /// <summary>Writes one work-memory byte, for setting up a position a cartridge would take many inputs to reach.</summary>
    /// <param name="address">The bus address.</param>
    /// <param name="value">The byte to write.</param>
    /// <remarks>The cartridge cannot tell this from its own store, so what runs afterward is the cartridge's own code.</remarks>
    public void Write(ushort address, byte value) => m_bus.WriteByte(address: address, value: value);
    /// <summary>Reads a completed framebuffer pixel without advancing the machine.</summary>
    /// <param name="x">The pixel column, 0..159.</param>
    /// <param name="y">The pixel row, 0..143.</param>
    /// <returns>The packed 0x00RRGGBB pixel.</returns>
    public uint ReadPixel(int x, int y) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: x, other: m_framebuffer.Width);
        ArgumentOutOfRangeException.ThrowIfNegative(value: y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: y, other: m_framebuffer.Height);
        return m_framebuffer.Pixels[y * m_framebuffer.Width + x];
    }
    public int ReadWide(ushort address) => Read(address: address) | (Read(address: ((ushort)(address + 1))) << 8);
    public void RunFrames(JoypadButtons buttons, int frames) {
        for (var frame = 0; (frame < frames); frame++) {
            m_joypad.SetButtons(pressed: buttons);
            m_machine.Machine.Run(tCycles: TCyclesPerFrame);
        }

        VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: m_label);
    }
    public void Press(JoypadButtons buttons) {
        RunFrames(buttons: buttons, frames: 8);
        RunFrames(buttons: JoypadButtons.None, frames: 6);
    }
    public void Dispose() => m_machine.Dispose();
    public static void Assert(bool condition, string message, string label) {
        if (!condition) {
            throw new InvalidOperationException(message: $"{label} ROM verification failed: {message}");
        }
    }
}
