using System.Buffers;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Scene publication waits for channel drain, and sourced chunks cover merged copy gaps.</summary>
public sealed partial class SdfWorldTablesWorkLawTests {
    private static readonly Type WorkType = typeof(SdfMeshIrradiance).Assembly.GetType("Puck.SdfVm.SdfMeshProbeGpuWork", true)!;
    private static readonly Type UploadType = WorkType.GetNestedType("SceneUpload", BindingFlags.NonPublic)!;
    private static readonly Type ChunkType = WorkType.GetNestedType("SceneChunk", BindingFlags.NonPublic)!;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task TheLastChunkWrittenAfterAnEmptyReadStillPrecedesTheRoot() {
        var run = typeof(SdfWorldTablesWorkLawTests).GetMethod(nameof(RootRace), BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task)run.MakeGenericMethod(ChunkType).Invoke(null, null)!;
    }

    private static async Task RootRace<T>() where T : class {
        var upload = RuntimeHelpers.GetUninitializedObject(UploadType);
        var channel = Channel.CreateBounded<T>(1);
        var root = Chunk(0, [99], 1);
        var last = Chunk(2, [7, 8], 2);
        using var gate = new ManualResetEventSlim(false);
        var writer = Task.Run(() => {
            Assert.True(gate.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(channel.Writer.TryWrite((T)last));
            channel.Writer.Complete();
        });
        UploadType.GetField("Chunks", Members)!.SetValue(upload, channel);
        UploadType.GetField("Work", Members)!.SetValue(upload, writer);
        UploadType.GetField("m_root", Members)!.SetValue(upload, root);
        // Force exactly the reviewed race: the reader sees empty, then the final writer completes before TryRoot.
        Assert.False(channel.Reader.TryRead(out _));
        gate.Set();
        await writer;
        object?[] args = [null];
        var takeRoot = UploadType.GetMethod("TryRoot", Members)!;
        Assert.False((bool)takeRoot.Invoke(upload, args)!);
        Assert.True(channel.Reader.TryRead(out var received));
        Assert.Same(last, received);
        Assert.True((bool)takeRoot.Invoke(upload, args)!);
        Assert.Same(root, args[0]);
        Assert.False((bool)takeRoot.Invoke(upload, args)!);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AFirstChunkNearTheRootSuppliesTheMergedGap(int word) {
        var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ }, indices: new uint[] { 0, 1, 2 });
        SdfMeshDraw[] draws = [new(Identity: new object(), Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity)];
        using var scene = SdfMeshIrradianceScene.Build(draws);
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>();
        _ = SdfMeshRegion.Plan(draws, placements);
        var export = typeof(SdfMeshIrradianceScene).GetMethod("CreateGpuExport", Members)!.Invoke(scene, [placements, null])!;
        var words = scene.ExportGpu(placements);
        var rented = ArrayPool<uint>.Shared.Rent(2);
        words.AsSpan(word, 2).CopyTo(rented);
        using var chunk = (IDisposable)Chunk(word, rented, 2, export);
        var gpu = new UploadModelGpu();
        var pass = new GpuRegionCopyPass(kernel: new byte[] { UploadModelGpu.RegionCopyBytecode }, pipelines: new GpuPassPipelineCache());
        var lease = pass.Acquire(gpu);
        try {
            GpuPassPipeline? pipeline = null;
            TestLiveness.Until(() => (pipeline = lease.Poll()) is not null, () => "copy pipeline building", lease.WaitFinished);
            using var region = new GpuRegion(GpuResidencyPolicy.Staged, GpuHostVisibleMemory.Host, 4, 4,
                gpu.Services.BufferFactory, gpu.Services.Bindings, gpu.Services.Recorder, pipeline!.Compute!, default);
            region.Shadow((IGpuRegionSource)chunk, 4);
            using var carry = region.Grow(words.Length * 4L);
            region.OweSourced(word * 4L, 8);
            var copy = new GpuRegionCopyRecording(gpu.Services.Recorder, GpuStage.ComputeShader, () => 2);
            copy.Record(region, 3, handsToReaders: true);
            copy.Finish();
            var actual = MemoryMarshal.Cast<byte, uint>(gpu.Memory(region.Buffer(0).BufferHandle));
            Assert.Equal(0u, actual[0]);
            Assert.Equal(words.AsSpan(1, word + 1).ToArray(), actual.Slice(1, word + 1).ToArray());
        } finally { lease.Release(); }
    }

