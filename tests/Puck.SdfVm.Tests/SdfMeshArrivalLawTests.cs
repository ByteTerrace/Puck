using System.Numerics;
using System.Runtime.InteropServices;
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
            SdfMeshIrradianceProbe.ProbeWords * 4L, 1, services.BufferFactory, services.Bindings,
            services.Recorder, null, default);
        PublishIrradiance(rig.Engine, feedback.Buffer(0), SdfMeshIrradianceProbe.ProbeWords, [0]);
        rig.Engine.RetireBackgroundResource(feedback);
        ServiceTrim(rig.Engine);
        ServiceTrim(rig.Engine); // A hidden residency may service retirements repeatedly before its next upload.
        Assert.NotEqual(0, feedback.Buffer(0).BufferHandle);
        rig.Engine.SubmitUpload();
        ServiceTrim(rig.Engine);
        Assert.Throws<ObjectDisposedException>(() => feedback.Buffer(0));
    }

    [Fact]
    public void ForgettingOnlyATailRelandsStandingMeshesWhenTheDestinationShrinks() {
        using var rig = new Rig(1);
        var small = HostDraw("standing", 1);
        var tail = small with { Mesh = new SdfMesh(new Vector3[1 << 21], new uint[] { 0, 1, 2 }) };
        var frame = Frame(rig.LiveProgram, 0, Transforms(1)) with { MeshDraws = new[] { small } };
        LandRootCut(rig, frame);
        frame = frame with { MeshDraws = new[] { small, tail }, MeshDrawsRevision = 1 };
        rig.Engine.Pack(frame);
        rig.Engine.SubmitUpload();
        var held = rig.Engine.MeshRegionBytes;
        var relocations = rig.Engine.DescribeLanding().Split(' ').Single(value => value.StartsWith("relocations=", StringComparison.Ordinal));
        frame = frame with { MeshDraws = new[] { small }, MeshDrawsRevision = 2 };
        rig.Engine.Pack(frame);
        rig.Engine.SubmitUpload();
        MemoryAccount.Shared.TrimCaches();
        rig.Engine.Pack(frame);
        rig.Engine.SubmitUpload();
        LandRootCut(rig, frame);
        Assert.True(rig.Engine.MeshRegionBytes < held);
        Assert.True(rig.Engine.MeshStands(small.Mesh));
        Assert.False(rig.Engine.DrawsAwaitMeshes);
        Assert.Contains(relocations, rig.Engine.DescribeLanding());
    }
    [Fact]
    public void LandingCompletionAndStandingRequireTheRecordedPageGeneration() {
        var pages = new GpuBufferPages(16L << 20, GpuBufferPages.MinimumPageBytes);
        using var rig = new Rig(1, meshPages: pages);
        var draw = HostDraw("page-generation", 1);
        var frame = Frame(rig.LiveProgram, 0, Transforms(1)) with { MeshDraws = new[] { draw } };
        rig.Engine.Pack(frame);
        rig.Engine.SubmitUpload();
        rig.Engine.GpuWork.Pump(rig.Engine);
        Assert.True(rig.Engine.GpuWork.InFlight > 0);
        Assert.False(rig.Engine.MeshStands(draw.Mesh));
        pages.SetWritable(0, 4096, false);
        pages.SetWritable(0, 4096, true);
        rig.Engine.GpuWork.Pump(rig.Engine);
        Assert.False(rig.Engine.MeshStands(draw.Mesh));
        Assert.Contains("landing-stale=1", rig.Engine.DescribeLanding());
        LandRootCut(rig, frame);
        Assert.True(rig.Engine.MeshStands(draw.Mesh));
        pages.SetWritable(0, 4096, false);
        pages.SetWritable(0, 4096, true);
        Assert.False(rig.Engine.MeshStands(draw.Mesh));
        LandRootCut(rig, frame);
        Assert.True(rig.Engine.MeshStands(draw.Mesh));
    }

    [Fact]
    public void AMovedMeshWithoutWritablePagesIsOwedAndItsRowsAreAbsentUntilItLands() {
        var pages = new GpuBufferPages(16L << 20, GpuBufferPages.MinimumPageBytes);
        using var rig = new Rig(1, meshPages: pages);
        var draw = HostDraw("moved-page", 1);
        var frame = Frame(rig.LiveProgram, 0, Transforms(1)) with { MeshDraws = new[] { draw } };
        LandRootCut(rig, frame);
        var draws = Enumerable.Repeat(draw, SdfMeshRegionArena.MinimumDrawCapacity + 1).ToArray();
        using var arena = new SdfMeshRegionArena();
        arena.Plan(new[] { draw });
        arena.Plan(draws);
        Assert.Contains(draw.Mesh, arena.Moved);
        var first = (int)((long)arena.Placements[draw.Mesh].VertexWord * sizeof(uint) / pages.PageBytes);
        pages.SetWritable(first, 1, false);
        frame = frame with { MeshDraws = draws, MeshDrawsRevision = 1 };
        rig.Engine.Pack(frame);
        Assert.True(rig.Engine.DrawsAwaitMeshes);
        Assert.True(rig.Engine.MeshesLanding);
        Assert.False(rig.Engine.MeshStands(draw.Mesh));
        rig.Engine.SubmitUpload();
        var records = MemoryMarshal.Cast<byte, uint>(rig.Gpu.DeviceLocal(rig.Engine.MeshRegionBytes, "mesh-region"));
        for (var row = 0; row < draws.Length; row++) {
            Assert.NotEqual(0u, records[row * SdfMeshRecord.DrawWords + SdfMeshRecord.SelectionWord + 3] & SdfMeshRecord.AbsentFlag);
        }
        pages.SetWritable(first, 1, true);
        LandRootCut(rig, frame);
        Assert.True(rig.Engine.MeshStands(draw.Mesh));
        Assert.False(rig.Engine.DrawsAwaitMeshes);
    }

    [Fact]
    public void RenamingTheLastRowAwayFromARefusedMeshRemovesThatRefusal() {
        var small = HostDraw(identity: "small", x: 1);
        var large = small with { Mesh = new SdfMesh(positions: new Vector3[1000], indices: new uint[] { 0, 1, 2 }) };
        var reserve = (((ulong)SdfMeshRegionArena.MinimumDrawCapacity) * SdfMeshRegion.DrawBytes);
        using var arena = new SdfMeshRegionArena(limitBytes: (reserve + (((ulong)SdfMeshRegion.WordsOf(mesh: small.Mesh)) * sizeof(uint))));
        arena.Plan(draws: new[] { small, large });
        Assert.Single(collection: arena.Refused);
        arena.Rename(index: 1, mesh: small.Mesh);
        Assert.Empty(collection: arena.Refused);
        Assert.Equal(SdfMeshRegion.WordsOf(mesh: small.Mesh), arena.NamedWords);
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

    [Fact]
    public void LandingStagesOnlyItsMeshRowsAndAbsenceRetiresTheirHistoryIncludingRefusalRelocationAndTrim() {
        SdfRowChecks.Enabled = true;
        try {
            using var rig = new Rig(1);
            var work = SdfMeshHistory.ProcessWork;
            var checks = SdfRowChecks.ProcessWork;
            var draw = HostDraw(identity: "a", x: 1);
            var other = HostDraw(identity: "b", x: 2) with { Glass = true };
            var draws = Enumerable.Repeat(count: 17, element: draw).Concat(second: Enumerable.Repeat(count: 16, element: other)).ToList();
            var journal = new GpuChangedRows(SdfMovedTransforms.History);
            long revision = 0;

            SdfFrame Input(bool full = false) {
                journal.Begin(everything: full, rows: draws.Count);
                return Frame(program: rig.LiveProgram, time: 0, transforms: Transforms(slots: 1)) with {
                    MeshDraws = draws,
                    MeshDrawsRevision = revision,
                    MeshDrawChanges = journal.Reader,
                    MeshDrawChangesSerial = journal.Serial,
                };
            }
            uint[] Records() => MemoryMarshal.Cast<byte, uint>(span: rig.Gpu.DeviceLocal(rig.Engine.MeshRegionBytes, "mesh-region"))
                [..(draws.Count * SdfMeshRecord.DrawWords)].ToArray();
            bool Absent(uint[] words, int row) => ((words[(((row * SdfMeshRecord.DrawWords) + SdfMeshRecord.SelectionWord) + 3)] & SdfMeshRecord.AbsentFlag) != 0);
            void Check() => Assert.Null(@object: rig.Engine.RowCheckFailure);

            rig.Engine.ForceMeshLimit(bytes: 0);
            var refusedBefore = checks.Read(kind: SdfRowChecks.RefusedAbsent);

            RenderRootCut(rig, Input(full: true));
            var records = Records();

            Assert.Equal(((uint)draws.Count), rig.Engine.MeshDrawCount);
            Assert.True(condition: rig.Engine.DrawsAwaitMeshes);
            Assert.Contains("refuses", rig.Engine.MeshResidencyPending());
            Assert.Equal(0u, rig.Engine.GlassDrawCount);
            Assert.All(Enumerable.Range(0, draws.Count), row => Assert.True(condition: Absent(row: row, words: records)));
            Assert.Equal(draws.Count, (checks.Read(kind: SdfRowChecks.RefusedAbsent) - refusedBefore));
            Check();

            rig.Engine.ForceMeshLimit(bytes: null);
            var packedBefore = work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Landing));
            var seedsBefore = work.Read(kind: SdfMeshHistory.Seeded);
            var frame = Input();

            LandRootCut(frame: frame, rig: rig);
            var firstPacked = (work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Landing)) - packedBefore);
            var firstSeeded = (work.Read(kind: SdfMeshHistory.Seeded) - seedsBefore);

            Assert.Equal(draws.Count, firstPacked);
            Assert.Equal(draws.Count, firstSeeded);
            Assert.Equal(16u, rig.Engine.GlassDrawCount);
            Check();

            records = Records();
            var generations = Enumerable.Range(0, draws.Count).Select(selector: row => records[(((row * SdfMeshRecord.DrawWords) + SdfMeshRecord.SelectionWord) + 11)]).ToArray();

            rig.Engine.ForceMeshRelocation();
            RenderRootCut(rig, Input());
            Assert.True(condition: rig.Engine.DrawsAwaitMeshes);
            LandRootCut(frame: frame, rig: rig);
            records = Records();
            for (var row = 0; (row < draws.Count); row++) {
                Assert.True(condition: (records[(((row * SdfMeshRecord.DrawWords) + SdfMeshRecord.SelectionWord) + 11)] > generations[row]));
            }
            Check();

            var trims = checks.Read(kind: SdfRowChecks.Trims);

            MemoryAccount.Shared.TrimCaches();
            RenderRootCut(rig, Input());
            Assert.True(condition: (checks.Read(kind: SdfRowChecks.Trims) > trims));
            Check();

            // The frame journals an actual edit while its new mesh is owed; the existing standing row must still
            // consume its change rather than waiting for the other mesh to land.
            var newDraw = HostDraw(identity: "c", x: 3);

            draws[0] = newDraw;
            draws[17] = other with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 9, yPosition: 0, zPosition: 0) };
            revision++;
            journal.Begin(everything: false, rows: draws.Count);
            journal.Owe(0, 1); journal.Owe(17, 1);
            frame = frame with { MeshDrawsRevision = revision, MeshDrawChangesSerial = journal.Serial };
            rig.Engine.Pack(frame: frame);
            journal.Begin(everything: false, rows: draws.Count);
            journal.Owe(17, 1);
            draws[17] = draws[17] with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 10, yPosition: 0, zPosition: 0) };
            frame = frame with { MeshDrawsRevision = ++revision, MeshDrawChangesSerial = journal.Serial };
            var journalPacked = work.Read(kind: SdfMeshHistory.RecordsPacked);

            rig.Engine.Pack(frame: frame);
            Assert.Equal(1, (work.Read(kind: SdfMeshHistory.RecordsPacked) - journalPacked));
            rig.Engine.SubmitUpload();
            Check();
            var onePacked = work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Landing));
            var oneSeeded = work.Read(kind: SdfMeshHistory.Seeded);

            LandRootCut(frame: frame, rig: rig);
            Assert.Equal(1, (work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Landing)) - onePacked));
            Assert.Equal(1, (work.Read(kind: SdfMeshHistory.Seeded) - oneSeeded));
            Check();

            // Dropping the last row naming an owed mesh ends that demand without waiting for its unused payload.
            draws[0] = HostDraw(identity: "abandoned", x: 4);
            journal.Begin(everything: false, rows: draws.Count);
            journal.Owe(start: 0, count: 1);
            frame = frame with { MeshDrawsRevision = ++revision, MeshDrawChangesSerial = journal.Serial };
            rig.Engine.Pack(frame: frame);
            Assert.True(condition: rig.Engine.MeshesLanding);
            Assert.Contains("owed-clock=running", rig.Engine.DescribeLanding());
            var owedFrame = rig.Engine.DescribeLanding().Split(' ').Single(predicate: value => value.StartsWith(comparisonType: StringComparison.Ordinal, value: "owed-frames="));
            rig.Engine.Pack(frame: frame);
            Assert.Contains(owedFrame, rig.Engine.DescribeLanding());
            draws[0] = newDraw;
            journal.Begin(everything: false, rows: draws.Count);
            journal.Owe(start: 0, count: 1);
            frame = frame with { MeshDrawsRevision = ++revision, MeshDrawChangesSerial = journal.Serial };
            rig.Engine.Pack(frame: frame);
            rig.Engine.SubmitUpload();
            Assert.False(condition: rig.Engine.MeshesLanding);
            Assert.False(condition: rig.Engine.DrawsAwaitMeshes);
            Assert.True(condition: rig.Engine.MeshesResident);
            Assert.Contains("owed-clock=settled", rig.Engine.DescribeLanding());
            Check();

            var whole = checks.Read(kind: SdfRowChecks.TablesWhole);
            var skipped = checks.Read(kind: SdfRowChecks.Skipped);

            rig.Engine.ForceSkipTables();
            for (var count = 0; (count < (SdfMovedTransforms.History + 4)); count++) { RenderRootCut(rig, Input()); Check(); }
            Assert.True(condition: (checks.Read(kind: SdfRowChecks.TablesWhole) > whole));
            Assert.True(condition: (checks.Read(kind: SdfRowChecks.Skipped) > skipped));
            // A newly constructed consumer has no cursor, even at the same producer endpoint.
            using (var second = new Rig(1)) { LandRootCut(frame: frame, rig: second); Assert.Null(@object: second.Engine.RowCheckFailure); }
            for (var count = 0; (count < 3); count++) { RenderRootCut(rig, Input()); }
            Assert.True(condition: (checks.Read(kind: SdfRowChecks.Device) > 0));
            output.WriteLine(message: $"rows={draws.Count} boot-landing-packed={firstPacked} boot-seeded={firstSeeded} one-mesh-landing-packed=1 one-mesh-seeded=1 journal-while-owed=1 pack={checks.Read(kind: SdfRowChecks.Packed)} ledger={checks.Read(kind: SdfRowChecks.Ledger)} seeds={checks.Read(kind: SdfRowChecks.Seeds)} device={checks.Read(kind: SdfRowChecks.Device)} refused={checks.Read(kind: SdfRowChecks.RefusedAbsent)} returned={checks.Read(kind: SdfRowChecks.ReturnedLive)} trims={checks.Read(kind: SdfRowChecks.Trims)} whole={checks.Read(kind: SdfRowChecks.TablesWhole)} skipped={checks.Read(kind: SdfRowChecks.Skipped)}");
            Check();
        } finally { SdfRowChecks.Enabled = false; }
    }
}
