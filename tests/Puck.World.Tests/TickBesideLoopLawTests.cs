using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The loop beside the tick: a host that goes on while its last due tick executes on the step thread, and collects it
/// later, steps the authority to the state a host that waits for every tick reaches, and the authority refuses the
/// loop's thread wherever it stands outside its boundary window.
/// </summary>
[Collection(SceneProbeCollection.Name)]
public sealed class TickBesideLoopLawTests {
    private const int Iterations = 48;
    private const int JoinIteration = 5;

    private static WorldDefinition OnDemandSeats() {
        var definition = Fixtures.BuildDocument();

        return definition with {
            PopulationRaw = definition.Population with {
                SeatActivationRaw = [SeatActivationPolicy.Eager, SeatActivationPolicy.OnDemand, SeatActivationPolicy.OnDemand, SeatActivationPolicy.OnDemand],
            },
        };
    }

    // Steps the fixture for a fixed count of host iterations, joining a second seat from a device press captured before
    // one of them, and answers the authority's state hash with the tick it stands at. A waiting host takes each tick
    // whole (FixedStepPump.TryStep); a beside host leaves each tick executing (FixedStepPump.Advance), does a frame's
    // worth of something else, and collects the tick wherever the schedule puts it: after that work, in its next step
    // section, or as the run ends.
    private static (ulong Tick, ulong Hash, bool Joined) Run(bool beside, int resumeEvery) {
        (ulong Tick, ulong Hash, bool Joined) result = default;

        WorldBootCompositionLawTests.WithHeadlessHost(OnDemandSeats(), services => {
            var server = services.GetRequiredService<WorldServer>();
            var roster = services.GetRequiredService<PlayerRoster>();
            var registry = services.GetRequiredService<CommandRegistry>();
            var simulation = services.GetRequiredService<IFixedStepSimulation>();
            var boundary = services.GetRequiredService<WorldLinkBoundary>();
            var device = InputDeviceId.FromKey("second-controller");

            // The device's remembered profile seats it in the second slot, so its press joins a seat of its own.
            _ = server.Profiles.Create(SafeName.Parse("blue"), "#4455EE", out var createReason);
            Assert.Empty(createReason);
            var preferred = server.Profiles.Create(SafeName.Parse("green"), "#44EE55", out createReason);
            Assert.NotNull(preferred);
            Assert.Empty(createReason);
            server.Profiles.RememberPreferredController(preferred, device);
            using var router = new InputRouter(bindings: new ConfirmBinding(), registry: registry, slotResolver: roster, principalResolver: roster);
            var pump = new FixedStepPump(simulation, router, registry, captureOriginTicks: 0, holdsClock: false);

            boundary.RegisterPump();
            try {
                for (var iteration = 0; (iteration < Iterations); iteration++) {
                    if (iteration == JoinIteration) {
                        roster.ObserveDeviceKind(device, InputDeviceKind.Gamepad);
                        _ = roster.ResolveSlot(device);
                        router.Capture(InputSignal.Press(ConfirmBinding.Source, device));
                    }

                    pump.BeginIteration();
                    if (!beside) {
                        Assert.True(pump.TryStep(Fixtures.StepTicks, Fixtures.StepTicks));

                        continue;
                    }

                    _ = pump.Advance(deltaTicks: Fixtures.StepTicks, maxFrameTicks: (Fixtures.StepTicks * 8UL), stepTicks: Fixtures.StepTicks);
                    // The frame's place: the loop's thread is outside its window, and the tick may be in flight.
                    Assert.Throws<InvalidOperationException>(() => _ = server.Population);
                    if ((resumeEvery > 0) && ((iteration % resumeEvery) == 0)) {
                        Thread.Sleep(millisecondsTimeout: 1);
                        _ = pump.Resume();
                    }
                }

                Assert.True(pump.Settle());
            } finally {
                boundary.UnregisterPump();
            }

            var tick = server.CompletedTick;

            result = (tick, WorldStateHashComposition.HashAuthoritative(server: server, tick: tick), roster.IsJoined(1) || roster.IsPending(1));
        });

        return result;
    }

