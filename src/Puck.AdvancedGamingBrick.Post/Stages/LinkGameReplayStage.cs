namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-C stage: the real link cable exercised by a REAL commercial multiplayer game. Two ARM7TDMI consoles boot the
/// same cartridge (named by <c>PUCK_AGB_LINK_GAME</c>) through the real BIOS — a full-boot commercial game, not the
/// hand-assembled <see cref="MicroRoms"/> protocol — joined on one <see cref="AgbLinkCable"/> and advanced together
/// through an <see cref="AgbLinkSession"/> in a fixed sub-frame budget schedule. The stage proves two things about the
/// link stack under a genuine game:
/// <list type="number">
/// <item>the game's own SIO link stack ENGAGES over the cable — each console reaches Multiplayer-mode setup AND clocks
/// real normal-mode transfers whose completions cross the cable (not a lone-console idle read); and</item>
/// <item>the whole linked scenario is REPLAY-IDENTICAL: re-run from fresh consoles with the identical budget schedule,
/// both final whole-machine snapshots reproduce byte-for-byte (the link session adds no nondeterminism, even under a
/// 4&#160;MiB commercial ROM booting the real BIOS).</item>
/// </list>
/// <para>
/// It does NOT assert a completed multiplayer lobby handshake, because a real cartridge (the captured link-session
/// cart; see the fallback path) does not reach one on the modeled SIO: the game detects a cable partner by polling the Multiplayer
/// SIOCNT ready-line status (SD/SI) — the hardware signal that children are physically connected and ready — and never
/// clocks a Multiplayer data round during detection (its SIOCNT writes are Multiplayer-mode setup with the start bit
/// NEVER set, plus symmetric normal-mode pings). The emulated <see cref="AgbSerialController"/> models the data-exchange
/// surface faithfully (the <c>link-replay</c> micro-ROM gate proves rounds cross both ways) but does not derive those
/// SD/SI ready-line bits from link-partner presence, so the game never advances to a lobby. The stage records how far
/// the handshake reached as its pass detail; the divergence is a documented core gap, not a stage failure. The stage
/// SKIPS cleanly when the ROM (<c>PUCK_AGB_LINK_GAME</c>) or a real boot BIOS (<c>PUCK_AGB_BIOS</c>) is absent.
/// </para>
/// </summary>
internal sealed class LinkGameReplayStage : IPostStage {
    /// <summary>The environment variable naming the commercial multiplayer ROM this stage links two consoles on.</summary>
    private const string RomEnvironmentVariable = "PUCK_AGB_LINK_GAME";

    // The dev-box path the ROM lives at, echoed into the skip message so the stage is discoverable without hunting.
    private const string DevBoxRomPath = @"D:\Source\ByteTerrace\Silo\ROMS\Mario Kart - Super Circuit (USA).gba";

    // Frames to advance the linked pair: enough to cover the game's boot-time link-probe window (empirically frames
    // ~30-270 for the captured link-session cart) with margin.
    private const int Frames = 300;

    // Sub-frame budget the session advances per step. Fine enough that a ~2k-cycle normal-mode transfer's start bit is
    // observed before it clears (a whole-frame poll misses it, since a cable-completed transfer clears fast), yet a
    // fixed schedule — identical across both determinism runs, so the interleave (and every snapshot) replays.
    private const long SubFrameBudget = 1024L;

    /// <inheritdoc/>
    public string Name =>
        "link-game-replay";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var romPath = Environment.GetEnvironmentVariable(variable: RomEnvironmentVariable);

        if (string.IsNullOrEmpty(value: romPath) || !File.Exists(path: romPath)) {
            return PostStageOutcome.Skip(detail: $"set {RomEnvironmentVariable} to a multiplayer ROM (dev box: {DevBoxRomPath}) to run this stage");
        }

        // A commercial game boots through the real BIOS; the zeroed replacement stub cannot full-boot it.
        if (AgbBiosProfile.Identify(image: context.BiosImage.Span).Kind == AgbBiosKind.ReplacementStub) {
            return PostStageOutcome.Skip(detail: $"needs a real boot BIOS (PUCK_AGB_BIOS) to full-boot {Path.GetFileName(path: romPath)}");
        }

        var rom = File.ReadAllBytes(path: romPath);

        var first = RunLinkedScenario(bios: context.BiosImage, rom: rom);

