using Puck.Demo.Forge.Framework;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Forge.Tune;

/// <summary>
/// The jukebox self-verify battery: boots the freshly-forged ROM on a REAL Humble machine (pure CPU, the same core
/// the demo's cabinets run) and asserts the state machine runs and START toggles play/stop — the state-graph half
/// of the forge's "verify by running" discipline; <see cref="RomForge.VerifyGameAudio"/>-style WAV capture (in
/// <see cref="TuneRom"/>) covers the sound path itself.
/// </summary>
internal static class TuneVerify {
    private const ulong TCyclesPerFrame = 70224UL;

    /// <summary>Runs the whole battery.</summary>
    /// <param name="rom">The ROM image.</param>
    public static void Run(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        using var driver = new Driver(rom: rom);

        // Boot: the machine reaches the (only) play state within a few frames, with the VBlank handler alive and
        // the loop marked playing.
        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == TuneProtocol.StatePlay), message: $"boot did not land on the play state (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.PendingState) == 0xFF), message: "the boot state request was never consumed (the frame dispatch is not running)");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) > 0), message: "the frame counter never advanced (the VBlank handler is not firing)");
        Assert(condition: (driver.Read(address: TuneProtocol.PlayingFlag) != 0), message: "the loop was not marked playing on boot");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.SoundMusicPointerHigh) != 0), message: "the music sequencer never started (pointer high byte still idle after boot)");

        // START stops the loop. The driver's idle convention is the POINTER HIGH BYTE being zero (the sequencer
        // tick's own "Idle?" check), not the whole pointer — MusicStop deliberately leaves the low byte alone.
        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: TuneProtocol.PlayingFlag) == 0), message: "START did not stop the loop (the playing flag is still set)");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.SoundMusicPointerHigh) == 0), message: "START did not silence the music sequencer (the pointer high byte is still live)");

        // START again restarts it.
        driver.Press(buttons: JoypadButtons.Start);
        Assert(condition: (driver.Read(address: TuneProtocol.PlayingFlag) != 0), message: "the second START did not restart the loop");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.SoundMusicPointerHigh) != 0), message: "the second START did not restart the music sequencer");

        Console.WriteLine(value: "tune verify | boot→play (loop running) | START stops | START restarts");
    }

    private static void Assert(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message: $"tune ROM verification failed: {message}");
        }
    }

    private sealed class Driver : IDisposable {
        private readonly ICpu m_cpu;
        private readonly IJoypad m_joypad;
        private readonly MachineInstance m_machine;
        private readonly ISystemBus m_bus;

        public Driver(byte[] rom) {
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

            VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: "tune");
        }

        public void Press(JoypadButtons buttons) {
            RunFrames(buttons: buttons, frames: 8);
            RunFrames(buttons: JoypadButtons.None, frames: 6);
        }

        public void Dispose() => m_machine.Dispose();
    }
}
