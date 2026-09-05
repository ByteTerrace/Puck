using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.Physics.Motion;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the tabletop primitive: a Grid topology's world-space frame (<see
/// cref="CompiledWorldTopology.TryCellOf"/>/<see cref="CompiledWorldTopology.TryOffset"/>) and the physical-to-
/// logical bridge a world rule builds over the <c>$board:cellOf:</c> channel — a rigid body's resting cell derives a
/// board row, and an illegal destination is recorded without disturbing <c>lastLegal</c>.</summary>
public sealed class TabletopBoardLawTests {
    [Fact]
    public void GridFrameRoundTripsPositionToCellAndRejectsOutOfBoundsAndWrongKind() {
        var grid = new WorldStateLatticeTopology.Grid("board", new DocumentVector3(10f, 2f, -5f), 0.5f, 4, 4);
        var hex = new WorldStateLatticeTopology.Hex("hex", new DocumentVector3(0, 0, 0), 1, Radius: 1);
        var state = new WorldStateSection(Lattices: [grid, hex]);
        var topology = WorldTopologyCompilation.Find(state, "board")!;

        // Every cell center resolves back to itself — the round trip the tabletop's cellOf/offset math promises.
        for (var z = 0; z < 4; z++) {
            for (var x = 0; x < 4; x++) {
                var center = new FixedVector3(
                    X: FixedQ4816.FromDouble(10d + ((x + 0.5) * 0.5)),
                    Y: FixedQ4816.FromDouble(2d),
                    Z: FixedQ4816.FromDouble(-5d + ((z + 0.5) * 0.5)));
                Assert.True(topology.TryCellOf(position: center, cell: out var resolved));
                Assert.Equal((z * 4) + x, resolved);
            }
        }

        // Without a band, TryCellOf is X/Z-only: the same cell-0 center hundreds of units above or below still
        // resolves to cell 0. With a band, only positions within the half-extent of the origin's Y resolve, so a
        // piece on the floor beneath the table reads as off the board.
        Assert.True(topology.TryCellOf(position: new FixedVector3(FixedQ4816.FromDouble(10.25d), FixedQ4816.FromDouble(502d), FixedQ4816.FromDouble(-4.75d)), cell: out var highCell));
        Assert.Equal(0, highCell);
        Assert.True(topology.TryCellOf(position: new FixedVector3(FixedQ4816.FromDouble(10.25d), FixedQ4816.FromDouble(-498d), FixedQ4816.FromDouble(-4.75d)), cell: out var lowCell));
        Assert.Equal(0, lowCell);
        var banded = WorldTopologyCompilation.Find(new WorldStateSection(Lattices: [.. state.Lattices!.Select(t => t.Name == "board" ? ((WorldStateLatticeTopology.Grid)t) with { Band = 0.3f } : t)]), "board")!;
        Assert.True(banded.TryCellOf(position: new FixedVector3(FixedQ4816.FromDouble(10.25d), FixedQ4816.FromDouble(2.2d), FixedQ4816.FromDouble(-4.75d)), cell: out var nearCell));
        Assert.Equal(0, nearCell);
        Assert.False(banded.TryCellOf(position: new FixedVector3(FixedQ4816.FromDouble(10.25d), FixedQ4816.FromDouble(1.5d), FixedQ4816.FromDouble(-4.75d)), cell: out _));
        Assert.False(WorldTopologyCompilation.TryValidate(((WorldStateLatticeTopology.Grid)state.Lattices![0]) with { Band = -1f }, out var bandReason));
        Assert.Contains("band", bandReason);

        // Below the origin corner: outside the frame on both axes.
        Assert.False(topology.TryCellOf(position: new FixedVector3(FixedQ4816.FromDouble(9d), FixedQ4816.Zero, FixedQ4816.FromDouble(-6d)), cell: out _));
        // One cell past the far edge: outside on X alone.
        Assert.False(topology.TryCellOf(position: new FixedVector3(FixedQ4816.FromDouble(12.1d), FixedQ4816.Zero, FixedQ4816.FromDouble(-4.75d)), cell: out _));

        // Offset walks the same rectangular coordinates neighbour() does, but to an ARBITRARY (dx, dz) — the leaper
        // reach neighbour()'s fixed eight directions cannot express.
        Assert.True(topology.TryOffset(cell: 0, dx: 1, dz: 2, result: out var leap));
        Assert.Equal(9, leap); // (x=1, z=2) => 2*4+1
        Assert.False(topology.TryOffset(cell: 0, dx: -1, dz: 0, result: out _)); // off the west edge, no wrap declared
        Assert.False(topology.TryOffset(cell: 3, dx: 1, dz: 0, result: out _)); // off the east edge

        // A CONTROL: neither spatial query means anything off a Grid topology — a hex frame answers false rather
        // than silently reusing rectangular coordinates that were never declared for it.
        var hexTopology = WorldTopologyCompilation.Find(state, "hex")!;
        Assert.False(hexTopology.TryCellOf(position: new FixedVector3(FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero), cell: out _));
        Assert.False(hexTopology.TryOffset(cell: 0, dx: 1, dz: 0, result: out _));
    }

