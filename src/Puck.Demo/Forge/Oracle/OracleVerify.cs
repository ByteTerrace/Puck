using Puck.Demo.Forge.Framework;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Forge;

/// <summary>
/// The ORACLE self-verify battery: boots the freshly-forged ROM on REAL Humble machines (pure CPU, the same core the
/// demo's cabinets run) and asserts the game's observable behaviour — (a) the title renders, (b) an A press types out
/// a fortune, (c) the SAME press tick produces the SAME fortune across two fresh machines while a different tick
/// produces a different one (the determinism joke, proven), and (d) a frame-perfect, power-on A press reveals the
/// hidden fortune. Throws on any violation.
/// </summary>
internal static class OracleVerify {
    private const ulong TCyclesPerFrame = 70224UL;

    /// <summary>Runs the whole battery.</summary>
    /// <param name="rom">The ROM image.</param>
    public static void Run(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        AssertTitleRenders(rom: rom);

        var typedIndex = AssertPressTypesFortune(rom: rom);
        var determinismIndex = AssertPressTickDeterminism(rom: rom);
        var hiddenIndex = AssertFramePerfectHiddenFortune(rom: rom);

        Console.WriteLine(value: $"oracle verify | title renders | A press → typed fortune #{typedIndex} \"{OracleProtocol.Fortunes[typedIndex]}\" | same press tick → same fortune #{determinismIndex} across two machines (different tick differs) | frame-perfect → hidden fortune #{hiddenIndex} \"{OracleProtocol.Fortunes[hiddenIndex]}\"");
    }

