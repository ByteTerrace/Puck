using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-C stage for the cross-gen-cart trade harness. Two <see cref="ConsoleModel.Cgb"/> machines
/// boot a real cross-gen trade cartridge, each with a distinct crafted <see cref="TradeSaveFactory"/> battery save
/// already in SRAM (side A leads with RATTATA, side B with PIDGEY), linked through a <see cref="SerialLinkSession"/> and
/// driven together under the frozen <see cref="ScriptedTradeHarness.ContinueScript"/>. The gate proves the crafted saves are
/// byte-exact and accepted by the shipping game — both reach a CONTINUE-loaded overworld standing on the POKECENTER_2F
/// Cable Club floor with the crafted lead species and a self-consistent primary checksum — and that the credit-preserving
/// suspend/resume machinery is transparent to that linked commercial-ROM boot: a mid-run
/// snapshot/restore/reconnect at a transfer-idle boundary reproduces a bit-identical final state, as does a second fresh
/// run on the same budget schedule.
/// <para>
/// This stage verifies crafted-save acceptance, linked CONTINUE behavior, and churn transparency. The
/// scripted link through the Cable Club is gated by the <see cref="ScriptedTradeLinkLockStage"/> (<c>link-lock</c>). The overworld
/// factory writes the player and receptionist object structs because CONTINUE restores overworld objects from saved
/// WRAM rather than regenerating them. The cartridge is a per-machine commercial asset, never committed:
/// its path comes from <c>PUCK_GB_TRADEROM</c> (with a known dev-box fallback), and the stage SKIPS cleanly when absent.
/// </para>
/// <para>
/// Both machines are pinned to CGB, and this cart's link code never writes rKEY1 or
/// SC_SPEED, so its same-model exchange always runs the normal (~8192&#160;Hz) serial clock — but that is a property of the
/// GAME, not a licence to pin the emulator's serial to a real-time rate. KEY1 legitimately doubles the serial shift clock
/// on hardware and in <see cref="SerialComponent"/> (the fast-clock bit taps DIV bit&#160;3 instead of bit&#160;8); this
/// cart simply never arms it.
/// </para>
/// </summary>
internal sealed class ScriptedTradeContinueStage : IPostStage {
    private const string RomEnvironmentVariable = "PUCK_GB_TRADEROM";

    // The known dev-box copies (laptop, desktop), tried in order when the env var is unset.
    private static readonly string[] RomFallbackPaths = [
        @"C:\Source\ByteTerrace\Temp\ROMS\Pokemon - Gold Version (USA, Europe) (SGB Enhanced) (GB Compatible).gbc",
        @"D:\Source\ByteTerrace\Silo\ROMS\Pokemon - Gold Version (USA, Europe) (SGB Enhanced) (GB Compatible).gbc",
    ];

    // A mid-CONTINUE budget boundary (the intro cinematic is still skipping to the menu here) — well inside the run and,
    // because no serial transfer ever starts before the receptionist walk, always transfer-idle: a genuine severable
    // instant for the churn leg.
    private const int ChurnStep = 300;
    private const ushort SerialControlAddress = 0xFF02;

    /// <inheritdoc/>
    public string Name =>
        "trade-continue";

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
        // drift this gate.
        var expectedLeadA = TradeSaveFactory.ReadLeadSpecies(sram: TradeSaveFactory.CreateSram(trainer: TradeSaveFactory.SideA));
        var expectedLeadB = TradeSaveFactory.ReadLeadSpecies(sram: TradeSaveFactory.CreateSram(trainer: TradeSaveFactory.SideB));

        var reference = RunScenario(rom: rom, churnAtStep: -1);

        if (Judge(result: reference, expectedLeadA: expectedLeadA, expectedLeadB: expectedLeadB) is { } failure) {
            return PostStageOutcome.Fail(detail: failure);
        }

        // Determinism: a second fresh run on the same budget schedule reproduces both final snapshots.
        var replay = RunScenario(rom: rom, churnAtStep: -1);

        if (Difference(expected: reference, actual: replay, leg: "replay") is { } replayFailure) {
            return PostStageOutcome.Fail(detail: replayFailure);
        }

        // Churn transparency: suspend/snapshot/restore/reconnect at a transfer-idle boundary mid-boot, continue the
        // identical budgets, and demand identical final snapshots from the credit-preserving resume.
        var churned = RunScenario(rom: rom, churnAtStep: ChurnStep);

        if (Difference(expected: reference, actual: churned, leg: "churn") is { } churnFailure) {
            return PostStageOutcome.Fail(detail: churnFailure);
        }