    // cellSize is the divisor TryCellOf resolves world positions against (localX / cellSize): zero divides by zero,
    // and a negative or non-finite edge resolves cells with the wrong sign or NaN silently, so a Grid must refuse a
    // cellSize that does not quantize to a positive Q48.16 value at validation, before any body ever settles.
    [Theory]
    [InlineData(0f, true)]
    [InlineData(-0.2f, true)]
    [InlineData(float.NaN, true)]
    [InlineData(float.PositiveInfinity, true)]
    [InlineData(1e30f, true)]  // does not quantize to Q48.16 — the same overflow guard fields.lattice.cellSize uses.
    [InlineData(0.2f, false)]  // the DISCRIMINATING control: an ordinary positive edge validates.
    public void GridTopologyRefusesNonPositiveOrUnrepresentableCellSize(float cellSize, bool refused) {
        var grid = new WorldStateLatticeTopology.Grid("board", new DocumentVector3(0f, 0f, 0f), cellSize, 8, 8);
        Assert.Equal(!refused, WorldTopologyCompilation.TryValidate(topology: grid, reason: out var reason));
        if (refused) {
            Assert.Contains("cellSize", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GridTopologyRefusesAnOriginThatDoesNotFitQ4816() {
        var grid = new WorldStateLatticeTopology.Grid("board", new DocumentVector3(1e30f, 0f, 0f), 0.2f, 8, 8);
        Assert.False(WorldTopologyCompilation.TryValidate(topology: grid, reason: out var reason));
        Assert.Contains("origin", reason, StringComparison.Ordinal);
    }

    // A 2x2 board (cell pitch 1, origin at the world origin) anchored beside a flat floor: one rigid body settling
    // over a cell derives that board's occupancy, and a second settle onto an already-occupied cell is recorded as
    // illegal without moving lastLegal — the exact "record only, never rejects" contract the garden's chess table
    // rides. Cell 1 is pre-seeded occupied (value 5) with no body ever placed there, so the illegality is a property
    // of the RULE reading previousBoard, not an artifact of two bodies colliding.
    private const string Quiescent = "$physics:quiescent";
    private static ActionPredicate.CompareState Cs(string state, ActionStateComparison comparison, decimal? value = null, string? key = null,
        string? comparandState = null, string? comparandKey = null) => new(state, comparison, value, key, comparandState, comparandKey);
    private static ActionPredicate All(params ActionPredicate[] predicates) => new ActionPredicate.All(predicates);
    private static ActionEffect.SetState Set(string state, decimal? value = null, string? key = null, string? fromState = null, string? fromKey = null) =>
        new(state, value, ActionTarget.Self, key, fromState, fromKey);
    private static WorldRule Rule(string name, ActionPredicate gate, ActionTriggerMode mode, params ActionEffect[] effects) =>
        new(CellName.Parse(name), effects, gate, mode);
    private static ActionPredicate[] MoverDetectPredicates(ActionPredicate quiescentEqualsOne, bool excludeOffFrameMover) {
        ActionPredicate[] baseline = [
            quiescentEqualsOne, Cs("justMoved", ActionStateComparison.Equal, 0m),
            Cs("piecePrevCell", ActionStateComparison.NotEqual, -1m, key: "0"),
            Cs("pieceCell", ActionStateComparison.NotEqual, key: "0", comparandState: "piecePrevCell", comparandKey: "0"),
        ];
        return excludeOffFrameMover
            ? [.. baseline, Cs("pieceCell", ActionStateComparison.NotEqual, -1m, key: "0")]
            : baseline;
    }
    private static WorldDefinition TabletopBridgeDocument(bool excludeOffFrameMover = true) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(Id: 0, Name: "floor", Type: SdfSolidPrimitive.Box, Position: Vector3.Zero,
            Rotation: Quaternion.Identity, Scale: new Vector3(x: 24f, y: 0.1f, z: 24f), Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0);
        var document = new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: "rigid-floor", Palette: null, Shapes: [shape], Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "rigid-floor");
        var creation = new WorldPrototype(Id: "floor", Document: canonical.Document, HashRaw: canonical.Hash);
        var rigid = new WorldRigid(Mass: 1f, Restitution: 0.05f, Friction: 1f, RollingFriction: 2f, LinearDamping: 1f, AngularDamping: 1f);
        var topology = new WorldStateLatticeTopology.Grid("board", new DocumentVector3(0f, 0f, 0f), 1f, 2, 2);

        long[] starting = [0, 5, 0, 0];
        WorldStateRow BoardRow(string name, bool seeded) => new(CellName.Parse(name), CellKind.Int,
            Cells: [.. Enumerable.Range(0, 4).Select(k => new WorldStateCell(CellName.Parse(k.ToString()), seeded ? starting[k] : 0))],
            Domain: new WorldStateDomain.CellsOf("board"));
        WorldStateRow Keyed(string name, long initial) => new(CellName.Parse(name), CellKind.Int, Min: -1, Max: 3,
            Cells: [new WorldStateCell(CellName.Parse("0"), initial)], Capacity: 1);
        WorldStateRow Slot(string name, long initial) => new(CellName.Parse(name), CellKind.Int, Min: -1, Max: 5,
            Cells: [new WorldStateCell(CellName.Parse(WorldStateRow.SlotKey), initial)]);

        var quiescentEqualsOne = Cs(Quiescent, ActionStateComparison.Equal, 1m);

        WorldRule[] rules = [
            Rule("snapshot-board", quiescentEqualsOne, ActionTriggerMode.Edge,
                Set("previousBoard", key: "0", fromState: "board", fromKey: "0"),
                Set("previousBoard", key: "1", fromState: "board", fromKey: "1"),
                Set("previousBoard", key: "2", fromState: "board", fromKey: "2"),
                Set("previousBoard", key: "3", fromState: "board", fromKey: "3")),
            Rule("clear-board", quiescentEqualsOne, ActionTriggerMode.Edge,
                Set("board", value: 0m, key: "0"), Set("board", value: 0m, key: "1"),
                Set("board", value: 0m, key: "2"), Set("board", value: 0m, key: "3")),
            Rule("snapshot-piece", quiescentEqualsOne, ActionTriggerMode.Edge,
                Set("piecePrevCell", key: "0", fromState: "pieceCell", fromKey: "0")),
            Rule("derive-piece", quiescentEqualsOne, ActionTriggerMode.Edge,
                Set("pieceCell", key: "0", fromState: "$board:cellOf:board:body:0"),
                Set("board", key: "$cell:pieceCell:0", value: 9m)),
            Rule("mover-detect", All(MoverDetectPredicates(quiescentEqualsOne, excludeOffFrameMover)),
                ActionTriggerMode.Edge, Set("justMoved", value: 1m)),
            Rule("verdict-optimistic", Cs("justMoved", ActionStateComparison.Equal, 1m), ActionTriggerMode.Level, Set("verdict", value: 1m)),
            Rule("illegal-check", All(Cs("justMoved", ActionStateComparison.Equal, 1m),
                    Cs("previousBoard", ActionStateComparison.NotEqual, 0m, key: "$cell:pieceCell:0")),
                ActionTriggerMode.Level, Set("verdict", value: 0m)),
            Rule("apply-legal", All(Cs("justMoved", ActionStateComparison.Equal, 1m), Cs("verdict", ActionStateComparison.Equal, 1m)),
                ActionTriggerMode.Level,
                Set("lastLegal", key: "0", fromState: "board", fromKey: "0"), Set("lastLegal", key: "1", fromState: "board", fromKey: "1"),
                Set("lastLegal", key: "2", fromState: "board", fromKey: "2"), Set("lastLegal", key: "3", fromState: "board", fromKey: "3")),
            Rule("apply-illegal", All(Cs("justMoved", ActionStateComparison.Equal, 1m), Cs("verdict", ActionStateComparison.Equal, 0m)),
                ActionTriggerMode.Level, new ActionEffect.AddState("illegalCount", 1m)),
            Rule("reset-just-moved", Cs("justMoved", ActionStateComparison.Equal, 1m), ActionTriggerMode.Level, Set("justMoved", value: 0m)),
        ];

        return source with {
            CollisionRaw = source.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
            CreationsRaw = [creation],
            GravityRaw = source.Gravity with { Uniform = new DocumentVector3(value: new Vector3(x: 0f, y: -9.8f, z: 0f)) },
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = new WorldCollider.Sphere(Radius: 0.15f),
                Rigid = rigid,
            })],
            PlacementRowsRaw = [new WorldPlacement(Id: "floor", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
            StateRaw = new WorldStateSection(
                Lattices: [topology],
                World: [
                    BoardRow("board", seeded: true), BoardRow("previousBoard", seeded: true), BoardRow("lastLegal", seeded: true),
                    Keyed("pieceCell", -1), Keyed("piecePrevCell", -1),
                    Slot("verdict", 1), Slot("illegalCount", 0), Slot("justMoved", 0),
                ]),
            Rules = rules,
        };
    }

