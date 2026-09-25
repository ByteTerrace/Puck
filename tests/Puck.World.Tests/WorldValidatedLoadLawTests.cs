using Puck.Storage;
using Puck.Testing;
using Puck.World.Machines;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// One load of a world is one whole-document validation and one rule compilation, and installing it moves no state
/// hash: a desktop boot, an instance start, a hosted activation read, and a checkpoint restore each pay once, even
/// when the document's rows settle on the way in. Each law counts through its own attributed
/// <see cref="WorldBootWork"/> ledger, so a class loading a document beside it moves nothing it reads.
/// </summary>
public sealed class WorldValidatedLoadLawTests : IDisposable {
    private readonly WorldBootWork m_work = new();
    // Every FreshProfiles call's own scratch directory, released together when the test instance is (a fresh
    // instance per [Fact], xUnit's default).
    private readonly List<IDisposable> m_scratch = [];

    private (long Validations, long Compilations) Counts() => (
        m_work.Read(kind: WorldBootWork.Validations),
        m_work.Read(kind: WorldBootWork.RuleCompilations)
    );
    // An advancing row carries no clock as authored, so loading it settles one and the admitted document is a
    // different object from the parsed one.
    private static WorldDefinition SettlingDocument() {
        var document = Fixtures.BuildDocument();

        return document.WithWorldState(rows: [
            .. document.AuthoredState,
            new WorldStateRow(Name: CellName.Parse(candidate: "settlingClock"), Kind: CellKind.Int,
                Advance: new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 1),
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 0L))]),
        ]);
    }
    private WorldOwnedWorlds FreshProfiles(WorldDefinition definition) {
        var profilesDirectory = new TemporaryDirectory(prefix: "puck-validated-load-");

        m_scratch.Add(item: profilesDirectory);

        return new(
            directory: profilesDirectory.RootPath,
            machineId: Guid.NewGuid(),
            template: definition
        );
    }

    /// <inheritdoc/>
    public void Dispose() {
        foreach (var disposable in m_scratch) {
            disposable.Dispose();
        }
    }
    [Fact]
    public void ADesktopBootInstallsItsReceiptWithoutValidatingOrCompilingAgain() {
        using var attribution = WorldBootWork.Attribute(work: m_work);

        var catalog = new WorldMachineCatalog(engines: []);

        Assert.True(condition: WorldDefinitionLoader.TryLoadForAdmission(
            WorldDefinitionSerialization.Serialize(definition: SettlingDocument()), "boot",
            out var admission, out var reason, catalog: catalog), userMessage: reason);
        Assert.NotNull(@object: admission);

        var definition = admission.Definition;

        using var machines = new WorldMachineHost(screens: definition.Screens, catalog: catalog);
        var before = Counts();
        var server = new WorldServer(definition, new WorldPopulation(definition: definition), FreshProfiles(definition: definition),
            new WorldRenderEnvelope(), machines, admission: admission);
        var after = Counts();

        Assert.Equal(actual: (after.Validations - before.Validations), expected: 0L);
        Assert.Equal(actual: (after.Compilations - before.Compilations), expected: 0L);
        // Settlement is done: the installed document is the very object the receipt was earned for, programs and all.
        Assert.Same(definition, server.Definition);
        Assert.Same(admission.Compilation.CostReport, server.CostReport);

        // A server that admits for itself reaches the same authoritative state, so reusing the receipt moves no hash.
        using var control = Fixtures.FreshServer(definition: SettlingDocument());

        Assert.Equal(
            expected: WorldStateHashComposition.HashAuthoritative(control.Server, tick: 0UL),
            actual: WorldStateHashComposition.HashAuthoritative(server, tick: 0UL)
        );
    }
    [Fact]
    public void AServerThatAdmitsForItselfSettlesFirstAndCompilesOnce() {
        using var attribution = WorldBootWork.Attribute(work: m_work);

        var definition = WorldDefinitionSerialization.Deserialize(
            utf8Json: WorldDefinitionSerialization.Serialize(definition: SettlingDocument()));

        using var machines = new WorldMachineHost(screens: definition.Screens, engines: []);
        var before = Counts();
        var server = new WorldServer(definition, new WorldPopulation(definition: definition), FreshProfiles(definition: definition),
            new WorldRenderEnvelope(), machines);
        var after = Counts();

        Assert.Equal(actual: (after.Validations - before.Validations), expected: 1L);
        Assert.Equal(actual: (after.Compilations - before.Compilations), expected: 1L);
        Assert.NotNull(@object: server.Definition.AuthoredState[^1].Cells![0].Clock);
    }
    [Fact]
    public void AnInstanceStartValidatesOnceAndCompilesOnce() {
        using var attribution = WorldBootWork.Attribute(work: m_work);

        using var files = new TemporaryDirectory();

        var path = files.WriteBytes(
            bytes: WorldDefinitionSerialization.Serialize(definition: SettlingDocument()),
            name: "instance.world.json"
        );

        // One catalog for the load and for every host the factory builds, as a composition root wires it: a
        // receipt validated against another catalog is one this host may not accept.
        var catalog = new WorldMachineCatalog(engines: []);

        using var boot = HostRow.Build(name: WorldInstanceHost.BootInstanceName);
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            machineCatalog: catalog,
            machineHostFactory: (screens, _, documentPath, narrationHub) => new WorldMachineHost(
                catalog: catalog,
                documentPath: documentPath,
                narrationHub: narrationHub,
                screens: screens
            ),
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: files.RootPath)
        );

        host.AdmitBoot(row: boot.Instance);

        var before = Counts();

        Assert.True(condition: host.TryStart(instance: out var instance, name: "started", path: path,
            reason: out var reason), userMessage: reason);
        var after = Counts();

        Assert.NotNull(@object: instance);
        Assert.Equal(actual: (after.Validations - before.Validations), expected: 1L);
        Assert.Equal(actual: (after.Compilations - before.Compilations), expected: 1L);
        Assert.NotNull(@object: instance.Server.Definition.AuthoredState[^1].Cells![0].Clock);
    }
    [Fact]
    public async Task AHostedActivationReadHandsItsReceiptToConstruction() {
        using var attribution = WorldBootWork.Attribute(work: m_work);

        var catalog = new WorldMachineCatalog(engines: []);
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true"),
            machines: catalog
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "hosted")
        );
        var cancellation = TestContext.Current.CancellationToken;

        Assert.True(condition: (await store.PublishDefinitionAsync(identity, SettlingDocument(), cancellation)).Ok);

        var before = Counts();
        var recovery = await store.LoadRecoveryAsync(cancellationToken: cancellation, identity: identity);
        var loaded = Counts();

        Assert.NotNull(value: recovery);
        Assert.NotNull(@object: recovery.Value.Admission);
        Assert.Equal(actual: (loaded.Validations - before.Validations), expected: 1L);
        Assert.Equal(actual: (loaded.Compilations - before.Compilations), expected: 1L);

        var definition = recovery.Value.Admission.Definition;

        using var machines = new WorldMachineHost(screens: definition.Screens, catalog: catalog);
        var server = new WorldServer(definition, new WorldPopulation(definition: definition), FreshProfiles(definition: definition),
            new WorldRenderEnvelope(), machines, identity.World.Value, admission: recovery.Value.Admission);
        var after = Counts();

        Assert.Equal(actual: (after.Validations - before.Validations), expected: 1L);
        Assert.Equal(actual: (after.Compilations - before.Compilations), expected: 1L);
        Assert.Same(definition, server.Definition);
    }
    [Fact]
    public void ACheckpointRestoreValidatesOnceAndCompilesOnce() {
        using var attribution = WorldBootWork.Attribute(work: m_work);

        using var fixture = Fixtures.FreshServer(definition: SettlingDocument());

        fixture.Step();
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            checkpoint: out var checkpoint, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: checkpoint);

        var captured = WorldStateHashComposition.HashAuthoritative(fixture.Server, tick: 0UL);
        var profiles = FreshProfiles(definition: fixture.Server.Definition);

        using var machines = new WorldMachineHost(screens: fixture.Server.Definition.Screens, engines: []);
        var before = Counts();

        var (restored, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "restored",
            machines: machines,
            profiles: profiles
        );
        var after = Counts();

        // The world is admitted once. Each owned-world identity the restore rehydrates is its own document and
        // carries its own validation, which retains no programs.
        Assert.Equal((1L + checkpoint.OwnedWorlds.IdentityDocumentsJson.Count), (after.Validations - before.Validations));
        Assert.Equal(actual: (after.Compilations - before.Compilations), expected: 1L);
        Assert.Equal(
            expected: captured,
            actual: WorldStateHashComposition.HashAuthoritative(restored, tick: 0UL)
        );
    }

}
