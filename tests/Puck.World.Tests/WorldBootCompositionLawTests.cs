using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Pacing;
using Puck.Commands;
using Puck.DirectX.Presentation;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Vulkan.Presentation;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
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

    private static void WithHeadlessRegistry(Action<CommandRegistry> inspect) => WithHeadlessHost(Fixtures.BuildDocument(), services =>
        inspect(new CommandRegistry(modules: services.GetServices<ICommandModule>())));

    private static void WithHeadlessHost(WorldDefinition definition, Action<IServiceProvider> inspect) {
        using var scratch = new Puck.Testing.TemporaryDirectory(
            bestEffortDelete: true,
            prefix: "puck-headless-boot-"
        );
        var extensions = Puck.Abstractions.PuckExtensionSet.Compose(extensions: []);
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

        inspect(host.Services);
    }

    [Fact]
    public void ControllerCapturedDuringHeldTickJoinsItsPreferredSeatAtTheBoundary() {
        var definition = Fixtures.BuildDocument();
        WithHeadlessHost(definition with {
            PopulationRaw = definition.Population with {
                SeatActivationRaw = [SeatActivationPolicy.Eager, SeatActivationPolicy.OnDemand, SeatActivationPolicy.OnDemand, SeatActivationPolicy.OnDemand],
            },
        }, services => {
            var server = services.GetRequiredService<WorldServer>();
            var device = InputDeviceId.FromKey("preferred-controller");
            _ = server.Profiles.Create(SafeName.Parse("blue"), "#4455EE", out var createReason);
            Assert.Empty(createReason);
            var preferred = server.Profiles.Create(SafeName.Parse("green"), "#44EE55", out createReason);
            Assert.NotNull(preferred);
            server.Profiles.RememberPreferredController(preferred, device);

            var roster = services.GetRequiredService<PlayerRoster>();
            var link = services.GetRequiredService<LoopbackTransport>();
            var registry = services.GetRequiredService<CommandRegistry>();
            using var router = new InputRouter(bindings: new ConfirmBinding(), registry: registry, slotResolver: roster, principalResolver: roster);
            var simulation = new HoldFirstStep(services.GetRequiredService<IFixedStepSimulation>());
            var boundary = services.GetRequiredService<WorldLinkBoundary>();
            var pump = new FixedStepPump(simulation, router, registry, captureOriginTicks: 0, holdsClock: false);
            var applied = new List<WorldCommand>();
            link.CommandTap = applied.Add;
            var earlier = new WorldCommand.Stop(EntityIndex: 0, Principal: Principal.Seat(0));
            var later = new WorldCommand.Stop(EntityIndex: 0, Principal: Principal.Console);

            boundary.RegisterPump();
            try {
                pump.BeginIteration();
                Assert.False(pump.TryStep(Fixtures.StepTicks, Fixtures.StepTicks));
                Assert.True(pump.StepPending);

                roster.ObserveDeviceKind(device, InputDeviceKind.Gamepad);
                Assert.Equal(1, roster.ResolveSlot(device));
                router.Capture(InputSignal.Press(ConfirmBinding.Source, device));
                link.SubmitCommand(earlier);
                link.SubmitCommand(later);
                Assert.Throws<InvalidOperationException>(() => link.Query(new WorldQuery.ProfileCatalog(), Principal.Console, _ => { }));
                Assert.Throws<InvalidOperationException>(() => link.SubmitSession(new SessionRequest.Leave(Principal.Console, 0), _ => { }));
                Assert.Empty(applied);
                Assert.False(roster.IsJoined(1));

                pump.BeginIteration();
                Assert.True(pump.TryStep(Fixtures.StepTicks, Fixtures.StepTicks));
                Assert.Empty(applied);
                Assert.False(roster.IsJoined(1));

                pump.BeginIteration();
                Assert.Equal(new[] { earlier, later }, applied);
                Assert.True(pump.TryStep(Fixtures.StepTicks, Fixtures.StepTicks));
                Assert.Equal(1, roster.DeviceSlot(device));
                Assert.True(roster.IsPending(1));
                Assert.Equal(preferred.Id, roster.ProfileAt(1)?.Id);
                Assert.NotNull(server.Body(1));
            } finally {
                boundary.UnregisterPump();
            }
        });
    }

    private sealed class ConfirmBinding : IInputBindings {
        public const string Source = "gamepad.buttonSouth";
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) =>
            source == Source ? [new CommandBinding(PlayerCommandNames.ConfirmCommand)] : null;
    }

    private sealed class HoldFirstStep(IFixedStepSimulation inner) : IFixedStepSimulation {
        private bool m_entered;
        public IHostTickBoundary? TickBoundary => inner.TickBoundary;
        public uint RatePerSecond => inner.RatePerSecond;
        public bool AwaitsFrame => inner.AwaitsFrame;
        public bool HoldsClock(ulong withheldTicks) => inner.HoldsClock(withheldTicks);
        public void SettleOwedFrames() => inner.SettleOwedFrames();
        public void Step(in FixedStepContext context, in CommandSnapshot commands) => inner.Step(in context, in commands);
        public bool TryStep(in FixedStepContext context, in CommandSnapshot commands) {
            if (m_entered) { return inner.TryStep(in context, in commands); }
            m_entered = true;
            return false;
        }
    }

    [Theory]
    [InlineData("world.interactions", CommandAudience.Operator)]
    [InlineData("world.fields", CommandAudience.Operator)]
    [InlineData("world.flock", CommandAudience.Operator)]
    [InlineData("world.navigation", CommandAudience.Operator)]
    [InlineData("world.explain", CommandAudience.Host)]
    [InlineData("pipeline.load", CommandAudience.Host)]
    [InlineData("world.transfer", CommandAudience.Host)]
    public void CompiledDiagnosticsAndPixelMaterialsRequireTheirAudience(string verb, CommandAudience audience) => WithHeadlessRegistry(registry => {
        // Compiled diagnostics and GPU material inspection cannot be reconstructed from a recipient's document.
        Assert.Equal(audience, Assert.Single(registry.Definitions, command => command.Name == verb).Audience);

        var source = new TextCommandSource(registry);
        using var router = new InputRouter(bindings: new NoBindings(), registry: registry, principalResolver: new SeatPrincipals());
        var answer = CommandResult.None;
        var seat = source.CreateSeatSession(router: router, slot: 1, onResult: (_, result) => answer = result, onSettled: (_, result) => answer = result);
        seat.Enqueue(verb);
        source.Collect();
        var tick = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in tick);
        source.Collect();
        Assert.True(answer.IsError, answer.Output);
        Assert.Contains(audience.Describe(), answer.Output);
        Assert.DoesNotContain(audience.Describe(), registry.Submit(verb).Output, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData("player.cycle")]
    [InlineData("player.claim")]
    public void DeviceGesturesNameTheHostKeyboardOnlyForTheConsole(string verb) => WithHeadlessRegistry(registry => {
        Assert.Equal(CommandAudience.Local, Assert.Single(registry.Definitions, command => command.Name == verb).Audience);

        var source = new TextCommandSource(registry);
        using var router = new InputRouter(bindings: new NoBindings(), registry: registry, principalResolver: new SeatPrincipals());
        var answer = CommandResult.None;
        var seat = source.CreateSeatSession(router: router, slot: 1, onResult: (_, result) => answer = result, onSettled: (_, result) => answer = result);
        seat.Enqueue(verb);
        source.Collect();
        var tick = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in tick);
        source.Collect();
        Assert.True(answer.IsError, answer.Output);
        Assert.Contains("moves its own device by pressing it", answer.Output);
        Assert.DoesNotContain("moves its own device by pressing it", registry.Submit(verb).Output, StringComparison.Ordinal);
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
