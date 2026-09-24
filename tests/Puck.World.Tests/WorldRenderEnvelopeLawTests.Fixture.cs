using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World.Tests;

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

    /// <summary>The boot probes the headroom laws measure a candidate against, built at most once per test class.
    /// Each probe costs one worst-case scene emission and build, so every law that places into the same authoring
    /// headroom reads one shared probe instead of re-deriving it.</summary>
    public sealed class Scenes {
        private readonly Lazy<HeadroomScene> m_none = new(valueFactory: static () => new HeadroomScene(headroom: 0));
        private readonly Lazy<HeadroomScene> m_one = new(valueFactory: static () => new HeadroomScene(headroom: 1));

        /// <summary>Returns the scene whose placement policy reserves <paramref name="headroom"/> authoring
        /// placements, building it on first use.</summary>
        /// <param name="headroom">The reserved authoring placements: zero or one.</param>
        /// <returns>The shared scene.</returns>
        public HeadroomScene Headroom(int headroom) => (headroom switch {
            0 => m_none,
            1 => m_one,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(headroom)),
        }).Value;
    }
    /// <summary>One document declaring every prototype the headroom laws place (none placed at boot), its scene
    /// emitter, and that emitter's boot probe. Nothing here changes after construction: the laws only compose
    /// candidates, which is the emitter's apply-time measure and reads only what construction and the boot probe
    /// froze, so no law can observe another's candidate.</summary>
    public sealed class HeadroomScene {
        private readonly WorldSceneEmitter m_emitter;
        private readonly int m_bootInstructions;
        private readonly int m_bootMaterials;

        /// <summary>Initializes a new instance of the <see cref="HeadroomScene"/> class, emitting and building the
        /// boot probe once.</summary>
        /// <param name="headroom">The reserved authoring placements.</param>
        public HeadroomScene(int headroom) {
            var definition = Fixtures.BuildDocument() with {
                CreationsRaw = [Plaque, Seam, Store],
                PlacementsRaw = new(
                Rows: [],
                Policy: new(
                    AuthoringHeadroomPlacements: headroom,
                    AuthoringHeadroomScreens: 0,
                    CandidateCap: 4,
                    CandidateRadius: 10,
                    DerivedFaceScreens: 0,
                    MaxPlacementScale: 1,
                    MinPlacementScale: 1,
                    PreviewDeadlineFrames: 8
                )
            ),
            };

            m_emitter = SceneEmitter(definition: definition).Scene;

            var bootBuilder = new SdfProgramBuilder();

            using (bootBuilder.BeginMaterialScope()) {
                m_emitter.Emit(
                    builder: bootBuilder,
                    context: new(
                        true,
                        0,
                        Vector3.Zero,
                        Vector3.Zero,
                        0
                    )
                );
            }

            var boot = bootBuilder.Build(buildInstanceGrid: false);

            Definition = definition;
            BootWords = boot.Words.Length;
            BootInstances = boot.Instances.Count;
            m_bootInstructions = boot.InstructionCount;
            m_bootMaterials = boot.MaterialCount;
        }

        /// <summary>Gets the boot document: every headroom prototype declared, none placed.</summary>
        public WorldDefinition Definition { get; }
        /// <summary>Gets the boot probe's packed program length, in words.</summary>
        public int BootWords { get; }
        /// <summary>Gets the boot probe's declared instance count.</summary>
        public int BootInstances { get; }

        /// <summary>Returns the boot document with one placement of <paramref name="prototype"/> at the origin.</summary>
        /// <param name="name">The placement's name.</param>
        /// <param name="prototype">The prototype the placement references.</param>
        /// <returns>The candidate document.</returns>
        public WorldDefinition Candidate(string name, string prototype) => Definition with {
            PlacementsRaw = Definition.PlacementsRaw! with {
                Rows = [new(
                name,
                prototype,
                Vector3.Zero,
                0,
                1
            )],
            },
        };
        /// <summary>Composes and builds <paramref name="candidate"/> through the emitter's apply-time measure. The
        /// builder is sized from the boot probe, which changes no emitted word.</summary>
        /// <param name="candidate">The candidate document.</param>
        /// <returns>The candidate program's length in words and its declared instance count.</returns>
        public (int Words, int Instances) Measure(WorldDefinition candidate) {
            var builder = new SdfProgramBuilder(
                instanceCapacity: BootInstances,
                instructionCapacity: m_bootInstructions,
                materialCapacity: m_bootMaterials
            );

            m_emitter.ComposeCandidate(
                builder: builder,
                candidate: candidate
            );

            var measured = builder.Build(buildInstanceGrid: false);

            return (measured.Words.Length, measured.Instances.Count);
        }
    }

    // The scene emitter WorldFramePresenter composes, over a client that runs no server.
    private static (WorldClient Client, WorldSceneEmitter Scene) SceneEmitter(WorldDefinition definition) {
        var routes = new WorldSeatAuthorityRouter();
        var client = new WorldClient(
            new PlayerRoster(
                definition: definition,
                link: new SilentLink(definition: definition),
                seatBindings: new WorldSeatBindings(definition: definition)
            ),
            definition,
            new WorldCompositionState(),
            routes
        );

        return (client, new WorldSceneEmitter(
            client,
            new(defaults: definition.Render),
            new(),
            new SilentAudio(),
            new(),
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
