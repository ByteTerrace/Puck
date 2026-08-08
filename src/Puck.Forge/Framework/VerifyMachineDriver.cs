using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Forge.Framework;

internal sealed class VerifyMachineDriver : IDisposable {
    private const ulong TCyclesPerFrame = 70224UL;

    private readonly ICpu m_cpu;
    private readonly IJoypad m_joypad;
    private readonly string m_label;
    private readonly MachineInstance m_machine;
    private readonly ISystemBus m_bus;

    public VerifyMachineDriver(byte[] rom, string label) {
        m_label = label;
        m_machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );
        m_bus = m_machine.GetRequiredService<ISystemBus>();
        m_cpu = m_machine.GetRequiredService<ICpu>();
        m_joypad = m_machine.GetRequiredService<IJoypad>();
    }

    public byte Read(ushort address) => m_bus.ReadByte(address: address);
    public int ReadWide(ushort address) => (Read(address: address) | (Read(address: (ushort)(address + 1)) << 8));
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
