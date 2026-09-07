using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// the law: a placement's <c>attach</c> facet can be driven live by an ordinary document rule — a body standing in a
/// region while holding a channel gets a named placement upserted onto it (<see cref="WorldPlacementAttach"/>,
/// <c>bodyIndex</c> literal, offset in the body's local frame); walking back out of the region upserts it back to its
/// authored stand pose. Both edges reuse the exact idiom <c>modules/studio.world.json</c>'s own
/// <c>studio-look-cycle</c> rule already ships (<c>compareState</c> over <c>$region:</c>/<c>$channel:</c>, <c>Edge</c>
/// mode) — no new engine primitive, no new schema member: the local-frame offset <see cref="WorldPlacementAttach.LocalOffset"/>
/// already carries is the only body-relative anchor a rig with no joint transforms can honor (see this suite's own
/// finding recorded in <c>modules/README.md</c>'s handheld section).
///
/// This mechanism is proven here rather than wired into the shipped <c>arcade.world.json</c>: each
/// <c>upsertPlacement</c>/<c>removePlacement</c> firing costs a flat 32,768 rule-work unit
/// (<see cref="Puck.World.UpsertPlacementEffect.Cost"/>), and the shipped <c>puck.world.json</c> already spends
/// 1,967,916 of its 2,000,000-unit ceiling (<see cref="Puck.State.RuleCapacity.MaxWorkUnitsPerTick"/>) before either
/// of these two rules lands — landing both overruns to 2,037,457 and refuses the whole world's boot (verified: every
/// <see cref="AuthoredGameFixtures.Nexus"/>-backed law in this suite fails the same way). That ceiling and every
/// other rule's own cost sit outside this task's file list, so the mechanism is authored and proven against an
/// isolated document instead.
/// </summary>
public sealed class HandheldAttachLawTests {
    private const string StandRegion = "handheldStand";
    private const string HandheldRow = "handheldHeld";
    private const string JumpChannel = "jump";
    private const int JumpOrdinal = 3;
    private const int ForwardOrdinal = 0;
    private static readonly Vector3 HandOffset = new(x: 0.22f, y: 1.02f, z: 0.34f);

    [Fact]
    public void APressedChannelInsideTheStandsRegionAttachesTheHandheldToTheBody_WalkingOutReturnsItToTheStand() {
        using var fixture = Fixtures.FreshServer(definition: Document(includeRules: true));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        // Control half: standing in the stand's reach with no channel pressed never attaches it.
        fixture.Step();
        Assert.Equal(expected: 0L, actual: HandheldHeldCell(fixture: fixture));
        Assert.Null(@object: Handheld(fixture: fixture).Attach);

        // The discriminating write: the seat's jump channel crosses the threshold while still in reach.
        SubmitChannel(fixture: fixture, actor: actor, ordinal: JumpOrdinal, value: FixedQ4816.One);
        fixture.Step();

        Assert.Equal(expected: 1L, actual: HandheldHeldCell(fixture: fixture));
        var held = Handheld(fixture: fixture);
        Assert.NotNull(@object: held.Attach);
        Assert.Equal(expected: 0, actual: held.Attach!.BodyIndex);
        Assert.Equal(expected: HandOffset, actual: held.Attach!.LocalOffset.Value);
        var resolvedAttach = WorldPlacementAttachment.TryResolve(
            attach: held.Attach!,
            population: fixture.Server.Population,
            position: out var attachedPosition,
            yawRadians: out _,
            reason: out var attachReason
        );
        Assert.True(condition: resolvedAttach, userMessage: attachReason);
        // The body starts unrotated, so the local offset adds unrotated onto its position — the same composition
        // WorldPlacementAttachment.TryResolve performs for the authoritative, fixed-point answer.
        var bodyPosition = fixture.Server.Body(index: 0)!.FixedPosition;
        Assert.Equal(expected: (double)bodyPosition.X + HandOffset.X, actual: (double)attachedPosition.X, precision: 3);
        Assert.Equal(expected: (double)bodyPosition.Y + HandOffset.Y, actual: (double)attachedPosition.Y, precision: 3);

        // Walk far enough out of the stand's reach for the region to clear — the release edge fires once.
        for (var tick = 0; (tick < 200); tick++) {
            SubmitChannel(fixture: fixture, actor: actor, ordinal: ForwardOrdinal, value: FixedQ4816.One);
            fixture.Step();
        }

        Assert.Equal(expected: 0L, actual: HandheldHeldCell(fixture: fixture));
        var returned = Handheld(fixture: fixture);
        Assert.Null(@object: returned.Attach);
        Assert.Equal(expected: 0f, actual: returned.Position.Value.X);
        Assert.Equal(expected: 1.08f, actual: returned.Position.Value.Y);
    }