    [Fact]
    public void AHostBesideItsTickReachesTheStateOfAHostThatWaitsForEveryTick() {
        var waiting = Run(beside: false, resumeEvery: 0);

        Assert.Equal(((ulong)Iterations), waiting.Tick);
        Assert.True(waiting.Joined);
        // Three schedules of the same input: collected only in the next step section, after every frame, and after
        // some frames.
        Assert.Equal(waiting, Run(beside: true, resumeEvery: 0));
        Assert.Equal(waiting, Run(beside: true, resumeEvery: 1));
        Assert.Equal(waiting, Run(beside: true, resumeEvery: 3));
    }

    [Fact]
    public void TheAuthorityRefusesTheLoopThreadOutsideItsWindowAndNoOtherThread() {
        WorldBootCompositionLawTests.WithHeadlessHost(Fixtures.BuildDocument(), services => {
            var server = services.GetRequiredService<WorldServer>();
            var boundary = services.GetRequiredService<WorldLinkBoundary>();

            // Composing the simulation admits the boot world to its host, which hands it the host's boundary.
            _ = services.GetRequiredService<IFixedStepSimulation>();
            Assert.Same(boundary, server.Boundary);

            // Before a loop registers, the host boots on this thread and nothing is refused.
            _ = server.Population;
            _ = server.Configuration;

            boundary.RegisterPump();
            try {
                Assert.Equal(WorldBoundaryRefusal.OutsideWindow, boundary.Refusal);
                Assert.Throws<InvalidOperationException>(() => _ = server.Population);
                Assert.Throws<InvalidOperationException>(() => _ = server.Configuration);
                Assert.Throws<InvalidOperationException>(() => _ = server.Grants);
                Assert.Throws<InvalidOperationException>(() => _ = server.GrantTable);
                Assert.Throws<InvalidOperationException>(() => _ = server.MutationBudget);
                Assert.Throws<InvalidOperationException>(() => _ = server.Addons);
                Assert.Throws<InvalidOperationException>(() => _ = server.Persistence);
                Assert.Throws<InvalidOperationException>(() => _ = server.CostReport);
                Assert.Throws<InvalidOperationException>(() => _ = server.RuleHazards);
                Assert.Throws<InvalidOperationException>(() => _ = server.Time);
                Assert.Throws<InvalidOperationException>(() => _ = server.InputHold);
                Assert.Throws<InvalidOperationException>(() => _ = server.ClockSeeds);
                Assert.Throws<InvalidOperationException>(() => _ = server.AnyAddonEverPumped);
                Assert.Throws<InvalidOperationException>(() => _ = server.AnyMachineEverPumped);
                Assert.Throws<InvalidOperationException>(() => _ = server.AnyScreenOpEverApplied);
                Assert.Throws<InvalidOperationException>(() => _ = server.NextInputTick);
                Assert.Throws<InvalidOperationException>(() => _ = server.DocumentVersion);
                Assert.Throws<InvalidOperationException>(() => server.ExecuteAuthorityOperation(() => { }));
                Assert.False(services.GetRequiredService<WorldEditorSeats>().IsReadOnly(slot: 0));

                // Another thread is not the loop's: the authority's gate orders it.
                var other = Task.Run(() => server.ExecuteAuthorityOperation(() => server.Population.Capacity));

                Assert.True(other.Wait(TimeSpan.FromSeconds(30)));
                Assert.True(other.Result > 0);

                boundary.Open(drain: false);
                try {
                    Assert.Equal(WorldBoundaryRefusal.None, boundary.Refusal);
                    _ = server.Population;
                    _ = server.NextInputTick;
                } finally {
                    boundary.Close();
                }

                Assert.Throws<InvalidOperationException>(() => _ = server.Population);
            } finally {
                boundary.UnregisterPump();
            }

            // The loop has ended: its thread tears the host down and reads freely again.
            _ = server.Population;
        });
    }

