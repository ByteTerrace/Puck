using System.Numerics;
using Puck.Assets.Documents;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a followed session reads through the one mirror path the local presentation reads. A snapshot's field
/// cells reach the followed state mirror's field rows, as <see cref="WorldClient.DeliverSnapshot"/>'s reach the local
/// mirror's, so a followed field row reads the session's cells. A state-cell write that moves a bound color the session's
/// static build baked moves the session scene emitter's revision, so the view rebuilds, as the local scene emitter's
/// baked-color component does.
/// </summary>
public sealed class WorldSessionFollowLawTests {
    private const int Depth = 2;
    private const long One = 65536L;
    private const int Width = 4;

    private static WorldFieldsSection Fields() => new(
        Lattice: new WorldFieldLatticeDefinition(
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: Width,
            Depth: Depth
        ),
        Fields: [
            new WorldFieldRow(Name: "heat", Max: 4f),
            new WorldFieldRow(Name: "bump", Max: 1f, HeightScale: 2f, Color: "state.colors.bump"),
        ]
    );
    private static WorldStateRow Colors(string bump) => new(
        Name: CellName.Parse(candidate: "colors"),
        Kind: CellKind.Text,
        Capacity: 8,
        Domain: StateDomain.Keys.Instance,
        Cells: [new StateCell(Key: CellName.Parse(candidate: "bump"), Value: CellValue.Text(value: bump))]
    );
    // The field document, with one static orb whose palette color is bound to the colors row's bump cell.
    private static WorldDefinition Document(string bump = "#3FAF6F") {
        var lattice = Fixtures.WithLattice(
            composite: Fields(),
            definition: Fixtures.BuildDocument()
        );
        var stated = lattice.WithWorldState(rows: [.. lattice.AuthoredState, Colors(bump: bump)]);

        return (stated with {
            CreationsRaw = [CreationFixtures.Prototype(document: CreationFixtures.Document(
                name: "orb",
                palette: [new PaletteEntryDocument(Color: "state.colors.bump", Emissive: null, Specular: null, Roughness: null)],
                shapes: [CreationFixtures.UnitSphereShape]
            ))],
            PlacementRowsRaw = [new WorldPlacement(
                Id: "orb-a",
                Position: new DocumentVector3(value: Vector3.Zero),
                PrototypeId: "orb",
                Scale: 1f,
                YawDegrees: 0f
            )],
        });
    }
    private static StateBinding Whole(string row) => new(
        Key: null,
        Row: row,
        Target: false
    );
    private static WorldSessionMirror Session(WorldDefinition definition) {
        var session = new WorldSessionMirror(placeholder: definition);

        session.DeliverDefinition(definition: definition, version: default);

        return session;
    }

    [Fact]
    public void ASnapshotsFieldCellsReachAFollowedSessionsFieldRows() {
        var session = Session(definition: Document());
        var state = session.FollowState();
        // The manifest registers the height field's row whole, which its brick reads.
        var bump = state.SlotOf(
            binding: Whole(row: "bump"),
            conversion: WorldStateConversion.Row
        );

        Assert.True(condition: (bump >= 0));
        Assert.Equal(expected: new double[(Width * Depth)], actual: state.RowValues(slot: bump).ToArray());

        session.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: 1UL,
            Revision: 0,
            StepTicks: 1UL,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            FieldCells: new[] { new FieldCellDelta(Cell: 5, Field: 1, Raw: One), new FieldCellDelta(Cell: 2, Field: 1, Raw: (One / 2L)) }
        ));
        state = session.FollowState();

        Assert.Equal(expected: [0d, 0d, 0.5d, 0d, 0d, 1d, 0d, 0d], actual: state.RowValues(slot: bump).ToArray());
    }
    [Fact]
    public void AStateWriteMovingABakedColorMovesTheSessionEmittersRevision() {
        var session = Session(definition: Document(bump: "#3FAF6F"));
        // Held as the presenter holds it, so the material-scope default below is the one the composition reads.
        ISdfSceneEmitter emitter = new WorldSessionSceneEmitter(
            effectiveCameraName: null,
            mirror: session
        );

        var builder = new SdfProgramBuilder();
        var context = new SdfEmitContext(false, 0, Vector3.Zero, Vector3.Zero, 0);

        // The live build bakes the orb's bound color, as the presenter composes a session view.
        if (emitter.OwnsMaterialScope) {
            using var scope = builder.BeginMaterialScope();

            emitter.Emit(
                builder: builder,
                context: context
            );
        } else {
            emitter.Emit(
                builder: builder,
                context: context
            );
        }

        var before = new int[emitter.RevisionComponentCount];
        var steady = new int[emitter.RevisionComponentCount];
        var after = new int[emitter.RevisionComponentCount];

        emitter.WriteRevision(destination: before);
        emitter.WriteRevision(destination: steady);

        // Nothing delivered, nothing moved.
        Assert.Equal(actual: steady, expected: before);

        // A value write of the bound cell, delivered as state (not a new definition shape).
        session.DeliverState(
            definition: Document(bump: "#112233"),
            stamp: new WorldStateStamp(EngineTick: 2UL, Everything: true, MovedRows: ReadOnlyMemory<int>.Empty, Tick: 2UL),
            version: default
        );
        emitter.WriteRevision(destination: after);

        Assert.NotEqual(actual: after, expected: before);
        Assert.Equal(expected: before[0], actual: after[0]);
    }
}
