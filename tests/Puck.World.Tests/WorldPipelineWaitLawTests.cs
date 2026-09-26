using Puck.Shaders;
using Puck.World.Client;
using Puck.Testing;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for <c>pipeline.wait</c>'s hold predicate (<see cref="WorldViewGraphHost.ArmWait"/>) on a <c>views.graphs</c>
/// row the host runs on a runtime whose render nodes never touch a GPU: every armed wait reports exactly one outcome through the runtime's report
/// and then releases, a missing compiler is unsupported rather than failed, and an unresolved phase keeps holding.
/// Deadlines are never exercised here; they bound liveness in presentation time and decide no verdict below.
/// </summary>
public sealed class WorldPipelineWaitLawTests : IDisposable {
    private readonly string m_directory = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-world-pipeline-wait-{Guid.NewGuid():N}"
    );
    private readonly List<string> m_reports = [];

    private readonly FakeGraphInstances m_instances;
    private readonly WorldViewGraphHost m_runtime;

    public WorldPipelineWaitLawTests() {
        var tools = Path.Combine(
            path1: m_directory,
            path2: "no-tools"
        );

        Directory.CreateDirectory(path: tools);
        File.WriteAllText(
            contents: "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { output[id.xy] = 0; }",
            path: Path.Combine(
                path1: m_directory,
                path2: "pass.hlsl"
            )
        );
        // An empty toolchain directory: every tool lookup refuses by name, the way a machine without DXC does.
        m_runtime = new WorldViewGraphHost(
            documentDirectory: m_directory,
            packager: new ShaderPackager(compiler: new ShaderCompiler(
                cacheDirectory: Path.Combine(
                    path1: m_directory,
                    path2: "cache"
                ),
                toolchainDirectory: tools
            ))
        ) {
            Report = (name, message) => m_reports.Add(item: $"[pipeline: {name} {message}]"),
        };
        m_instances = FakeGraphInstances.Attach(
            create: static name => new ShaderPipelineRenderNode(
                pipelines: new GpuPassPipelineCache(),
                deviceContext: new RefusingGpuDevice(),
                height: 4,
                hostsOnDirectX: false,
                name: name,
                width: 4
            ),
            host: m_runtime
        );
        // The accepted row compiles its source at once, as a booted or upserted row does.
        m_runtime.Reconcile(views: new WorldViewDefaults(Graphs: [new WorldViewGraph(
            Name: "ink",
            Source: "pass.hlsl"
        )]));
        PumpUntilCompiled();
        m_reports.Clear();
    }

    // Pumps the runtime until the background compile has been installed as the instance's last result. The bound is
    // liveness for a task that has already failed synchronously; it decides nothing.
    private void PumpUntilCompiled() {
        var entry = m_runtime.Entries["ink"];

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                m_runtime.PumpWatches();
                return !entry.IsCompiling;
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
    }

    public void Dispose() {
        m_runtime.Dispose();
        m_instances.Dispose();
        try {
            Directory.Delete(
                path: m_directory,
                recursive: true
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }
    }
    [Fact]
    public void AMissingCompilerReleasesACompiledWaitAsUnsupported() {
        m_runtime.QueueCompile(
            name: "ink",
            source: "pass.hlsl"
        );
        var hold = m_runtime.ArmWait(
            name: "ink",
            phase: WorldPipelinePhase.Compiled,
            seconds: 30,
            submissions: 0
        );

        PumpUntilCompiled();
        Assert.False(condition: hold());
        Assert.False(condition: m_runtime.Entries["ink"].IsWaiting);
        Assert.Contains(
            collection: m_reports,
            filter: static line => line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "[pipeline: ink unsupported: "
            )
        );
        Assert.Single(
            collection: m_reports,
            predicate: static line => line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "[pipeline: ink wait compiled unsupported: "
            )
        );
        // Released means released: polling again reports nothing more.
        Assert.False(condition: hold());
        Assert.Single(
            collection: m_reports,
            predicate: static line => line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: " wait "
            )
        );
    }
    [Fact]
    public void AMissingSourceReleasesAnInstalledWaitAsFailed() {
        m_runtime.QueueCompile(
            name: "ink",
            source: "missing.hlsl"
        );
        var hold = m_runtime.ArmWait(
            name: "ink",
            phase: WorldPipelinePhase.Installed,
            seconds: 30,
            submissions: 0
        );

        PumpUntilCompiled();
        Assert.False(condition: hold());
        Assert.Single(
            collection: m_reports,
            predicate: static line => line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "[pipeline: ink wait installed failed: "
            )
        );
    }
    [Fact]
    public void AWaitHoldsWhileItsCompilationIsPendingAndARemovedInstanceReleasesIt() {
        m_runtime.QueueCompile(
            name: "ink",
            source: "pass.hlsl"
        );
        var hold = m_runtime.ArmWait(
            name: "ink",
            phase: WorldPipelinePhase.Compiled,
            seconds: 30,
            submissions: 0
        );

        // Nothing pumps the runtime, so the completed task is never installed and the phase is still pending.
        Assert.True(condition: hold());
        Assert.DoesNotContain(
            collection: m_reports,
            filter: static line => line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: " wait "
            )
        );
        Assert.Throws<InvalidOperationException>(testCode: () => m_runtime.ArmWait(
            name: "ink",
            phase: WorldPipelinePhase.Captured,
            seconds: 30,
            submissions: 0
        ));
        m_runtime.Reconcile(views: new WorldViewDefaults());
        Assert.False(condition: hold());
        Assert.Contains(
            collection: m_reports,
            expected: "[pipeline: ink wait compiled failed: the instance was removed]"
        );
    }
    [InlineData(WorldPipelinePhase.Captured, 0UL, "[pipeline: ink wait captured failed: no capture was requested]")]
    [InlineData(WorldPipelinePhase.Submitted, 1UL, "[pipeline: ink wait submitted 1 unsupported: ")]
    [InlineData(WorldPipelinePhase.Counted, 1UL, "[pipeline: ink wait counted 1 unsupported: ")]
    [Theory]
    public void APhaseThatCanNeverBeReachedFailsAtOnceByName(WorldPipelinePhase phase, ulong submissions, string report) {
        var hold = m_runtime.ArmWait(
            name: "ink",
            phase: phase,
            seconds: 30,
            submissions: submissions
        );

        Assert.False(condition: hold());
        Assert.StartsWith(
            actualString: Assert.Single(collection: m_reports),
            expectedStartString: report
        );
    }
    [Fact]
    public void ReconcilingKeepsTheNodeOfARowThatStaysAndRemovingTheRowFailsItsWaitAsRemoved() {
        var node = m_runtime.Entries["ink"].Node;
        var hold = m_runtime.ArmWait(
            name: "ink",
            phase: WorldPipelinePhase.Captured,
            seconds: 30,
            submissions: 0
        );

        m_reports.Clear();
        m_runtime.Reconcile(views: new WorldViewDefaults(Graphs: [
            new WorldViewGraph(
                Name: "ink",
                Source: "pass.hlsl",
                TimeScale: 0.5f
            ),
            new WorldViewGraph(
                Name: "tint",
                Source: "pass.hlsl"
            ),
        ]));

        Assert.Same(
            actual: m_runtime.Entries["ink"].Node,
            expected: node
        );
        Assert.Equal(
            actual: (m_runtime.Entries["ink"].ClockScale, m_runtime.Entries.Count, m_instances.Reconfigurations),
            expected: (0.5f, 2, 2)
        );

        m_runtime.Reconcile(views: new WorldViewDefaults(Graphs: [new WorldViewGraph(
            Name: "tint",
            Source: "pass.hlsl"
        )]));

        Assert.False(condition: hold());
        Assert.Equal(
            actual: m_reports,
            expected: ["[pipeline: ink wait captured failed: the instance was removed]"]
        );
        Assert.Null(@object: m_instances.NodeOf(instance: "ink"));
    }
    // A slot showing a pane whose graph never installs places nothing, so the root draws the world beneath the slot and
    // nothing the root shows waits on the pane; the same pane places while a compilation is still on its way.
    [Fact]
    public void ARefusedPaneIsNotPlacedAndAPaneStillCompilingIs() {
        var views = new WorldViewDefaults(
            Graphs: [new WorldViewGraph(
                Name: "ink",
                Source: "pass.hlsl"
            )],
            Layouts: [new WorldViewLayout(
                Name: "pane",
                Slots: [new WorldViewSlot(Instance: "ink")]
            )]
        );
        var region = new Puck.Abstractions.Presentation.NormalizedRect(
            Height: 0.5f,
            Width: 0.5f,
            X: 0f,
            Y: 0f
        );

        m_runtime.BeginFrame(views: views);
        Assert.NotNull(@object: m_runtime.Entries["ink"].Refusal);
        Assert.False(condition: m_runtime.Place(
            instance: "ink",
            region: region,
            sharpness: 0f
        ));
        Assert.DoesNotContain(
            collection: m_runtime.Footprints,
            filter: static footprint => (footprint.Producer == "ink")
        );
        Assert.True(condition: m_runtime.TryGet(
            instance: WorldViewGraphs.MainInstance,
            pass: "ink",
            placement: out var placement
        ));
        Assert.False(condition: placement.Shown);

        // Control: the same pane with a compilation on its way is placed.
        m_runtime.QueueCompile(
            name: "ink",
            source: "pass.hlsl"
        );
        m_runtime.BeginFrame(views: views);
        Assert.Null(@object: m_runtime.Entries["ink"].Refusal);
        Assert.True(condition: m_runtime.Place(
            instance: "ink",
            region: region,
            sharpness: 0f
        ));
        Assert.Contains(
            collection: m_runtime.Footprints,
            filter: static footprint => (footprint.Producer == "ink")
        );
        PumpUntilCompiled();
    }
    // A capture of a refused pane can never be served, so a wait on it fails naming the refusal at once and withdraws
    // the capture, rather than holding until its deadline.
    [Fact]
    public async Task ACaptureWaitOnARefusedPaneFailsNamingTheRefusal() {
        var entry = m_runtime.Entries["ink"];
        var request = new Puck.Abstractions.Presentation.FrameCaptureRequest(path: Path.Combine(
            path1: m_directory,
            path2: "ink.png"
        ));

        entry.RequestCapture(request: request);

        var hold = m_runtime.ArmWait(
            name: "ink",
            phase: WorldPipelinePhase.Captured,
            seconds: 30,
            submissions: 0
        );

        Assert.False(condition: hold());
        Assert.Equal(
            actual: Assert.Single(collection: m_reports),
            expected: $"[pipeline: ink wait captured unsupported: {entry.Refusal}]"
        );
        Assert.True(condition: request.Completion.IsCompleted);
        Assert.False(condition: (await request.Completion).Succeeded);
        Assert.Null(@object: entry.Capture);
    }
    [InlineData(0)]
    [InlineData((WorldViewGraphHost.MaxWaitSeconds + 1))]
    [Theory]
    public void ADeadlineOutsideItsRangeIsRefused(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => m_runtime.ArmWait(
            name: "ink",
            phase: WorldPipelinePhase.Compiled,
            seconds: seconds,
            submissions: 0
        ));
}