    // (a) The title renders: within a few frames the machine reaches the title state with the VBlank handler alive and
    // the boot state request consumed, and the framebuffer carries text (more than one distinct colour — the word and
    // the prompt over the field, not a blank screen).
    private static void AssertTitleRenders(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == OracleProtocol.StateTitle), message: $"boot did not land on the title state (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.PendingState) == 0xFF), message: "the boot state request was never consumed (the frame dispatch is not running)");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) > 0), message: "the frame counter never advanced (the VBlank handler is not firing)");
        Assert(condition: (driver.DistinctColourCount() > 1), message: "the title screen is a blank field (nothing rendered)");
    }

    // (b) An A press produces a typed-out fortune: after some idle frames a press moves to the reading state, the
    // typewriter advances the cursor character by character, and the fortune eventually completes (DoneFlag), with the
    // ASK AGAIN prompt then live — a follow-up A press asks again (a fresh reading), and B returns to the title.
    private static int AssertPressTypesFortune(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: 40);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == OracleProtocol.StateReading), message: "an A press on the title did not open a reading");

        var index = driver.Read(address: OracleProtocol.FortuneIndex);

        Assert(condition: (index < OracleProtocol.FortuneCount), message: $"a normal press selected fortune {index} (outside 0..{(OracleProtocol.FortuneCount - 1)} — the modulo is broken)");

        driver.RunFrames(buttons: JoypadButtons.None, frames: 30);
        Assert(condition: ((driver.Read(address: OracleProtocol.CursorColumn) > OracleProtocol.ReadingBaseColumn) || (driver.Read(address: OracleProtocol.CursorRow) > OracleProtocol.ReadingBaseRow)), message: "the typewriter never advanced the cursor (the reveal is dead)");
        Assert(condition: (driver.Read(address: OracleProtocol.DoneFlag) == 0), message: "the fortune finished before the typewriter could plausibly have typed it (no reveal)");

        Assert(condition: driver.RunUntilFlag(address: OracleProtocol.DoneFlag, value: 1, buttons: JoypadButtons.None, maxFrames: 400), message: "the fortune never finished typing");
        Assert(condition: (driver.DistinctColourCount() > 1), message: "the completed reading is a blank field (the fortune never rendered)");

        // ASK AGAIN is live: B returns to the title, A opens a fresh reading.
        driver.Press(buttons: JoypadButtons.B);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == OracleProtocol.StateTitle), message: "B on a completed reading did not return to the title");

        driver.RunFrames(buttons: JoypadButtons.None, frames: 5);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == OracleProtocol.StateReading), message: "a second A press did not open another reading");

        return index;
    }

    // (c) The determinism joke, proven: two FRESH machines pressing A after the SAME idle count select the identical
    // fortune (same press tick → same fortune, always right under a replay), while a machine pressing one frame later
    // selects a different one (real input entropy — it feels random to a human).
    private static int AssertPressTickDeterminism(byte[] rom) {
        var first = SelectFortuneAt(rom: rom, idleFrames: 40);
        var replay = SelectFortuneAt(rom: rom, idleFrames: 40);
        var shifted = SelectFortuneAt(rom: rom, idleFrames: 41);

        Assert(condition: (first == replay), message: $"the SAME press tick produced different fortunes ({first} vs {replay} — replay determinism broken)");
        Assert(condition: (first != shifted), message: $"pressing one frame later produced the SAME fortune ({first} — no press-tick entropy)");

        return first;
    }

    // (d) The easter egg: a scripted replay that already holds A on the first sampled frame after power-on hits the
    // frame-perfect window and bypasses the modulo for the hidden fortune. THE EXACT TRIGGER (a test hits it like
    // this): from a FRESH reset, drive A held from the very first frame input is sampled — RunFrames(A, ...) with no
    // prior idle. A human, whose press always lands on a later frame, can never reach it.
    private static int AssertFramePerfectHiddenFortune(byte[] rom) {
        using var driver = new Driver(rom: rom);

        // A held from the very first sampled frame through the first title tick — the frame-perfect window. Boot spans a
        // few host frames before that first tick fires, so hold A across them (a scripted replay holds it continuously).
        driver.RunFrames(buttons: JoypadButtons.A, frames: 6);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == OracleProtocol.StateReading), message: "a frame-perfect held-A power-on did not open a reading");
        Assert(condition: (driver.Read(address: OracleProtocol.FortuneIndex) == OracleProtocol.HiddenFortuneIndex), message: $"a frame-perfect press did not reveal the hidden fortune (index {driver.Read(address: OracleProtocol.FortuneIndex)}, expected {OracleProtocol.HiddenFortuneIndex})");

        return OracleProtocol.HiddenFortuneIndex;
    }

    // Boot a fresh machine, idle, press A on the title, and return the fortune it selected at that press tick.
    private static int SelectFortuneAt(byte[] rom, int idleFrames) {
        using var driver = new Driver(rom: rom);

        driver.RunFrames(buttons: JoypadButtons.None, frames: idleFrames);
        driver.Press(buttons: JoypadButtons.A);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == OracleProtocol.StateReading), message: "an A press on the title did not open a reading");

        return driver.Read(address: OracleProtocol.FortuneIndex);
    }
    private static void Assert(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message: $"oracle ROM verification failed: {message}");
        }
    }

    // One real Humble CGB machine: frame stepping, joypad edges, work-RAM peeks, and the rendered framebuffer.
    private sealed class Driver : IDisposable {
        private readonly ICpu m_cpu;
        private readonly IFramebuffer m_framebuffer;
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
            m_framebuffer = m_machine.GetRequiredService<IFramebuffer>();
            m_joypad = m_machine.GetRequiredService<IJoypad>();
        }

        public byte Read(ushort address) => m_bus.ReadByte(address: address);
        public int ReadWide(ushort address) => Read(address: address) | (Read(address: (ushort)(address + 1)) << 8);
        public void RunFrames(JoypadButtons buttons, int frames) {
            for (var frame = 0; (frame < frames); frame++) {
                m_joypad.SetButtons(pressed: buttons);
                m_machine.Machine.Run(tCycles: TCyclesPerFrame);
            }

            VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: "oracle");
        }
        public void Press(JoypadButtons buttons) {
            RunFrames(buttons: buttons, frames: 2);
            RunFrames(buttons: JoypadButtons.None, frames: 2);
        }
        public bool RunUntilFlag(ushort address, byte value, JoypadButtons buttons, int maxFrames) {
            for (var frame = 0; (frame < maxFrames); frame++) {
                RunFrames(buttons: buttons, frames: 1);

                if (Read(address: address) == value) {
                    return true;
                }
            }

            return false;
        }

        // The number of distinct colours in the last rendered frame — > 1 means text was drawn over the field.
        public int DistinctColourCount() {
            var seen = new HashSet<uint>();

            foreach (var pixel in m_framebuffer.Pixels) {
                _ = seen.Add(item: pixel);
            }

            return seen.Count;
        }
        public void Dispose() => m_machine.Dispose();
    }
}