        // Gate 1: the real game's link stack must ENGAGE on both consoles — Multiplayer-mode setup AND a real
        // cable-completed normal-mode transfer. A console that never touches SIO means the game never ran its link
        // probe (wrong ROM, or a boot regression), which this stage is meaningless without.
        if (Verify(result: first) is { } failure) {
            return PostStageOutcome.Fail(detail: failure);
        }

        // Gate 2: replay-identical determinism — the whole linked scenario reproduced from fresh consoles.
        var second = RunLinkedScenario(bios: context.BiosImage, rom: rom);

        if (!first.ParentState.ContentEquals(other: second.ParentState)) {
            return PostStageOutcome.Fail(detail: $"the parent console's final state differed between two identical linked game runs — {HashDivergenceProbe.DescribeDivergence(a: first.ParentState, b: second.ParentState)}");
        }

        if (!first.ChildState.ContentEquals(other: second.ChildState)) {
            return PostStageOutcome.Fail(detail: $"the child console's final state differed between two identical linked game runs — {HashDivergenceProbe.DescribeDivergence(a: first.ChildState, b: second.ChildState)}");
        }

        // The finding, reported (never gated): whether the game reached a multiplayer round, and whether the linked
        // parent's screen diverged from a lone-console control — evidence it did (or, as observed, did not) detect the
        // partner. A future core change that lands the SD/SI ready-line would flip these, which is desirable, so they
        // must not be a gate.
        var solo = RunSoloControl(bios: context.BiosImage, rom: rom);
        var reacted = (first.ParentFrameHash != solo);
        var handshake = (first.ParentProbe.SawMultiplayerRound || first.ChildProbe.SawMultiplayerRound);

