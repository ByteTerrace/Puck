using Puck.Shaders;
using Puck.World.Client;
using Puck.Testing;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for <c>pipeline.wait</c>'s hold predicate (<see cref="WorldPipelineRuntime.ArmWait"/>) on a registered instance
/// whose render node never touches a GPU: every armed wait reports exactly one outcome through the runtime's report
/// and then releases, a missing compiler is unsupported rather than failed, and an unresolved phase keeps holding.
/// Deadlines are never exercised here; they bound liveness in presentation time and decide no verdict below.
/// </summary>
public sealed class WorldPipelineWaitLawTests : IDisposable {
    private readonly string m_directory = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-world-pipeline-wait-{Guid.NewGuid():N}"
    );
    private readonly List<string> m_reports = [];

    private readonly WorldPipelineRuntime m_runtime;

    public WorldPipelineWaitLawTests() {
        var tools = Path.Combine(
            path1: m_directory,
            path2: "no-tools"
        );

        Directory.CreateDirectory(path: tools);
        File.WriteAllText(
            contents: "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }",
            path: Path.Combine(
                path1: m_directory,
                path2: "pass.hlsl"
            )
        );
        // An empty toolchain directory: every tool lookup refuses by name, the way a machine without DXC does.
        m_runtime = new WorldPipelineRuntime(
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
        m_runtime.Register(
            name: "ink",
            node: new ShaderPipelineRenderNode(
                deviceContext: new RefusingGpuDevice(),
                height: 4,
                hostsOnDirectX: false,
                name: "ink",
                width: 4
            )
        );
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
        m_runtime.Reconcile(rows: []);
        Assert.False(condition: hold());
        Assert.Contains(
            collection: m_reports,
            expected: "[pipeline: ink wait compiled failed: the instance was removed]"
        );
    }
    [InlineData(WorldPipelinePhase.Captured, 0UL, "[pipeline: ink wait captured failed: no capture was requested]")]
    [InlineData(WorldPipelinePhase.Submitted, 1UL, "[pipeline: ink wait submitted 1 failed: no compilation was requested]")]
    [InlineData(WorldPipelinePhase.Counted, 1UL, "[pipeline: ink wait counted 1 failed: no compilation was requested]")]
    [Theory]
    public void APhaseThatCanNeverBeReachedFailsAtOnceByName(WorldPipelinePhase phase, ulong submissions, string report) {
        var hold = m_runtime.ArmWait(
            name: "ink",
            phase: phase,
            seconds: 30,
            submissions: submissions
        );

        Assert.False(condition: hold());
        Assert.Equal(
            actual: m_reports,
            expected: [report]
        );
    }
    [InlineData(0)]
    [InlineData((WorldPipelineRuntime.MaxWaitSeconds + 1))]
    [Theory]
    public void ADeadlineOutsideItsRangeIsRefused(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => m_runtime.ArmWait(
            name: "ink",
            phase: WorldPipelinePhase.Compiled,
            seconds: seconds,
            submissions: 0
        ));
}
