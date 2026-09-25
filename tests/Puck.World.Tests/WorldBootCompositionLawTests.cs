using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Testing;
using Puck.World.Machines;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: each presentation shape's service collection, built by the same
/// <see cref="WorldBootComposition.AddWorldBoot"/> a boot calls and resolved with no device created, carries what a GPU
/// run of that shape needs before its first frame: the offscreen shape answers every verb the <c>puck counters</c>
/// workload sends, and both the offscreen and the windowed shape register the persistent pipeline-cache store the
/// backend's device creation reads. The neutral GPU services a node records through resolve to a
/// <see cref="FakeGpuDevice"/>, and every other service that owns or brings up a device throws when resolved, so a law
/// that reached a device fails by name instead of creating one.
/// </summary>
public sealed class WorldBootCompositionLawTests {
    private const string WorkloadScript = "tests/Puck.Counters/counters.script.txt";
    private const string WorkloadWorld = "tests/Puck.Counters/counters.world.json";

    // The collector closes the workload's script with these two lines (WorldOffscreenLeg.Launch in Puck.Cli), so the
    // offscreen World receives them as well.
    private static readonly string[] CollectorClosingVerbs = [
        "wire.errors",
        "quit",
    ];

    private static HostApplicationBuilder ComposeBoot(WorldHostPresentation presentation) {
        var extensions = WorldBootComposition.ComposeExtensions(directories: []);
        var machineCatalog = WorldMachineCatalog.From(extensions: extensions);

        Assert.True(
            condition: WorldDefinitionLoader.TryResolve(
                catalog: machineCatalog,
                catalogFingerprint: WorldBootComposition.MachineCatalogFingerprint(machineCatalog: machineCatalog),
                explicitPath: Path.Combine(
                    path1: AuthoredGameFixtures.Root,
                    path2: WorkloadWorld
                ),
                failure: out var failure,
                source: out var source
            ),
            userMessage: failure
        );

        var builder = new HostApplicationBuilder(settings: new HostApplicationBuilderSettings { DisableDefaults = true });

        builder.Services.AddWorldBoot(inputs: new WorldBootInputs(
            Authenticator: new WorldAttestedAuthenticator(),
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
            Source: source
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
    // A neutral GPU service the fake stands in for: the device context, the compute bundle, and every device-bound
    // recorder, binding writer, factory and submitter a backend registers over its own device context.
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

            if (IsNeutralGpuService(type: serviceType, fake: fake)) {
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
    // The verbs the workload sends: the command word of every line its script runs, read from the script the collector
    // reads (blank lines and # comments are skipped, as the console skips them), then the collector's closing pair.
    private static IReadOnlyList<string> WorkloadVerbs() {
        var verbs = File.ReadAllLines(path: Path.Combine(
            path1: AuthoredGameFixtures.Root,
            path2: WorkloadScript
        ))
            .Select(selector: static line => line.Trim())
            .Where(predicate: static line => ((line.Length > 0) && !line.StartsWith(value: '#')))
            .Select(selector: static line => line.Split(separator: ' ', count: 2)[0])
            .Concat(second: CollectorClosingVerbs)
            .Distinct(comparer: StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            condition: (verbs.Length > CollectorClosingVerbs.Length),
            userMessage: $"{WorkloadScript} names no verb of its own."
        );

        return verbs;
    }
    // The law: every verb the workload sends that the shape's command registry, resolved as a boot resolves it, cannot
    // dispatch.
    private static IReadOnlyList<string> UnansweredWorkloadVerbs(HostApplicationBuilder builder) {
        using var host = builder.Build();
        var registry = host.Services.GetRequiredService<CommandRegistry>();

        return WorkloadVerbs().Where(predicate: verb => !registry.TryGetId(
            id: out _,
            name: verb
        )).ToArray();
    }
    private static int Remove(IServiceCollection services, Func<ServiceDescriptor, bool> match) {
        var removed = 0;

        for (var index = (services.Count - 1); (index >= 0); --index) {
            if (match(arg: services[index])) {
                services.RemoveAt(index: index);
                ++removed;
            }
        }

        return removed;
    }
    // The law: the shape registers the store both backends' device creation reads (sp.GetService, so an absent
    // registration silently keeps the pipeline cache in memory).
    private static bool RegistersPipelineCacheStore(IServiceCollection services) => services.Any(predicate: static descriptor => (descriptor.ServiceType == typeof(GpuPipelineCacheStore)));

    [Fact]
    public void TheOffscreenShapeAnswersEveryVerbTheCountersWorkloadSends() => Assert.Empty(collection: UnansweredWorkloadVerbs(builder: ComposeBoot(presentation: WorldHostPresentation.Offscreen)));
    [Fact]
    public void TheOffscreenVerbLawFailsWhenTheModuleOwningAWorkloadVerbIsMissing() {
        var builder = ComposeBoot(presentation: WorldHostPresentation.Offscreen);
        var removed = Remove(
            match: static descriptor => (
                (descriptor.ServiceType == typeof(ICommandModule)) &&
                (descriptor.ImplementationType?.Name == "WorldRenderLeverCommandModule")
            ),
            services: builder.Services
        );

        Assert.Equal(
            actual: removed,
            expected: 1
        );
        Assert.Equal(
            actual: Assert.Single(collection: UnansweredWorkloadVerbs(builder: builder)),
            expected: "world.cadence"
        );
    }
    [InlineData(WorldHostPresentation.Offscreen)]
    [InlineData(WorldHostPresentation.Windowed)]
    [Theory]
    public void EveryPresentationShapeRegistersThePipelineCacheStore(WorldHostPresentation presentation) => Assert.True(
        condition: RegistersPipelineCacheStore(services: ComposeBoot(presentation: presentation).Services),
        userMessage: $"the {presentation} shape registers no {nameof(GpuPipelineCacheStore)}, so its device keeps its pipeline cache in memory only."
    );
    [InlineData(WorldHostPresentation.Offscreen)]
    [InlineData(WorldHostPresentation.Windowed)]
    [Theory]
    public void ThePipelineCacheLawFailsWhenTheStoreIsNotRegistered(WorldHostPresentation presentation) {
        var services = ComposeBoot(presentation: presentation).Services;

        Assert.True(condition: (Remove(
            match: static descriptor => (descriptor.ServiceType == typeof(GpuPipelineCacheStore)),
            services: services
        ) > 0));
        Assert.False(condition: RegistersPipelineCacheStore(services: services));
    }
    [Fact]
    public void TheHeadlessShapeRegistersNoPipelineCacheStore() => Assert.False(condition: RegistersPipelineCacheStore(services: ComposeBoot(presentation: WorldHostPresentation.None).Services));
}
