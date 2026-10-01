using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests.FidgetStudy;

/// <summary>The composed render program of a world, as its presenter's emitters build it on the CPU with no device:
/// the scene, the loaded SDF documents and the field emitter, composed through <see cref="SdfCompositionFrameSource"/>
/// and captured once. No server link delivers a snapshot, so no body is active; static placements, screens and text
/// are all present. Adjacency bands are left out (they need neighbour worlds).</summary>
/// <param name="Name">The world's short name.</param>
/// <param name="Program">The composed program.</param>
/// <param name="Transforms">The frame's dynamic transforms.</param>
public sealed record SdfStudyScene(string Name, SdfProgram Program, DynamicTransform[] Transforms) {
    /// <summary>The worlds the study measures: the shipped Nexus, its standard and courtyard worlds, the parity
    /// world and the counters workload, forward-slashed relative to the repository root.</summary>
    public static IReadOnlyList<(string Name, string Path)> Worlds { get; } = [
        ("counters", "tests/Puck.Counters/counters.world.json"),
        ("parity", "tests/Puck.Parity/parity.world.json"),
        ("nexus", "src/Puck.World/Assets/worlds/puck.world.json"),
        ("standard", "src/Puck.World/Assets/worlds/standard.world.json"),
        ("courtyard", "src/Puck.World/Assets/worlds/moth-courtyard.puck"),
    ];

    /// <summary>Loads and composes one world.</summary>
    /// <param name="name">The short name.</param>
    /// <param name="relativePath">The document path relative to the repository root.</param>
    /// <returns>The composed scene.</returns>
    public static SdfStudyScene Load(string name, string relativePath) {
        var definition = AuthoredGameFixtures.Load(relativePath: relativePath);
        var routes = new WorldSeatAuthorityRouter();
        var client = new WorldClient(
            composition: new WorldCompositionState(),
            definition: definition,
            roster: new PlayerRoster(
                definition: definition,
                link: new SilentLink(definition: definition),
                seatBindings: new WorldSeatBindings(definition: definition)
            ),
            seatRouter: routes
        );
        var source = new WorldDefinitionSource(
            Definition: definition,
            SourcePath: Path.Combine(path1: AuthoredGameFixtures.Root, path2: relativePath)
        );
        var text = new WorldTextCatalog(source: source);

        text.Reconcile(definition: definition);

        var scene = new WorldSceneEmitter(
            anchor: new WorldPerceptionAnchor(),
            animator: new WorldStampPool(),
            audio: new SilentCues(),
            client: client,
            continuum: new WorldContinuum(client, routes, new NoNeighbours()),
            settings: new WorldRenderSettings(defaults: definition.Render),
            text: text
        );
        var dresser = new CapturingDresser();
        var frames = new SdfCompositionFrameSource(
            dresser: dresser,
            emitters: [scene, new WorldSdfDocumentEmitter(), new WorldFieldEmitter(client: client)]
        );

        scene.Tick(deltaSeconds: (1f / 60f));
        _ = frames.CaptureFrame(deltaSeconds: (1f / 60f), height: 1080, interpolationAlpha: 0f, width: 1920);

        return new SdfStudyScene(Name: name, Program: dresser.Program!, Transforms: dresser.Transforms);
    }

    private sealed class CapturingDresser : ISdfFrameDresser {
        public SdfProgram? Program { get; private set; }
        public DynamicTransform[] Transforms { get; private set; } = [];

        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            Program = program;
            Transforms = [.. transforms];

            return new SdfFrame(Program: program, ProgramChanged: false, Time: 0f, Views: []) {
                DynamicTransforms = transforms,
                MeshDraws = meshDraws,
                MeshDrawsRevision = meshDrawsRevision,
                MovedTransforms = moved,
            };
        }
    }
    private sealed class SilentCues : IWorldAudioCueSink {
        public void SubmitCue(string eventToken, Vector3? site) { }
    }
    private sealed class NoNeighbours : IWorldAdjacencySource {
        public void BeginTick(ulong tick) { }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;

            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [];
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
            departedFrom = default;

            return false;
        }
    }
}
