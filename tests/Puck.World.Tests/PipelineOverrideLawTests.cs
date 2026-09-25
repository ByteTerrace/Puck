using Puck.Commands;
using System.Text.Json;
using Puck.Shaders;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.Testing;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for per-instance pipeline parameter overrides through the server's one mutation door: a commit built the way
/// <c>pipeline.commit</c> builds it lands in its own row and survives a save and a reload, two instances of one source
/// keep their own values while the source keeps its defaults, and a commit refuses by name when the row's revision,
/// the source's content, or the instance it names is not the one the preview was based on. Every server reads the
/// same copy of the shipped ink pipeline, so no law compiles a shader or needs a GPU.
/// </summary>
public sealed partial class PipelineOverrideLawTests : IDisposable {
    private readonly string m_directory = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-pipeline-overrides-{Guid.NewGuid():N}"
    );
    private readonly List<WorldEditEcho> m_echoes = [];

    public PipelineOverrideLawTests() {
        Directory.CreateDirectory(path: m_directory);
        File.Copy(
            destFileName: SourcePath,
            sourceFileName: Path.Combine(
                path1: AuthoredGameFixtures.Root,
                path2: "src/Puck.World/Assets/pipelines/ink.graph.json"
            )
        );
    }

    private string SourcePath => Path.Combine(
        path1: m_directory,
        path2: "ink.graph.json"
    );

    private static WorldDefinition Document() {
        var definition = Fixtures.BuildDocument();

        return (definition with {
            ViewsRaw = (definition.Views with {
                Pipelines = [
                    new WorldViewPipeline(Name: "left", Source: "ink.graph.json"),
                    new WorldViewPipeline(Name: "right", Source: "ink.graph.json"),
                ],
            }),
        });
    }
    private static JsonElement Change(string json) {
        using var document = JsonDocument.Parse(json: json);

        return document.RootElement.Clone();
    }
    private static WorldViewPipeline Row(WorldFixture fixture, string name) =>
        WorldDefinitionRows.FindPipeline(
            name: name,
            pipelines: fixture.Server.Definition.Views.Pipelines
        )!;
    private static double Exposure(WorldViewPipeline row) => (((row.Overrides is { } overrides) && overrides.TryGetValue(key: "visualize", value: out var visualize))
        ? visualize.GetProperty(propertyName: "exposure").GetDouble()
        : double.NaN);
    private WorldFixture Server() {
        var fixture = Fixtures.FreshServer(definition: Document());

        fixture.Server.PipelineSources = new WorldPipelineSources(documentDirectory: m_directory);
        fixture.Server.EchoTap = m_echoes.Add;

        return fixture;
    }
    private ShaderPipelineSource Installed() {
        Assert.True(condition: ShaderPipelineSource.TryRead(
            name: "ink",
            path: SourcePath,
            reason: out var reason,
            source: out var source
        ), userMessage: reason);

        return source;
    }
    // The commit pipeline.commit builds for one instance previewing one visualize exposure, based on the row as the
    // server holds it now.
    private static WorldMutation.CommitViewPipeline Preview(WorldFixture fixture, string name, ShaderPipelineSource installed, double exposure) {
        var row = Row(
            fixture: fixture,
            name: name
        );

        Assert.True(condition: WorldPipelineRuntime.TryMergeOverride(
            change: Change(json: $"{{\"exposure\":{exposure.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}}}"),
            current: null,
            merged: out var visualize,
            reason: out _
        ));

        return WorldPipelineRuntime.BuildCommit(
            installed: installed,
            output: null,
            pending: new Dictionary<string, JsonElement> { ["visualize"] = visualize },
            principal: Principal.Console,
            revision: WorldDefinitionFingerprint.ComputePipeline(pipeline: row),
            row: row,
            timeScale: row.TimeScale
        );
    }
    // Applies one mutation and answers whether it was accepted; a refusal's echo is left in m_echoes for the caller.
    private bool Apply(WorldFixture fixture, WorldMutation mutation) {
        var before = m_echoes.Count;

        fixture.Server.EnqueueMutation(mutation: mutation);
        fixture.Step();

        var echo = Assert.Single(collection: m_echoes.Skip(count: before));

        return !echo.Rejected;
    }
    private void AssertLastRefusal(string refusal) => Assert.Contains(
        expectedSubstring: $"pipeline.overrides/{refusal}:",
        actualString: m_echoes[^1].Message
    );

    public void Dispose() {
        try {
            Directory.Delete(
                path: m_directory,
                recursive: true
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }
    }
    [Fact]
    public void ACommittedOverrideSurvivesSaveAndReloadWhileTheSharedSourceKeepsItsDefault() {
        using var fixture = Server();
        var sourceBytes = File.ReadAllBytes(path: SourcePath);
        var saved = Path.Combine(
            path1: m_directory,
            path2: "saved.world.json"
        );

        Assert.True(condition: Apply(
            fixture: fixture,
            mutation: Preview(
                exposure: 0.25,
                fixture: fixture,
                installed: Installed(),
                name: "left"
            )
        ));
        _ = WorldDefinitionSerialization.SavePreservingBasis(
            basisPath: out _,
            definition: fixture.Server.Definition,
            imports: out _,
            note: out _,
            path: saved
        );

        var reloaded = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: saved));

        Assert.Equal(
            actual: Exposure(row: WorldDefinitionRows.FindPipeline(name: "left", pipelines: reloaded.Views.Pipelines)!),
            expected: 0.25
        );
        Assert.Null(@object: WorldDefinitionRows.FindPipeline(name: "right", pipelines: reloaded.Views.Pipelines)!.Overrides);
        Assert.Equal(
            actual: File.ReadAllBytes(path: SourcePath),
            expected: sourceBytes
        );
    }
    [Fact]
    public void TwoInstancesSharingOneSourceKeepDistinctCommittedOverrides() {
        using var fixture = Server();
        var installed = Installed();

        Assert.True(condition: Apply(fixture: fixture, mutation: Preview(exposure: 0.5, fixture: fixture, installed: installed, name: "left")));
        Assert.True(condition: Apply(fixture: fixture, mutation: Preview(exposure: 2, fixture: fixture, installed: installed, name: "right")));
        Assert.Equal(
            actual: Exposure(row: Row(fixture: fixture, name: "left")),
            expected: 0.5
        );
        Assert.Equal(
            actual: Exposure(row: Row(fixture: fixture, name: "right")),
            expected: 2
        );
    }
    [Fact]
    public void ASourceEditedAfterItsGraphInstalledRefusesTheCommit() {
        using var fixture = Server();
        var installed = Installed();

        File.WriteAllText(
            contents: File.ReadAllText(path: SourcePath).Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: "Visualized pigment intensity, edited.",
                oldValue: "Visualized pigment intensity."
            ),
            path: SourcePath
        );
        Laws.RefusalWithControl(
            lawId: "pipeline.overrides.source-changed",
            controlOutcome: () => Apply(
                fixture: fixture,
                mutation: Preview(exposure: 0.5, fixture: fixture, installed: Installed(), name: "left")
            ),
            deniedOutcome: () => {
                var accepted = Apply(
                    fixture: fixture,
                    mutation: Preview(exposure: 0.5, fixture: fixture, installed: installed, name: "left")
                );

                AssertLastRefusal(refusal: nameof(WorldPipelineOverrideRefusal.SourceChanged));

                return accepted;
            }
        );
    }
    [Fact]
    public void ACommitBasedOnAStaleRevisionRefuses() {
        using var fixture = Server();
        var installed = Installed();
        var first = Preview(exposure: 0.5, fixture: fixture, installed: installed, name: "left");
        var second = Preview(exposure: 3, fixture: fixture, installed: installed, name: "left");

        Assert.True(condition: Apply(fixture: fixture, mutation: first));
        Laws.RefusalWithControl(
            lawId: "pipeline.overrides.revision-stale",
            controlOutcome: () => Apply(
                fixture: fixture,
                mutation: Preview(exposure: 3, fixture: fixture, installed: installed, name: "left")
            ),
            deniedOutcome: () => {
                var accepted = Apply(
                    fixture: fixture,
                    mutation: second
                );

                AssertLastRefusal(refusal: nameof(WorldPipelineOverrideRefusal.RevisionStale));

                return accepted;
            }
        );
        Assert.Equal(
            actual: Exposure(row: Row(fixture: fixture, name: "left")),
            expected: 3
        );
    }
    [Fact]
    public void APreviewIsNeverCommittedForAnotherInstance() {
        using var fixture = Server();
        var installed = Installed();

        Laws.RefusalWithControl(
            lawId: "pipeline.overrides.wrong-instance",
            controlOutcome: () => Apply(
                fixture: fixture,
                mutation: Preview(exposure: 0.5, fixture: fixture, installed: installed, name: "right")
            ),
            deniedOutcome: () => {
                var accepted = Apply(
                    fixture: fixture,
                    mutation: (Preview(exposure: 3, fixture: fixture, installed: installed, name: "left") with { Name = "right" })
                );

                AssertLastRefusal(refusal: nameof(WorldPipelineOverrideRefusal.RevisionStale));

                return accepted;
            }
        );
        Assert.Null(@object: Row(fixture: fixture, name: "left").Overrides);
        Assert.Equal(
            actual: Exposure(row: Row(fixture: fixture, name: "right")),
            expected: 0.5
        );
    }
    [Fact]
    public void AnOverrideOutsideItsFieldsRangeRefusesThroughEveryDoor() {
        using var fixture = Server();

        Laws.RefusalWithControl(
            lawId: "pipeline.overrides.commit-unbound",
            controlOutcome: () => Apply(fixture: fixture, mutation: Preview(exposure: 4, fixture: fixture, installed: Installed(), name: "left")),
            deniedOutcome: () => {
                var accepted = Apply(fixture: fixture, mutation: Preview(exposure: 4.5, fixture: fixture, installed: Installed(), name: "left"));

                AssertLastRefusal(refusal: nameof(WorldPipelineOverrideRefusal.OverrideUnbound));

                return accepted;
            }
        );
        Laws.RefusalWithControl(
            lawId: "pipeline.overrides.upsert-unbound",
            controlOutcome: () => Apply(fixture: fixture, mutation: new WorldMutation.UpsertViewPipeline(
                Pipeline: (Row(fixture: fixture, name: "right") with { Overrides = new Dictionary<string, JsonElement> { ["visualize"] = Change(json: "{\"exposure\":1}") } }),
                Principal: Principal.Console
            )),
            deniedOutcome: () => {
                var accepted = Apply(fixture: fixture, mutation: new WorldMutation.UpsertViewPipeline(
                    Pipeline: (Row(fixture: fixture, name: "right") with { Overrides = new Dictionary<string, JsonElement> { ["visualize"] = Change(json: "{\"gain\":1}") } }),
                    Principal: Principal.Console
                ));

                AssertLastRefusal(refusal: nameof(WorldPipelineOverrideRefusal.OverrideUnbound));

                return accepted;
            }
        );
    }
    [Fact]
    public void AMergedPreviewReplacesFieldByFieldAndNullRestoresTheDefault() {
        Assert.True(condition: WorldPipelineRuntime.TryMergeOverride(
            change: Change(json: "{\"b\":3,\"a\":null}"),
            current: Change(json: "{\"a\":1,\"c\":[1,2]}"),
            merged: out var merged,
            reason: out _
        ));
        Assert.Equal(
            actual: merged.GetRawText(),
            expected: "{\"b\":3,\"c\":[1,2]}"
        );
        Assert.False(condition: WorldPipelineRuntime.TryMergeOverride(
            change: Change(json: "[1]"),
            current: null,
            merged: out _,
            reason: out _
        ));
    }
    // Rendering is presentation: a host that reconciles, compiles and previews pipeline instances beside the server
    // and one that renders nothing accept and refuse the same commits and end on the same document.
    [Fact]
    public void RenderingAndHeadlessHostsAcceptTheSameCommits() {
        (IReadOnlyList<(bool Rejected, string Message)> Verdicts, byte[] Document) Run(bool rendered) {
            m_echoes.Clear();

            using var fixture = Server();
            using var runtime = new WorldPipelineRuntime(
                documentDirectory: m_directory,
                packager: new ShaderPackager(compiler: new ShaderCompiler(
                    cacheDirectory: Path.Combine(
                        path1: m_directory,
                        path2: "cache"
                    ),
                    toolchainDirectory: Path.Combine(
                        path1: m_directory,
                        path2: "no-tools"
                    )
                ))
            ) {
                CreateNode = static name => new ShaderPipelineRenderNode(
                    deviceContext: new RefusingGpuDevice(),
                    height: 4,
                    hostsOnDirectX: false,
                    name: name,
                    width: 4
                ),
            };
            var installed = Installed();
            var stale = Preview(exposure: 1.5, fixture: fixture, installed: installed, name: "left");
            var mutations = new Func<WorldMutation>[] {
                () => Preview(exposure: 0.5, fixture: fixture, installed: installed, name: "left"),
                () => stale,
                () => Preview(exposure: 9, fixture: fixture, installed: installed, name: "right"),
                () => Preview(exposure: 2, fixture: fixture, installed: installed, name: "right"),
            };

            foreach (var mutation in mutations) {
                if (rendered) {
                    runtime.Reconcile(rows: fixture.Server.Definition.Views.Pipelines);
                    runtime.PumpWatches();
                    foreach (var entry in runtime.Entries.Values) {
                        _ = entry.TrySetOverride(
                            change: Change(json: "{\"exposure\":3}"),
                            pass: "visualize",
                            reason: out _
                        );
                    }
                }

                fixture.Server.EnqueueMutation(mutation: mutation());
                fixture.Step();
            }

            return ([.. m_echoes.Select(selector: static echo => (echo.Rejected, echo.Message))], fixture.DefinitionBytes());
        }

        var headless = Run(rendered: false);
        var rendered = Run(rendered: true);

        Assert.Equal(
            actual: rendered.Verdicts,
            expected: headless.Verdicts
        );
        Assert.Equal(
            actual: headless.Verdicts.Select(selector: static verdict => verdict.Rejected),
            expected: [false, true, true, false]
        );
        Assert.Equal(
            actual: rendered.Document,
            expected: headless.Document
        );
    }
}
