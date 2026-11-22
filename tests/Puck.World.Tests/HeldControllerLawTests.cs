using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Waiting;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(SceneProbeCollection.Name)]
public sealed class HeldControllerLawTests {
    [Fact]
    public void ControllerCapturedDuringHeldTickJoinsItsPreferredSeatAtTheBoundary() {
        var definition = Fixtures.BuildDocument();
        WorldBootCompositionLawTests.WithHeadlessHost(definition with {
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
            Assert.Empty(createReason);
            server.Profiles.RememberPreferredController(preferred, device);

            var roster = services.GetRequiredService<PlayerRoster>();
            var link = services.GetRequiredService<LoopbackTransport>();
            var registry = services.GetRequiredService<CommandRegistry>();
            using var router = new InputRouter(bindings: new ConfirmBinding(), registry: registry, slotResolver: roster, principalResolver: roster);
            var simulation = services.GetRequiredService<IFixedStepSimulation>();
            var machines = (HeldMachineAdvance)services.GetRequiredService<IWorldMachineHost>();
            var boundary = services.GetRequiredService<WorldLinkBoundary>();
            var pump = new FixedStepPump(simulation, router, registry, captureOriginTicks: 0, holdsClock: false);
            var applied = new List<WorldCommand.Stop>();
            link.CommandTap = command => {
                if (command is WorldCommand.Stop stop) { applied.Add(stop); }
            };
            var earlier = new WorldCommand.Stop(EntityIndex: 0, Principal: Principal.Seat(0));
            var later = new WorldCommand.Stop(EntityIndex: 0, Principal: Principal.Console);

            boundary.RegisterPump();
            try {
                pump.BeginIteration();
                Assert.False(pump.TryStep(Fixtures.StepTicks, Fixtures.StepTicks));
                Assert.True(pump.StepPending);
                Assert.Throws<InvalidOperationException>(() => _ = server.Population);

                roster.ObserveDeviceKind(device, InputDeviceKind.Gamepad);
                Assert.Equal(1, roster.ResolveSlot(device));
                router.Capture(InputSignal.Press(ConfirmBinding.Source, device));
                link.SubmitCommand(earlier);
                link.SubmitCommand(later);
                Assert.Throws<InvalidOperationException>(() => link.Query(new WorldQuery.ProfileCatalog(), Principal.Console, _ => { }));
                Assert.Throws<InvalidOperationException>(() => link.SubmitSession(new SessionRequest.Leave(Principal.Console, 0), _ => { }));
                Assert.Empty(applied);
                Assert.False(roster.IsJoined(1));

                machines.Hold = false;
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
            } finally {
                boundary.UnregisterPump();
            }
            Assert.NotNull(server.Body(1));
        }, services => services.AddSingleton<IWorldMachineHost>(provider => {
            var machines = DispatchProxy.Create<IWorldMachineHost, HeldMachineAdvance>();
            ((HeldMachineAdvance)machines).Inner = provider.GetRequiredService<WorldMachineHost>();
            return machines;
        }));
    }

    private sealed class ConfirmBinding : IInputBindings {
        public const string Source = "gamepad.buttonSouth";
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) =>
            source == Source ? [new CommandBinding(PlayerCommandNames.ConfirmCommand)] : null;
    }

    public class HeldMachineAdvance : DispatchProxy {
        public IWorldMachineHost Inner { get; set; } = null!;
        public bool Hold { get; set; } = true;

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Hold && method!.Name == nameof(IWorldMachineHost.TryAdvance)
                ? new WaitReason("machines", "held for controller input")
                : method!.Invoke(Inner, args);
    }
}
