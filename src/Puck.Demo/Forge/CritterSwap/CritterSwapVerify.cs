using Puck.Demo.Forge.Framework;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Forge;

/// <summary>
/// The CRITTER-SWAP self-verify battery: boots the freshly-forged ROM on REAL Humble machines (the same core the demo's
/// cabinets run) and asserts the game's observable behaviour — (a) the title renders the held critter, (b) a lone cart
/// that offers a trade times out to NO LINK instead of freezing, (c) two linked machines holding DIFFERENT critters
/// swap them over a real <see cref="SerialLinkSession"/>: both handshake, both commit the partner's critter to their
/// own battery SRAM, and the exchanged wire traffic + both final snapshots are REPLAY-IDENTICAL across two fresh runs
/// (the <c>SerialLinkStage</c> rigor, mirrored here as the forge's "verify by running" gate). Throws on any violation,
/// BEFORE the forge writes a single byte.
/// </summary>
internal static class CritterSwapVerify {
    private const ulong TCyclesPerFrame = 70224UL;
    // How long the linked pair runs after both offer — comfortably past the protocol's worst-case (a couple of retry
    // rounds of backoff + the four-byte block + the ACK), with headroom, so a clean run always lands well inside it.
    private const int LinkedFrames = 320;

    // The two parties: distinct species AND distinct levels, so the swap is visible on BOTH payload bytes.
    private const byte SpeciesA = 0;
    private const byte LevelA = 0x05;
    private const byte LevelB = 0x08;
    private const byte SpeciesB = 3;

    /// <summary>Runs the whole battery. Throws loudly on any violation.</summary>
    /// <param name="rom">The ROM image.</param>
    public static void Run(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        AssertTitleRenders(rom: rom);
        AssertLoneOfferTimesOut(rom: rom);

        var first = RunLinkedSwap(rom: rom);

        AssertSwapped(result: first);

        var second = RunLinkedSwap(rom: rom);

        if (!first.FirstState.ContentEquals(other: second.FirstState)) {
            throw new InvalidOperationException(message: "critterswap verification failed: machine A's final state differed between two identical linked swaps (non-deterministic).");
        }

        if (!first.SecondState.ContentEquals(other: second.SecondState)) {
            throw new InvalidOperationException(message: "critterswap verification failed: machine B's final state differed between two identical linked swaps (non-deterministic).");
        }

        if ((first.FirstTraffic != second.FirstTraffic) || (first.SecondTraffic != second.SecondTraffic)) {
            throw new InvalidOperationException(message: "critterswap verification failed: the exchanged link traffic differed between two identical runs (non-deterministic).");
        }

        Console.WriteLine(value: $"critterswap verify | title renders held critter | lone offer -> NO LINK (no freeze) | two machines swapped {CritterSwapProtocol.Species[SpeciesA].Name} LV{LevelA:X2} <-> {CritterSwapProtocol.Species[SpeciesB].Name} LV{LevelB:X2} over the cable, both SRAMs committed | traffic + final snapshots replay-identical across two runs ({first.FirstState.Size}+{first.SecondState.Size} state bytes)");
    }

