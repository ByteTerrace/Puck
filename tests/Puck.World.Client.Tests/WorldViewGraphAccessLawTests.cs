using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// Laws for <see cref="WorldViewGraphHost.Present"/> and <see cref="WorldViewGraphHost.BeginFrame"/> at hub scale
/// (a dozen worlds and four split-screen panes): steady frames index nodes, access epochs and entries by ordinal with
/// zero name lookups and zero allocations, while preserving epoch-change withdrawal and pause gating.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldViewGraphAccessLawTests : IDisposable {
    private readonly TemporaryDirectory m_directory = new(
        bestEffortDelete: true,
        prefix: "puck-world-graph-access-"
    );

    public void Dispose() => m_directory.Dispose();

    [Fact]
    public void HubScalePresentAndBindAccessUseOrdinalsWithoutAllocatingOrHashing() {
        const int Worlds = 12;
        const int Panes = 4;

        using var host = new WorldViewGraphHost(
            documentDirectory: m_directory.RootPath,
            packager: new ShaderPackager(compiler: new ShaderCompiler(cacheDirectory: Path.Combine(
                path1: m_directory.RootPath,
                path2: "cache"
            )))
        );
        using var fake = FakeGraphInstances.Attach(
            create: static name => new ShaderPipelineRenderNode(
                pipelines: new GpuPassPipelineCache(),
                deviceContext: new RefusingGpuDevice(),
                height: 4,
                hostsOnDirectX: false,
                name: name,
                width: 4
            ),
            host: host
        );

        var screens = new WorldScreenMappingSet(world: WorldViewGraphs.WorldInstance);
        var worldViews = new List<WorldView>(capacity: (Worlds * 2));

        for (var world = 0; (world < Worlds); world++) {
            worldViews.Add(item: new WorldView(
                Demand: WorldViewDemand.Screen,
                FilmsWorld: false,
                Height: 0.5,
                Name: $"session.{world}",
                Refresh: RenderGraphRefresh.EveryFrame,
                Width: 0.5
            ));
            worldViews.Add(item: new WorldView(
                Demand: WorldViewDemand.Screen,
                FilmsWorld: false,
                Height: 0.25,
                Name: $"session.{world}.cam.overview",
                Refresh: RenderGraphRefresh.EveryFrame,
                Width: 0.25
            ));
        }

        screens.ReconcileViews(views: WorldViewInstances.Of(views: worldViews));

        var paneRows = new WorldViewGraph[Panes];

        for (var pane = 0; (pane < Panes); pane++) {
            paneRows[pane] = new WorldViewGraph(
                Name: $"pane-{pane}"
            );
        }

        var views = new WorldViewDefaults(Graphs: paneRows);
        var scenes = new CountingViewScenes();

        host.Screens = screens;
        host.ViewScenes = scenes;

        var frame = new ShaderFrameValues(
            Pointer: Vector2.Zero,
            PointerDown: false,
            StateTick: 1L,
            Tick: 1L,
            Time: 0.1d,
            TimeDelta: 0.016d
        );

        // Warm up composition, ordinal access tables and JIT.
        for (var warmup = 0; (warmup < 4); warmup++) {
            host.BeginFrame(views: views);
            host.Present(frame: in frame);
        }

        var instanceCount = fake.Instances.Instances.Count;

        Assert.True(condition: (instanceCount >= ((Worlds * 2) + Panes)));

        fake.ResetLookupCounts();
        scenes.ResetLookupCounts();

        void SteadyFrame() {
            host.BeginFrame(views: views);
            host.Present(frame: in frame);
        }

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: SteadyFrame));
        Assert.Equal(expected: 0, actual: fake.NodeOfLookups);
        Assert.Equal(expected: 0, actual: scenes.AccessOfLookups);
        Assert.True(condition: (scenes.ReadAccessCalls > 0));
        Assert.Empty(collection: fake.Withdrawn);

        // Epoch change on one view withdraws only that view; marking another unavailable pauses its node.
        var withdrawnName = "pane-0";
        var pausedName = "pane-1";
        var withdrawnOrdinal = fake.Instances.IndexOf(name: withdrawnName);
        var pausedOrdinal = fake.Instances.IndexOf(name: pausedName);

        scenes.Set(index: withdrawnOrdinal, access: new WorldViewAccess(Available: true, Epoch: 2L));
        scenes.Set(index: pausedOrdinal, access: new WorldViewAccess(Available: false, Epoch: 0L));
        host.BeginFrame(views: views);

        Assert.Equal(expected: [withdrawnName], actual: fake.Withdrawn);
        Assert.True(condition: fake.Nodes[pausedOrdinal]!.Paused);
        Assert.False(condition: fake.Nodes[withdrawnOrdinal]!.Paused);
    }

    private sealed class CountingViewScenes : IWorldViewScenes {
        private WorldViewAccess[] m_values = [];

        public int AccessOfLookups { get; private set; }
        public int ReadAccessCalls { get; private set; }

        public void ResetLookupCounts() {
            AccessOfLookups = 0;
            ReadAccessCalls = 0;
        }
        public void Set(int index, WorldViewAccess access) {
            if (index >= m_values.Length) {
                var previous = m_values.Length;

                Array.Resize(array: ref m_values, newSize: (index + 1));
                Array.Fill(array: m_values, count: (m_values.Length - previous), startIndex: previous, value: WorldViewAccess.Open);
            }
            m_values[index] = access;
        }
        public WorldViewAccess AccessOf(string view) {
            AccessOfLookups++;

            return WorldViewAccess.Open;
        }
        public void ReadAccess(RenderGraphInstanceSet set, Span<WorldViewAccess> access) {
            ReadAccessCalls++;
            if (m_values.Length < access.Length) {
                var previous = m_values.Length;

                Array.Resize(array: ref m_values, newSize: access.Length);
                Array.Fill(array: m_values, count: (m_values.Length - previous), startIndex: previous, value: WorldViewAccess.Open);
            }
            m_values.AsSpan(start: 0, length: access.Length).CopyTo(destination: access);
        }

        public bool TryCamera(string view, out CameraSnapshot camera) { camera = default; return false; }
        public bool TrySurface(string view, SourceRay ray, out FixedVector3 point) { point = default; return false; }
        public bool TryPlacements(string view, out IReadOnlyList<SourceMapping> placements) { placements = []; return false; }
        public WorldPortalGlass PortalGlass(string consumer, string producer, out WorldScreen? glass) { glass = null; return WorldPortalGlass.None; }
        public object? ArrivingWorld(string view, string consumer) => null;
    }
}