    [Fact]
    public void WithNoRulesDeclaredTheChannelAndRegionNeverMoveTheHandheld_TheDiscriminatingControl() {
        using var fixture = Fixtures.FreshServer(definition: Document(includeRules: false));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        SubmitChannel(fixture: fixture, actor: actor, ordinal: JumpOrdinal, value: FixedQ4816.One);
        fixture.Step();

        Assert.Equal(expected: 0L, actual: HandheldHeldCell(fixture: fixture));
        Assert.Null(@object: Handheld(fixture: fixture).Attach);
    }

    private static void SubmitChannel(WorldFixture fixture, WorldPrincipal actor, int ordinal, FixedQ4816 value) => fixture.Server.ApplyIntentSubmission(
        body: fixture.Server.Body(index: actor.Index)!,
        submission: new IntentSubmission(
            Tick: 0UL,
            EntityIndex: actor.Index,
            Intent: default(PlayerIntent).WithChannel(ordinal: ordinal, value: value),
            Principal: actor
        )
    );

    private static long HandheldHeldCell(WorldFixture fixture) =>
        fixture.Server.Definition.State.Single(predicate: static row => (row.Name.Value == HandheldRow)).Cells!.Single().Value;

    private static WorldPlacement Handheld(WorldFixture fixture) =>
        fixture.Server.Definition.Placements.Single(predicate: static placement => (placement.Id == "handheld"));

    private static WorldPrototype BuildBoxCreation(string id) {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: 0.1f, y: 0.1f, z: 0.1f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: id,
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: id);

        return new WorldPrototype(Id: id, Document: canonical.Document, HashRaw: canonical.Hash);
    }

    // The stand carries the region a rule senses; the handheld starts on it (no attach, the authored stand pose) and
    // is upserted onto the body's attach facet on pickup, back to this same literal shape on release.
    private static WorldDefinition Document(bool includeRules) {
        var stand = BuildBoxCreation(id: "handheldStand");
        var handheld = BuildBoxCreation(id: "handheld");
        var standPlacement = new WorldPlacement(
            Id: "handheldStand",
            PrototypeId: stand.Id,
            Position: new Vector3(x: 0f, y: 0f, z: 0f),
            YawDegrees: 0f,
            Scale: 1f,
            Solid: new WorldSolid(Margin: 0f),
            Region: new WorldPlacementRegion(Radius: 1.0f)
        );
        var handheldPlacement = new WorldPlacement(
            Id: "handheld",
            PrototypeId: handheld.Id,
            Position: new Vector3(x: 0f, y: 1.08f, z: 0f),
            YawDegrees: 0f,
            Scale: 1f
        );
        var handheldAttached = handheldPlacement with {
            Position = new Vector3(x: 0f, y: 0f, z: 0f),
            Attach = new WorldPlacementAttach(BodyIndex: 0, LocalOffset: HandOffset, LocalYawDegrees: 0f),
        };
        var handheldHeld = CellName.Parse(candidate: HandheldRow);

        var document = Fixtures.BuildDocument() with {
            ChannelsRaw = [.. Fixtures.BuildDocument().Channels, new WorldChannel(Name: JumpChannel, Shape: ChannelShape.Unipolar, Composition: true)],
            CreationsRaw = [stand, handheld],
            PlacementsRaw = new WorldPlacementsSection(Policy: null, Rows: [standPlacement, handheldPlacement]),
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: handheldHeld,
                    Kind: CellKind.Int,
                    NonNegative: true,
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0L)]
                ),
            ]),
        };

        if (!includeRules) {
            return document;
        }

        return document with {
            Rules = [
                new WorldRule(
                    Name: CellName.Parse(candidate: "handheld-pickup"),
                    Gate: new ActionPredicate.All(Predicates: [
                        new ActionPredicate.CompareState(State: HandheldRow, Comparison: ActionStateComparison.Equal, Value: 0m),
                        new ActionPredicate.CompareState(State: $"{WorldRuleFacts.RegionPrefix}{StandRegion}", Comparison: ActionStateComparison.GreaterOrEqual, Value: 1m),
                        new ActionPredicate.CompareState(State: $"{WorldRuleFacts.ChannelPrefix}1:{JumpChannel}", Comparison: ActionStateComparison.GreaterOrEqual, Value: 1m),
                    ]),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [
                        new ActionEffect.SetState(State: HandheldRow, Value: 1m),
                        new WorldEffect.UpsertPlacement(Placement: handheldAttached),
                    ]
                ),
                new WorldRule(
                    Name: CellName.Parse(candidate: "handheld-release"),
                    Gate: new ActionPredicate.All(Predicates: [
                        new ActionPredicate.CompareState(State: HandheldRow, Comparison: ActionStateComparison.Equal, Value: 1m),
                        new ActionPredicate.CompareState(State: $"{WorldRuleFacts.RegionPrefix}{StandRegion}", Comparison: ActionStateComparison.Less, Value: 1m),
                    ]),
                    Mode: ActionTriggerMode.Edge,
                    Effects: [
                        new ActionEffect.SetState(State: HandheldRow, Value: 0m),
                        new WorldEffect.UpsertPlacement(Placement: handheldPlacement),
                    ]
                ),
            ],
        };
    }
}