    private static object Chunk(int word, uint[] words, int count, object? export = null) =>
        Activator.CreateInstance(ChunkType, Members, null, [word, words, count, export], null)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneCopiesUseAllSlotsAndRetireBeforePublicationOrCancellation(bool cancel) {
        using var rig = new Rig(holdFences: true, distinctBuffers: true);
        rig.Render();
        using var volume = new SdfMeshIrradiance();
        var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ }, indices: new uint[] { 0, 1, 2 });
        SdfMeshDraw[] draws = [new(Identity: new object(), Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity)];
        using var scene = SdfMeshIrradianceScene.Build(draws);
        typeof(SdfMeshIrradiance).GetField("m_scene", Members)!.SetValue(volume, scene.Acquire());
        var client = (ISdfGpuWorkClient)Activator.CreateInstance(WorkType, Members, null, [rig.Engine, volume], null)!;
        rig.Engine.GpuWork.Add(client);
        var arena = WorkType.GetField("m_sceneArena", Members)!.GetValue(client)!;
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>();
        _ = SdfMeshRegion.Plan(draws, placements);
        var create = typeof(SdfMeshIrradianceScene).GetMethod("CreateGpuExport", Members)!;
        var export = create.Invoke(scene, [placements, arena])!;
        var words = scene.ExportGpu(placements);
        var seed = typeof(SdfWorldTablesWorkLawTests).GetMethod(nameof(SeedUpload), BindingFlags.Static | BindingFlags.NonPublic)!;
        var upload = seed.MakeGenericMethod(ChunkType).Invoke(null, [scene, placements, export, words])!;
        WorkType.GetField("m_sceneUpload", Members)!.SetValue(client, upload);
        // Host-time pacing may stop a pump early; held fences keep each admitted chunk in a distinct service slot.
        for (var pump = 0; pump < 8 && rig.Engine.GpuWork.InFlight < 4; pump++) { rig.Engine.GpuWork.Pump(rig.Engine); }
        Assert.Equal(4, rig.Engine.GpuWork.InFlight);
        Assert.Equal(4, WorkType.GetField("m_sceneInFlight", Members)!.GetValue(client));
        Assert.Equal(0L, WorkType.GetField("m_sceneUploads", Members)!.GetValue(client));
        Assert.False(client.TryTake(rig.Engine, out _, out _));
        if (cancel) {
            typeof(SdfMeshIrradiance).GetField("m_scene", Members)!.SetValue(volume, null);
            scene.Dispose(); // Release the volume's claim, keeping this law's and the upload's claims.
            Assert.False(client.TryTake(rig.Engine, out _, out _));
            Assert.Same(upload, WorkType.GetField("m_sceneUpload", Members)!.GetValue(client));
        }
        foreach (var fence in rig.Gpu.SubmittedFences) { fence.Completed = true; }
        rig.Engine.GpuWork.Pump(rig.Engine);
        if (!cancel) {
            Assert.Equal(1, rig.Engine.GpuWork.InFlight); // Only the root, after all four chunk fences.
            foreach (var fence in rig.Gpu.SubmittedFences) { fence.Completed = true; }
            rig.Engine.GpuWork.Pump(rig.Engine);
            Assert.Equal(1L, WorkType.GetField("m_sceneUploads", Members)!.GetValue(client));
        } else {
            Assert.Equal(0, rig.Engine.GpuWork.InFlight);
            Assert.Equal(0L, WorkType.GetField("m_sceneUploads", Members)!.GetValue(client));
            var replacement = create.Invoke(scene, [placements, arena])!;
            Assert.Equal(export.GetType().GetProperty("Prefix", Members)!.GetValue(export), replacement.GetType().GetProperty("Prefix", Members)!.GetValue(replacement));
            Assert.Single((System.Collections.IEnumerable)replacement.GetType().GetProperty("Changed", Members)!.GetValue(replacement)!);
            arena.GetType().GetMethod("Abandon", Members)!.Invoke(arena, [replacement]);
        }
        Assert.Null(WorkType.GetField("m_sceneUpload", Members)!.GetValue(client));
    }

    private static object SeedUpload<T>(SdfMeshIrradianceScene scene, Dictionary<SdfMesh, SdfMeshRegionMesh> placements, object export, uint[] words) where T : class {
        var upload = RuntimeHelpers.GetUninitializedObject(UploadType);
        var channel = Channel.CreateUnbounded<T>();
        for (var part = 0; part < 4; part++) {
            var start = 1 + part * (words.Length - 1) / 4;
            var end = 1 + (part + 1) * (words.Length - 1) / 4;
            var data = ArrayPool<uint>.Shared.Rent(end - start);
            words.AsSpan(start, end - start).CopyTo(data);
            Assert.True(channel.Writer.TryWrite((T)Chunk(start, data, end - start, export)));
        }
        channel.Writer.Complete();
        Set("Scene", scene.Acquire()); Set("Placements", placements); Set("PlacementVersion", 0L);
        Set("Chunks", channel); Set("Work", Task.CompletedTask); Set("Export", export);
        Set("m_root", Chunk(0, [words[0]], 1)); Set("m_cancel", new CancellationTokenSource());
        return upload;
        void Set(string field, object value) => UploadType.GetField(field, Members)!.SetValue(upload, value);
    }

    [Fact]
    public void SceneCopiesResumeEvenAPartiallyCopiedGeometry() {
        var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ }, indices: new uint[] { 0, 1, 2 });
        SdfMeshDraw[] draws = [new(Identity: new object(), Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity)];
        using var scene = SdfMeshIrradianceScene.Build(draws);
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>();
        _ = SdfMeshRegion.Plan(draws, placements);
        var arenaType = typeof(SdfMeshIrradianceScene).GetNestedType("GpuArena", BindingFlags.NonPublic)!;
        var arena = Activator.CreateInstance(arenaType, Members, null, null, null)!;
        var create = typeof(SdfMeshIrradianceScene).GetMethod("CreateGpuExport", Members)!;
        var complete = arenaType.GetMethod("Complete", Members)!;
        var abandon = arenaType.GetMethod("Abandon", Members)!;
        var export = create.Invoke(scene, [placements, arena])!;
        var changed = (List<(int Word, int Count)>)export.GetType().GetProperty("Changed", Members)!.GetValue(export)!;
        var geometry = changed[0];
        // Repeated incomplete admissions cannot publish a root, but still deliver all immutable geometry.
        for (var copied = 0; copied < geometry.Count; copied++) {
            complete.Invoke(arena, [export, geometry.Word + copied, 1]);
            abandon.Invoke(arena, [export]);
            export = create.Invoke(scene, [placements, arena])!;
            changed = (List<(int Word, int Count)>)export.GetType().GetProperty("Changed", Members)!.GetValue(export)!;
            if (copied + 1 < geometry.Count) { Assert.Equal((geometry.Word + copied + 1, geometry.Count - copied - 1), changed[0]); }
        }
        Assert.Single(changed); // Only the new prefix remains; no completed geometry is sent again.
        abandon.Invoke(arena, [export]);
    }
}
