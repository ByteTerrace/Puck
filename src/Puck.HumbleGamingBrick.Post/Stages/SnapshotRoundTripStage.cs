using Puck.Snapshots;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: a snapshot restores exactly. Run to a mid-point and snapshot; run on and capture the resulting state;
/// restore the mid-point snapshot and run the same span again; the two resulting states must be byte-identical. This
/// proves the save-state layer is complete (no live field left unserialized) and faithful — the prerequisite for the
/// mid-frame rewind / netplay the machine is committed to.
/// <para>
/// Also carries the L-01 format assertion: the section-name sequence a snapshot's table records (metadata riding
/// alongside the bytes; see <see cref="SnapshotSection"/>) must match the exact, ordered <see cref="ExpectedSectionRoster"/>
/// derived from <c>HumbleGamingBrickComponents</c>'s registration order — the increment-on-layout-change contract
/// (<see cref="MachineIdentity.CurrentVersion"/>) has no other automatic guard, so a component silently added, removed,
/// or reordered without a version bump now fails this stage loudly instead of just shifting bytes.
/// </para>
/// </summary>
internal sealed class SnapshotRoundTripStage : IPostStage {
    private const int TailFrames = 200;
    private const int WarmFrames = 200;

    // L-01: the exact section sequence a fresh Humble machine's snapshot must produce — "clock" first (Machine.Snapshot
    // writes it before walking the snapshotables), then every ISnapshotable in HumbleGamingBrickComponents.cs's
    // registration order. Update this roster (and bump MachineIdentity.CurrentVersion) in the SAME change that adds,
    // removes, or reorders a registration — that is exactly the discipline a stale version number silently broke once.
    private static readonly string[] ExpectedSectionRoster = [
        "clock", "ModelState", "SystemMemory", "InterruptController", "TimerComponent", "JoypadComponent",
        "Key1Component", "SerialComponent", "InfraredPort", "ApuComponent", "AudioOutputComponent",
        "TiltSensorComponent", "CartridgeSlot", "OamDmaController", "Framebuffer", "Ppu", "HdmaController",
        "SystemBus", "Sm83",
    ];

    /// <inheritdoc/>
    public string Name =>
        "snapshot-round-trip";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        using var machine = PostMachine.Build(model: ConsoleModel.Dmg, rom: SyntheticRom.Create());

        PostMachine.RunFrames(instance: machine, frames: WarmFrames);

        var midpoint = machine.Machine.Snapshot();

        if (midpoint.Identity.Version != MachineIdentity.CurrentVersion) {
            return PostStageOutcome.Fail(detail: $"snapshot identity version={midpoint.Identity.Version}; expected the current format version {MachineIdentity.CurrentVersion}");
        }

        if (SectionRosterMismatch(sections: midpoint.Sections) is { } rosterFailure) {
            return PostStageOutcome.Fail(detail: rosterFailure);
        }

        PostMachine.RunFrames(instance: machine, frames: TailFrames);

        var afterFirstRun = machine.Machine.Snapshot();

        machine.Machine.Restore(snapshot: midpoint);

        PostMachine.RunFrames(instance: machine, frames: TailFrames);

        var afterSecondRun = machine.Machine.Snapshot();

        return (afterFirstRun.ContentEquals(other: afterSecondRun)
            ? PostStageOutcome.Pass(detail: $"save@{WarmFrames}f, then +{TailFrames}f twice, byte-identical ({midpoint.Size} state bytes, format v{midpoint.Identity.Version}, {midpoint.Sections.Count} sections in the expected order)")
            : PostStageOutcome.Fail(detail: $"restored run diverged from the original after {TailFrames} frames — {HashDivergenceProbe.DescribeDivergence(a: afterFirstRun, b: afterSecondRun)}"));
    }

    // L-01: fails loudly on ANY roster drift — count, name, or order — rather than letting a stale
    // MachineIdentity.CurrentVersion silently mislabel a shifted layout.
    private static string? SectionRosterMismatch(IReadOnlyList<SnapshotSection> sections) {
        if (sections.Count != ExpectedSectionRoster.Length) {
            return $"snapshot has {sections.Count} sections; expected {ExpectedSectionRoster.Length} ({string.Join(separator: ", ", values: ExpectedSectionRoster)}) — a component was added, removed, or the registration order changed without updating this roster (and MachineIdentity.CurrentVersion, if the byte layout moved)";
        }

        for (var index = 0; (index < sections.Count); ++index) {
            if (sections[index].Name != ExpectedSectionRoster[index]) {
                return $"snapshot section[{index}]=\"{sections[index].Name}\"; expected \"{ExpectedSectionRoster[index]}\" — section order drifted silently";
            }
        }

        return null;
    }
}
