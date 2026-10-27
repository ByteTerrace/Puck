using Puck.Maths;
using Xunit;
using RuleGroupDeclaration = Puck.State.Rules.RuleGroupDeclaration;
using RuleGroupShape = Puck.State.Rules.RuleGroupShape;
using RuleGroupStep = Puck.State.Rules.RuleGroupStep;

namespace Puck.World.Server.Tests;

public sealed partial class WorldScopedReplayTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ABodyOnlyEdgeRuleRestartsWithItsScopeIncludingGroupedContinuation(bool grouped) {
        var definition = Document();
        var source = definition.ReplaysRaw![0];

        definition = definition with {
            ReplaysRaw = [source with { Bodies = [0], Inputs = [source.Inputs[0] with { Tick = 0 }] }],
            Rules = [new WorldRule(CellName.Parse(candidate: "body-edge"),
                [new WorldEffect.Pose(Key: "0", Position: new System.Numerics.Vector3(x: 91, y: 10, z: 0))],
                Gate: new ActionPredicate.CompareState("counter", ExpressionOp.GreaterOrEqual, 1m), Mode: ActionTriggerMode.Edge),
                new WorldRule(CellName.Parse(candidate: "outside-body-edge"),
                    [new WorldEffect.Pose(Key: "1", Position: new System.Numerics.Vector3(x: 92, y: 10, z: 0))],
                    Gate: new ActionPredicate.CompareState("counter", ExpressionOp.GreaterOrEqual, 1m), Mode: ActionTriggerMode.Edge)],
            RuleGroupsRaw = (grouped ? [new RuleGroupDeclaration(CellName.Parse(candidate: "body-group"), RuleGroupShape.Staged,
                [new RuleGroupStep(CellName.Parse(candidate: "body-edge"))], Trigger: new ActionPredicate.CompareState("counter", ExpressionOp.GreaterOrEqual, 1m))] : null),
        };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat();
        fixture.JoinSeat(slot: 1);
        Request(fixture: fixture, request: 4); Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 8); i++) { fixture.Step(); }
        var scope = fixture.Server.ScopedReplays[0];

        Assert.Equal(FixedQ4816.FromInteger(value: 91), fixture.Server.Body(index: 0)!.FixedPosition.X);
        if (!grouped) { Assert.Single(collection: scope.Checkpoint().RuleGateHeld); }
        Assert.Equal((grouped ? 1 : 0), scope.Checkpoint().RuleGroups.Count);
        Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 8); i++) { fixture.Step(); }
        Assert.Equal(4, Read(fixture: fixture, key: "status", row: "control"));
        Assert.Equal(FixedQ4816.FromInteger(value: 91), fixture.Server.Body(index: 0)!.FixedPosition.X);
        var hash = scope.Hash();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        fixture.Server.RestoreCheckpoint(checkpoint: checkpoint! with {
            Server = checkpoint.Server with {
                RuleGateHeld = checkpoint.Server.RuleGateHeld.Select(selector: entry => ((entry.Rule == "outside-body-edge") ? entry with { Held = false } : entry)).ToArray(),
            },
        });
        Assert.Equal(hash, fixture.Server.ScopedReplays[0].Hash());
        fixture.Server.RestoreCheckpoint(checkpoint: checkpoint! with {
            Server = checkpoint.Server with {
                RuleGateHeld = checkpoint.Server.RuleGateHeld.Select(selector: entry => ((entry.Rule == "body-edge") ? entry with { Held = false } : entry)).ToArray(),
            },
        });
        Assert.NotEqual(hash, fixture.Server.ScopedReplays[0].Hash());
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void SharedBodyContinuationIsRefusedBeforeCapture(bool dynamic) {
        var definition = Document();

        definition = definition with {
            ReplaysRaw = [definition.ReplaysRaw![0] with { Bodies = [0] }],
            Rules = [new WorldRule(CellName.Parse(candidate: "shared-body"), (dynamic
                ? [new WorldEffect.Pose(Key: "$each", Position: new System.Numerics.Vector3(x: 91, y: 10, z: 0))]
                : [new WorldEffect.Pose(Key: "0", Position: new System.Numerics.Vector3(x: 91, y: 10, z: 0)),
                    new WorldEffect.Pose(Key: "1", Position: new System.Numerics.Vector3(x: 92, y: 10, z: 0))]),
                Gate: new ActionPredicate.CompareState("counter", ExpressionOp.GreaterOrEqual, 1m),
                Mode: ActionTriggerMode.Edge, ForEach: (dynamic ? (StateChannelRef)"$bodies" : null))],
        };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat();
        fixture.JoinSeat(slot: 1);
        Request(fixture: fixture, request: 4);
        Assert.Equal(-1, Read(fixture: fixture, key: "status", row: "control"));
        Assert.Null(@object: fixture.Server.ScopedReplays[0].Checkpoint().Start);
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.ScopedReplays[0].Hash());

        Assert.Contains("scope", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("shared-body", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("cannot isolate body continuation", refusal.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void AnActiveScopeHashAllocatesNothingAfterBinding() {
        using var fixture = Fixtures.FreshServer(Document());

        Request(fixture: fixture, request: 4); Request(fixture: fixture, request: 1);
        var scope = fixture.Server.ScopedReplays[0];

        Assert.True(condition: scope.Checkpoint().Active);
        for (var i = 0; (i < 2000); i++) { scope.Hash(); }
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; (i < 1000); i++) { scope.Hash(); }
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before));
    }
    [Fact]
    public void AnotherScopesFallingRigidBodyCannotChangeTheLocalSettlingFactOrReplayVerdict() {
        var scoped = Document();
        var definition = WorldRigidCommandLawTests.FallingRigidBallDocument() with {
            StateRaw = scoped.StateRaw,
            GrantsRaw = scoped.GrantsRaw,
            ReplaysRaw = [scoped.ReplaysRaw![0] with { Rows = ["counter", "witness"] }],
            Rules = [new WorldRule(CellName.Parse(candidate: "local-quiet"), [new ActionEffect.SetState("witness", 1m)],
                Gate: new ActionPredicate.CompareState("$physics:quiescent:scope", ExpressionOp.Equal, 1m), Mode: ActionTriggerMode.Level)],
        };
        using var fixture = Fixtures.FreshServer(definition);
        var ball = fixture.JoinSeat();

        ball.Pose(pitchRadians: 0, rollRadians: 0, x: 0, y: 30, yawRadians: 0, z: 0);
        Request(fixture: fixture, request: 4); Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 8); i++) { fixture.Step(); }
        Assert.False(condition: fixture.Server.Population.RigidBodiesQuiescent());
        Assert.True(condition: fixture.Server.Population.RigidBodiesQuiescent(bodies: []));
        Assert.False(condition: fixture.Server.Population.RigidBodiesQuiescent(bodies: [0]));
        ball.Pose(pitchRadians: 0, rollRadians: 0, x: 0, y: 90, yawRadians: 0, z: 0);
        Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 8); i++) { fixture.Step(); }
        Assert.Equal(4, Read(fixture: fixture, key: "status", row: "control"));
        Assert.Equal(1, Read(fixture, "witness"));
    }
    [Fact]
    public void AnEdgeRuleRestartsWithItsScopeAndItsLatchChangesTheScopeHash() {
        var definition = Document();
        var source = definition.ReplaysRaw![0];

        definition = definition with {
            ReplaysRaw = [source with { Rows = ["counter", "witness"], Inputs = [source.Inputs[0] with { Tick = 0 }] }],
            Rules = [new WorldRule(CellName.Parse(candidate: "edge"), [new ActionEffect.SetState("witness", 1m)],
                Gate: new ActionPredicate.CompareState("counter", ExpressionOp.GreaterOrEqual, 1m), Mode: ActionTriggerMode.Edge)],
        };
        using var fixture = Fixtures.FreshServer(definition);

        Request(fixture: fixture, request: 4); Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 8); i++) { fixture.Step(); }
        Assert.Equal(1, Read(fixture, "witness"));
        Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 8); i++) { fixture.Step(); }
        Assert.Equal(4, Read(fixture: fixture, key: "status", row: "control"));
        Assert.Equal(1, Read(fixture, "witness"));
        var scope = fixture.Server.ScopedReplays[0];
        var tape = scope.Checkpoint();
        var authority = WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0);

        Assert.Throws<InvalidOperationException>(testCode: () => scope.RestoreCheckpoint(checkpoint: tape with {
            RuleGateHeld = [new WorldRuleLatchEntry("edge", "forged-outside-key", -1, -1, true)],
        }));
        Assert.Equal(authority, WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0));
        Assert.False(condition: fixture.Server.Arena.Keys.TryResolve(CellName.Parse(candidate: "forged-outside-key"), out _));
        var scopeHash = fixture.Server.ScopedReplays[0].Hash();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        fixture.Server.RestoreCheckpoint(checkpoint: checkpoint! with {
            Server = checkpoint.Server with {
                RuleGateHeld = checkpoint.Server.RuleGateHeld.Select(selector: entry => ((entry.Rule == "edge") ? entry with { Held = false } : entry)).ToArray(),
            },
        });
        Assert.NotEqual(scopeHash, fixture.Server.ScopedReplays[0].Hash());
    }
    [Fact]
    public void AStartingImageCannotRestoreAnOutsideBodyAndItsActualContentsAreHashed() {
        using var fixture = Fixtures.FreshServer(Document());

        fixture.JoinSeat(slot: 1);
        Request(fixture: fixture, request: 4); Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 8); i++) { fixture.Step(); }
        var scope = fixture.Server.ScopedReplays[0];
        var checkpoint = scope.Checkpoint();
        var outsider = fixture.Server.Population.Capture().Entries.Single(predicate: entry => (entry.Index == 1));
        var before = WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0);
        var exception = Assert.Throws<InvalidOperationException>(testCode: () => scope.RestoreCheckpoint(checkpoint: checkpoint with {
            Bodies = [outsider with { Position = new FixedVector3(X: FixedQ4816.FromInteger(value: 91), Y: FixedQ4816.FromInteger(value: 92), Z: FixedQ4816.FromInteger(value: 93)) }],
        }));

        Assert.Contains("scope", exception.Message, StringComparison.Ordinal);
        Assert.Contains("body inventory", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0));
        Assert.Equal(outsider.Position, fixture.Server.Body(index: 1)!.FixedPosition);

        var image = checkpoint.Start!;
        var numbers = image.Numbers.ToArray();
        var cell = Array.FindIndex(array: numbers, match: entry => (entry.Column == ArenaColumn.Number));

        numbers[cell] = numbers[cell] with { Value = (numbers[cell].Value + 1) };
        scope.RestoreCheckpoint(checkpoint: checkpoint with { Start = image with { Numbers = numbers } });
        Assert.NotEqual(before, WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0));
        // The installed image owns its arrays; editing the caller's copy cannot change it.
        var installed = WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0);

        numbers[cell] = numbers[cell] with { Value = (numbers[cell].Value + 1) };
        Assert.Equal(installed, WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0));
    }
    [Fact]
    public void ATruncatedHashTapeIsRefusedByNameBeforePlayback() {
        using var fixture = Fixtures.FreshServer(Document());

        Request(fixture: fixture, request: 4); Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 8); i++) { fixture.Step(); }
        var scope = fixture.Server.ScopedReplays[0];
        var checkpoint = scope.Checkpoint();
        var before = WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0);
        var exception = Assert.Throws<InvalidOperationException>(testCode: () => scope.RestoreCheckpoint(checkpoint: checkpoint with { Hashes = checkpoint.Hashes.Take(count: 1).ToArray() }));

        Assert.Contains("Scoped replay 'scope' refused: incomplete hash tape", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0));
        Request(fixture: fixture, request: 1);
        for (var i = 0; (i < 8); i++) { fixture.Step(); }
        Assert.Equal(4, Read(fixture: fixture, key: "status", row: "control"));
    }
    [Fact]
    public void AnInactiveScopeAllocatesNothingOnSteadyTicks() {
        using var fixture = Fixtures.FreshServer(Document());

        for (var i = 0; (i < 2000); i++) { fixture.Step(); }
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; (i < 1000); i++) { fixture.Step(); }
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before));
    }
}
