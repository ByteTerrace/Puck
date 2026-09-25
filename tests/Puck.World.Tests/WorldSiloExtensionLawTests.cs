using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Storage;
using Puck.Testing;
using Puck.World.Agents.Harness;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.World.Silo;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a silo row's <c>extensions</c> configuration is the same <c>puck.world.extensions.v1</c>
/// document a local World reads, attached through the same <see cref="WorldConfiguredExtensions.Attach"/> path. The
/// silo selects and uses an installed operation provider, refuses an unknown or ambiguous provider type by name, and
/// selects exactly what a local World's boot row selects from the same configuration — agent participants included.
/// </summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class WorldSiloExtensionLawTests {
    private const string DocumentId = "extensions-test";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string Configuration(string type, bool participant = false) => $$"""
        {
          "schema": "puck.world.extensions.v1",
          "world": "{{DocumentId}}",
          "lineage": "00000000-0000-0000-0000-000000000042",
          "providers": [ { "name": "first", "type": "{{type}}", "settings": { "identity": "first" } } ],
          "operations": [ { "name": "delete-a", "provider": "first", "description": "First service", "settings": {} } ],
          "clients": [ {
            "principal": "{{Principal.Console.Describe()}}",
            "operations": [ "delete-a" ],
            "requests": [
              { "capability": "observe", "subject": "state:requests-a" },
              { "capability": "observe", "subject": "state:status-a" },
              { "capability": "mutate", "subject": "{{GrantSubject.Section(section: WorldSection.State).Describe()}}" }
            ]
          } ],
          "connections": [ { "name": "a", "client": "{{Principal.Console.Describe()}}", "operation": "delete-a", "requests": "requests-a", "status": "status-a" } ],
          "scanEveryTicks": 1{{(participant
            ? """
            ,
              "participants": [ { "name": "guide", "type": "agent.harness", "principal": "addon:guide", "body": 1,
                "settings": { "objective": "Greet visitors.", "provider": "scripted", "planning": false, "telemetry": false } } ]
            """
            : "")}}
        }
        """;
    private static WorldDefinition Document() {
        var document = Fixtures.BuildDocument();

        return document with {
            DocumentId = DocumentId,
            HostRaw = Fixtures.StandardHost with {
                Authority = null,
                Listen = null,
                Presentation = WorldHostPresentation.None,
            },
            StateRaw = new(World: [.. document.State,
                Table(
                    kind: CellKind.Text,
                    name: "requests-a",
                    request: true
                ),
                Table(
                    kind: CellKind.Int,
                    name: "status-a",
                    request: false
                )]),
        };
    }
    private static PuckExtensionSet Extensions(List<Provider> providers, bool ambiguous = false) => PuckExtensionSet.Compose(extensions: [
        new TestExtension(
            name: "fake",
            register: registry => {
                registry.AddOperation(provider: new(
                    "fake",
                    settings => {
                        var provider = new Provider();

                        lock (providers) { providers.Add(item: provider); }
                        return provider;
                    }
                ));
                if (ambiguous) {
                    registry.AddEmbedding(provider: new(
                        "fake",
                        static _ => throw new InvalidOperationException(message: "never created")
                    ));
                }
            }
        ),
        new TestExtension(
            name: "scripted",
            register: static registry => registry.AddChatClient(
                name: "scripted",
                provider: new ChatClientProvider(Create: static _ => new SilentChatClient())
            )
        ),
        new WorldAgentHarnessExtension(),
    ]);
    private static WorldStateRow Table(string name, CellKind kind, bool request) => new(
        CellName.Parse(candidate: name),
        kind,
        Capacity: 8,
        Cells: (request
            ? [new(
                CellName.Parse(candidate: "incarnation-1"),
                CellValue.Text(value: "{}")
            )]
            : []),
        Visibility: new()
    );
    private static long? Status(WorldServer server) => server.Definition.State.First(predicate: static row => (row.Name.Value == "status-a"))
        .Cells?.FirstOrDefault(predicate: static cell => (cell.Key.Value == "incarnation-1"))?.Value.AsInt;
    private static Task PumpAsync(WorldSiloHost host, Task operation) => WorldSiloHost.PumpActivationMailboxesAsync(
        cancellationToken: Cancel,
        hosts: [host],
        operation: operation
    );
    // The World's side of the comparison: a boot row attached exactly as a local World's --extensions-config-file
    // attaches it.
    private static (string? Refusal, WorldConfiguredExtensions? Runtime) AttachToBootRow(HostRow boot, WorldInstanceHost instances, string configuration, PuckExtensionSet extensions, string stateDirectory) {
        try {
            return (null, WorldConfiguredExtensions.Attach(
                configuration: WorldExtensionConfiguration.Parse(utf8: System.Text.Encoding.UTF8.GetBytes(s: configuration)),
                extensions: extensions,
                instances: instances,
                row: boot.Instance,
                store: PuckStorageTestComposition.BuildStore(),
                target: new DirectoryObjectStorageTarget(stateDirectory)
            ));
        } catch (ArgumentException error) {
            return (error.Message, null);
        }
    }

    private sealed class Silo : IAsyncDisposable {
        private readonly TemporaryDirectory m_directory = new();
        private readonly BufferedConsoleOutput m_output = new();

        private readonly string m_extensionsFile;

        public Silo(string configuration, PuckExtensionSet extensions) {
            using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
            var keyFile = Path.Combine(
                path1: m_directory.RootPath,
                path2: "row.key"
            );

            File.WriteAllBytes(
                bytes: key.ExportPkcs8PrivateKey(),
                path: keyFile
            );
            m_extensionsFile = Path.Combine(
                path1: m_directory.RootPath,
                path2: "row.extensions.json"
            );
            File.WriteAllText(
                contents: configuration,
                path: m_extensionsFile
            );
            Identity = new WorldAuthorityIdentity(
                Owner: Guid.NewGuid(),
                World: SafeName.Parse(candidate: "row")
            );
            Store = PuckStorageTestComposition.BuildStore();
            var source = new TextCommandSource(new CommandRegistry(modules: []));

            Host = new WorldSiloHost(
                blobStore: Store,
                definition: new(
                    [new(
                        Owner: Identity.Owner,
                        World: Identity.World,
                        Federation: new(KeyFile: keyFile),
                        Extensions: m_extensionsFile
                    )],
                    new(Budget: 1),
                    new(
                        "directory",
                        JsonElement.Parse(json: "{}")
                    ),
                    m_directory.RootPath,
                    new(Kind: "Localhost")
                ),
                extensions: extensions,
                routing: new(
                    source: () => source,
                    tagging: new SiloConsoleTagging(output: m_output)
                ),
                storageTarget: new DirectoryObjectStorageTarget(m_directory.RootPath)
            );
        }

        public WorldSiloHost Host { get; }
        public WorldAuthorityIdentity Identity { get; }
        public IObjectBlobStore Store { get; }

        public async Task<bool> ActivateAsync() {
            Assert.True(condition: (await new WorldAuthorityBlobStore(
                store: Store,
                target: new DirectoryObjectStorageTarget(m_directory.RootPath)
            ).PublishDefinitionAsync(
                Identity,
                Document(),
                Cancel
            )).Ok);
            var activation = Host.ActivateAsync(
                ct: Cancel,
                identity: Identity
            );

            await PumpAsync(
                host: Host,
                operation: activation
            );
            return await activation;
        }

        public WorldInstance? Row => (Host.Instances.TryGet(
            instance: out var row,
            name: Identity.World.Value
        )
            ? row
            : null
        );

        public async ValueTask DisposeAsync() {
            await Host.DisposeAsync();
            Host.Instances.Dispose();
            m_output.Dispose();
            m_directory.Dispose();
        }
    }

    private static async Task<(bool Activated, string Error)> ActivateCapturingAsync(Silo silo) {
        var original = Console.Error;
        using var captured = new StringWriter();

        Console.SetError(newError: captured);
        try {
            return (await silo.ActivateAsync(), captured.ToString());
        } finally { Console.SetError(newError: original); }
    }

    [Fact]
    public async Task TheSiloSelectsAndUsesTheInstalledProviderItsRowConfigurationNames() {
        var providers = new List<Provider>();
        await using var silo = new Silo(
            configuration: Configuration(type: "fake"),
            extensions: Extensions(providers: providers)
        );

        Assert.True(condition: await silo.ActivateAsync());
        var row = Assert.IsType<WorldInstance>(@object: silo.Row);
        var runtime = Assert.IsType<WorldConfiguredExtensions>(@object: row.Extensions);

        Assert.Equal(
            expected: ["delete-a"],
            actual: runtime.OperationNames
        );
        // The silo's own step drives the row, then pumps its runtime at the master boundary; the installed provider
        // executes the request and the status projects back through authority.
        var simulation = new WorldSiloSimulation(host: silo.Host);

        for (var step = 0UL; ((step < 4000UL) && (Status(server: row.Server) != ((long)WorldExternalOperationStatus.Succeeded))); step++) {
            simulation.Step(
                commands: default,
                context: new FixedStepContext(
                    ElapsedTicks: (step * Fixtures.StepTicks),
                    StepTicks: Fixtures.StepTicks,
                    Tick: step
                )
            );
            await runtime.FlushAsync(cancellationToken: Cancel);
            await Task.Delay(
                cancellationToken: Cancel,
                millisecondsDelay: 1
            );
        }
        Assert.Equal(
            expected: ((long)WorldExternalOperationStatus.Succeeded),
            actual: Status(server: row.Server)
        );
        Assert.Equal(
            expected: 1,
            actual: Assert.Single(collection: providers).Executions
        );

        var deactivation = silo.Host.DeactivateAsync(
            ct: Cancel,
            identity: silo.Identity
        );

        await PumpAsync(
            host: silo.Host,
            operation: deactivation
        );
        Assert.Null(@object: silo.Row);
        Assert.True(condition: providers[0].Disposed);
    }
    [InlineData("missing", false, "Provider 'first' selects uninstalled type 'missing'; name one of: fake.")]
    [InlineData("fake", true, "Provider 'first' type 'fake' is installed as both an operation and an embedding provider type")]
    [Theory]
    public async Task AnUnknownOrAmbiguousProviderTypeRefusesTheActivationByName(string type, bool ambiguous, string expected) {
        await using var silo = new Silo(
            configuration: Configuration(type: type),
            extensions: Extensions(
                ambiguous: ambiguous,
                providers: []
            )
        );

        var (activated, error) = await ActivateCapturingAsync(silo: silo);

        Assert.False(condition: activated);
        Assert.Null(@object: silo.Row);
        Assert.Contains(
            actualString: error,
            expectedSubstring: $"[silo.activate: 'owner/{silo.Identity.Owner:D}/row' refused (extensions: {expected}"
        );
    }
    [InlineData("fake", false, true)]
    [InlineData("missing", false, false)]
    [InlineData("fake", true, false)]
    [Theory]
    public async Task TheWorldAndTheSiloSelectIdenticallyFromOneConfiguration(string type, bool ambiguous, bool participant) {
        var configuration = Configuration(
            participant: participant,
            type: type
        );
        using var state = new TemporaryDirectory();
        using var boot = HostRow.Build(
            definition: Document(),
            name: WorldInstanceHost.BootInstanceName
        );
        using var instances = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: state.RootPath)
        );

        instances.AdmitBoot(row: boot.Instance);
        var (worldRefusal, worldRuntime) = AttachToBootRow(
            boot: boot,
            configuration: configuration,
            extensions: Extensions(
                ambiguous: ambiguous,
                providers: []
            ),
            instances: instances,
            stateDirectory: state.RootPath
        );

        await using (worldRuntime) {
            await using var silo = new Silo(
                configuration: configuration,
                extensions: Extensions(
                    ambiguous: ambiguous,
                    providers: []
                )
            );

            var (activated, error) = await ActivateCapturingAsync(silo: silo);

            Assert.Equal(
                actual: activated,
                expected: (worldRefusal is null)
            );
            if (worldRefusal is not null) {
                Assert.Contains(
                    actualString: error,
                    expectedSubstring: $"refused (extensions: {worldRefusal})"
                );
                return;
            }
            var siloRuntime = Assert.IsType<WorldConfiguredExtensions>(@object: silo.Row!.Extensions);

            Assert.Equal(
                expected: worldRuntime!.OperationNames,
                actual: siloRuntime.OperationNames
            );
            Assert.Equal(
                expected: worldRuntime.Participants,
                actual: siloRuntime.Participants
            );
            Assert.Equal(
                expected: (participant
                    ? ["guide: type=agent.harness principal=addon:guide body=1 provider=scripted state=idle turns=0 failure=none"]
                    : []),
                actual: siloRuntime.Participants
            );
        }
    }

    private sealed class Provider : IWorldConfiguredProvider, IWorldExternalOperationProvider {
        public bool Disposed;
        public int Executions;

        public string Identity => "first";

        public WorldExtensionOperation Bind(string name, string description, JsonElement settings) => new(
            new(
                Description: description,
                InputSchema: "{}",
                Name: name
            ),
            this,
            (id, input) => new(
                Binding: name,
                BindingIdentity: "first",
                Id: id,
                Payload: input
            )
        );
        public void Dispose() => Disposed = true;
        public ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken) {
            Interlocked.Increment(location: ref Executions);
            return ValueTask.FromResult(result: new WorldExternalOperationResult(
                Result: "complete",
                Status: WorldExternalOperationStatus.Succeeded
            ));
        }
        public ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation,
            WorldExternalOperationResult previous, CancellationToken cancellationToken) => throw new InvalidOperationException(message: "No resend expected.");
    }
    private sealed class SilentChatClient : IChatClient {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(result: new ChatResponse(message: new ChatMessage(
                content: "done",
                role: ChatRole.Assistant
            )));
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