        return PostStageOutcome.Pass(
            detail: (((($"{Path.GetFileName(path: romPath)}: both consoles ran the game's SIO link probe over the cable "
                + $"(multiplayer-mode setup + {first.ParentProbe.NormalTransfers}/{first.ChildProbe.NormalTransfers} cable-completed normal transfers P/C), ")
                + $"replay-identical across two runs ({first.ParentState.Size}+{first.ChildState.Size} state bytes over {Frames} frames); ")
                + $"no multiplayer round completed and the linked screen is {(reacted ? "DIFFERENT from" : "identical to")} a lone-console control ")
                + $"(the game polls the unmodeled Multiplayer SD/SI ready-line to detect a partner; handshake={(handshake ? "reached" : "not reached")})")
        );
    }

    // One complete linked scenario from freshly built, full-booted consoles: connect on a cable, advance the fixed
    // sub-frame schedule while sampling both consoles' SIO for link-probe evidence, then snapshot. Self-contained so the
    // determinism leg repeats it identically.
    private static LinkGameResult RunLinkedScenario(ReadOnlyMemory<byte> bios, byte[] rom) {
        using var parent = CreateConsole(bios: bios, rom: rom);
        using var child = CreateConsole(bios: bios, rom: rom);

        var parentBus = (AgbBus)parent.Machine.Bus;
        var childBus = (AgbBus)child.Machine.Bus;
        var parentProbe = new ProbeEvidence();
        var childProbe = new ProbeEvidence();

        using var session = new AgbLinkSession(parent, child);

        var subSteps = ((Frames * PostMachine.CyclesPerFrame) / SubFrameBudget);

        for (var step = 0L; (step < subSteps); ++step) {
            session.Run(cycles: SubFrameBudget);

            parentProbe.Sample(bus: parentBus);
            childProbe.Sample(bus: childBus);
        }

        return new LinkGameResult(
            ParentProbe: parentProbe,
            ChildProbe: childProbe,
            ParentFrameHash: FrameHash(machine: parent.Machine),
            ParentState: parent.Machine.Snapshot(),
            ChildState: child.Machine.Snapshot()
        );
    }

    // A lone console (no cable) full-booted and advanced the same number of frames — the control the linked parent's
    // screen is compared against to see whether the game reacted to the detected partner.
    private static ulong RunSoloControl(ReadOnlyMemory<byte> bios, byte[] rom) {
        using var console = CreateConsole(bios: bios, rom: rom);

        for (var frame = 0; (frame < Frames); ++frame) {
            _ = console.Machine.RunFrame();
        }

        return FrameHash(machine: console.Machine);
    }

    // Builds a console and full-BIOS-boots it (Cpu.Reset), the real boot path a commercial game's link probe needs —
    // NOT the HLE direct boot, on which the game mis-boots and never runs its link stack.
    private static AgbMachineInstance CreateConsole(ReadOnlyMemory<byte> bios, byte[] rom) {
        // Each console gets its own ROM copy: a shared array would let one console's cartridge writes corrupt the other.
        var console = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: bios, rom: (byte[])rom.Clone()));

        console.Machine.Cpu.Reset();

        return console;
    }

    // Both consoles must have engaged the game's link stack: Multiplayer-mode setup AND a real cable-completed normal
    // transfer. Null means the evidence held.
    private static string? Verify(LinkGameResult result) =>
        (VerifySide(probe: result.ParentProbe, side: "parent")
            ?? VerifySide(probe: result.ChildProbe, side: "child"));
    private static string? VerifySide(ProbeEvidence probe, string side) {
        if (!probe.SawMultiplayerMode) {
            return $"the {side} console never entered SIO Multiplayer mode; the game's link probe did not run (wrong ROM, or a boot regression)";
        }

        if (probe.NormalTransfers == 0) {
            return $"the {side} console clocked no normal-mode transfers over the cable; the link probe never exchanged a word";
        }

        return null;
    }
    private static ulong FrameHash(AdvancedGamingBrickMachine machine) {
        var hash = 0xCBF29CE484222325ul;

        foreach (var pixel in machine.Framebuffer) {
            hash = ((hash ^ (pixel & 0xFFu)) * 0x100000001B3ul);
            hash = ((hash ^ ((pixel >> 8) & 0xFFu)) * 0x100000001B3ul);
            hash = ((hash ^ ((pixel >> 16) & 0xFFu)) * 0x100000001B3ul);
            hash = ((hash ^ ((pixel >> 24) & 0xFFu)) * 0x100000001B3ul);
        }

        return hash;
    }

    // Accumulates link-probe evidence from repeated side-effect-free SIOCNT/SIOMULTI peeks across one console's run.
    private sealed class ProbeEvidence {
        private bool m_startArmed;

        /// <summary>Whether the console entered SIO Multiplayer mode (SIOCNT bits 12-13 = 2) at any sample.</summary>
        public bool SawMultiplayerMode { get; private set; }

        /// <summary>The count of normal-mode transfers observed completing (a start-bit set then cleared) — real
        /// cable-completed exchanges under the linked session (a lone console leaves an external-clock transfer
        /// pending).</summary>
        public int NormalTransfers { get; private set; }

        /// <summary>Whether a Multiplayer round ever completed with an assigned player id or partner-slot data — the
        /// completed-handshake signal (observed to stay false: the game never clocks a round during detection).</summary>
        public bool SawMultiplayerRound { get; private set; }

        /// <summary>Samples one console's SIO via side-effect-free debug peeks (no clock movement, so sampling can never
        /// perturb the deterministic snapshots).</summary>
        /// <param name="bus">The console's concrete bus.</param>
        public void Sample(AgbBus bus) {
            var control = bus.DebugReadIo(offset: 0x128u);
            var mode = (control >> 12) & 0x3u;
            var start = ((control & 0x0080u) != 0u);
            var id = (control >> 4) & 0x3u;

            if (mode == 2u) {
                SawMultiplayerMode = true;

                var slot0 = bus.DebugReadIo(offset: 0x120u);
                var slot1 = bus.DebugReadIo(offset: 0x122u);

                if ((id != 0u) || IsRealData(slot: slot0) || IsRealData(slot: slot1)) {
                    SawMultiplayerRound = true;
                }
            }

            // A normal-mode (bits 12-13 = 0 or 1) start bit that goes set→clear is one completed transfer; count the
            // falling edge so a held probe is not double-counted.
            if ((mode < 2u) && start) {
                m_startArmed = true;
            } else if (m_startArmed && !start) {
                ++NormalTransfers;
                m_startArmed = false;
            }
        }

        private static bool IsRealData(ushort slot) =>
            ((slot != 0xFFFF) && (slot != 0x0000));
    }
    private readonly record struct LinkGameResult(
        ProbeEvidence ParentProbe,
        ProbeEvidence ChildProbe,
        ulong ParentFrameHash,
        AgbMachineSnapshot ParentState,
        AgbMachineSnapshot ChildState
    );
}