    // A second 2x2-board fixture, isolating ONE property: whether one piece's derive failing (its body off the
    // frame entirely) can cost a SIBLING piece its own, otherwise-successful, derive. Piece 0 rides body:0, posed
    // over cell 0 and left to settle; piece 1 rides body:1, a declared-but-never-joined (inactive) body — $board:
    // cellOf answers -1 for an inactive body exactly as it does for one that has physically left the frame, so this
    // stands in for a second piece knocked clean off the table without needing a second physical body. splitRules
    // toggles between the fix (each piece's pieceCell+board write is its OWN rule, so one failing write is isolated)
    // and the defect it replaces (all four writes riding one rule, so the whole contiguous run — including piece
    // 0's otherwise-valid write — is preflighted, and rejected, as one candidate).
    private static WorldDefinition TwoPieceTabletopDocument(bool splitRules) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(Id: 0, Name: "floor", Type: SdfSolidPrimitive.Box, Position: Vector3.Zero,
            Rotation: Quaternion.Identity, Scale: new Vector3(x: 24f, y: 0.1f, z: 24f), Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0);
        var document = new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: "rigid-floor", Palette: null, Shapes: [shape], Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "rigid-floor");
        var creation = new WorldPrototype(Id: "floor", Document: canonical.Document, HashRaw: canonical.Hash);
        var rigid = new WorldRigid(Mass: 1f, Restitution: 0.05f, Friction: 1f, RollingFriction: 2f, LinearDamping: 1f, AngularDamping: 1f);
        var topology = new WorldStateLatticeTopology.Grid("board", new DocumentVector3(0f, 0f, 0f), 1f, 2, 2);

        WorldStateRow BoardRow(string name) => new(CellName.Parse(name), CellKind.Int,
            Cells: [.. Enumerable.Range(0, 4).Select(k => new WorldStateCell(CellName.Parse(k.ToString()), 0))],
            Domain: new WorldStateDomain.CellsOf("board"));
        WorldStateRow Keyed(string name, long initial, int capacity) => new(CellName.Parse(name), CellKind.Int, Min: -1, Max: 3,
            Cells: [.. Enumerable.Range(0, capacity).Select(k => new WorldStateCell(CellName.Parse(k.ToString()), initial))], Capacity: capacity);

        var quiescentEqualsOne = Cs(Quiescent, ActionStateComparison.Equal, 1m);
        ActionEffect[] deriveEffects = [
            Set("pieceCell", key: "0", fromState: "$board:cellOf:board:body:0"),
            Set("board", key: "$cell:pieceCell:0", value: 9m),
            Set("pieceCell", key: "1", fromState: "$board:cellOf:board:body:1"),
            Set("board", key: "$cell:pieceCell:1", value: 7m),
        ];
        WorldRule[] deriveRules = splitRules
            ? [
                Rule("derive-piece0", quiescentEqualsOne, ActionTriggerMode.Edge, deriveEffects[0], deriveEffects[1]),
                Rule("derive-piece1", quiescentEqualsOne, ActionTriggerMode.Edge, deriveEffects[2], deriveEffects[3]),
            ]
            : [Rule("derive-pieces", quiescentEqualsOne, ActionTriggerMode.Edge, deriveEffects)];

        WorldRule[] rules = [
            Rule("clear-board", quiescentEqualsOne, ActionTriggerMode.Edge,
                Set("board", value: 0m, key: "0"), Set("board", value: 0m, key: "1"),
                Set("board", value: 0m, key: "2"), Set("board", value: 0m, key: "3")),
            .. deriveRules,
        ];

        return source with {
            CollisionRaw = source.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
            CreationsRaw = [creation],
            GravityRaw = source.Gravity with { Uniform = new DocumentVector3(value: new Vector3(x: 0f, y: -9.8f, z: 0f)) },
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = new WorldCollider.Sphere(Radius: 0.15f),
                Rigid = rigid,
            })],
            PlacementRowsRaw = [new WorldPlacement(Id: "floor", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
            StateRaw = new WorldStateSection(
                Lattices: [topology],
                World: [BoardRow("board"), Keyed("pieceCell", -1, capacity: 2)]),
            Rules = rules,
        };
    }

    [Theory]
    [InlineData(true)]  // isolated per-piece rules — piece 0 derives despite piece 1 being off-frame.
    [InlineData(false)] // one combined rule — every top-level effect is its own boundary, so piece 0 still derives.
    public void OneOffFrameSiblingNeverCostsTheOtherPieceItsDeriveWhateverTheRuleShape(bool splitRules) {
        using var fixture = Fixtures.FreshServer(definition: TwoPieceTabletopDocument(splitRules: splitRules));
        var left = WorldPrincipal.Seat(slot: 0);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(left, left.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: 0)!;
        body.Pose(x: 0.5f, y: 1f, z: 0.5f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f); // over cell 0
        Assert.Null(fixture.Server.Body(index: 1)); // never joined — body:1 stays inactive, cellOf answers -1.

        for (var tick = 0; tick < 400; tick++) {
            fixture.Step();
        }

        var board = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "board")!;
        var cell0 = board.Cells!.Single(c => c.Key.Value == "0").Value;

        Assert.Equal(9, cell0); // piece 1's off-frame write is refused alone — piece 0 still derives, in either shape.
    }

    [Fact]
    public void RestingBodyDerivesOccupancyAndAnIllegalDestinationLeavesLastLegalUntouched() {
        using var fixture = Fixtures.FreshServer(definition: TabletopBridgeDocument());
        var left = WorldPrincipal.Seat(slot: 0);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(left, left.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: 0)!;
        body.Pose(x: 0.5f, y: 1f, z: 0.5f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f); // over cell 0

        for (var tick = 0; tick < 400; tick++) {
            fixture.Step();
        }

        WorldStateRow Row(string name) => WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, name)!;
        long Cell(WorldStateRow row, string key) => row.Cells!.Single(c => c.Key.Value == key).Value;

        // The bridge: the resting body's cellOf derived cell 0 as occupied — never a boot phantom move (the row's
        // OWN sentinel guard keeps the first-ever derive from registering as a "move").
        Assert.Equal(9, Cell(Row("board"), "0"));
        Assert.Equal(1, Cell(Row("verdict"), WorldStateRow.SlotKey));
        Assert.Equal(0, Cell(Row("illegalCount"), WorldStateRow.SlotKey));
        Assert.Equal(5, Cell(Row("lastLegal"), "1")); // still the seeded initial position — nothing moved yet.

        // The verdict/lastLegal half needs a SECOND observed transition, not a second real settle (the physical
        // bridge itself is already proven above): stamp piecePrevCell/pieceCell exactly as piece-snapshot then
        // piece-derive would on a genuine second settle — cellOf re-resolving onto cell 1, pre-seeded occupied in
        // previousBoard — so mover-detect's gate crosses on the very next tick with quiescent still held.
        void Set(string row, long value, string key = "0") => fixture.Server.Submit(envelope: new(SubmissionEnvelope.LocalConnectionId, 0, 1, 1, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.UpsertStateCell(WorldPrincipal.Console, row, key, value, WorldDocumentWriteKind.Set))));
        Set("piecePrevCell", 0);
        Set("pieceCell", 1);
        fixture.Step();
        fixture.Step();

        Assert.Equal(0, Cell(Row("verdict"), WorldStateRow.SlotKey));
        Assert.Equal(1, Cell(Row("illegalCount"), WorldStateRow.SlotKey));
        // The CONTROL: an illegal destination is recorded — never adopted. lastLegal still names the SEEDED
        // starting position, never a legal move having landed (none has, in this whole test).
        Assert.Equal(0, Cell(Row("lastLegal"), "0"));
        Assert.Equal(5, Cell(Row("lastLegal"), "1"));

        // The DISCRIMINATING half of the control: the SAME mechanism recognizes a legal destination and DOES adopt
        // it — proving illegal-check does not just always fire (which would make the assertions above vacuous).
        Set("board", key: "1", value: 9); // the physical write piece-derive would have made, landing on cell 1.
        // Re-sync piecePrevCell to the settled pieceCell FIRST, closing mover-detect's Edge latch (it is still
        // OPEN from the illegal move above — an unrelated cell hopping from 1 to 2 would never re-cross it).
        Set("piecePrevCell", 1);
        fixture.Step();
        fixture.Step();
        Set("pieceCell", 2); // cell 2 is empty in previousBoard — a legal destination.
        for (var tick = 0; tick < 4; tick++) {
            fixture.Step();
        }

        Assert.Equal(1, Cell(Row("verdict"), WorldStateRow.SlotKey));
        Assert.Equal(1, Cell(Row("illegalCount"), WorldStateRow.SlotKey)); // unchanged — this move was legal.
        Assert.Equal(9, Cell(Row("lastLegal"), "1")); // NOW lastLegal adopts the board this legal move left behind.
    }

    // A piece resting on cell 0, then physically lifted clear of the topology's own cell frame entirely (cellOf
    // answers -1, not merely a different cell), settles a second time off-board. The exclusion — pieceCell != -1
    // joining mover-detect's gate — never lets a piece whose own destination resolves to no cell register as its own
    // mover: nothing about verdict/illegalCount/lastLegal moves. The control (excludeOffFrameMover: false) reproduces
    // what the exclusion prevents: the disappearance registers as a move to key "-1", illegal-check's predicate reads
    // a cell previousBoard never declares (defaulting to 0, "not occupied"), verdict stays optimistically legal, and
    // apply-legal copies the now piece-less board into lastLegal — a piece vanishing off the table, ruled a legal
    // move of its own, silently erasing it from the remembered legal position.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PieceLeavingTheTopologyEntirelyNeverRegistersAsItsOwnMover(bool excludeOffFrameMover) {
        using var fixture = Fixtures.FreshServer(definition: TabletopBridgeDocument(excludeOffFrameMover: excludeOffFrameMover));
        var left = WorldPrincipal.Seat(slot: 0);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(left, left.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: 0)!;
        body.Pose(x: 0.5f, y: 1f, z: 0.5f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f); // over cell 0, on the topology
        for (var tick = 0; tick < 400; tick++) {
            fixture.Step();
        }

        WorldStateRow Row(string name) => WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, name)!;
        long Cell(WorldStateRow row, string key) => row.Cells!.Single(c => c.Key.Value == key).Value;
        Assert.Equal(9, Cell(Row("board"), "0")); // settled, on the topology — the baseline both variants share.
        var verdictBefore = Cell(Row("verdict"), WorldStateRow.SlotKey);
        var illegalCountBefore = Cell(Row("illegalCount"), WorldStateRow.SlotKey);
        var lastLegalCell0Before = Cell(Row("lastLegal"), "0");

        body.Pose(x: 10f, y: 1f, z: 10f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f); // still on the floor, off the 2x2 topology
        for (var tick = 0; tick < 400; tick++) {
            fixture.Step();
        }

        if (excludeOffFrameMover) {
            Assert.Equal(0, Cell(Row("justMoved"), WorldStateRow.SlotKey));
            Assert.Equal(verdictBefore, Cell(Row("verdict"), WorldStateRow.SlotKey));
            Assert.Equal(illegalCountBefore, Cell(Row("illegalCount"), WorldStateRow.SlotKey));
            Assert.Equal(lastLegalCell0Before, Cell(Row("lastLegal"), "0"));
        } else {
            Assert.Equal(1, Cell(Row("verdict"), WorldStateRow.SlotKey)); // ruled legal — nothing checks a destination of "no cell".
            Assert.Equal(illegalCountBefore, Cell(Row("illegalCount"), WorldStateRow.SlotKey)); // never even counted illegal.
            Assert.Equal(0, Cell(Row("lastLegal"), "0")); // the piece is erased from the remembered legal position.
        }
    }
}