    // (a) The title renders the held critter: within a few frames the machine reaches the title state, the held critter
    // is the seeded one, and the framebuffer carries more than one colour (the face + name over the field, not blank).
    private static void AssertTitleRenders(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.ImportSave(species: SpeciesB, level: LevelB);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);

        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == CritterSwapProtocol.StateTitle), message: $"boot did not land on the title state (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        Assert(condition: (driver.Read(address: CritterSwapProtocol.SaveSpecies) == SpeciesB), message: $"the title did not load the seeded critter (species {driver.Read(address: CritterSwapProtocol.SaveSpecies)}, expected {SpeciesB})");
        Assert(condition: (driver.DistinctColourCount() > 1), message: "the title screen is a blank field (the critter never rendered)");
    }

    // (b) A lone cart offering a trade must not hang: with no cable the protocol's listens/receives time out every round
    // and, after the retry cap, it lands on the NO LINK screen — the disconnect story the LinkProtocolModule documents.
    private static void AssertLoneOfferTimesOut(byte[] rom) {
        using var driver = new Driver(rom: rom);

        driver.ImportSave(species: SpeciesA, level: LevelA);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        driver.RunFrames(buttons: JoypadButtons.Start, frames: 2);
        driver.RunFrames(buttons: JoypadButtons.None, frames: 2);

        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == CritterSwapProtocol.StateTrade), message: "a START press on the title did not open the trade");
        Assert(condition: driver.RunUntilState(state: CritterSwapProtocol.StateNoLink, maxFrames: 200), message: "a lone cart's trade never reached NO LINK (the bounded protocol hung instead of giving up)");
        Assert(condition: (driver.Read(address: CritterSwapProtocol.SaveSpecies) == SpeciesA), message: "a failed lone trade must not have altered the held critter");
    }

    // (c) The swap over a real session: two machines with different critters offer a trade (staggered so the DIV-seeded
    // negotiation breaks symmetry cleanly), the protocol runs to completion, and each commits the OTHER's critter to its
    // own SRAM. Fully self-contained so the determinism leg can repeat it identically.
    private static SwapResult RunLinkedSwap(byte[] rom) {
        using var a = new Driver(rom: rom);
        using var b = new Driver(rom: rom);

        a.ImportSave(species: SpeciesA, level: LevelA);
        b.ImportSave(species: SpeciesB, level: LevelB);

        // Boot both to their title independently (safe — no cable, serial idle), then wire the cable and offer.
        a.RunFrames(buttons: JoypadButtons.None, frames: 8);
        b.RunFrames(buttons: JoypadButtons.None, frames: 8);

        a.WatchTraffic();
        b.WatchTraffic();

        using (var session = new SerialLinkSession(first: a.Machine, second: b.Machine)) {
            for (var frame = 0; (frame < LinkedFrames); frame++) {
                // Stagger the two START offers by two frames so the negotiation's DIV^FrameCounter seed differs and one
                // side cleanly claims master while the other is still listening (the boot-order-proof rendezvous).
                var aButtons = ((frame < 2) ? JoypadButtons.Start : JoypadButtons.None);
                var bButtons = (((frame >= 2) && (frame < 4)) ? JoypadButtons.Start : JoypadButtons.None);

                a.SetButtons(buttons: aButtons);
                b.SetButtons(buttons: bButtons);
                session.Run(tCycles: TCyclesPerFrame);
            }
        }

        a.Settle();
        b.Settle();

        return new SwapResult(
            FirstSpecies: a.Read(address: CritterSwapProtocol.SaveSpecies),
            FirstLevel: a.Read(address: CritterSwapProtocol.SaveLevel),
            FirstSramSpecies: a.SramSpecies(),
            FirstState: a.Snapshot(),
            FirstTraffic: a.TrafficHash,
            SecondSpecies: b.Read(address: CritterSwapProtocol.SaveSpecies),
            SecondLevel: b.Read(address: CritterSwapProtocol.SaveLevel),
            SecondSramSpecies: b.SramSpecies(),
            SecondState: b.Snapshot(),
            SecondTraffic: b.TrafficHash
        );
    }
    private static void AssertSwapped(SwapResult result) {
        // Each side now HOLDS the other's critter (species + level), in both the work-RAM mirror and the committed SRAM.
        Assert(condition: (result.FirstSpecies == SpeciesB), message: $"machine A did not receive B's critter (species {result.FirstSpecies}, expected {SpeciesB})");
        Assert(condition: (result.FirstLevel == LevelB), message: $"machine A did not receive B's level (0x{result.FirstLevel:X2}, expected 0x{LevelB:X2})");
        Assert(condition: (result.SecondSpecies == SpeciesA), message: $"machine B did not receive A's critter (species {result.SecondSpecies}, expected {SpeciesA})");
        Assert(condition: (result.SecondLevel == LevelA), message: $"machine B did not receive A's level (0x{result.SecondLevel:X2}, expected 0x{LevelA:X2})");
        Assert(condition: (result.FirstSramSpecies == SpeciesB), message: $"machine A did not COMMIT B's critter to SRAM (species {result.FirstSramSpecies}, expected {SpeciesB})");
        Assert(condition: (result.SecondSramSpecies == SpeciesA), message: $"machine B did not COMMIT A's critter to SRAM (species {result.SecondSramSpecies}, expected {SpeciesA})");
    }
    private static void Assert(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message: $"critterswap ROM verification failed: {message}");
        }
    }

    private readonly record struct SwapResult(
        byte FirstSpecies,
        byte FirstLevel,
        byte FirstSramSpecies,
        MachineSnapshot FirstState,
        ulong FirstTraffic,
        byte SecondSpecies,
        byte SecondLevel,
        byte SecondSramSpecies,
        MachineSnapshot SecondState,
        ulong SecondTraffic
    );

    // One real Humble CGB machine with battery SRAM: frame stepping, joypad edges, work-RAM peeks, the rendered frame,
    // an SRAM import/export seam (to seed and read the held critter), and a completed-transfer traffic fingerprint.
    private sealed class Driver : IDisposable {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private readonly ICartridge m_cartridge;
        private readonly ICpu m_cpu;
        private readonly IFramebuffer m_framebuffer;
        private readonly IJoypad m_joypad;
        private readonly MachineInstance m_machine;
        private readonly SerialComponent m_serial;
        private readonly ISystemBus m_bus;
        private ulong m_traffic = FnvOffset;

        public Driver(byte[] rom) {
            m_machine = MachineFactory.Create(
                configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
                compose: static services => services.AddHumbleGamingBrickComponents()
            );
            m_bus = m_machine.GetRequiredService<ISystemBus>();
            m_cartridge = m_machine.GetRequiredService<ICartridge>();
            m_cpu = m_machine.GetRequiredService<ICpu>();
            m_framebuffer = m_machine.GetRequiredService<IFramebuffer>();
            m_joypad = m_machine.GetRequiredService<IJoypad>();
            m_serial = m_machine.GetRequiredService<SerialComponent>();
        }

        public MachineInstance Machine => m_machine;

        // The completed-transfer fingerprint (both roles, per SerialComponent.TransferCompleted) — a pure host observer,
        // never serialized, so subscribing cannot perturb determinism; the same idiom the Post link stages fingerprint.
        public ulong TrafficHash => m_traffic;

        public byte Read(ushort address) => m_bus.ReadByte(address: address);

        // Seeds the held critter by importing a valid framework battery save into SRAM before boot loads it.
        public void ImportSave(byte species, byte level) =>
            m_cartridge.ImportExternalRam(source: CritterSwapRom.BuildSaveImage(species: species, level: level));

        // The species byte the game COMMITTED to SRAM (the save block's payload byte 0, at SRAM offset 3).
        public byte SramSpecies() => m_cartridge.ExportExternalRam()[3];
        public void WatchTraffic() =>
            m_serial.TransferCompleted = value => m_traffic = ((m_traffic ^ value) * FnvPrime);
        public void SetButtons(JoypadButtons buttons) => m_joypad.SetButtons(pressed: buttons);
        public void RunFrames(JoypadButtons buttons, int frames) {
            for (var frame = 0; (frame < frames); frame++) {
                m_joypad.SetButtons(pressed: buttons);
                m_machine.Machine.Run(tCycles: TCyclesPerFrame);
            }

            Settle();
        }
        public bool RunUntilState(byte state, int maxFrames) {
            for (var frame = 0; (frame < maxFrames); frame++) {
                RunFrames(buttons: JoypadButtons.None, frames: 1);

                if (Read(address: FrameworkMemoryMap.GameState) == state) {
                    return true;
                }
            }

            return false;
        }
        public void Settle() => VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: "critterswap");
        public int DistinctColourCount() {
            var seen = new HashSet<uint>();

            foreach (var pixel in m_framebuffer.Pixels) {
                _ = seen.Add(item: pixel);
            }

            return seen.Count;
        }
        public MachineSnapshot Snapshot() => m_machine.Machine.Snapshot();
        public void Dispose() => m_machine.Dispose();
    }
}
