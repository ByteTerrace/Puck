using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Hosting.Jobs;
using Xunit;
using static Puck.SdfVm.Tests.SdfWorldTablesUploadLawTests;

namespace Puck.SdfVm.Tests;

[Collection(ProcessWorkCollection.Name)]
public sealed class SdfMeshArrivalLawTests(ITestOutputHelper output) {
    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "PublishMeshIrradiance")]
    private static extern void PublishIrradiance(SdfWorldTables tables, IGpuBuffer source, int wordCount, List<int> slots);
    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "ServiceMemoryTrim")]
    private static extern void ServiceTrim(SdfWorldTables tables);

    [Fact]
    public void RetiredFeedbackRegionStaysHeldUntilItsPendingIrradianceCopyIsSubmitted() {
        using var rig = new Rig(1);
        var services = rig.Gpu.Services;
        using var feedback = new GpuRegion(GpuResidencyPolicy.InPlace, GpuHostVisibleMemory.Host,
            (SdfMeshIrradianceProbe.ProbeWords * 4L), 1, services.BufferFactory, services.Bindings,
            services.Recorder, null, default);

        PublishIrradiance(rig.Engine, feedback.Buffer(slot: 0), SdfMeshIrradianceProbe.ProbeWords, [0]);
        rig.Engine.RetireBackgroundResource(replaced: feedback);
        ServiceTrim(tables: rig.Engine);
        ServiceTrim(tables: rig.Engine); // A hidden residency may service retirements repeatedly before its next upload.
        Assert.NotEqual(0, feedback.Buffer(slot: 0).BufferHandle);
        rig.Engine.SubmitUpload();
        ServiceTrim(tables: rig.Engine);
        Assert.Throws<ObjectDisposedException>(testCode: () => feedback.Buffer(slot: 0));
    }
    [Fact]
    public void ForgettingOnlyATailRelandsStandingMeshesWhenTheDestinationShrinks() {
        using var rig = new Rig(1);
        var small = HostDraw(identity: "standing", x: 1);
        var tail = small with { Mesh = new SdfMesh(new Vector3[(1 << 21)], new uint[] { 0, 1, 2 }) };
        var frame = Frame(program: rig.LiveProgram, time: 0, transforms: Transforms(slots: 1)) with { MeshDraws = new[] { small } };

        LandRootCut(frame: frame, rig: rig);
        frame = frame with { MeshDraws = new[] { small, tail }, MeshDrawsRevision = 1 };
        rig.Engine.Pack(frame: frame);
        rig.Engine.SubmitUpload();
        var held = rig.Engine.MeshRegionBytes;
        var relocations = rig.Engine.DescribeLanding().Split(' ').Single(predicate: value => value.StartsWith(comparisonType: StringComparison.Ordinal, value: "relocations="));

        frame = frame with { MeshDraws = new[] { small }, MeshDrawsRevision = 2 };
        rig.Engine.Pack(frame: frame);
        rig.Engine.SubmitUpload();
        MemoryAccount.Shared.TrimCaches();
        rig.Engine.Pack(frame: frame);
        rig.Engine.SubmitUpload();
        LandRootCut(frame: frame, rig: rig);
        Assert.True(condition: (rig.Engine.MeshRegionBytes < held));
        Assert.True(condition: rig.Engine.MeshStands(mesh: small.Mesh));
        Assert.False(condition: rig.Engine.DrawsAwaitMeshes);
        Assert.Contains(relocations, rig.Engine.DescribeLanding());
    }
    [Fact]
    public void LandingCompletionAndStandingRequireTheRecordedPageGeneration() {
        var pages = new GpuBufferPages((16L << 20), GpuBufferPages.MinimumPageBytes);
        using var rig = new Rig(1, meshPages: pages);
        var draw = HostDraw(identity: "page-generation", x: 1);
        var frame = Frame(program: rig.LiveProgram, time: 0, transforms: Transforms(slots: 1)) with { MeshDraws = new[] { draw } };

        rig.Engine.Pack(frame: frame);
        rig.Engine.SubmitUpload();
        rig.Engine.GpuWork.Pump(tables: rig.Engine);
        Assert.True(condition: (rig.Engine.GpuWork.InFlight > 0));
        Assert.False(condition: rig.Engine.MeshStands(mesh: draw.Mesh));
        pages.SetWritable(count: 4096, firstPage: 0, writable: false);
        pages.SetWritable(count: 4096, firstPage: 0, writable: true);
        rig.Engine.GpuWork.Pump(tables: rig.Engine);
        Assert.False(condition: rig.Engine.MeshStands(mesh: draw.Mesh));
        Assert.Contains("landing-stale=1", rig.Engine.DescribeLanding());
        LandRootCut(frame: frame, rig: rig);
        Assert.True(condition: rig.Engine.MeshStands(mesh: draw.Mesh));
        pages.SetWritable(count: 4096, firstPage: 0, writable: false);
        pages.SetWritable(count: 4096, firstPage: 0, writable: true);
        Assert.False(condition: rig.Engine.MeshStands(mesh: draw.Mesh));
        LandRootCut(frame: frame, rig: rig);
        Assert.True(condition: rig.Engine.MeshStands(mesh: draw.Mesh));
    }
    [Fact]
    public void ForcedAdmissionAndTrimShrinkAnOversizedRegionThroughTheOrdinaryRelocation() {
        SdfRowChecks.Enabled = true;
        try {
            using var rig = new Rig(slots: 1);
            var mesh = new SdfMesh(positions: new Vector3[(1 << 20)], indices: new uint[] { 0, 1, 2 });
            var draw = new SdfMeshDraw(mesh, Matrix4x4.Identity, 0, "large");
            var frame = Frame(program: rig.LiveProgram, time: 0, transforms: Transforms(slots: 1)) with { MeshDraws = new[] { draw } };

            RenderRootCut(frame: frame, rig: rig);
            var held = rig.Engine.MeshRegionBytes;
            var shrinks = SdfRowChecks.ProcessWork.Read(kind: SdfRowChecks.Shrinks);

            rig.Engine.ForceMeshLimit(bytes: 0);
            MemoryAccount.Shared.TrimCaches();
            RenderRootCut(frame: frame, rig: rig);
            Assert.True(condition: (rig.Engine.MeshRegionBytes < held));
            Assert.Equal((shrinks + 1), SdfRowChecks.ProcessWork.Read(kind: SdfRowChecks.Shrinks));
            Assert.True(condition: rig.Engine.DrawsAwaitMeshes);
            Assert.Null(@object: rig.Engine.RowCheckFailure);
            output.WriteLine(message: $"region-before={held} region-after={rig.Engine.MeshRegionBytes} shrinks=1");
        } finally { SdfRowChecks.Enabled = false; }
    }
}
