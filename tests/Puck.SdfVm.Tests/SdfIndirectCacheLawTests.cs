using System.Buffers.Binary;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfIndirectCacheLawTests {
    private static IrradianceFrameInputs Inputs => new(Cameras: [Double3.Zero],
        Bounds: [new IrradianceSphere(Center: Double3.Zero, Radius: 0.1)], WorldMin: Double3.Zero, WorldMax: new Double3(X: 0.1, Y: 0.1, Z: 0.1));

    [Fact]
    public void AnUnchangedBrickDirectoryOwesNoUploadAfterEveryRingSlotHasCaughtUp() {
        using var rig = new Rig();
        for (var frame = 0; frame < 16; frame++) { rig.Cache.Plan(Inputs); rig.Cache.Submitted(); }
        Assert.True(rig.Cache.IsComplete);
        var region = rig.Cache.Regions[0];
        Assert.Equal(5120, region.ByteCount);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(region.Contents[(256 * 16)..]));
        Assert.Equal(192, BinaryPrimitives.ReadInt32LittleEndian(region.Contents[(256 * 16 + 4)..]));
        for (var slot = 0; slot < SdfWorldTables.FrameRingSize; slot++) { region.Flush(slot); region.RecordCopy(0, slot); }
        var before = region.Contents.ToArray();
        var writes = rig.Gpu.Count("IGpuStorageBuffer.Write(offset)");
        var dispatches = rig.Gpu.Count("IGpuRecorder.Dispatch");
        Assert.True(writes > 0);

        rig.Cache.Plan(Inputs);
        Assert.False(rig.Cache.NeedsPublish);
        for (var slot = 0; slot < SdfWorldTables.FrameRingSize; slot++) { region.Flush(slot); region.RecordCopy(0, slot); }
        Assert.Equal(before, region.Contents.ToArray());
        Assert.Equal(writes, rig.Gpu.Count("IGpuStorageBuffer.Write(offset)"));
        Assert.Equal(dispatches, rig.Gpu.Count("IGpuRecorder.Dispatch"));
        rig.Cache.Reset(2);
        Assert.All(region.Contents[(256 * 16)..].ToArray(), value => Assert.Equal(byte.MaxValue, value));
    }

    [Fact]
    public void GeometryWaitsForThawAndAnAdmittedBatchThenWithdrawsEveryOldStratum() {
        using var rig = new Rig();

        for (var frame = 0; (frame < 16); frame++) { rig.Cache.Plan(inputs: Inputs); rig.Cache.Submitted(); }
        rig.Cache.BeginLighting();
        for (var batch = 0; (batch < 6); batch++) { rig.Cache.PlanLighting(); rig.Cache.SubmittedLighting(); }
        var publication = rig.Cache.PublishedStamp;

        Assert.NotEqual(actual: publication, expected: 0u);
        var revision = rig.Cache.CertificateRevision;
        var records = rig.Cache.Regions[0].Contents.ToArray();

        foreach (var brick in rig.Cache.Snapshot().Bricks) {
            var state = BinaryPrimitives.ReadInt32LittleEndian(source: records.AsSpan(start: ((brick.Slot * 16) + 12)));

            Assert.Equal(brick.Key.Level, state & SdfIndirectLayout.BrickLevelMask);
            Assert.NotEqual(actual: state & SdfIndirectLayout.BrickClassified, expected: 0);
        }
        rig.Cache.Frozen = true;
        var before = new IrradianceSphere(Center: Double3.Zero, Radius: 0.1);
        var after = new IrradianceSphere(Center: new(X: 0.1, Y: 0, Z: 0), Radius: 0.1);

        rig.Cache.MarkGeometry(current: after, previous: before);
        rig.Cache.Plan(inputs: Inputs);
        Assert.False(condition: rig.Cache.NeedsPublish);
        Assert.Equal(publication, rig.Cache.PublishedStamp);
        Assert.Equal(revision, rig.Cache.CertificateRevision);
        Assert.Equal(records, rig.Cache.Regions[0].Contents.ToArray());
        rig.Cache.Frozen = false;
        rig.Cache.Plan(inputs: Inputs);
        Assert.True(condition: (rig.Cache.PlaceCount > 0));
        Assert.Equal(0u, rig.Cache.PublishedStamp);
        Assert.True(condition: (rig.Cache.CertificateRevision > revision));
        Assert.All(rig.Cache.Regions[3].Contents.ToArray(), value => Assert.Equal(actual: value, expected: 0));
        var admitted = rig.Cache.Regions[1].Contents.ToArray();

        revision = rig.Cache.CertificateRevision;
        rig.Cache.MarkGeometry(current: before, previous: after);
        rig.Cache.Plan(inputs: Inputs);
        Assert.Equal(admitted, rig.Cache.Regions[1].Contents.ToArray());
        Assert.Equal(revision, rig.Cache.CertificateRevision);
        rig.Cache.Submitted();
        rig.Cache.Plan(inputs: Inputs);
        Assert.True(condition: (rig.Cache.PlaceCount > 0));
        Assert.True(condition: (rig.Cache.CertificateRevision > revision));
        Assert.All(rig.Cache.Regions[3].Contents.ToArray(), value => Assert.Equal(actual: value, expected: 0));
        var firstTrace = ((rig.Cache.PlaceCount + rig.Cache.ClassifyCount) * 16);

        Assert.Equal(((int)IrradianceUpdateReason.Geometry),
            BinaryPrimitives.ReadInt32LittleEndian(source: rig.Cache.Regions[1].Contents[(firstTrace + 12)..]));
    }
    [Fact]
    public void ReceiverCertificatesFollowTransportButSurviveLightingAndAllowanceOnlySubmissions() {
        using var rig = new Rig();
        var before = rig.Cache.CertificateRevision;

        rig.Cache.Plan(inputs: Inputs);
        Assert.True(condition: (rig.Cache.CertificateRevision > before));
        var planned = rig.Cache.CertificateRevision;

        rig.Cache.Plan(inputs: Inputs);
        Assert.Equal(planned, rig.Cache.CertificateRevision);
        rig.Cache.Submitted();
        for (var frame = 1; (frame < 16); frame++) { rig.Cache.Plan(inputs: Inputs); rig.Cache.Submitted(); }
        Assert.True(condition: rig.Cache.IsComplete);
        var completed = rig.Cache.CertificateRevision;

        rig.Cache.BeginLighting();
        for (var batch = 0; (batch < 6); batch++) { rig.Cache.PlanLighting(); rig.Cache.SubmittedLighting(); }
        rig.Cache.AdmitReceivers();
        rig.Cache.Submitted();
        rig.Cache.Plan(inputs: Inputs with { Cameras = [new Double3(X: 0.01, Y: 0, Z: 0)] });
        Assert.Equal(completed, rig.Cache.CertificateRevision);
        rig.Cache.Reset(epoch: 2);
        Assert.True(condition: (rig.Cache.CertificateRevision > completed));
        using var other = new Rig();

        Assert.NotEqual(rig.Cache.History.Allocation, other.Cache.History.Allocation);
    }
    [Fact]
    public void ReceiverAdmissionCommitsOnceWithoutRepeatingCompletedTransport() {
        using var rig = new Rig();

        for (var frame = 0; (frame < 16); frame++) { rig.Cache.Plan(inputs: Inputs); rig.Cache.Submitted(); }
        var rays = rig.Work.Read(kind: SdfIndirectWork.Rays);
        var sequence = rig.Cache.Frame;

        Assert.True(condition: rig.Cache.IsComplete);
        rig.Cache.AdmitReceivers();
        rig.Cache.AdmitReceivers();
        Assert.True(condition: rig.Cache.NeedsPublish);
        Assert.False(condition: rig.Cache.HasWork);
        Assert.Equal(sequence, rig.Cache.Frame);
        rig.Cache.Submitted();
        Assert.Equal((sequence + 1u), rig.Cache.Frame);
        Assert.Equal(rays, rig.Work.Read(kind: SdfIndirectWork.Rays));
        Assert.False(condition: rig.Cache.NeedsPublish);
        rig.Cache.Submitted();
        Assert.Equal((sequence + 1u), rig.Cache.Frame);
        rig.Cache.Frozen = true;
        rig.Cache.AdmitReceivers();
        Assert.False(condition: rig.Cache.NeedsPublish);
        rig.Cache.Submitted();
        Assert.Equal((sequence + 1u), rig.Cache.Frame);
    }
    [Fact]
    public void ReceiverBlocksUsePublishedLightingAndFreezeRetainsCompletedProofsWithoutNewAdmission() {
        using var rig = new Rig();

        for (var frame = 0; (frame < 16); frame++) { rig.Cache.Plan(inputs: Inputs); rig.Cache.Submitted(); }
        rig.Cache.BeginLighting();
        var block = new byte[SdfFrameBlock.SizeBytes];

        uint Word(string member) => BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: member))));
        SdfFrameBlock.WriteIndirect(block: block, cache: rig.Cache);
        Assert.Equal(0u, Word(member: Puck.Shaders.SdfWorldPackage.IndirectReadPublication));
        rig.Cache.PlanLighting();
        rig.Cache.SubmittedLighting();
        rig.Cache.PlanLighting();
        rig.Cache.SubmittedLighting();
        SdfFrameBlock.WriteIndirect(block: block, cache: rig.Cache);
        Assert.Equal(rig.Cache.PublishedStamp, Word(member: Puck.Shaders.SdfWorldPackage.IndirectReadPublication));
        Assert.NotEqual(0u, Word(member: Puck.Shaders.SdfWorldPackage.IndirectReadPublication));
        Assert.Equal(32768u, Word(member: Puck.Shaders.SdfWorldPackage.IndirectReceiverProofs));
        Assert.Equal(rig.Cache.Frame, Word(member: Puck.Shaders.SdfWorldPackage.IndirectFrame));
        rig.Cache.Frozen = true;
        SdfFrameBlock.WriteIndirect(block: block, cache: rig.Cache);
        Assert.Equal(0u, Word(member: Puck.Shaders.SdfWorldPackage.IndirectReceiverProofs));
        Assert.Equal((rig.Cache.Frame + 1u), Word(member: Puck.Shaders.SdfWorldPackage.IndirectFrame));
        Assert.Equal(rig.Cache.PublishedStamp, Word(member: Puck.Shaders.SdfWorldPackage.IndirectReadPublication));
        SdfFrameBlock.WriteIndirect(block: block, cache: null);
        Assert.Equal(0u, Word(member: Puck.Shaders.SdfWorldPackage.IndirectTier));
        Assert.Equal(0u, Word(member: Puck.Shaders.SdfWorldPackage.IndirectReadPublication));
        Assert.Equal(0u, Word(member: Puck.Shaders.SdfWorldPackage.IndirectReceiverProofs));
    }
    [Fact]
    public void APlanIsCountedOnceOnlyAfterItsSuccessfulSubmission() {
        using var rig = new Rig();

        rig.Cache.Plan(inputs: Inputs);
        var traces = rig.Cache.TraceCount;
        var words = rig.Cache.Regions[1].Contents.ToArray();
        var pending = rig.Cache.Snapshot();

        Assert.True(condition: (traces > 0));
        Assert.Equal(0, rig.Work.Read(kind: SdfIndirectWork.Rays));
        rig.Cache.Plan(inputs: Inputs);
        Assert.Equal(words, rig.Cache.Regions[1].Contents.ToArray());
        rig.Cache.Submitted();
        var submitted = rig.Cache.Snapshot();

        Assert.Equal(pending.Allocation, submitted.Allocation);
        Assert.All(pending.Bricks.SelectMany(selector: static brick => brick.SubmittedStrata), static mask => Assert.Equal(actual: mask, expected: 0u));
        Assert.Contains(collection: submitted.Bricks.SelectMany(selector: static brick => brick.SubmittedStrata), filter: static mask => (mask != 0u));
        Assert.Equal((pending.Submission + 1), submitted.Submission);
        Assert.Equal((traces * 64), rig.Work.Read(kind: SdfIndirectWork.Rays));
        rig.Cache.Submitted();
        Assert.Equal((traces * 64), rig.Work.Read(kind: SdfIndirectWork.Rays));
    }
    [Fact]
    public void LightingPublishesWholeSweepsAndFreezeRetainsOnlyTheAdmittedBatch() {
        using var rig = new Rig();
        var completed = SdfIndirectWork.Kinds.Single(predicate: kind => (kind.Name == "indirect.sweeps.completed"));

        for (var frame = 0; (frame < 16); frame++) { rig.Cache.Plan(inputs: Inputs); rig.Cache.Submitted(); }
        Assert.True(condition: rig.Cache.IsComplete);
        Assert.Equal(0, rig.Work.Read(kind: completed));
        rig.Cache.BeginLighting();
        rig.Cache.PlanLighting();
        var retained = rig.Cache.Regions[4].Contents.ToArray();

        Assert.Equal(64, rig.Cache.ShadeCount);
        Assert.Equal(12288, BinaryPrimitives.ReadInt32LittleEndian(source: retained));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(source: retained.AsSpan(start: 8)));
        rig.Cache.PlanLighting();
        Assert.Equal(retained, rig.Cache.Regions[4].Contents.ToArray());
        Assert.Equal(-1, rig.Cache.PublishedGeneration);
        rig.Cache.SubmittedLighting();
        Assert.Equal(-1, rig.Cache.PublishedGeneration);
        Assert.Equal(0u, rig.Cache.PublishedStamp);
        Assert.Equal(0, rig.Work.Read(kind: completed));
        rig.Cache.PlanLighting();
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(source: rig.Cache.Regions[4].Contents));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(source: rig.Cache.Regions[4].Contents[8..]));
        rig.Cache.Frozen = true;
        rig.Cache.SubmittedLighting();
        var published = rig.Cache.Snapshot();

        Assert.Equal(0, published.PublishedGeneration);
        Assert.True(condition: (published.PublishedStamp > 0));
        Assert.Equal(1, published.CompletedSweeps);
        rig.Cache.PlanLighting();
        Assert.Equal(0, rig.Cache.ShadeCount);
        Assert.Equal(1, rig.Work.Read(kind: completed));
        rig.Cache.Frozen = false;
        for (var batch = 0; (batch < 4); batch++) { rig.Cache.PlanLighting(); rig.Cache.SubmittedLighting(); }
        Assert.True(condition: rig.Cache.LightingComplete);
        Assert.Equal(3, rig.Work.Read(kind: completed));
        Assert.Equal(1, published.CompletedSweeps);
        Assert.True(condition: (rig.Cache.PublishedStamp > published.PublishedStamp));
        rig.Cache.PlanLighting();
        Assert.False(condition: rig.Cache.NeedsPublish);
        Assert.False(condition: rig.Cache.HasWork);
        rig.Cache.Reset(epoch: 2);
        Assert.Equal(-1, rig.Cache.PublishedGeneration);
        Assert.Equal(0u, rig.Cache.PublishedStamp);
        Assert.False(condition: rig.Cache.LightingComplete);
    }
    [Fact]
    public void ANewSolveRetainsThePublishedSweepDepthUntilItsFirstWholeSweep() {
        using var rig = new Rig();

        for (var frame = 0; (frame < 16); frame++) { rig.Cache.Plan(inputs: Inputs); rig.Cache.Submitted(); }
        rig.Cache.BeginLighting();
        for (var batch = 0; (batch < 6); batch++) { rig.Cache.PlanLighting(); rig.Cache.SubmittedLighting(); }
        var previous = rig.Cache.Snapshot();

        Assert.Equal(3, previous.CompletedSweeps);
        Assert.Equal(3, previous.PublishedSweeps);
        rig.Cache.BeginLighting();
        Assert.Equal(0, rig.Cache.CompletedSweeps);
        Assert.Equal(3, rig.Cache.Snapshot().PublishedSweeps);
        Assert.Equal(previous.PublishedStamp, rig.Cache.PublishedStamp);
        rig.Cache.PlanLighting();
        rig.Cache.SubmittedLighting();
        Assert.Equal(0, rig.Cache.CompletedSweeps);
        Assert.Equal(3, rig.Cache.PublishedSweeps);
        rig.Cache.PlanLighting();
        rig.Cache.SubmittedLighting();
        Assert.Equal(1, rig.Cache.CompletedSweeps);
        Assert.Equal(1, rig.Cache.Snapshot().PublishedSweeps);
        Assert.NotEqual(previous.PublishedStamp, rig.Cache.PublishedStamp);
        Assert.Equal(3, previous.PublishedSweeps);
        rig.Cache.Reset(epoch: 2);
        Assert.Equal(0, rig.Cache.Snapshot().PublishedSweeps);
    }
    [Fact]
    public void CompletedDemandAndAnIdlePanLeaveNoPendingWork() {
        using var rig = new Rig();

        for (var frame = 0; (frame < 16); frame++) { rig.Cache.Plan(inputs: Inputs); rig.Cache.Submitted(); }
        Assert.True(condition: rig.Cache.IsComplete);
        var rays = rig.Work.Read(kind: SdfIndirectWork.Rays);

        rig.Cache.Plan(inputs: Inputs with { Cameras = [new Double3(X: 0.01, Y: 0, Z: 0)] });
        Assert.False(condition: rig.Cache.NeedsPublish);
        Assert.False(condition: rig.Cache.HasWork);
        Assert.Equal(rays, rig.Work.Read(kind: SdfIndirectWork.Rays));
    }
    [Fact]
    public void FreezeFinishesOnlyTheAdmittedFrameAndResetWithdrawsItUntilThawed() {
        using var rig = new Rig();

        rig.Cache.Plan(inputs: Inputs);
        var pending = rig.Cache.TraceCount;

        rig.Cache.Frozen = true;
        rig.Cache.Submitted();
        var history = rig.Cache.History;

        Assert.Equal((pending * 64), rig.Work.Read(kind: SdfIndirectWork.Rays));
        rig.Cache.Plan(inputs: Inputs);
        Assert.False(condition: rig.Cache.NeedsPublish);
        Assert.False(condition: rig.Cache.HasWork);
        Assert.True(condition: rig.Cache.Snapshot().Frozen);
        rig.Cache.Reset(epoch: 7);
        Assert.Equal(history.Allocation, rig.Cache.History.Allocation);
        Assert.Equal(7u, rig.Cache.History.Epoch);
        Assert.True(condition: (rig.Cache.History.Publication > history.Publication));
        rig.Cache.Plan(inputs: Inputs);
        Assert.Empty(collection: rig.Cache.Snapshot().Bricks);
        Assert.False(condition: rig.Cache.NeedsPublish);
        rig.Cache.Frozen = false;
        rig.Cache.Plan(inputs: Inputs);
        Assert.True(condition: rig.Cache.NeedsPublish);
        Assert.Equal(pending, rig.Cache.TraceCount);
    }
    [Fact]
    public void TraceSupportOnlySeesStrataFromEarlierSuccessfulSubmissions() {
        using var rig = new Rig();

        rig.Cache.Plan(inputs: Inputs);
        Assert.All(rig.Cache.Regions[3].Contents.ToArray(), value => Assert.Equal(actual: value, expected: 0));
        var expected = new uint[rig.Cache.Layout.ProbeCapacity];
        var first = rig.Cache.Regions[1].Contents[((rig.Cache.PlaceCount + rig.Cache.ClassifyCount) * 16)..];

        for (var index = 0; (index < rig.Cache.TraceCount); index++) {
            var probe = BinaryPrimitives.ReadInt32LittleEndian(source: first[(index * 16)..]);
            var stratum = BinaryPrimitives.ReadInt32LittleEndian(source: first[((index * 16) + 4)..]);

            expected[probe] |= (1u << stratum);
        }
        rig.Cache.Submitted();
        Assert.All(rig.Cache.Regions[3].Contents.ToArray(), value => Assert.Equal(actual: value, expected: 0));
        rig.Cache.Plan(inputs: Inputs);
        for (var probe = 0; (probe < expected.Length); probe++) {
            Assert.Equal(expected[probe], BinaryPrimitives.ReadUInt32LittleEndian(source: rig.Cache.Regions[3].Contents[(probe * 4)..]));
        }
        var next = rig.Cache.Regions[1].Contents[((rig.Cache.PlaceCount + rig.Cache.ClassifyCount) * 16)..];

        for (var index = 0; (index < rig.Cache.TraceCount); index++) {
            var probe = BinaryPrimitives.ReadInt32LittleEndian(source: next[(index * 16)..]);
            var stratum = BinaryPrimitives.ReadInt32LittleEndian(source: next[((index * 16) + 4)..]);

            Assert.Equal(0u, expected[probe] & (1u << stratum));
        }
        rig.Cache.Reset(epoch: 2);
        Assert.All(rig.Cache.Regions[3].Contents.ToArray(), value => Assert.Equal(actual: value, expected: 0));
    }
    [Fact]
    public void AnEpochResetClearsTheBrickTableAndRepeatsTheColdUpdateList() {
        using var rig = new Rig();

        rig.Cache.Plan(inputs: Inputs);
        var cold = rig.Cache.Regions[1].Contents.ToArray();

        rig.Cache.Submitted();
        rig.Cache.Reset(epoch: 7);
        Assert.Equal(7u, rig.Cache.Epoch);
        for (var slot = 0; (slot < rig.Cache.Layout.BrickCapacity); slot++) {
            Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(source: rig.Cache.Regions[0].Contents[((slot * 16) + 12)..]));
        }
        rig.Cache.Plan(inputs: Inputs);
        Assert.Equal(cold, rig.Cache.Regions[1].Contents.ToArray());
        Assert.True(condition: rig.Cache.NeedsPublish);
    }
    [Fact]
    public void TheCacheAndRegionsLiveUntilTheLastGraphLeaseRetires() {
        using var rig = new Rig();
        var held = rig.Cache.Retain();
        var buffer = rig.CacheBufferCreation;

        rig.Cache.Dispose();
        Assert.Equal(0, buffer.DisposeCount);
        held.Dispose();
        Assert.Equal(1, buffer.DisposeCount);
        Assert.Throws<ObjectDisposedException>(testCode: () => held.Retain());
        rig.Released = true;
    }
    [Fact]
    public void TheUpdateRegionOrdersPlacementPartitionAndStrataWithinTheFloorCeilings() {
        using var rig = new Rig();

        rig.Cache.Plan(inputs: Inputs);
        Assert.InRange(rig.Cache.PlaceCount, 1, 4);
        Assert.InRange(rig.Cache.ClassifyCount, 1, 4);
        Assert.InRange((rig.Cache.TraceCount * 64), 1, 8192);
        Assert.Equal(2, rig.Cache.PlaceCount);
        Assert.Equal(2, rig.Cache.ClassifyCount);
        Assert.Equal(128, rig.Cache.TraceCount);
        // This box demands just the origin brick of each medium level. The room pool occupies slots 0..191;
        // the world brick takes slot 192. Placement, partition and first-stratum tracing all put world first.
        var expectedRows = new List<(int Slot, int Stratum, int Level, int Reason)> {
            (192, 0, 1, 0), (0, 0, 0, 0),
            (192, 0, 1, 0), (0, 0, 0, 0),
        };

        for (var probe = 0; (probe < 64); probe++) { expectedRows.Add(item: ((12288 + probe), 0, 1, 0)); }
        for (var probe = 0; (probe < 64); probe++) { expectedRows.Add(item: (probe, 0, 0, 0)); }
        var updates = rig.Cache.Regions[1].Contents;

        for (var row = 0; (row < expectedRows.Count); row++) {
            var record = updates[(row * 16)..];
            var actual = (BinaryPrimitives.ReadInt32LittleEndian(source: record), BinaryPrimitives.ReadInt32LittleEndian(source: record[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(source: record[8..]), BinaryPrimitives.ReadInt32LittleEndian(source: record[12..]));

            Assert.Equal(expectedRows[row], actual);
        }
        Assert.Equal(860160, rig.Cache.Layout.TraceEvaluationCeiling);
        Assert.Equal(230144, rig.Cache.Layout.ClassifyEvaluationCeiling);
        var expected = rig.Cache.Regions.Aggregate(new GpuMemoryBytes(DeviceLocal: rig.Cache.Buffer.SizeBytes, HostVisible: 0), (sum, region) => (sum + region.OwnedBytes));

        Assert.Equal(expected, rig.Cache.Bytes);
    }

    private sealed class Rig : IDisposable {
        public FakeGpuDevice Gpu { get; } = new(countCalls: true, trackObjects: true);
        public WorkCounterSet Work { get; } = new(SdfIndirectWork.SourceName, SdfIndirectWork.Kinds);

        public SdfIndirectCache Cache { get; }
        public FakeGpuDevice.Creation CacheBufferCreation { get; }

        private readonly Puck.Shaders.GpuPassPipeline m_copy;

        public bool Released { get; set; }

        public Rig() {
            m_copy = SdfTestPipelines.RegionCopy(Gpu, new GpuWorkLedger(framesInFlight: SdfWorldTables.FrameRingSize, name: "gpu.indirect-test"));
            var firstCacheObject = Gpu.Created.Count;

            Cache = new SdfIndirectCache(new SdfIndirectLayout(tier: SdfIndirectTier.Medium), Gpu.Services, Gpu.MemoryProfile, m_copy.Compute!, 40, Work, 1);
            CacheBufferCreation = Gpu.Created[firstCacheObject];
            Assert.Equal("device-local buffer", CacheBufferCreation.Kind);
        }

        public void Dispose() { if (!Released) { Cache.Dispose(); } m_copy.Dispose(); }
    }
}
