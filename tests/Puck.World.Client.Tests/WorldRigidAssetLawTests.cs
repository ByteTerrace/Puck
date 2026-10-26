using System.Numerics;
using Puck.Physics.Motion;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class WorldRigidAssetLawTests {
    [Fact]
    public void SixHundredRigidBodiesShareOneBakeAndKeepTheirIdentityWithoutRebuildingTheProgram() {
        var emitter = new RigidEmitter();
        var source = new SdfCompositionFrameSource([emitter], new Dresser());
        var first = source.CaptureFrame(1280, 720, 0, 0);

        Assert.Single(first.Program.Instances);
        var materials = first.Program.MaterialCount;

        emitter.Count = 600;
        var crowd = source.CaptureFrame(1280, 720, (1f / 60f), 0);

        Assert.Same(first.Program, crowd.Program);
        Assert.Equal(1, source.ProgramBuilds);
        Assert.Equal(materials, crowd.Program.MaterialCount);
        Assert.Equal(601, crowd.DynamicTransforms.Count); // one local template, one root per actual copy
        Assert.Equal(600, crowd.MeshInstances.Count);

        // Supply the one completed local bake. The frame path must expand it into every host identity.
        var group = Assert.Single(SdfMeshBakeGroup.Plan(crowd.Program, crowd.DynamicTransforms, 40,
            static (slot, dynamic) => new SdfMeshBakeGroup(slot, dynamic)));
        var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 });
        var library = new SdfMeshLibrary();

        library.Store(group.Sealed, mesh, group.MaterialOrder);
        var bake = new SdfMeshPathBake { Library = library };
        var drawn = bake.Dress(crowd);

        Assert.False(bake.Pending);
        Assert.Equal(600, drawn.MeshDraws.Count);
        for (var index = 0; (index < drawn.MeshDraws.Count); index++) {
            Assert.Same(mesh, drawn.MeshDraws[index].Mesh);
            var copy = Assert.IsType<SdfMeshInstance>(drawn.MeshDraws[index].Identity);

            Assert.Equal(index, Assert.IsType<WorldPickTarget>(copy.Identity).BodyIndex);
            Assert.Equal((index + 1), drawn.MeshDraws[index].FrameSlot);
            Assert.Equal(index, drawn.MeshDraws[index].ObjectToWorld.M41);
        }

        emitter.Count = 1;
        var departed = source.CaptureFrame(1280, 720, (1f / 60f), 0);

        Assert.Same(first.Program, departed.Program);
        Assert.Single(bake.Dress(departed).MeshDraws);
        Assert.Equal(1, source.ProgramBuilds);
        emitter.Count = 0;
        var empty = source.CaptureFrame(1280, 720, (1f / 60f), 0);

        Assert.Same(first.Program, empty.Program);
        Assert.Empty(bake.Dress(empty).MeshDraws);
    }

    private sealed class RigidEmitter : ISdfSceneEmitter {
        private readonly WorldRigidAssets m_assets = new();

        private readonly WorldPrototype m_creation;

        private readonly StampSource m_source = new();
        private readonly WorldPickMapBuilder m_picks = new();

        public int Count { get; set; } = 1;

        public RigidEmitter() {
            var document = CreationCanonicalizer.Canonicalize(new CreationDocument(CreationDocument.CurrentSchema,
                "stone", CreationFixtures.Grey, [CreationFixtures.UnitSphereShape], null), "stone");

            m_creation = new WorldPrototype("stone", document.Document, document.Hash);
        }

        public void PrepareFrame() => m_assets.Reconcile([m_creation],
            Enumerable.Range(0, Count).Select(index => new WorldCreationInstances.BodyStamp(index, m_creation, 1, WorldLook.Implicit)).ToArray(),
            [WorldLook.Implicit with { Source = new WorldLookSource.Creation(m_creation.Id) }], []);

        public int DynamicSlotCount => m_assets.SlotCount;
        public IReadOnlyList<int> MeshAssetSlots => m_assets.Slots;
        public IReadOnlyList<SdfMeshInstance> MeshInstances => m_assets.Copies;
        public ISdfPickMap? PickMap => m_picks.Snapshot([]);

        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) => m_assets.Emit(builder,
            WorldBakedColors.Of(m_source.Definition), context.SlotBase, null, m_picks);
        public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context, SdfMovedTransforms moved) =>
            m_assets.Pack(slots, m_source, context.SlotBase);
    }
    private sealed class Dresser : ISdfFrameDresser {
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved,
            IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            new(Program: program, ProgramChanged: false, Time: 0, Views: []) { DynamicTransforms = transforms, MeshDraws = meshDraws, MeshDrawsRevision = meshDrawsRevision, MovedTransforms = moved };
    }
    private sealed class StampSource : IWorldStampSource {
        private readonly WorldClient m_client = ClientFixtures.Client(Fixtures.BuildGradientUpDocument(false));

        public WorldPoseCarries Carries => m_client.Carries;
        public WorldDefinition Definition => m_client.Definition;
        public WorldStateMirror StateMirror => m_client.StateMirror;
        public SdfFieldEvaluator? StaticField => null;

        public WorldEntityAddress EntityAddress(int index) => new("rigid", index, 0);
        public BodyFacts Facts(int index) => default;
        public bool IsActive(int index) => true;
        public WorldLook Look(int index) => WorldLook.Implicit;
        public Quaternion Orientation(int index) => Quaternion.Identity;
        public string? PlacementId(int index) => null;
        public int PoseEpoch(int index) => 0;
        public Vector3 Position(int index) => new(index, 0, 0);
        public bool TryInhabitantBody(string placementId, out int index) { index = -1; return false; }
    }
}
