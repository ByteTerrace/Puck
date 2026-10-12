using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Testing;

/// <summary>Captures the presenter's composed program and posed transform table without opening a device.</summary>
internal static class ComposedSdfWorldFixture {
    public static SdfFrame Capture(string relativePath) => Capture(
        definition: AuthoredGameFixtures.Load(relativePath: relativePath), relativePath: relativePath);
    public static SdfFrame Capture(WorldDefinition definition, string relativePath) {
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
        var text = new WorldTextCatalog(source: new(
            Definition: definition,
            SourcePath: Path.Combine(path1: AuthoredGameFixtures.Root, path2: relativePath)
        ));

        text.Reconcile(definition: definition);
        var scene = new WorldSceneEmitter(
            anchor: new WorldPerceptionAnchor(),
            animator: new WorldStampPool(),
            audio: new SilentAudio(),
            client: client,
            continuum: new WorldContinuum(client, routes, new NoNeighbours()),
            settings: new WorldRenderSettings(defaults: definition.Render),
            text: text
        );
        var frames = new SdfCompositionFrameSource(
            dresser: new CapturingDresser(),
            emitters: [scene, new WorldSdfDocumentEmitter(), new WorldFieldEmitter(client: client)]
        );

        scene.Tick(deltaSeconds: (1f / 60f));
        return frames.CaptureFrame(deltaSeconds: (1f / 60f), height: 1080, interpolationAlpha: 0f, width: 1920);
    }
    public static Vector4[] PackTransforms(SdfFrame frame) {
        var result = new Vector4[(Math.Max(val1: 1, val2: frame.DynamicTransforms.Count) * 3)];

        if (frame.DynamicTransforms.Count == 0) {
            result[1] = new Vector4(w: 1, x: 0, y: 0, z: 0);
        }
        for (var index = 0; (index < frame.DynamicTransforms.Count); index++) {
            SdfWorldTables.PackDynamicTransform(floats: MemoryMarshal.Cast<Vector4, float>(span: result.AsSpan(length: 3, start: (index * 3))),
                transform: frame.DynamicTransforms[index]);
        }
        return result;
    }

    private sealed class CapturingDresser : ISdfFrameDresser {
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws,
            long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            new(Program: program, ProgramChanged: false, Time: 0f, Views: []) {
                DynamicTransforms = transforms,
                MeshDraws = meshDraws,
                MeshDrawsRevision = meshDrawsRevision,
                MovedTransforms = moved,
            };
    }
    private sealed class SilentAudio : IWorldAudioCueSink {
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