    [Fact]
    public void AControlPhaseWithdrawalReachesTheNextFrameBeforeTheInFlightTickCollects() {
        var basis = Fixtures.BuildDocument();
        var room = basis with {
            Admission = [new WorldAdmissionEntry(
                Disclosure: WorldDisclosureTier.Presentation,
                Domain: WorldAdmissionEntry.AnyAuthority,
                Grants: [new WorldAdmissionGrant(Budget: 64, Capability: WorldCapability.Observe, Subject: GrantSubject.All)],
                Mode: WorldAdmissionTrustMode.FederatedAuthority
            )],
        };
        var template = basis.Screens.Single();
        var bootDefinition = basis with {
            Destinations = [new WorldDestination(Durability: WorldDestinationDurability.Ephemeral, Name: SafeName.Parse("room"), Reference: "room")],
            References = [new WorldReference(Document: "room", Name: SafeName.Parse("room"))],
            ScreensRaw = [template with { Index = 0, Source = new WorldScreenSource.Session(Destination: "room") }],
        };

        WorldBootCompositionLawTests.WithHeadlessHost(bootDefinition, services => {
            var sourceDir = Path.GetDirectoryName(services.GetRequiredService<WorldDefinitionSource>().SourcePath)!;
            File.WriteAllBytes(Path.Join(sourceDir, WorldDocumentName.DocumentFile("room")), WorldDefinitionSerialization.Serialize(room));
            Assert.True(WorldPostBuildWiring.Install(services));

            var server = services.GetRequiredService<WorldServer>();
            var instances = services.GetRequiredService<WorldInstanceHost>();
            var feed = services.GetRequiredService<WorldPresentationFeed>();
            var roster = services.GetRequiredService<PlayerRoster>();
            var registry = services.GetRequiredService<CommandRegistry>();
            var simulation = services.GetRequiredService<IFixedStepSimulation>();
            var boundary = services.GetRequiredService<WorldLinkBoundary>();
            var revokeInControlPhase = false;
            WorldGrant observeGrant = default;
            WorldInstance? destination = null;
            var seatPrincipal = roster.PrincipalOf(slot: 0);
            var placementsSubject = GrantSubject.Section(section: WorldSection.Placements);
            var placementsGrant = Assert.Single(server.GrantRows(seatPrincipal),
                row => (row.Capability == WorldCapability.Mutate) && ((row.Subject == GrantSubject.All) || (row.Subject == placementsSubject)));

            using var router = new InputRouter(bindings: new ConfirmBinding(), registry: registry, slotResolver: roster, principalResolver: roster);
            var pump = new FixedStepPump(
                simulation: simulation,
                inputRouter: router,
                registry: registry,
                captureOriginTicks: 0,
                beforeStep: atBoundary => {
                    if (!atBoundary || !revokeInControlPhase) { return; }
                    revokeInControlPhase = false;
                    destination!.Server.Revoke(observeGrant, Principal.Console);
                    server.Revoke(placementsGrant, Principal.Console);
                },
                holdsClock: false
            );

            boundary.RegisterPump();
            try {
                pump.BeginIteration();
                Assert.True(pump.TryStep(Fixtures.StepTicks, Fixtures.StepTicks));

                boundary.Open(drain: false);
                try {
                    var hosted = instances.ScreenSession(WorldInstanceHost.BootInstanceName, 0)!;
                    Assert.True(instances.TryGet(hosted.InstanceName!, out destination));
                    observeGrant = Assert.Single(destination!.Server.GrantRows(hosted.Observation!.Session),
                        row => (row.Capability == WorldCapability.Observe) && (row.Subject == GrantSubject.All));
                } finally {
                    boundary.Close();
                }

                Assert.True(feed.Observations.Screens[(WorldInstanceHost.BootInstanceName, 0)].Observation!.CanObserve);
                Assert.True(feed.Tick.SeatMayEditPlacements(seat: 0));

                revokeInControlPhase = true;
                pump.BeginIteration();
                _ = pump.Advance(deltaTicks: Fixtures.StepTicks, maxFrameTicks: (Fixtures.StepTicks * 4UL), stepTicks: Fixtures.StepTicks);

                // Here stands the frame, beside any in-flight step: the control-phase window already closed and collected,
                // so both revocations are visible to the frame before the step settles.
                Assert.False(feed.Observations.Screens[(WorldInstanceHost.BootInstanceName, 0)].Observation!.CanObserve);
                Assert.False(feed.Tick.SeatMayEditPlacements(seat: 0));
                Assert.True(services.GetRequiredService<WorldEditorSeats>().IsReadOnly(slot: 0));
                Assert.True(pump.Settle());
            } finally {
                boundary.UnregisterPump();
            }
        });
    }

    private sealed class ConfirmBinding : IInputBindings {
        public const string Source = "gamepad.buttonSouth";
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) =>
            ((source == Source) ? [new CommandBinding(PlayerCommandNames.ConfirmCommand)] : null);
    }
}
