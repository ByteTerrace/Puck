namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-C stage for the scripted two-machine cross-gen-cart Cable Club trade. Two
/// <see cref="ConsoleModel.Cgb"/> machines boot a real cross-gen trade cartridge, each with a distinct crafted
/// <see cref="TradeSaveFactory"/> battery save already in SRAM (side A leads with RATTATA 0x13, side B with PIDGEY
/// 0x10), linked through a <see cref="SerialLinkSession"/> and driven together by <see cref="ScriptedTradeDriver"/>'s
/// peek-gated phase machine: turn to the Trade Center receptionist, mash through the dialogue + save prompt, the
/// <c>WaitForLinkedFriend</c> rendezvous, the room-match warp into TRADE_CENTER, the walk onto each side's vacated-CHRIS
/// trade seat ((6,4) facing LEFT / (3,4) facing RIGHT — a directional bg_event fires from the tile ADJACENT to the
/// console FACING it), the console A-press into <c>special TradeCenter</c>, the full mon-selection menu drive
/// (A → RIGHT → A through the STATS|TRADE submenu and the confirm popup), the trade animation + double auto-save, and
/// the CANCEL handshake back out to the overworld.
/// <para>
/// The gate asserts: (a) the rendezvous resolved to exactly one $01 (external/slave) and one $02 (internal/master) clock
/// role — the mandated DIV-offset symmetry-break worked, no livelock; (b) both machines warped into TRADE_CENTER with
/// <c>wLinkMode</c> = LINK_TRADECENTER — the link fully established; (c) the trade COMMITTED: each side's exported SRAM
/// lead species is the OTHER side's crafted original (side A ends 0x10 PIDGEY, side B ends 0x13 RATTATA) with a valid
/// primary checksum — the auto-save wrote the swap; (d) the drive ran to completion (both sides cancelled out of the
/// re-entry loop and landed back in the TRADE_CENTER overworld); (e) real, non-idle block-exchange serial traffic
/// crossed the cable on both sides; (f) traffic fingerprints + final whole-machine snapshots + exported SRAMs are
/// bit-identical across two fresh runs on the same budget schedule (the long, data-dependent commercial link workload
/// adds no nondeterminism); and (g) a mid-run credit-preserving churn — suspend/snapshot/restore/reconnect at a
/// transfer-idle boundary via <see cref="SerialLinkSession.Suspend"/> — reproduces the identical tail.
/// </para>
/// <para>
/// The seat walk depends on VRAM reads becoming available with the mode-0 STAT transition at dot offset +4. The crafted
/// save must also initialize <c>wObjectFollow_Leader</c>/<c>wObjectFollow_Follower</c> ($D1F4/$D1F5) to $FF/$FF;
/// zero values mean "object 0 follows object 0" and arm the follower recorder, whose 5-byte
/// <c>wFollowMovementQueue</c> sits immediately before <c>wPlayerStruct</c> — side A's 5-step seat approach overflowed
/// the queue into the object structs. See <see cref="TradeSaveFactory"/>'s OffsetObjectFollowLeader note.
/// </para>
/// <para>
/// The cartridge is a per-machine commercial asset, never committed: its path comes from <c>PUCK_GB_TRADEROM</c> (the same
/// env var the <see cref="ScriptedTradeContinueStage"/> foundation gate uses, with known dev-box fallbacks), and the stage SKIPS
/// cleanly when it is absent. Both machines are pinned to Cgb; this cart's link code never writes rKEY1/SC_SPEED so the link
/// runs the normal (~8192&#160;Hz) serial clock — a property of the GAME, not a licence to pin the emulator's serial to a
/// real-time rate.
/// </para>
/// </summary>
internal sealed class ScriptedTradeLinkLockStage : IPostStage {
    private const string RomEnvironmentVariable = "PUCK_GB_TRADEROM";

    // The known dev-box copies (laptop, desktop), tried in order when the env var is unset.
    private static readonly string[] RomFallbackPaths = [
        @"C:\Source\ByteTerrace\Temp\ROMS\Pokemon - Gold Version (USA, Europe) (SGB Enhanced) (GB Compatible).gbc",
        @"D:\Source\ByteTerrace\Silo\ROMS\Pokemon - Gold Version (USA, Europe) (SGB Enhanced) (GB Compatible).gbc",
    ];

    /// <inheritdoc/>
    public string Name =>
        "link-lock";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var romPath = ResolveRomPath();

        if (romPath is null) {
            return PostStageOutcome.Skip(detail: $"no trade-cart ROM (set {RomEnvironmentVariable} to the cross-gen trade cartridge)");
        }

