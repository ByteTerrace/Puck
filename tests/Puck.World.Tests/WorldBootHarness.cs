using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;
using Puck.World.Machines;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A presentation shape's service collection, built by the same <see cref="WorldBootComposition.AddWorldBoot"/> a
/// boot calls and sealed so that it resolves with no device created: the neutral GPU services a node records through
/// resolve to a <see cref="FakeGpuDevice"/>, and every other service that owns or brings up a device throws when
/// resolved, so a law that reached a device fails by name instead of creating one.</summary>
internal static class WorldBootHarness {
    /// <summary>Composes a boot of a world under a state root of its own and seals its device.</summary>
    /// <param name="stateDirectory">The root the boot's per-run files and device caches resolve under.</param>
    /// <param name="presentation">The presentation shape.</param>
    /// <param name="world">The world document, relative to the repository root.</param>
    /// <param name="edit">Rewrites the resolved definition before the boot composes it, or <see langword="null"/>; a
    /// rewritten definition drops the loader's admission, so the server admits it.</param>
    /// <returns>The builder, whose services a law may still edit before it builds the host.</returns>
    public static HostApplicationBuilder Compose(TemporaryDirectory stateDirectory, WorldHostPresentation presentation, string world, Func<WorldDefinition, WorldDefinition>? edit = null) {
        var extensions = WorldBootComposition.ComposeExtensions(directories: []);
        var machineCatalog = WorldMachineCatalog.From(extensions: extensions);

        Assert.True(
            condition: WorldDefinitionLoader.TryResolve(
                catalog: machineCatalog,
                catalogFingerprint: WorldBootComposition.MachineCatalogFingerprint(machineCatalog: machineCatalog),
                explicitPath: Path.Combine(
                    path1: AuthoredGameFixtures.Root,
                    path2: world
                ),
                failure: out var failure,
                source: out var source
            ),
            userMessage: failure
        );

        if (edit is not null) {
            source = (source with { Admission = null, Definition = edit(arg: source.Definition) });
        }

        var builder = new HostApplicationBuilder(settings: new HostApplicationBuilderSettings { DisableDefaults = true });

        builder.Services.AddWorldBoot(inputs: new WorldBootInputs(
            Authenticator: new WorldAttestedAuthenticator(),
            Caches: new WorldCacheRoots(
                bakes: stateDirectory.PathOf(name: "bakes"),
                compilations: stateDirectory.PathOf(name: "compilations"),
                compiledWorlds: stateDirectory.PathOf(name: "compiled-worlds")
            ),
            Extensions: extensions,
            HostSettings: WorldHostSettings.Resolve(
                backendOverride: null,
                defaults: source.Definition.Host,
                directXAvailable: OperatingSystem.IsWindowsVersionAtLeast(
                    major: 10,
                    minor: 0,
                    build: 10240
                ),
                exitAfterSecondsOverride: null,
                heightOverride: null,
                presentationOverride: presentation,
                presentModeOverride: null,
                widthOverride: null
            ),
            MachineCatalog: machineCatalog,
            Source: source,
            StateRoot: new WorldStateRoot(path: stateDirectory.RootPath)
        ));
        SealDevice(services: builder.Services);

        return builder;
    }

    // The backend assemblies, and the neutral services whose resolution brings a device up: the presenter, the root
    // render node, and the offscreen shape's device activation (internal to Puck.World, so named).
    private static bool BringsUpDevice(Type type) {
        var assembly = (type.Assembly.GetName().Name ?? string.Empty);

        return (
            assembly.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck.DirectX") ||
            assembly.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck.Vulkan") ||
            (type == typeof(IRenderNode)) ||
            (type == typeof(ISurfacePresenter)) ||
            (type.Name == "WorldOffscreenGpuActivation")
        );
    }
    // A neutral GPU service the fake stands in for: the device context, whose services the fake also is, and the
    // optional surface export a backend registers beside it.
    private static bool IsNeutralGpuService(Type type, FakeGpuDevice fake) => (
        type.IsInterface &&
        string.Equals(a: type.Namespace, b: typeof(IGpuDeviceContext).Namespace, comparisonType: StringComparison.Ordinal) &&
        type.IsInstanceOfType(o: fake)
    );
    // Replaces the neutral GPU services with one device-free fake and every registration that brings up a device with
    // one that throws when resolved.
    private static void SealDevice(IServiceCollection services) {
        var fake = new FakeGpuDevice(reportVersion: 0);

        for (var index = 0; (index < services.Count); ++index) {
            var descriptor = services[index];
            var serviceType = descriptor.ServiceType;

            if (descriptor.IsKeyedService || serviceType.IsGenericTypeDefinition) {
                Assert.False(
                    condition: BringsUpDevice(type: serviceType),
                    userMessage: $"{serviceType.FullName} is registered in a shape the device seal does not cover."
                );

                continue;
            }

            if (IsNeutralGpuService(fake: fake, type: serviceType)) {
                services[index] = new ServiceDescriptor(
                    instance: fake,
                    serviceType: serviceType
                );
            } else if (BringsUpDevice(type: serviceType)) {
                var reason = $"a composition law resolved {serviceType.FullName}, which brings up a GPU device";

                services[index] = new ServiceDescriptor(
                    factory: _ => throw new InvalidOperationException(message: reason),
                    lifetime: descriptor.Lifetime,
                    serviceType: serviceType
                );
            }
        }
    }
}
