namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-C stage that replays a commercial game across a cross-generation
/// pair. Two SM83 machines of different costumes — a Color (<see cref="ConsoleModel.Cgb"/>) console and an Advance
/// (<see cref="ConsoleModel.Agb"/>) console, the same GB-family cartridge in both, which by the carry-forward rule is
/// one SM83 core under two capability gates — run the game from power-on under frozen per-machine input scripts, through
/// a <see cref="SerialLinkSession"/>, to its in-game two-player link handshake. The gate asserts REAL serial traffic
/// (both sides raise serial interrupts; the exchanged byte stream is non-idle, not the all-<c>0xFF</c> of an unplugged
/// cable), then re-runs the whole session from fresh machines and asserts both sides' final whole-machine snapshots are
/// byte-identical — the replay-identical proof that a full commercial link workload (menu code, interrupt-driven
/// handshake, retry loops) adds no nondeterminism across the generation gap.
/// <para>
/// The cartridge is a per-machine commercial asset, never committed: its path comes from <c>PUCK_GB_LINKROM</c> (with a
/// known dev-box fallback), and the stage SKIPS cleanly when it is absent. The scripts were authored once by exploring
/// the game with <c>--link-explore</c>; a per-side traffic-fingerprint floor makes the gate
/// catch a serial-behavior regression that still happens to be self-consistent across the two runs.
/// </para>
/// </summary>
internal sealed class LinkGameReplayStage : IPostStage {
    // The frozen scripts drive the captured link-session cart (see RomFallbackPath) — a CGB link-from-menu game — from
    // power-on to its two-player link handshake. Both consoles walk the identical menu path, so one script drives both
    // sides. Authored with --link-explore; see the HGB Post README's "link-game-replay" section for the ROM/env-var contract.
    private const string RomEnvironmentVariable = "PUCK_GB_LINKROM";
    private const string RomFallbackPath = @"D:\Source\ByteTerrace\Silo\ROMS\Mario Tennis (USA).gbc";

    // The frozen menu walk reaches the handshake by ~frame 700; 1200 frames leaves the pair deep in the live
    // "LINKING… Waiting for other player" exchange with hundreds of transfers each way — ample, robust traffic.
    private const int Frames = 1200;

    // The traffic floor, captured from the verified-good run (the captured link-session cart, cgb↔agb, 1200 frames): the exact
    // byte-stream fingerprint each console shifted in over the handshake. The replay-identical check already proves the
    // two runs agree with each other; this pinned floor additionally catches a serial-behavior regression that stays
    // self-consistent across both runs (both wrong, identically). The two hashes differ because each side receives what
    // the OTHER sent. If a legitimate core change moves these, re-capture from a fresh good run (the render-hash-floor
    // discipline) rather than deleting the floor. Last re-capture: the HGB link branch's core corrections (the PPU
    // video-RAM READ-unlock cohering with the polled STAT mode-0 flip at +4, and the serial
    // shifter's move to the free-running DIV-derived divider) legitimately shifted the cgb side's received stream; the
    // agb side's pin came through unchanged.
    private const ulong ExpectedCgbTrafficHash = 0x8AF238BC0931D513ul;
    private const ulong ExpectedAgbTrafficHash = 0xC68DC189E9861B53ul;

    /// <inheritdoc/>
    public string Name =>
        "link-game-replay";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var romPath = ResolveRomPath();

        if (romPath is null) {
            return PostStageOutcome.Skip(detail: $"no link-game ROM (set {RomEnvironmentVariable} to a link-capable cartridge)");
        }

        var rom = File.ReadAllBytes(path: romPath);
        var title = CartridgeTitle(rom: rom);

        var first = RunLinkedGame(rom: rom);

        if (Verify(result: first, title: title) is { } failure) {
            return PostStageOutcome.Fail(detail: failure);
        }

        var second = RunLinkedGame(rom: rom);

        if (!first.FirstState.ContentEquals(other: second.FirstState)) {
            return PostStageOutcome.Fail(detail: $"the Cgb console's final state differed between two identical linked runs — {HashDivergenceProbe.DescribeDivergence(a: first.FirstState, b: second.FirstState)}");
        }

        if (!first.SecondState.ContentEquals(other: second.SecondState)) {
            return PostStageOutcome.Fail(detail: $"the Agb console's final state differed between two identical linked runs — {HashDivergenceProbe.DescribeDivergence(a: first.SecondState, b: second.SecondState)}");
        }

