using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a replay drive retires the recorded extensions admitted before it, with the timeline they
/// observed, and its end opens the live epoch again: a world whose drive has ended admits a fresh recorded extension,
/// whose contribution drains and applies on the next tick, while the retired instance stays retired.
/// </summary>
public sealed class ReplayExtensionEpochLawTests {
    private static WorldDefinition Scored() => Fixtures.BuildDocument() with {
        StateRaw = new(World: [new WorldStateRow(
            Name: CellName.Parse(candidate: "score"),
            Kind: CellKind.Int,
            Cells: [new StateCell(CellName.Parse(candidate: "value"), CellValue.Int(value: 0L))]
        )]),
    };
    private static WorldMutation Score(long value) => new WorldMutation.UpsertStateCell(
        Kind: WorldDocumentWriteKind.Set,
        Key: "value",
        Principal: Principal.Console,
        Row: "score",
        Value: value
    );
    private static WorldRecordedExtension Extension(WorldServer server) => new(
        server: server,
        principal: Principal.Console,
        requests: [new WorldCapabilityRequest(
            Capability: WorldCapability.Mutate,
            Subject: GrantSubject.Section(section: WorldSection.State)
        )],
        maximumPendingContributions: 8
    );
    private static long ScoreValue(WorldHistoryHarness harness) => harness.Fixture.Row(name: "score").Cells!.Single().Value.Raw;

    [Fact]
    public void ADriveEndOpensTheExtensionEpochAgainAndKeepsThePreDriveInstanceRetired() {
        using var harness = new WorldHistoryHarness(definition: Scored(), on: false, seats: 1);
        var server = harness.Fixture.Server;

        harness.Steps(count: 5);
        Assert.True(condition: harness.Tape.TryBeginRecording(name: "epoch", refusal: out var refusal), userMessage: refusal);
        harness.Steps(count: 10);

        var stop = harness.Tape.StopRecording();

        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());

        using var before = Extension(server: server);

        Assert.True(condition: before.IsActive);
        Assert.True(condition: harness.Tape.TryBeginDrive(documentPath: null, forkName: null, name: "epoch", refusal: out refusal, toTick: null), userMessage: refusal);
        Assert.False(condition: before.IsActive);
        Assert.Throws<InvalidOperationException>(testCode: () => Extension(server: server));

        while (harness.Tape.Mode == WorldReplayMode.Replaying) {
            harness.Tape.InjectDriveTick();
            harness.StepWithoutInput();
        }

        // The drive ended: the instance that watched the replaced timeline stays retired, a fresh one admits, and its
        // contribution reaches the state on the next tick.
        Assert.False(condition: before.IsActive);
        Assert.Throws<InvalidOperationException>(testCode: () => before.Submit(mutation: Score(value: 3L)));

        using var after = Extension(server: server);

        Assert.True(condition: after.IsActive);
        _ = after.Submit(mutation: Score(value: 7L));
        Assert.Equal(expected: 1, actual: after.PendingCount);
        harness.StepWithoutInput();
        Assert.Equal(expected: 0, actual: after.PendingCount);
        Assert.Equal(expected: 7L, actual: ScoreValue(harness: harness));
    }
}
