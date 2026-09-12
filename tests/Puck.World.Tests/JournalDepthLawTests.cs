using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// <c>host.journalDepth</c>: the undo horizon, in journal entries. <c>0</c> (the default, every world authored
/// before this field existed) keeps today's unbounded growth. A positive depth folds entries past the horizon
/// forward into the base the journal already keeps as they age out — <see cref="WorldServer.EnforceJournalDepth"/>
/// — so the journal never grows past it, <c>world.undo</c> can never reach past what it kept, and a checkpoint
/// captured at any point still restores the live definition bit-identically.
/// </summary>
public sealed class JournalDepthLawTests {
    private static WorldDefinition WithDepth(int depth) {
        var source = Fixtures.BuildDocument();

        return (source with { HostRaw = (source.Host with { JournalDepth = depth }) });
    }

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [], AppliedTransferHighWater: null, AppliedTransferIds: [], ElapsedEngineTicks: 0,
        ForwardedBodies: [], FreshCounter: 0, InDoubtTransfers: [], IsPaused: false, NextTransferId: 1,
        PortalOccupancy: [], Retained: false, ScheduleAccumulatorTicks: 0, SeededArrivals: []
    );

    [Fact]
    public void AuthoredDepthBoundsTheJournalAcrossTenThousandRuleAndConsoleMutationsAndACheckpointRestoreMatches() {
        const int depth = 50;
        const int mutationCount = 10_000;
        using var fixture = Fixtures.FreshServer(definition: WithDepth(depth: depth));
        var row = new WorldStateRow(Name: CellName.Parse(candidate: "probe"), Kind: CellKind.Int);

        for (var index = 0; (index < mutationCount); index++) {
            // Alternates the console door with the one structural principal a world's own rule effect submits
            // under (see WorldServer.TryAdmitMutation's remarks on WorldPrincipal.World), so both origins land in
            // the same journal this law bounds.
            var principal = (((index % 2) == 0) ? WorldPrincipal.Console : WorldPrincipal.World);

            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(Principal: principal, Row: row));
            fixture.Step();
            fixture.Server.EnforceJournalDepth();

            Assert.True(
                condition: (fixture.Server.JournalLength <= depth),
                userMessage: $"journal length {fixture.Server.JournalLength} exceeded host.journalDepth {depth} after {index + 1} mutations"
            );
        }

        Assert.Equal(expected: depth, actual: fixture.Server.JournalLength);
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            hostRow: EmptyHostRow(),
            checkpoint: out var checkpoint,
            reason: out var captureReason
        ), userMessage: captureReason);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        var machines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);

        try {
            var (restored, _) = WorldServer.FromCheckpoint(
                checkpoint: checkpoint,
                instanceIdentity: "journal-depth-restore",
                machines: machines,
                profiles: new WorldOwnedWorlds(
                    directory: Directory.CreateTempSubdirectory(prefix: "puck-journal-depth-tests-").FullName,
                    machineId: Guid.NewGuid(),
                    template: restoredDefinition
                )
            );

            Assert.Equal(expected: fixture.DefinitionBytes(), actual: WorldDefinitionSerialization.Serialize(definition: restored.Definition));
            Assert.Equal(expected: depth, actual: restored.JournalLength);
        } finally {
            machines.Dispose();
        }
    }

    [Fact]
    public void AnUnauthoredDepthLeavesTheJournalUnboundedLikeToday() {
        using var fixture = Fixtures.FreshServer(); // default host — JournalDepth 0.
        var row = new WorldStateRow(Name: CellName.Parse(candidate: "probe"), Kind: CellKind.Int);

        for (var index = 0; (index < 4); index++) {
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(Principal: WorldPrincipal.Console, Row: row));
            fixture.Step();
            fixture.Server.EnforceJournalDepth();
        }

        Assert.Equal(expected: 30, actual: fixture.Server.JournalLength);
    }

    [Fact]
    public void UndoPastTheBoundedHorizonRefusesNamingTheDepth_UndoWithinTheHorizonStillSucceeds() {
        const int depth = 3;
        using var fixture = Fixtures.FreshServer(definition: WithDepth(depth: depth));
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        for (var index = 0; (index < 1); index++) {
            var row = new WorldStateRow(Name: CellName.Parse(candidate: $"probe{index}"), Kind: CellKind.Int);

            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(Principal: WorldPrincipal.Console, Row: row));
            fixture.Step();
            fixture.Server.EnforceJournalDepth();
        }

        Assert.Equal(expected: depth, actual: fixture.Server.JournalLength);

        Laws.RefusalWithControl(
            lawId: "journal-depth.undo-past-horizon",
            deniedOutcome: () => {
                var before = fixture.DefinitionBytes();

                fixture.Server.EnqueueUndo(count: (depth + 1), principal: WorldPrincipal.Console);
                fixture.Step();

                return !before.AsSpan().SequenceEqual(other: fixture.DefinitionBytes());
            },
            controlOutcome: () => {
                var before = fixture.DefinitionBytes();

                fixture.Server.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
                fixture.Step();

                return !before.AsSpan().SequenceEqual(other: fixture.DefinitionBytes());
            }
        );

        Assert.Contains(collection: refusals, filter: reason => (
            reason.Contains(value: "journalDepth", comparisonType: StringComparison.Ordinal) &&
            reason.Contains(value: depth.ToString(), comparisonType: StringComparison.Ordinal)
        ));
    }

    [Fact]
    public void ANegativeJournalDepthIsRefused_ANonNegativeControlIsAdmitted() {
        Laws.RefusalWithControl(
            lawId: "journal-depth.non-negative",
            deniedOutcome: () => WorldDefinitionValidator.TryValidate(definition: WithDepth(depth: -1), neighbours: null, reason: out _),
            controlOutcome: () => WorldDefinitionValidator.TryValidate(definition: WithDepth(depth: 0), neighbours: null, reason: out _)
        );
    }
}
