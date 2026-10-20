using Microsoft.Extensions.DependencyInjection;
using Puck.DirectX.Presentation;
using Puck.Vulkan.Presentation;
using Xunit;

namespace Puck.World.Tests;

[Collection(SceneProbeCollection.Name)]
public sealed class WorldBootCompositionLawTests {
    // A headless boot registers no pointer, cursor or presentation service, so every console module must activate
    // without one: the boot builds its command registry from these at start.
    [Fact]
    public void AHeadlessBootActivatesEveryCommandModule() {
        using var scratch = new Puck.Testing.TemporaryDirectory(
            bestEffortDelete: true,
            prefix: "puck-headless-boot-"
        );
        var extensions = Puck.Abstractions.PuckExtensionSet.Compose(extensions: []);
        var definition = Fixtures.BuildDocument();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddWorldBoot(inputs: new WorldBootInputs(
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

        using var provider = services.BuildServiceProvider();

        Assert.NotEmpty(collection: provider.GetServices<Puck.Commands.ICommandModule>());
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