        return PostStageOutcome.Pass(
            detail: $"{CartridgeTitle(rom: rom)} cgb↔cgb: both crafted saves CONTINUE-accepted onto the POKECENTER_2F Cable Club floor (leads 0x{reference.LeadA:X2}/0x{reference.LeadB:X2}, checksums valid), replay-identical and churn-identical over {ScriptedTradeHarness.ContinueSettledFrame} frames (severed transfer-idle at budget step {ChurnStep}, {reference.StateA.Size}+{reference.StateB.Size} state bytes). The overworld is navigable + the receptionist interactable via the crafted object structs (see TradeSaveFactory.WriteObjects); the scripted link through TRADE_CENTER is gated by link-lock."
        );
    }

    // One complete scenario on the fixed per-frame budget schedule. With churnAtStep >= 0 the session is suspended at that
    // boundary (transfer-idle before any receptionist link starts), both machines snapshotted and restored into fresh
    // machines, and the cable reconnected with the resume token before the remaining frames run.
    private static ScenarioResult RunScenario(byte[] rom, int churnAtStep) {
        var script = ScriptedTradeHarness.ContinueScript();
        var machineA = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideA);
        var machineB = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideB);

        try {
            var joypadA = machineA.GetRequiredService<IJoypad>();
            var joypadB = machineB.GetRequiredService<IJoypad>();
            var session = new SerialLinkSession(first: machineA, second: machineB);

            try {
                for (var frame = 0; (frame < ScriptedTradeHarness.ContinueSettledFrame); ++frame) {
                    if (frame == churnAtStep) {
                        if (!IsTransferIdle(machine: machineA) || !IsTransferIdle(machine: machineB)) {
                            throw new InvalidOperationException(message: $"the churn boundary at frame {frame} is not transfer-idle on both ports.");
                        }

                        var token = session.Suspend();
                        var stateA = machineA.Machine.Snapshot();
                        var stateB = machineB.Machine.Snapshot();
                        var freshA = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideA);
                        var freshB = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideB);

                        freshA.Machine.Restore(snapshot: stateA);
                        freshB.Machine.Restore(snapshot: stateB);

                        machineA.Dispose();
                        machineB.Dispose();

                        machineA = freshA;
                        machineB = freshB;
                        joypadA = machineA.GetRequiredService<IJoypad>();
                        joypadB = machineB.GetRequiredService<IJoypad>();
                        session = new SerialLinkSession(first: machineA, second: machineB, resumeToken: token);
                    }

                    joypadA.SetButtons(pressed: script.ButtonsAt(frame: frame));
                    joypadB.SetButtons(pressed: script.ButtonsAt(frame: frame));
                    session.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
                }

                return new ScenarioResult(
                    LeadA: TradeSaveFactory.ReadLeadSpecies(sram: ScriptedTradeHarness.ExportSram(machine: machineA)),
                    LeadB: TradeSaveFactory.ReadLeadSpecies(sram: ScriptedTradeHarness.ExportSram(machine: machineB)),
                    ReachedA: ScriptedTradeHarness.IsAtCableClubFloor(machine: machineA),
                    ReachedB: ScriptedTradeHarness.IsAtCableClubFloor(machine: machineB),
                    ChecksumOkA: TradeSaveFactory.VerifyChecksum(sram: ScriptedTradeHarness.ExportSram(machine: machineA)),
                    ChecksumOkB: TradeSaveFactory.VerifyChecksum(sram: ScriptedTradeHarness.ExportSram(machine: machineB)),
                    StateA: machineA.Machine.Snapshot(),
                    StateB: machineB.Machine.Snapshot()
                );
            } finally {
                session.Dispose();
            }
        } finally {
            machineA.Dispose();
            machineB.Dispose();
        }
    }

    // Crafted-save acceptance requires both machines to load CONTINUE onto the Cable Club floor, carry
    // the crafted lead species (distinct per side), and each exported SRAM's primary checksum + check bytes are
    // self-consistent.
    private static string? Judge(ScenarioResult result, byte expectedLeadA, byte expectedLeadB) {
        if (!result.ReachedA || !result.ReachedB) {
            return $"a crafted save did not CONTINUE onto the POKECENTER_2F Cable Club floor (side A reached={result.ReachedA}, side B reached={result.ReachedB})";
        }

        if ((result.LeadA != expectedLeadA) || (result.LeadB != expectedLeadB)) {
            return $"the loaded lead species drifted from the crafted parties (side A 0x{result.LeadA:X2} expected 0x{expectedLeadA:X2}, side B 0x{result.LeadB:X2} expected 0x{expectedLeadB:X2})";
        }

        if (result.LeadA == result.LeadB) {
            return $"both sides loaded the same lead species 0x{result.LeadA:X2}; the crafted trainers must carry distinct leads for an observable trade";
        }

        if (!result.ChecksumOkA || !result.ChecksumOkB) {
            return $"a crafted save's primary checksum/check bytes are inconsistent after CONTINUE (side A ok={result.ChecksumOkA}, side B ok={result.ChecksumOkB})";
        }

        return null;
    }

    // Compares a later run against the reference: both final snapshots must match. Snapshot equality also checks Identity
    // (free rigor: refuses a model/ROM mismatch).
    private static string? Difference(ScenarioResult expected, ScenarioResult actual, string leg) {
        if (!expected.StateA.ContentEquals(other: actual.StateA)) {
            return $"the {leg} side-A final state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.StateA, b: actual.StateA)}";
        }

        if (!expected.StateB.ContentEquals(other: actual.StateB)) {
            return $"the {leg} side-B final state diverged — {HashDivergenceProbe.DescribeDivergence(a: expected.StateB, b: actual.StateB)}";
        }

        return null;
    }
    private static bool IsTransferIdle(MachineInstance machine) =>
        ((machine.GetRequiredService<ISystemBus>().ReadByte(address: SerialControlAddress) & 0x80) == 0);
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

    private readonly record struct ScenarioResult(
        byte LeadA,
        byte LeadB,
        bool ReachedA,
        bool ReachedB,
        bool ChecksumOkA,
        bool ChecksumOkB,
        MachineSnapshot StateA,
        MachineSnapshot StateB
    );
}
