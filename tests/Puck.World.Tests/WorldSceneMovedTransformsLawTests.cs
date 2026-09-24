using System.Numerics;

using Puck.Abstractions.Counting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the SDF moved set over the real scene emitter (<see cref="WorldSceneEmitter"/>) composed by
/// <see cref="SdfCompositionFrameSource"/>: once its bodies have settled, a still frame packs no dynamic-transform
/// rows, compares no bytes and owes nothing, and a frame moving k of many bodies packs, compares and owes work
/// proportional to k — each moved body's leaf range once — whatever the population.
/// </summary>
public sealed class WorldSceneMovedTransformsLawTests {
    private const int Bodies = 16;
    private const int FirstBody = 8;
    private const float FrameSeconds = (1f / 60f);

    private static long Read(IWorkCounterSource source, WorkKind kind) {
        Assert.True(condition: source.TryRead(
            kind: kind,
            value: out var value
        ));

        return value;
    }
    private static void Deliver(WorldClient client, ulong tick, Func<int, Vector3> position) {
        var entries = new EntitySnapshot[Bodies];

        for (var body = 0; (body < Bodies); body++) {
            entries[body] = new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Continuous,
                Generation: 1,
                Index: (FirstBody + body),
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.Identity,
                Position: position(arg: body)
            );
        }

        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Authority: "a",
            Entries: entries,
            Revision: 1,
            StepTicks: 1UL,
            Tick: tick
        ));
    }
    private static Vector3 Rest(int body) => new(
        x: (body * 3f),
        y: 0f,
        z: 0f
    );
    // One frame's counted work: rows packed, bytes compared, rows owed.
    private static (long Packed, long Compared, long Owed) Frame(Scene scene) {
        var moved = scene.Frames.MovedTransforms;
        var packed = Read(kind: SdfMovedTransforms.PackedRows, source: moved);
        var compared = Read(kind: SdfMovedTransforms.ComparedBytes, source: moved);
        var owed = Read(kind: SdfMovedTransforms.OwedRows, source: moved);

        scene.Emitter.Tick(deltaSeconds: FrameSeconds);
        _ = scene.Frames.CaptureFrame(
            deltaSeconds: FrameSeconds,
            height: 64,
            interpolationAlpha: 0f,
            width: 64
        );

        return (
            (Read(kind: SdfMovedTransforms.PackedRows, source: moved) - packed),
            (Read(kind: SdfMovedTransforms.ComparedBytes, source: moved) - compared),
            (Read(kind: SdfMovedTransforms.OwedRows, source: moved) - owed)
        );
    }
    // A settled scene: the first frame owes the whole table, the next repacks every body once more and finds it
    // unchanged, and from then on nothing is restless.
    private static Scene Settled() {
        var scene = new Scene();

        Deliver(
            client: scene.Client,
            position: Rest,
            tick: 1UL
        );
        _ = Frame(scene: scene);
        _ = Frame(scene: scene);

        return scene;
    }

    [Fact]
    public void AStillFramePacksNoRowsComparesNoBytesAndOwesNothing() {
        var scene = Settled();

        var (packed, compared, owed) = Frame(scene: scene);

        Assert.Equal(actual: packed, expected: 0L);
        Assert.Equal(actual: compared, expected: 0L);
        Assert.Equal(actual: owed, expected: 0L);
    }
    [InlineData(1)]
    [InlineData(4)]
    [Theory]
    public void AFrameMovingKBodiesDoesWorkProportionalToK(int k) {
        var scene = Settled();

        Deliver(
            client: scene.Client,
            position: body => ((body < k)
                ? (Rest(body: body) + new Vector3(x: 0f, y: 0f, z: 1f))
                : Rest(body: body)),
            tick: 2UL
        );

        var (packed, compared, owed) = Frame(scene: scene);
        var rows = (((long)k) * WorldRigCatalog.TransformSlotsPerBody);

        Assert.Equal(actual: packed, expected: rows);
        Assert.Equal(actual: owed, expected: rows);
        Assert.Equal(actual: compared, expected: (rows * 48L));

        // The moved bodies repack once more to find they have come to rest, owing nothing; then the scene is still.
        var (settling, _, settlingOwed) = Frame(scene: scene);

        Assert.Equal(actual: settling, expected: rows);
        Assert.Equal(actual: settlingOwed, expected: 0L);
        Assert.Equal(expected: (0L, 0L, 0L), actual: Frame(scene: scene));
    }

    private sealed class Scene {
        public Scene() {
            var definition = Fixtures.BuildDocument();
            var routes = new WorldSeatAuthorityRouter();

            Client = new WorldClient(
                composition: new WorldCompositionState(),
                definition: definition,
                roster: new PlayerRoster(
                    definition: definition,
                    link: new SilentLink(definition: definition),
                    seatBindings: new WorldSeatBindings(definition: definition)
                ),
                seatRouter: routes
            );
            Emitter = new WorldSceneEmitter(
                anchor: new WorldPerceptionAnchor(),
                animator: new WorldStampPool(),
                audio: new SilentAudio(),
                client: Client,
                continuum: new WorldContinuum(
                    Client,
                    routes,
                    new NoNeighbours()
                ),
                settings: new WorldRenderSettings(defaults: definition.Render),
                text: new WorldTextCatalog(source: new(
                    Definition: definition,
                    SourcePath: "unused.world.json"
                ))
            );
            Frames = new SdfCompositionFrameSource(
                dresser: new BareDresser(),
                emitters: [Emitter]
            );
        }

        public WorldClient Client { get; }
        public WorldSceneEmitter Emitter { get; }
        public SdfCompositionFrameSource Frames { get; }
    }
    // Dresses nothing: the law reads the table's moved set, not a rendered view.
    private sealed class BareDresser : ISdfFrameDresser {
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, uint width, uint height, float deltaSeconds, float interpolationAlpha) => new(
            Program: program,
            ProgramChanged: false,
            Time: 0f,
            Views: [],
            WarpAmount: 0f
        ) {
            DynamicTransforms = transforms,
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
