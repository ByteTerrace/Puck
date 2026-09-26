using System.Text.Json;
using Puck.Commands;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>views.post</c> is edited live. A views section whose post passes moved recomposes the
/// synthesized root on the running host, with no reboot: a pass added, the passes reordered and a pass removed each
/// leave the root running exactly the rows the section names, in its order. On the server a <c>views.post</c> edit is a
/// <see cref="WorldMutation.SetViewPost"/>, and one whose config does not bind to its package is refused by the validator
/// naming the row, leaving the running passes as they were.
/// </summary>
public sealed class WorldViewPostLiveLawTests : IDisposable {
    private const string Grain = "grain";
    private const string Heavy = "heavy-grain";

    private readonly TemporaryDirectory m_directory = new(prefix: "puck-world-post-live-");

    private static JsonElement Json(string text) => JsonDocument.Parse(json: text).RootElement.Clone();
    private static WorldViewPostPass Pass(string name, string config = """{"intensity":0.2}""") => new(
        Config: Json(text: config),
        Name: name,
        Package: RenderGraphPackageCatalog.SdfFilmGrain
    );
    // The post passes the synthesized root runs, in its order; the root's own view passes run other packages.
    private static string[] RootPasses(WorldViewGraphHost host) => [.. (host.Synthesized?.Plan?.Definition.Packages ?? [])
        .Where(predicate: static pass => string.Equals(
            a: pass.Package,
            b: RenderGraphPackageCatalog.SdfFilmGrain,
            comparisonType: StringComparison.Ordinal
        ))
        .Select(selector: static pass => pass.Name)];

    public void Dispose() => m_directory.Dispose();
    [Fact]
    public void AddingReorderingAndRemovingPostRowsRecomposesTheRunningRoot() {
        using var host = new WorldViewGraphHost(
            documentDirectory: m_directory.RootPath,
            packager: new ShaderPackager(compiler: new ShaderCompiler(cacheDirectory: m_directory.PathOf(name: "cache")))
        );
        using var instances = FakeGraphInstances.Attach(
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

        host.Reconcile(views: new WorldViewDefaults());
        Assert.Empty(collection: RootPasses(host: host));

        // Each step is a new views section, as a live edit delivers one; the host and its runtime stay the same.
        var steps = new (IReadOnlyList<WorldViewPostPass> Post, string[] Expected)[] {
            ([Pass(name: Grain)], [Grain]),
            ([Pass(name: Grain), Pass(config: """{"intensity":0.6}""", name: Heavy)], [Grain, Heavy]),
            ([Pass(config: """{"intensity":0.6}""", name: Heavy), Pass(name: Grain)], [Heavy, Grain]),
            ([Pass(name: Grain)], [Grain]),
            ([], []),
        };

        foreach (var (post, expected) in steps) {
            var accepted = instances.Reconfigurations;

            host.Reconcile(views: new WorldViewDefaults(Post: post));

            Assert.Equal(
                actual: RootPasses(host: host),
                expected: expected
            );
            Assert.Equal(
                actual: instances.Reconfigurations,
                expected: (accepted + 1)
            );
        }
    }
    [Fact]
    public void ALiveViewsPostEditLandsAndAConfigThatDoesNotBindIsRefusedNamingTheRow() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument());
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => {
            if (echo.Rejected) {
                refusals.Add(item: echo.Message);
            }
        };
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetViewPost(
            Post: [Pass(name: Grain), Pass(name: Heavy)],
            Principal: Principal.Console
        ));
        Assert.True(condition: fixture.Server.DrainAdministrative());
        Assert.Equal(
            actual: fixture.Server.Definition.Views.Post!.Select(selector: static pass => pass.Name),
            expected: [Grain, Heavy]
        );

        Assert.Empty(collection: refusals);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetViewPost(
            Post: [Pass(name: Heavy), Pass(config: """{"grit":1}""", name: Grain)],
            Principal: Principal.Console
        ));
        _ = fixture.Server.DrainAdministrative();

        Assert.Contains(
            actualString: Assert.Single(collection: refusals),
            expectedSubstring: $"views.post[1].config of post pass '{Grain}' does not bind to package '{RenderGraphPackageCatalog.SdfFilmGrain}'"
        );
        Assert.Equal(
            actual: fixture.Server.Definition.Views.Post!.Select(selector: static pass => pass.Name),
            expected: [Grain, Heavy]
        );

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetViewPost(
            Post: [],
            Principal: Principal.Console
        ));
        Assert.True(condition: fixture.Server.DrainAdministrative());
        Assert.Null(@object: fixture.Server.Definition.Views.Post);
    }
}