        var rom = File.ReadAllBytes(path: romPath);

        // Expected crafted leads, derived from the factory rather than hard-coded, so a factory change can't silently
        // drift this gate; the trade must end with them CROSSED.
        var craftedLeadA = TradeSaveFactory.ReadLeadSpecies(sram: TradeSaveFactory.CreateSram(trainer: TradeSaveFactory.SideA));
        var craftedLeadB = TradeSaveFactory.ReadLeadSpecies(sram: TradeSaveFactory.CreateSram(trainer: TradeSaveFactory.SideB));

        // The reference run records a per-frame idle/phase probe so the churn boundary is picked deterministically from a
        // genuine mid-trade transfer-idle instant.
        var probes = new List<TradeProbe>();
        var reference = ScriptedTradeDriver.Run(rom: rom, probes: probes);

        if (Judge(result: reference, craftedLeadA: craftedLeadA, craftedLeadB: craftedLeadB) is { } failure) {
            return PostStageOutcome.Fail(detail: failure);
        }

        // Determinism: a second fresh run on the same schedule reproduces roles, traffic, snapshots, and SRAMs.
        var replay = ScriptedTradeDriver.Run(rom: rom);

        if (Difference(expected: reference, actual: replay, leg: "replay") is { } replayFailure) {
            return PostStageOutcome.Fail(detail: replayFailure);
        }

        // Churn: suspend/snapshot/restore/reconnect at a mid-trade transfer-idle boundary and demand the identical tail.
        var churnStep = PickChurnStep(probes: probes);

        if (churnStep < 0) {
            return PostStageOutcome.Fail(detail: "no mid-trade transfer-idle budget boundary appeared to churn at (the phase probe found no severable instant)");
        }

        var churned = ScriptedTradeDriver.Run(rom: rom, churnAtStep: churnStep);

        if (Difference(expected: reference, actual: churned, leg: "churn") is { } churnFailure) {
            return PostStageOutcome.Fail(detail: churnFailure);
        }

