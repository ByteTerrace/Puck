using System.Numerics;
using System.Text;
using Puck.Assets.Documents;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a prototype's inline mesh round-trips through the document, every malformed mesh and every placement
/// that would carry one where no mesh is drawn is refused by name, and each static placement instance reaches the
/// frame's mesh draws as the prototype's engine-frame triangles under the placement's scale, yaw and position.
/// <para>Each refusal is paired with a control that differs in one authored field.</para>
/// </summary>
public sealed class PrototypeMeshLawTests {
    private const string PlacementId = "slab-at-gate";
    private const string PrototypeId = "slab";

    // A unit quad in the author frame's XY plane, facing +Z, on the palette's second (blue) entry.
    private static WorldPrototypeMesh Quad() => new(
        Indices: [0U, 1U, 2U, 0U, 2U, 3U],
        Material: 1,
        Vertices: [
            new Vector3(x: 0f, y: 0f, z: 0f),
            new Vector3(x: 1f, y: 0f, z: 0f),
            new Vector3(x: 1f, y: 1f, z: 0f),
            new Vector3(x: 0f, y: 1f, z: 0f),
        ]
    );
    private static WorldPrototype Slab(WorldPrototypeMesh? mesh) => (CreationFixtures.Prototype(document: CreationFixtures.Document(
        name: PrototypeId,
        palette: CreationFixtures.GreyAndBlue,
        shapes: [CreationFixtures.UnitSphereShape]
    )) with { Mesh = mesh });
    private static WorldPlacement Placement(WorldPlacementInhabit? inhabit = null) => new(
        Id: PlacementId,
        Inhabit: inhabit,
        Position: new DocumentVector3(value: new Vector3(
            x: 2f,
            y: 0f,
            z: -3f
        )),
        PrototypeId: PrototypeId,
        Scale: 2f,
        YawDegrees: 90f
    );
    // An inhabited placement's body takes one population slot past the local seats, as the other inhabit laws grant it.
    private static WorldDefinition With(WorldPrototypeMesh? mesh, WorldPlacementInhabit? inhabit = null) {
        var document = Fixtures.BuildDocument();

        return (document with {
            CreationsRaw = [Slab(mesh: mesh)],
            PlacementRowsRaw = [Placement(inhabit: inhabit)],
            PopulationRaw = ((inhabit is null)
                ? document.PopulationRaw
                : (document.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1) })),
        });
    }
    private static List<SdfMeshDraw> Draws(WorldDefinition definition) {
        var draws = new List<SdfMeshDraw>();

        WorldPlacementStamper.EmitStatic(
            builder: new SdfProgramBuilder(),
            creations: definition.Creations,
            definition: definition,
            meshDraws: draws,
            placements: definition.Placements
        );

        return draws;
    }

    /// <summary>The mesh serializes as <c>mesh</c> with its vertices, indices and material, and parses back equal; a
    /// prototype without one writes no member.</summary>
    [Fact]
    public void TheDocumentRoundTripsItsMesh() {
        var definition = With(mesh: Quad());
        var bytes = WorldDefinitionSerialization.Serialize(definition: definition);
        var reparsed = WorldDefinitionSerialization.Deserialize(utf8Json: bytes).Creations.Single().Mesh;

        Assert.Contains(expectedSubstring: "\"mesh\"", actualString: Encoding.UTF8.GetString(bytes: bytes));
        Assert.NotNull(@object: reparsed);
        Assert.Equal(expected: Quad().Vertices, actual: reparsed.Vertices);
        Assert.Equal(expected: Quad().Indices, actual: reparsed.Indices);
        Assert.Equal(expected: 1, actual: reparsed.Material);
        Assert.DoesNotContain(expectedSubstring: "\"mesh\"", actualString: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: With(mesh: null))));
    }
    /// <summary>DENIAL: every malformed mesh, each by name. CONTROL: the well-formed quad validates.</summary>
    [Fact]
    public void AMalformedMeshIsRefusedByName() {
        var quad = Quad();

        Laws.Validates(definition: With(mesh: quad), locally: true);
        Laws.Refuses(definition: With(mesh: (quad with { Indices = [], Vertices = [] })), locally: true, needle: "prototypes[0].mesh is empty");
        Laws.Refuses(definition: With(mesh: (quad with { Indices = [0U, 1U, 2U, 0U] })), locally: true, needle: "prototypes[0].mesh.indices holds 4 entries, not a whole number of triangles");
        Laws.Refuses(definition: With(mesh: (quad with { Indices = [0U, 1U, 4U] })), locally: true, needle: "prototypes[0].mesh.indices[2] names vertex 4, past the mesh's 4 vertices");
        Laws.Refuses(definition: With(mesh: (quad with { Vertices = [quad.Vertices[0], quad.Vertices[1], new Vector3(x: float.NaN, y: 1f, z: 0f), quad.Vertices[3]] })), locally: true, needle: "prototypes[0].mesh.vertices[2] is not finite");
        Laws.Refuses(definition: With(mesh: (quad with { Indices = [0U, 1U, 1U] })), locally: true, needle: "prototypes[0].mesh triangle 0 is degenerate: it names vertex 1 twice");
        Laws.Refuses(definition: With(mesh: (quad with { Vertices = [quad.Vertices[0], quad.Vertices[1], new Vector3(x: 2f, y: 0f, z: 0f), quad.Vertices[3]], Indices = [0U, 1U, 2U] })), locally: true, needle: "prototypes[0].mesh triangle 0 is degenerate: its vertices 0, 1 and 2 enclose no area");
        Laws.Refuses(definition: With(mesh: (quad with { Material = 2 })), locally: true, needle: "prototypes[0].mesh.material 2 names no entry of the creation's 2-entry palette");
    }
    /// <summary>DENIAL: an inhabited placement of a mesh prototype, which rides the stamp pool that draws no mesh.
    /// CONTROL: the same inhabited placement of the prototype without a mesh validates.</summary>
    [Fact]
    public void AnInhabitedPlacementRefusesAMeshPrototype() {
        var inhabit = new WorldPlacementInhabit(
            Distribution: WorldDistribution.Default,
            Kit: Fixtures.SeatKitName,
            Look: null,
            Source: IntentSource.Idle
        );

        Laws.Refuses(definition: With(inhabit: inhabit, mesh: Quad()), locally: true, needle: "placements.rows[0] inhabits prototype 'slab', whose mesh only a static placement draws");
        Laws.Validates(definition: With(inhabit: inhabit, mesh: null), locally: true);
    }
    /// <summary>A static placement reaches the mesh draws once, as the prototype's triangles turned into the engine
    /// frame (X and Z negated) under the placement's scale 2, yaw 90° and position (2, 0, -3), derived here from the
    /// axes rather than from the stamper's matrix; the material is the palette entry the mesh names, one past entry 0's.
    /// A prototype without a mesh draws nothing.</summary>
    [Fact]
    public void AStaticPlacementDrawsThePrototypeMesh() {
        var draws = Draws(definition: With(mesh: Quad()));
        var draw = Assert.Single(collection: draws);

        Assert.Equal(expected: 2, actual: draw.Mesh.TriangleCount);
        Assert.Equal(expected: new Vector3(x: -1f, y: 0f, z: 0f), actual: draw.Mesh.Positions.Span[1]);

        // Author (1, 0, 0) is engine (-1, 0, 0); scaled by 2 it is (-2, 0, 0); a +90° yaw about +Y takes -X to +Z,
        // so (0, 0, 2); placed at (2, 0, -3) it lands at (2, 0, -1). Author (0, 1, 0) stays on the axis: (2, 2, -3).
        AssertNear(expected: new Vector3(x: 2f, y: 0f, z: -1f), actual: Vector3.Transform(position: draw.Mesh.Positions.Span[1], matrix: draw.ObjectToWorld));
        AssertNear(expected: new Vector3(x: 2f, y: 2f, z: -3f), actual: Vector3.Transform(position: draw.Mesh.Positions.Span[3], matrix: draw.ObjectToWorld));

        var entryZero = Assert.Single(collection: Draws(definition: With(mesh: (Quad() with { Material = 0 }))));

        Assert.Equal(expected: (entryZero.Material + 1), actual: draw.Material);
        Assert.Empty(collection: Draws(definition: With(mesh: null)));
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) => Assert.True(
        condition: (Vector3.Distance(value1: expected, value2: actual) < 1e-5f),
        userMessage: $"expected {expected}, drew {actual}"
    );
}
