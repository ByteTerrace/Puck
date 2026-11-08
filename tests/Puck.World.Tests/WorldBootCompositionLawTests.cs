using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.DirectX.Presentation;
using Puck.Vulkan.Presentation;
using Xunit;

namespace Puck.World.Tests;

[Collection(SceneProbeCollection.Name)]
public sealed class WorldBootCompositionLawTests {
    // A headless boot registers no pointer, cursor or presentation service, so every console module must activate
    // without one: the boot builds its command registry from these at start.
    [Fact]
    public void AHeadlessBootActivatesEveryCommandModule() => WithHeadlessRegistry(registry => {
        foreach (var line in (string[])["player.orbit", "player.steer"]) {
            var result = registry.Submit(line: line);
            Assert.False(condition: result.IsError, userMessage: result.Output);
        }
    });

    private static void WithHeadlessRegistry(Action<CommandRegistry> inspect) {
        using var scratch = new Puck.Testing.TemporaryDirectory(
            bestEffortDelete: true,
            prefix: "puck-headless-boot-"
        );
        var extensions = Puck.Abstractions.PuckExtensionSet.Compose(extensions: []);
        var definition = Fixtures.BuildDocument();
        // The host builder supplies what a boot reads from its host (logging, the application lifetime), as Program's does.
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(settings: new());

        builder.Services.AddLogging();
        builder.Services.AddWorldBoot(inputs: new WorldBootInputs(
            Authenticator: new InertAuthenticator(),
            Caches: new WorldCacheRoots(
                compilations: Path.Join(path1: scratch.RootPath, path2: "compilations"),
                compiledWorlds: Path.Join(path1: scratch.RootPath, path2: "compiled-worlds"),
                meshes: Path.Join(path1: scratch.RootPath, path2: "meshes")
            ),
            Extensions: extensions,
            HostSettings: WorldHostSettings.Resolve(
                backendOverride: null,
                defaults: definition.Host,
                directXAvailable: false,
                exitAfterSecondsOverride: null,
                heightOverride: null,
                presentationOverride: WorldHostPresentation.None,
                presentModeOverride: null,
                widthOverride: null
            ),
            MachineCatalog: Puck.World.Machines.WorldMachineCatalog.From(extensions: extensions),
            Source: new WorldDefinitionSource(
                Definition: definition,
                SourcePath: Path.Join(path1: scratch.RootPath, path2: "fixture.world.json")
            ),
            StateRoot: new Puck.World.Server.WorldStateRoot(path: Path.Join(path1: scratch.RootPath, path2: "state"))
        ));

        using var host = builder.Build();

        var registry = new Puck.Commands.CommandRegistry(modules: host.Services.GetServices<Puck.Commands.ICommandModule>());

        inspect(registry);
    }

    [Theory]
    [InlineData("world.interactions")]
    [InlineData("world.fields")]
    [InlineData("world.flock")]
    [InlineData("world.navigation")]
    [InlineData("world.explain")]
    public void CompiledDiagnosticsAndPixelMaterialsRequireOperator(string verb) => WithHeadlessRegistry(registry => {
        // Compiled diagnostics and GPU material inspection cannot be reconstructed from a recipient's document.
        Assert.Equal(CommandAudience.Operator, Assert.Single(registry.Definitions, command => command.Name == verb).Audience);

        var source = new TextCommandSource(registry);
        using var router = new InputRouter(bindings: new NoBindings(), registry: registry, principalResolver: new SeatPrincipals());
        var answer = CommandResult.None;
        var seat = source.CreateSeatSession(router: router, slot: 1, onResult: (_, result) => answer = result);
        seat.Enqueue(verb);
        source.Collect();
        Assert.True(answer.IsError);
        Assert.Contains("an operator verb", answer.Output);
        Assert.DoesNotContain("an operator verb", registry.Submit(verb).Output, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData("world.status", "sleeping ")]
    [InlineData("world.gravity", "last globalTargets=")]
    public void RecipientSummariesExcludeAuthorityWork(string verb, string operatorField) => WithHeadlessRegistry(registry => {
        var source = new TextCommandSource(registry);
        using var router = new InputRouter(bindings: new NoBindings(), registry: registry, principalResolver: new SeatPrincipals());
        var answer = CommandResult.None;
        var seat = source.CreateSeatSession(router: router, slot: 1, onResult: (_, result) => answer = result);
        seat.Enqueue(verb);
        source.Collect();
        Assert.False(answer.IsError, answer.Output);
        foreach (var authorityField in new[] { "sleeping ", "source ", "documents ", "last globalTargets=", "areaMatches=" }) {
            Assert.DoesNotContain(authorityField, answer.Output, StringComparison.Ordinal);
        }
        Assert.Contains(operatorField, registry.Submit(verb).Output, StringComparison.Ordinal);
    });

    [Fact]
    public void SearchAudienceRefusalsAreInTheCatalog() =>
        Assert.Contains(RefusalCatalog.All(), entry => entry.Id == nameof(RuleRefusal.SearchWidensAudience));

    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class SeatPrincipals : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Seat(slot);
    }
    // world.counters reads both backends alike: each backend's presenter registration carries its own
    // presentation.skipped source, registered without resolving a device or a presenter.
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    public void EachBackendRegistersItsPresentationCounters() {
        var services = new ServiceCollection();

        services.AddVulkanPresenter();
        services.AddDirectXPresenter();

        var names = services
            .Where(predicate: static descriptor => ((descriptor.ServiceType == typeof(Puck.Abstractions.Counting.IWorkCounterSource)) && (descriptor.ImplementationInstance is Puck.Abstractions.Presentation.PresentationWork)))
            .Select(selector: static descriptor => ((Puck.Abstractions.Counting.IWorkCounterSource)descriptor.ImplementationInstance!).Name)
            .Order(comparer: StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            actual: names,
            expected: ["presentation.directx", "presentation.vulkan"]
        );
    }
}
