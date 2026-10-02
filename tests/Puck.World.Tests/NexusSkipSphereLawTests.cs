using System.Numerics;

using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the skip spheres of the shipped Nexus world's composed render program, built on the CPU by its
/// presenter's emitters with no device. Placement emission wraps most static shapes in a uniform
/// <see cref="SdfOp.Scale"/>, so these laws hold that a uniform scale costs no sphere: every Union shape whose chain
/// carries only rigid ops and uniform scales, and whose primitive has a local sphere at all, is bounded.</summary>
public sealed class NexusSkipSphereLawTests {
    private static readonly Lazy<SdfProgram> Nexus = new(valueFactory: Compose);

    private static SdfProgram Compose() {
        const string Path = "src/Puck.World/Assets/worlds/puck.world.json";
        var definition = AuthoredGameFixtures.Load(relativePath: Path);
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
            SourcePath: System.IO.Path.Combine(path1: AuthoredGameFixtures.Root, path2: Path)
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
        var dresser = new CapturingDresser();
        var frames = new SdfCompositionFrameSource(
            dresser: dresser,
            emitters: [scene, new WorldSdfDocumentEmitter(), new WorldFieldEmitter(client: client)]
        );

        scene.Tick(deltaSeconds: (1f / 60f));
        _ = frames.CaptureFrame(deltaSeconds: (1f / 60f), height: 1080, interpolationAlpha: 0f, width: 1920);

        return (dresser.Program ?? throw new InvalidOperationException(message: "The frame source dressed no program."));
    }
    // Whether a primitive has a local sphere at all, asked of a one-shape program. A primitive that needs a side table
    // the probe cannot supply (a sweep's curve, a convex profile, a path) is refused and left out of the law.
    private static bool? HasLocalSphere(SdfInstruction shape) {
        try {
            var probe = new SdfProgram(
                instructions: [
                    new SdfInstruction(Blend: ((uint)SdfBlendOp.Union), Data0: Vector4.Zero, Data1: Vector4.Zero, Material: 0u, Op: SdfOp.ResetPoint, Shape: 0u),
                    (shape with { Blend = ((uint)SdfBlendOp.Union), Material = 0u }),
                ],
                materials: [new SdfMaterial(Albedo: Vector3.One)]
            );

            return (probe.ShapeSkipSphere(instruction: 1).Mode != SdfProgram.BoundModeNone);
        } catch (ArgumentException) {
            return null;
        }
    }
    // Whether the chain from the shape's segment start reaches it through ops a sphere survives: rigid moves, uniform
    // positive scales, scope boundaries, and one dynamic transform with no rotation or scale before it.
    private static bool ChainKeepsSphere(SdfProgram program, int shape) {
        var start = 0;

        for (var index = shape; (index >= 0); index--) {
            if (program.Instructions[index].Op == SdfOp.ResetPoint) {
                start = index;
                break;
            }
        }
        foreach (var instance in program.Instances) {
            foreach (var boundary in ((int[])[instance.First, instance.End])) {
                if ((boundary <= shape) && (boundary > start)) {
                    start = boundary;
                }
            }
        }
        var rotated = false;
        var scaled = false;
        var dynamic = false;

        for (var index = start; (index < shape); index++) {
            var instruction = program.Instructions[index];

            switch (instruction.Op) {
                case SdfOp.ResetPoint:
                case SdfOp.Translate:
                case SdfOp.PushField:
                case SdfOp.PopField:
                case SdfOp.ShapeBlend:
                    break;
                case SdfOp.Rotate:
                    rotated = true;
                    break;
                case SdfOp.Scale: {
                        var data0 = instruction.Data0;

                        if (!((data0.X > 0f) && (data0.X == data0.Y) && (data0.Y == data0.Z) && (data0.Z == data0.W))) {
                            return false;
                        }
                        scaled |= (data0.X != 1f);
                        break;
                    }
                case SdfOp.TransformDynamic:
                    if (dynamic || rotated || scaled) {
                        return false;
                    }
                    dynamic = true;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    [Fact]
    public void EveryUnionShapeBehindRigidOpsAndUniformScalesIsBounded() {
        var program = Nexus.Value;
        var shapes = 0;
        var bounded = 0;
        var scaledBounded = 0;

        var (noLocalSphere, notUnion, brokenChain, unprobed) = (0, 0, 0, 0);
        var mismatches = new List<string>();

        for (var index = 0; (index < program.Instructions.Count); index++) {
            var instruction = program.Instructions[index];

            if (instruction.Op != SdfOp.ShapeBlend) {
                continue;
            }
            shapes++;
            var isBounded = (program.ShapeSkipSphere(instruction: index).Mode != SdfProgram.BoundModeNone);

            bounded += (isBounded ? 1 : 0);
            if (HasLocalSphere(shape: instruction) is not bool local) {
                unprobed++;
                continue;
            }
            var union = (instruction.Blend == ((uint)SdfBlendOp.Union));
            var chain = ChainKeepsSphere(program: program, shape: index);
            var expected = (local && union && chain);

            noLocalSphere += (local ? 0 : 1);
            notUnion += ((local && !union) ? 1 : 0);
            brokenChain += ((local && union && !chain) ? 1 : 0);

            if (expected != isBounded) {
                mismatches.Add(item: $"shape {index} ({((SdfShapeType)(instruction.Shape & SdfProgram.ShapeTypeMask))}): bounded {isBounded}, expected {expected}");
            }
            if (isBounded && HasScaleInChain(program: program, shape: index)) {
                scaledBounded++;
            }
        }
        var segments = program.SkipSegmentCount;
        var boundedSegments = Enumerable.Range(count: segments, start: 0).Count(predicate: segment => (program.SegmentSkipSphere(segment: segment).Mode != SdfProgram.BoundModeNone));

        TestContext.Current.TestOutputHelper?.WriteLine(message: $"nexus: {bounded} of {shapes} shapes bounded ({scaledBounded} behind a scale), {boundedSegments} of {segments} segments bounded; unbounded: {noLocalSphere} without a local sphere, {notUnion} non-Union, {brokenChain} behind another op, {unprobed} unprobed");
        Assert.Empty(collection: mismatches);
        Assert.True(condition: (scaledBounded > 0), userMessage: "No Nexus shape behind a Scale is bounded.");
    }

    private static bool HasScaleInChain(SdfProgram program, int shape) {
        for (var index = (shape - 1); (index >= 0); index--) {
            switch (program.Instructions[index].Op) {
                case SdfOp.ResetPoint:
                    return false;
                case SdfOp.Scale:
                    return true;
            }
        }

        return false;
    }

    private sealed class CapturingDresser : ISdfFrameDresser {
        public SdfProgram? Program { get; private set; }

        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            Program = program;

            return new SdfFrame(Program: program, ProgramChanged: false, Time: 0f, Views: []) {
                DynamicTransforms = transforms,
                MeshDraws = meshDraws,
                MeshDrawsRevision = meshDrawsRevision,
                MovedTransforms = moved,
            };
        }
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