        return PostStageOutcome.Pass(
            detail: ((($"{CartridgeTitle(rom: rom)} cgb↔cgb Cable Club trade COMPLETED: rendezvous roles $01/$02 resolved (A=0x{reference.RoleA:X2} B=0x{reference.RoleB:X2}), TRADE_CENTER warp + seat walk + console + mon-selection menu drive, "
                + $"leads swapped and auto-saved (A 0x{craftedLeadA:X2}→0x{reference.LeadA:X2}, B 0x{craftedLeadB:X2}→0x{reference.LeadB:X2}, checksums valid), CANCEL handshake back to the overworld, ")
                + $"A sent {reference.TrafficA.MasterSends}/completed {reference.TrafficA.Completions} B sent {reference.TrafficB.MasterSends}/completed {reference.TrafficB.Completions} transfers (traffic 0x{reference.TrafficA.TrafficHash:X16}/0x{reference.TrafficB.TrafficHash:X16}), ")
                + $"replay- and churn-identical (severed transfer-idle at budget step {churnStep}, {reference.StateA.Size}+{reference.StateB.Size} state bytes).")
        );
    }

    // The full-trade conditions: the drive completed (through the post-trade CANCEL), the pair reached TRADE_CENTER with
    // the link established, the rendezvous resolved to distinct clock roles (no livelock), the species swap committed to
    // both auto-saves with valid checksums, and real (non-idle) serial traffic crossed the cable on both sides.
    private static string? Judge(TradeResult result, byte craftedLeadA, byte craftedLeadB) {
        if (!result.ReachedTradeCenter) {
            return "the pair did not reach TRADE_CENTER with the link established (a phase blew its frame ceiling — see ScriptedTradeDriver)";
        }

        if (!result.RolesResolved) {
            return $"the WaitForLinkedFriend rendezvous did not resolve to distinct clock roles (side A 0x{result.RoleA:X2}, side B 0x{result.RoleB:X2}; expected one $01 and one $02) — the symmetry-break failed or the pair livelocked";
        }

        if (!result.Completed) {
            return "the trade drive did not run to completion (seat walk, console, mon selection, swap, or the CANCEL exit blew its frame ceiling — see ScriptedTradeDriver)";
        }

        if ((result.LeadA != craftedLeadB) || (result.LeadB != craftedLeadA)) {
            return $"the species swap did not commit: exported leads A=0x{result.LeadA:X2} B=0x{result.LeadB:X2}, expected the crafted originals crossed (A=0x{craftedLeadB:X2} B=0x{craftedLeadA:X2})";
        }

        if (!result.ChecksumOkA || !result.ChecksumOkB) {
            return $"a post-trade auto-save's primary checksum/check bytes are inconsistent (side A ok={result.ChecksumOkA}, side B ok={result.ChecksumOkB})";
        }

        if ((result.TrafficA.Completions == 0) || (result.TrafficB.Completions == 0)) {
            return $"a side saw no completed serial transfers (A {result.TrafficA.Completions}, B {result.TrafficB.Completions}) — no real link traffic crossed the cable";
        }

        if (IsIdle(traffic: result.TrafficA) || IsIdle(traffic: result.TrafficB)) {
            return $"the link exchanged only idle 0xFF bytes — no real block data crossed (A 0x{result.TrafficA.TrafficHash:X16}, B 0x{result.TrafficB.TrafficHash:X16})";
        }

        return null;
    }

    // Compares a later run against the reference: roles, both traffic fingerprints, both final snapshots, and both
    // exported SRAMs must match. Snapshot equality also checks Identity (free rigor: refuses a model/ROM mismatch).
    private static string? Difference(TradeResult expected, TradeResult actual, string leg) {
        if ((expected.RoleA != actual.RoleA) || (expected.RoleB != actual.RoleB)) {
            return $"the {leg} rendezvous roles diverged (expected A=0x{expected.RoleA:X2} B=0x{expected.RoleB:X2}, got A=0x{actual.RoleA:X2} B=0x{actual.RoleB:X2})";
        }

        if (expected.Completed != actual.Completed) {
            return $"the {leg} run's completion diverged (expected {expected.Completed}, got {actual.Completed})";
        }

        if (expected.TrafficA != actual.TrafficA) {
            return $"the {leg} side-A traffic diverged (expected {expected.TrafficA}, got {actual.TrafficA})";
        }

        if (expected.TrafficB != actual.TrafficB) {
            return $"the {leg} side-B traffic diverged (expected {expected.TrafficB}, got {actual.TrafficB})";
        }

        if (!expected.StateA.ContentEquals(other: actual.StateA)) {
            return $"the {leg} side-A final state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.StateA, b: actual.StateA)}";
        }

        if (!expected.StateB.ContentEquals(other: actual.StateB)) {
            return $"the {leg} side-B final state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.StateB, b: actual.StateB)}";
        }

        if (!expected.SramA.AsSpan().SequenceEqual(other: actual.SramA)) {
            return $"the {leg} side-A exported SRAM diverged from the reference trade";
        }

        if (!expected.SramB.AsSpan().SequenceEqual(other: actual.SramB)) {
            return $"the {leg} side-B exported SRAM diverged from the reference trade";
        }

        return null;
    }

    // A mid-trade transfer-idle budget boundary: idle on both ports while the trade UI's menu drive is running (real
    // traffic long since begun), 60% of the way through that phase so the sever lands solidly inside the mon-selection
    // exchange rather than at its first or last byte. Falls back to a mid-rendezvous boundary if the TradeMenu phase
    // offered none.
    private static int PickChurnStep(List<TradeProbe> probes) {
        foreach (var phase in (ReadOnlySpan<string>)["TradeMenu", "Receptionist"]) {
            var first = probes.FindIndex(match: p => string.Equals(a: p.Phase.ToString(), b: phase, comparisonType: StringComparison.Ordinal));
            var last = probes.FindLastIndex(match: p => string.Equals(a: p.Phase.ToString(), b: phase, comparisonType: StringComparison.Ordinal));

            if (first < 0) {
                continue;
            }

            for (var step = (first + (((last - first) * 3) / 5)); (step <= last); ++step) {
                var probe = probes[index: step];

                if (probe.Idle && (probe.Completed >= 1)) {
                    return step;
                }
            }
        }

        return -1;
    }

    // The all-0xFF stream an unplugged port shifts in has a fixed FNV fingerprint per length; a real exchange never
    // matches it.
    private static bool IsIdle(LinkSideTraffic traffic) {
        const ulong fnvOffsetBasis = 0xCBF29CE484222325ul;
        const ulong fnvPrime = 0x100000001B3ul;
        var idle = fnvOffsetBasis;

        for (var index = 0; (index < traffic.Completions); ++index) {
            idle = ((idle ^ 0xFF) * fnvPrime);
        }

        return (traffic.TrafficHash == idle);
    }
    private static string? ResolveRomPath() {
        var fromEnvironment = Environment.GetEnvironmentVariable(variable: RomEnvironmentVariable);

        if (!string.IsNullOrEmpty(value: fromEnvironment) && File.Exists(path: fromEnvironment)) {
            return fromEnvironment;
        }

        return Array.Find(array: RomFallbackPaths, match: File.Exists);
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
