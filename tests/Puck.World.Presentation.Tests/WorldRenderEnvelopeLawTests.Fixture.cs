using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World.Presentation.Tests;

public sealed partial class WorldRenderEnvelopeLawTests {
    /// <summary>The panelled creation: one box plate whose inset face carries a second material.</summary>
    internal static readonly WorldPrototype Plaque = new(
        "plaque",
        new(
            "puck.creation.v1",
            "plaque",
            [new(
                    "#AA7755",
                    null,
                    null,
                    null
                ), new(
                    "#EEEEDD",
                    null,
                    null,
                    null
                )],
            [new(
                    0,
                    "plate",
                    SdfSolidPrimitive.Box,
                    Vector3.Zero,
                    Quaternion.Identity,
                    new Vector3(
                        x: 0.4f,
                        y: 0.3f,
                        z: 0.2f
                    ),
                    0,
                    null,
                    0,
                    null,
                    Panel: new(
                        Inset: 0.05f,
                        Depth: 0.1f,
                        Material: 1
                    )
                )],
            null
        )
    );
    /// <summary>The trimmed creation: a box plate carrying one trim against a sphere cutter.</summary>
    internal static readonly WorldPrototype Seam = new(
        "seam",
        new(
            "puck.creation.v1",
            "seam",
            [new(
                    "#AA7755",
                    null,
                    null,
                    null
                ), new(
                    "#EEEEDD",
                    null,
                    null,
                    null
                )],
            [new(
                    0,
                    "cutter",
                    SdfSolidPrimitive.Sphere,
                    Vector3.UnitZ,
                    Quaternion.Identity,
                    new Vector3(value: 0.3f),
                    0,
                    null,
                    0,
                    null
                ),
             new(
                    1,
                    "plate",
                    SdfSolidPrimitive.Box,
                    Vector3.Zero,
                    Quaternion.Identity,
                    Vector3.One,
                    0,
                    null,
                    0,
                    null,
                    Trims: [new(
                            Shape: "cutter",
                            Width: 0.4f,
                            Material: 1
                        )]
                )],
            null
        )
    );
    /// <summary>The scope-free creation: two unblended boxes, so each shape is its own instance.</summary>
    internal static readonly WorldPrototype Store = new(
        "store",
        new(
            "puck.creation.v1",
            "store",
            [new(
                    "#AA7755",
                    null,
                    null,
                    null
                )],
            [new(
                    0,
                    "wall",
                    SdfSolidPrimitive.Box,
                    Vector3.Zero,
                    Quaternion.Identity,
                    Vector3.One,
                    0,
                    null,
                    0,
                    null
                ),
             new(
                    1,
                    "roof",
                    SdfSolidPrimitive.Box,
                    (Vector3.UnitY * 2),
                    Quaternion.Identity,
                    Vector3.One,
                    0,
                    null,
                    0,
                    null
                )],
            null
        )
    );

    // A document declaring the three prototypes, with the placements given and an authored instance headroom.
    private static WorldDefinition Document(int headroom, IReadOnlyList<WorldPlacement> placements) => Fixtures.BuildDocument() with {
        CreationsRaw = [Plaque, Seam, Store],
        PlacementsRaw = new(
            Rows: placements,
            Policy: new(
                AuthoringHeadroomInstances: headroom,
                AuthoringHeadroomScreens: 0,
                CandidateCap: 4,
                CandidateRadius: 10,
                DerivedFaceScreens: 2,
                MaxPlacementScale: 1,
                MinPlacementScale: 1,
                PreviewDeadlineFrames: 8
            )
        ),
    };
    // One placement of a prototype at the origin.
    private static WorldPlacement Placed(string id, string prototype) => new(
        id,
        prototype,
        Vector3.Zero,
        0,
        1
    );

    // The scene emitter WorldFramePresenter composes, over a client that runs no server.
    private static (WorldClient Client, WorldSceneEmitter Scene) SceneEmitter(WorldDefinition definition) {
        var routes = new WorldSeatAuthorityRouter();
        var client = new WorldClient(
            new PlayerRoster(
                definition: definition,
                link: new SilentLink(),
                seatBindings: new WorldSeatBindings(definition: definition)
            ),
            definition,
            new WorldCompositionState(),
            routes
        );

        return (client, new WorldSceneEmitter(
            client,
            new(),
            new SilentAudio(),
            new(
                client,
                routes,
                new NoNeighbours()
            ),
            new(source: new(
                Definition: definition,
                SourcePath: "unused.world.json"
            ))
        ));
    }
}
