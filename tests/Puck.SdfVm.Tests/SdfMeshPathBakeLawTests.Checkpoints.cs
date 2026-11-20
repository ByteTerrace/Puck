using System.Numerics;

using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfMeshPathBakeLawTests {
    [Fact]
    public void StoredStagesResumeWithoutChangingTheLandedMesh() {
        using var scratch = new TemporaryDirectory(prefix: "puck-bake-checkpoints-");
        var store = new SdfMeshStore(directory: scratch.PathOf(name: "meshes"));
        var kernels = SdfKernelSet.Kernels.Select(kernel => new ReadOnlyMemory<byte>([((byte)kernel)])).ToArray();
        var identity = SdfMeshStore.IdentityOf(new SdfKernelSet(kernels));
        Shape[] shapes = [
            new(Center: new Vector3(0.13f, 0.29f, -0.43f), Half: new Vector3(0.37f, 0.22f, 0.31f), Rotation: Turn, Material: 1),
        ];
        var (program, materials) = Program(shapes);

        (int Dispatches, string Report) Bake() {
            var bake = new SdfMeshPathBake { Identity = identity, Library = new SdfMeshLibrary(store), PlannedTier = MeshBakeTestRun.PlannedTier };
            using var run = new MeshBakeTestRun(bake);
            var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };
            var dispatches = 0;
            TestLiveness.Until(reason: bake.Describe, step: () => {
                _ = run.Dresser.Dress(frame);
                if (bake.TryTake(batch: out var batch, field: bake.Program)) {
                    dispatches += batch.Lattices.Length;
                    bake.Complete(batch, Answer(batch, materials, shapes));
                }
                return !bake.Pending;
            });
            Assert.True(bake.TryLandedMesh(selector: -1, out _), bake.Describe());
            // A stage's checkpoints and samples are written beside the bake, on the store's writer.
            Assert.True(SdfMeshStore.WaitForWrites(TimeSpan.FromSeconds(30)));
            return (dispatches, bake.Describe());
        }

        // A finished mesh's set stands under the store's directory, a stage's intermediates under its intermediates'.
        string SetOf(UInt128 stage) => (stage == identity.Value)
            ? store.SetDirectoryOf(stage)
            : Path.Combine(store.IntermediateDirectory, stage.ToString("x32", provider: null));
        void Drop(UInt128 stage) {
            foreach (var path in Directory.EnumerateFiles(SetOf(stage))) { File.Delete(path); }
        }

        var cold = Bake();
        var entry = Assert.Single(store.EntryPaths());
        var original = File.ReadAllBytes(entry);
        Assert.True(cold.Dispatches > 0);
        Assert.Equal(0, Bake().Dispatches);

        Drop(identity.Value);
        var landing = Bake();
        Assert.Equal(0, landing.Dispatches);
        Assert.Contains("simplification:1/0", landing.Report);
        Assert.Equal(original, File.ReadAllBytes(entry));

        Drop(identity.Value);
        Drop(identity.Simplification);
        var simplify = Bake();
        Assert.Equal(0, simplify.Dispatches);
        Assert.Contains("contouring:1/0", simplify.Report);
        Assert.Equal(original, File.ReadAllBytes(entry));

        Drop(identity.Value);
        Drop(identity.Simplification);
        Drop(identity.Contouring);
        var contour = Bake();
        Assert.Equal(0, contour.Dispatches);
        Assert.Contains($"sampling:{cold.Dispatches}/0", contour.Report);
        Assert.Equal(original, File.ReadAllBytes(entry));

        Drop(identity.Value);
        Drop(identity.Simplification);
        Drop(identity.Contouring);
        File.WriteAllBytes(Directory.EnumerateFiles(SetOf(identity.Sampling)).First(), [0]);
        Assert.Equal(1, Bake().Dispatches);
        Assert.Equal(original, File.ReadAllBytes(entry));
    }
}
