using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for <see cref="WorldViewGraphHost.Reconcile"/>'s rebinding of an installed graph to a row's moved inputs: a row
/// that became an engine package, and a row whose new source declares another input version, are never handed their
/// old graph, so the reconfiguration is accepted and the new source compiles; and a refused reconfiguration is tried
/// again on the next call with the same section. The fake runtime refuses as the real one does: a graph handed to an
/// external instance, and a graph whose external versions the inputs do not bind exactly. The installed graph is
/// compiled, so the laws need DXC.
/// </summary>
public sealed class WorldViewGraphRebindLawTests : IDisposable {
    private const string Camera = "cam";
    private const string Ink = "ink";

    private readonly string m_directory = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-world-graph-rebind-{Guid.NewGuid():N}"
    );
    private readonly List<string> m_reports = [];

    private FakeGraphInstances? m_instances;
    private WorldViewGraphHost? m_runtime;

    // Boots the host with a camera row and an ink row whose graph reads the camera, compiled and installed.
    private (WorldViewGraphHost Host, FakeGraphInstances Instances) Start() {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile the installed graph."
        );
        Directory.CreateDirectory(path: m_directory);
        Write(name: "pass.hlsl", text: "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { output[id.xy] = 0; }");
        Write(name: "feed.hlsl", text: Convert(input: "feed"));
        Write(name: "other.hlsl", text: Convert(input: "other"));
        Write(name: "feed.graph.json", text: Graph(input: "feed"));
        Write(name: "other.graph.json", text: Graph(input: "other"));
        m_runtime = new WorldViewGraphHost(
            documentDirectory: m_directory,
            packager: new ShaderPackager(compiler: new ShaderCompiler(cacheDirectory: Path.Combine(
                path1: m_directory,
                path2: "cache"
            )))
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
        m_runtime.Reconcile(views: Views(ink: Row(source: "feed.graph.json", input: "feed")));
        PumpUntilInstalled();
        Assert.True(
            condition: m_instances.Installed.Contains(item: Ink),
            userMessage: string.Join(separator: "; ", values: [.. m_reports, (m_runtime.Entries[Ink].LastCompile?.Message ?? string.Empty)])
        );
        m_reports.Clear();

        return (m_runtime, m_instances);
    }
    private static string Convert(string input) => $$"""
        #include "{{input}}.interface.hlsli"

        [numthreads(8, 8, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
            image[id.xy] = {{input}}.SampleLevel({{input}}Sampler, float2(0.5, 0.5), 0.0);
        }
        """;
    private static string Graph(string input) => $$"""
        {
          "$schema": "puck.render.graph.v1",
          "name": "{{input}}",
          "resources": [
            { "name": "{{input}}", "kind": "Image", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 }, "initialization": "External" },
            { "name": "image", "kind": "Image", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } }
          ],
          "passes": [
            {
              "name": "convert",
              "source": "{{input}}.hlsl",
              "entryPoint": "main",
              "kind": "Compute",
              "inputs": [ { "name": "{{input}}" } ],
              "outputs": [ { "name": "image" } ]
            }
          ],
          "outputs": [ "image" ]
        }
        """;
    private static WorldViewGraph Row(string? source = null, string? input = null, string? package = null) => new(
        Inputs: ((input is null)
            ? null
            : [new WorldViewGraphInput(Instance: Camera, Resource: input)]),
        Name: Ink,
        Package: package,
        Source: source
    );
    private static WorldViewDefaults Views(WorldViewGraph ink) => new(Graphs: [
        new WorldViewGraph(
            Name: Camera,
            Source: "pass.hlsl"
        ),
        ink,
    ]);
    // Pumps the host until no compile is pending and the latest result is installed. The bound is liveness; it decides
    // nothing.
    private void PumpUntilInstalled() => Assert.True(condition: SpinWait.SpinUntil(
        condition: () => {
            m_runtime!.PumpWatches();

            return m_runtime.Entries.Values.All(predicate: static entry => !entry.IsCompiling);
        },
        timeout: TimeSpan.FromSeconds(value: 60)
    ));
    private void Write(string name, string text) => File.WriteAllText(
        contents: text,
        path: Path.Combine(
            path1: m_directory,
            path2: name
        )
    );

    public void Dispose() {
        m_runtime?.Dispose();
        m_instances?.Dispose();

        try {
            Directory.Delete(
                path: m_directory,
                recursive: true
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }
    }
    [Fact]
    public void ARowThatBecomesAPackageIsNotHandedItsInstalledGraph() {
        var (host, instances) = Start();
        var accepted = instances.Reconfigurations;

        host.Reconcile(views: Views(ink: Row(package: RenderGraphPackageCatalog.SdfWorld)));

        Assert.Equal(
            actual: instances.Reconfigurations,
            expected: (accepted + 1)
        );
        Assert.DoesNotContain(
            collection: m_reports,
            filter: static report => report.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "refused"
            )
        );
    }
    [Fact]
    public void ANewSourceDeclaringAnotherInputCompilesRatherThanRebindingTheOldGraph() {
        var (host, instances) = Start();
        var accepted = instances.Reconfigurations;
        var installs = instances.Installed.Count;

        host.Reconcile(views: Views(ink: Row(source: "other.graph.json", input: "other")));

        Assert.Equal(
            actual: instances.Reconfigurations,
            expected: (accepted + 1)
        );
        PumpUntilInstalled();
        Assert.Equal(
            actual: instances.Installed.Count,
            expected: (installs + 1)
        );
        Assert.DoesNotContain(
            collection: m_reports,
            filter: static report => report.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "refused"
            )
        );
    }
    [Fact]
    public void ARefusedReconfigurationIsTriedAgainOnTheNextCallAndReportedOnce() {
        var (host, instances) = Start();
        var accepted = instances.Reconfigurations;
        var views = Views(ink: Row(source: "feed.graph.json", input: "feed"));

        instances.RefuseNext = true;
        host.Reconcile(views: views);
        Assert.Equal(
            actual: instances.Reconfigurations,
            expected: accepted
        );
        Assert.Single(
            collection: m_reports,
            predicate: static report => report.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "refused"
            )
        );

        host.Reconcile(views: views);

        Assert.Equal(
            actual: instances.Reconfigurations,
            expected: (accepted + 1)
        );
        Assert.Single(
            collection: m_reports,
            predicate: static report => report.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "refused"
            )
        );
    }
}
