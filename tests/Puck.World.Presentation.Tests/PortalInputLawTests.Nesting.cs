using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class PortalInputLawTests {
    // THE LAW: an ephemeral world viewing itself is reaped when the last root observing it disappears. Closing its
    // own session can retire it inside its settle, so a faster world must not consume the rest of its tick batch.
    [Fact]
    public void ReapingASelfObservingWorldStopsItsCurrentTickBatch() {
        using var files = new TemporaryDirectory(prefix: "puck-self-portal-files-");
        using var state = new TemporaryDirectory(prefix: "puck-self-portal-state-");
        var template = Fixtures.BuildDocument().Screens.Single();
        var screen = (template with { Index = 1, Source = new WorldScreenSource.Session(Destination: "b") });

        WorldDefinition ObservesB(WorldDefinition definition) => definition with {
            Destinations = [new WorldDestination(
                Durability: WorldDestinationDurability.Ephemeral,
                Name: SafeName.Parse(candidate: "b"),
                Reference: "b"
            )],
            References = [new WorldReference(Document: "b", Name: SafeName.Parse(candidate: "b"))],
            ScreensRaw = [template, screen],
        };

        var beyond = (ObservesB(definition: Observable(admits: true)) with {
            Simulation = new WorldSimulationDefaults(RateHz: 60),
        });

        File.WriteAllBytes(
            bytes: WorldDefinitionSerialization.Serialize(definition: beyond),
            path: files.PathOf(name: WorldDocumentName.DocumentFile(name: "b"))
        );

        var bootPath = files.PathOf(name: "boot.world.json");
        var definition = ObservesB(definition: Fixtures.BuildDocument());

        File.WriteAllBytes(bytes: WorldDefinitionSerialization.Serialize(definition: definition), path: bootPath);

        var (boot, bootState) = FileBackedRow(
            definition: WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: bootPath)),
            name: WorldInstanceHost.BootInstanceName,
            path: bootPath
        );

        using var disposeBootState = bootState;
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: state.RootPath)
        );

        host.AdmitBoot(row: boot);
        host.StepInstances(masterDeltaTicks: PortalStep);

        var name = host.ScreenSession(instanceName: WorldInstanceHost.BootInstanceName, screenIndex: 1)!.InstanceName!;

        Assert.Equal(expected: name, actual: host.ScreenSession(instanceName: name, screenIndex: 1)!.InstanceName);
        Assert.True(condition: host.TryGet(instance: out var observed, name: name));
        var before = observed!.CompletedTicks;

        boot.Server.EnqueueMutation(mutation: new WorldMutation.RemoveScreen(Index: 1, Principal: Principal.Console));
        boot.Server.Advance(stepTicks: PortalStep);
        host.SettleBootScreenSessions(stepped: true);
        Assert.Contains(expected: name, collection: host.Names);

        host.StepInstances(masterDeltaTicks: PortalStep);

        Assert.DoesNotContain(expected: name, collection: host.Names);
        Assert.True(condition: observed.Retired.IsCancellationRequested);
        Assert.Equal(expected: (before + 1UL), actual: observed.CompletedTicks);
    }
}