        return PostStageOutcome.Pass(
            detail: $"{title} cgb↔agb over {Frames} frames: cgb sent {first.First.MasterSends}/completed {first.First.Completions}, agb sent {first.Second.MasterSends}/completed {first.Second.Completions} serial transfers (traffic 0x{first.First.TrafficHash:X16}/0x{first.Second.TrafficHash:X16}), replay-identical across two runs ({first.FirstState.Size}+{first.SecondState.Size} state bytes)"
        );
    }

    // One complete linked session from fresh machines: a Cgb console and an Agb console booting the same cartridge,
    // both walking the frozen menu script to the handshake. Self-contained so the determinism leg repeats it exactly.
    private static LinkReplayResult RunLinkedGame(byte[] rom) {
        using var cgb = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);
        using var agb = PostMachine.Build(model: ConsoleModel.Agb, rom: rom);

        return LinkReplay.Run(first: cgb, firstScript: Script(), second: agb, secondScript: Script(), frames: Frames);
    }

    // Judges the traffic evidence of the first run; null means the handshake produced real, non-idle serial traffic on
    // both sides.
    private static string? Verify(LinkReplayResult result, string title) {
        if ((result.First.Completions == 0) || (result.Second.Completions == 0)) {
            return $"{title} never reached a two-way link handshake — cgb completed {result.First.Completions} transfers, agb {result.Second.Completions} (expected both > 0)";
        }

        if (IsIdle(hash: result.First.TrafficHash, completions: result.First.Completions) || IsIdle(hash: result.Second.TrafficHash, completions: result.Second.Completions)) {
            return $"{title}'s link exchanged only idle 0xFF bytes — no real traffic crossed the cable (cgb 0x{result.First.TrafficHash:X16}, agb 0x{result.Second.TrafficHash:X16})";
        }

        if ((result.First.TrafficHash != ExpectedCgbTrafficHash) || (result.Second.TrafficHash != ExpectedAgbTrafficHash)) {
            return $"{title}'s link traffic drifted from the pinned floor — cgb 0x{result.First.TrafficHash:X16} (expected 0x{ExpectedCgbTrafficHash:X16}), agb 0x{result.Second.TrafficHash:X16} (expected 0x{ExpectedAgbTrafficHash:X16}); if a core change legitimately moved this, re-capture the floor";
        }

        return null;
    }

    // The all-0xFF stream an unplugged port would shift in has a fixed FNV fingerprint per length; a real exchange never
    // matches it. Compares against the FNV of `completions` copies of 0xFF.
    private static bool IsIdle(ulong hash, int completions) {
        const ulong fnvOffsetBasis = 0xCBF29CE484222325ul;
        const ulong fnvPrime = 0x100000001B3ul;
        var idle = fnvOffsetBasis;

        for (var index = 0; (index < completions); ++index) {
            idle = ((idle ^ 0xFF) * fnvPrime);
        }

        return (hash == idle);
    }

    // The single menu-walk script both consoles follow (identical inputs on each side to reach the shared handshake).
    // Authored with --link-explore against the captured link-session cart: five Start taps blow through the attract/title screens
    // to the MAIN MENU (cursor on EXHIBITION, top-left), Right·Right walks to the LINKED PLAY icon (top-right), and A
    // enters it — after which both consoles sit in the interrupt-driven "LINKING…" character-select handshake,
    // exchanging serial bytes continuously. Each keyframe presses (tap) then releases six frames later.
    private static LinkInputScript Script() =>
        new(
            (100, JoypadButtons.Start), (106, JoypadButtons.None),
            (170, JoypadButtons.Start), (176, JoypadButtons.None),
            (240, JoypadButtons.Start), (246, JoypadButtons.None),
            (310, JoypadButtons.Start), (316, JoypadButtons.None),
            (380, JoypadButtons.Start), (386, JoypadButtons.None),
            (480, JoypadButtons.Right), (486, JoypadButtons.None),
            (540, JoypadButtons.Right), (546, JoypadButtons.None),
            (600, JoypadButtons.A), (606, JoypadButtons.None)
        );
    private static string? ResolveRomPath() {
        var fromEnvironment = Environment.GetEnvironmentVariable(variable: RomEnvironmentVariable);

        if (!string.IsNullOrEmpty(value: fromEnvironment) && File.Exists(path: fromEnvironment)) {
            return fromEnvironment;
        }

        return (File.Exists(path: RomFallbackPath) ? RomFallbackPath : null);
    }
    private static string CartridgeTitle(byte[] rom) {
        var builder = new System.Text.StringBuilder(capacity: 11);

        for (var offset = 0x0134; (offset < 0x013F); ++offset) {
            var character = rom[offset];

            if ((character == 0) || (character >= 0x80)) {
                break;
            }

            _ = builder.Append(value: (char)character);
        }

        return builder.ToString().Trim();
    }
}
