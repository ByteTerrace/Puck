using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;
using Puck.AdvancedGamingBrick.Forge;
using Puck.Hosting;
using Puck.HumbleGamingBrick.Forge;
using Puck.Launcher;
using Puck.Mcp;
using Puck.Mcp.Azure;
using Puck.World.Machines;
using Puck.World.Server;
using Puck.World.Silo;
using Xunit;

namespace Puck.World.Tests;

/// <summary>An extension whose registration is a delegate, for composition laws.</summary>
internal sealed class TestExtension(string name, Action<IPuckExtensionRegistry> register) : IPuckExtension {
    public string Name => name;

    public void Register(IPuckExtensionRegistry registry) => register(obj: registry);
}

/// <summary>
/// CONTRACT UNDER TEST: the one extension model every host composes through — <see cref="PuckExtensionSet"/>'s
/// deterministic composition and named refusals, <see cref="PuckExtensionDiscovery"/>'s installation layout and
/// load-context sharing, <see cref="PuckExtensionServiceRegistration"/>'s hosted-service lifetime, and the World and silo
/// composing one installation identically.
/// </summary>
public sealed class ExtensionModelLawTests {
    private sealed record Probe(string Value);

    private const string Application = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string DelegatedSettings = """{"managedIdentityClientId":"dddddddd-dddd-dddd-dddd-dddddddddddd","observations":[],"onboardingUrl":"https://api.example.test/api/self-onboard"}""";
    private const string Tenant = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static readonly HostedControl Control = new(Create: static (_, _, _) => throw new InvalidOperationException());

    private static IPuckExtension Empty(string name) => new TestExtension(
        name: name,
        register: static _ => { }
    );
    private static IPuckExtension Probing(string name, params string[] keys) => new TestExtension(
        name: name,
        register: registry => {
            foreach (var key in keys) {
                registry.Add(
                    contribution: new Probe(Value: name),
                    key: key
                );
            }
        }
    );
    private static IPuckExtension Controlling(string name) => new TestExtension(
        name: name,
        register: static registry => registry.AddHostedControl(control: Control)
    );
    private static IPuckExtension Providing(string name) => new TestExtension(
        name: name,
        register: registry => registry.Add(
            contribution: new McpServicesProvider(Create: (_, _) => new ServicesHost(name: name)),
            key: name
        )
    );
    private static RemoteMcpOptions McpOptions(bool services) => new() {
        AllowedSubjects = [],
        Audience = Application,
        Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
        PublicUrl = "https://mcp.example.test/mcp",
        Scope = "user_impersonation",
        Services = (services
            ? JsonElement.Parse(json: DelegatedSettings)
            : null),
        SubjectClaim = "oid",
        Target = "row",
        TenantId = Tenant,
    };

    public static TheoryData<string, IPuckExtension[], string> Refusals() {
        var cases = new TheoryData<string, IPuckExtension[], string>();

        cases.Add(
            p1: "two extensions share a name",
            p2: [Empty(name: "same"), Empty(name: "same")],
            p3: "share one name"
        );
        cases.Add(
            p1: "a blank name",
            p2: [Empty(name: " ")],
            p3: "has a blank name"
        );
        cases.Add(
            p1: "two extensions register one key of a kind",
            p2: [Probing("zeta", "shared"), Probing("alpha", "shared")],
            p3: "Extensions 'alpha' and 'zeta' both register Probe 'shared'."
        );
        cases.Add(
            p1: "one extension registers a key twice",
            p2: [Probing("twice", "k", "k")],
            p3: "Extension 'twice' registers Probe 'k' twice."
        );
        cases.Add(
            p1: "a blank key",
            p2: [Probing("blank", "")],
            p3: "registered a Probe with a blank key"
        );
        cases.Add(
            p1: "two hosted controls",
            p2: [Controlling(name: "second"), Controlling(name: "first")],
            p3: "Extensions 'first' and 'second' both register HostedControl 'control'."
        );
        cases.Add(
            p1: "a machine provider for another engine",
            p2: [new TestExtension(
                name: "forge",
                register: static registry => registry.AddMachineEngine(
                    contentProvider: new HgbCartridgeCompiler(),
                    engine: new AdvancedGamingBrickEngine()
                )
            )],
            p3: "cannot register with machine engine"
        );
        cases.Add(
            p1: "a registration that fails",
            p2: [new TestExtension(
                name: "thrower",
                register: static _ => throw new FormatException(message: "bad settings")
            )],
            p3: "Extension 'thrower' failed to register: bad settings"
        );
        return cases;
    }

    private static string Assembly(string name) {
        var path = Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: $"{name}.dll"
        );

        Assert.True(
            condition: File.Exists(path: path),
            userMessage: $"{name}.dll is not beside the test assembly."
        );
        return path;
    }
    private static string Install(DirectoryInfo root, string name) => Install(
        assembly: Assembly(name: name),
        name: name,
        root: root
    );
    private static string Install(DirectoryInfo root, string name, string assembly) {
        var directory = Directory.CreateDirectory(path: Path.Combine(
            path1: root.FullName,
            path2: name
        ));

        File.Copy(
            destFileName: Path.Combine(
                path1: directory.FullName,
                path2: $"{name}.dll"
            ),
            overwrite: true,
            sourceFileName: assembly
        );
        return directory.FullName;
    }
    private static void Remove(DirectoryInfo root) {
        // Installed assemblies stay mapped for the process lifetime on Windows; their directories may outlive the test.
        try { root.Delete(recursive: true); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { }
    }

    [MemberData(memberName: nameof(Refusals))]
    [Theory]
    public void CompositionRefusesEveryConflictByName(string conflict, IPuckExtension[] extensions, string expected) {
        var refusal = Assert.Throws<PuckExtensionException>(testCode: () => PuckExtensionSet.Compose(extensions: extensions));

        Assert.True(
            condition: refusal.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: expected
            ),
            userMessage: $"{conflict}: {refusal.Message}"
        );
    }
    [Fact]
    public void ARefusedCompositionDisposesEveryReceivedExtensionOnceInReverseNameOrder() {
        var disposed = new List<string>();
        var shared = new DisposableExtension(
            disposed: disposed,
            name: "gamma"
        );
        var failure = new FormatException(message: "bad settings");
        var refusal = Assert.Throws<PuckExtensionException>(testCode: () => PuckExtensionSet.Compose(extensions: [
            new DisposableExtension(
                disposed: disposed,
                name: "alpha"
            ),
            shared,
            new AsyncDisposableExtension(
                disposed: disposed,
                name: "beta"
            ),
            new DisposableExtension(
                disposed: disposed,
                name: "delta",
                register: _ => throw failure
            ),
        ]));

        Assert.Same(
            failure,
            refusal.InnerException
        );
        Assert.Equal(
            actual: disposed,
            expected: ["gamma", "delta", "beta", "alpha"]
        );

        // A refusal before registration (here, one instance supplied twice) still disposes each instance once.
        disposed.Clear();
        Assert.Contains(
            actualString: Assert.Throws<PuckExtensionException>(testCode: () => PuckExtensionSet.Compose(extensions: [shared, shared])).Message,
            expectedSubstring: "share one name"
        );
        Assert.Equal(
            actual: disposed,
            expected: ["gamma"]
        );

        // A disposal that also fails is reported beside the refusal, and the rest are still disposed.
        disposed.Clear();
        var aggregate = Assert.Throws<AggregateException>(testCode: () => PuckExtensionSet.Compose(extensions: [
            new DisposableExtension(
                disposed: disposed,
                name: "zeta",
                disposeFailure: new IOException(message: "held open")
            ),
            new DisposableExtension(
                disposed: disposed,
                name: "eta"
            ),
            Empty(name: " "),
        ]));

        Assert.IsType<PuckExtensionException>(@object: aggregate.InnerExceptions[0]);
        Assert.IsType<IOException>(@object: aggregate.InnerExceptions[1]);
        Assert.Equal(
            actual: disposed,
            expected: ["zeta", "eta"]
        );
    }
    [Fact]
    public void ARegistryRetainedPastRegistrationRefuses() {
        IPuckExtensionRegistry? retained = null;

        PuckExtensionSet.Compose(extensions: [new TestExtension(
            name: "retainer",
            register: registry => retained = registry
        )]);
        Assert.Contains(
            actualString: Assert.Throws<PuckExtensionException>(testCode: () => retained!.Add(
                contribution: new Probe(Value: "late"),
                key: "late"
            )).Message,
            expectedSubstring: "after its registration ended"
        );
    }
    [Fact]
    public void CompositionIsIndependentOfSupplyOrder() {
        IPuckExtension[] extensions = [Probing("gamma", "g2", "g1"), Probing("alpha", "a"), Controlling(name: "beta")];
        var expected = PuckExtensionSet.Compose(extensions: extensions);

        Assert.Equal(
            ["alpha: Probe a", "beta: HostedControl control", "gamma: Probe g1, Probe g2"],
            expected.Describe()
        );
        Assert.Equal(
            ["a", "g1", "g2"],
            expected.Contributions<Probe>().Select(selector: static entry => entry.Key)
        );
        int[][] orders = [[2, 1, 0], [1, 0, 2], [1, 2, 0]];

        foreach (var order in orders) {
            var permuted = PuckExtensionSet.Compose(extensions: order.Select(selector: index => extensions[index]));

            Assert.Equal(
                expected.Describe(),
                permuted.Describe()
            );
            Assert.Equal(
                expected.Extensions,
                permuted.Extensions
            );
        }
    }
    [Fact]
    public async Task DisposalDisposesEachExtensionOnceInReverseNameOrder() {
        var disposed = new List<string>();
        var set = PuckExtensionSet.Compose(extensions: [
            new DisposableExtension(name: "alpha", disposed: disposed),
            new AsyncDisposableExtension(disposed: disposed, name: "beta"),
            Empty(name: "gamma"),
        ]);

        await set.DisposeAsync();
        await set.DisposeAsync();
        Assert.Equal(
            actual: disposed,
            expected: ["beta", "alpha"]
        );
        Assert.Throws<ObjectDisposedException>(testCode: () => set.Contributions<Probe>());
    }
    [Fact]
    public async Task TheHostStartsStopsAndDisposesContributedServicesBeforeTheirExtension() {
        var events = new List<string>();
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddPuckExtensions(extensions: PuckExtensionSet.Compose(extensions: [
            new DisposableExtension(
                disposed: events,
                name: "runner",
                register: registry => registry.Add(
                    contribution: new PuckHostedService(Create: _ => new RecordingService(events: events)),
                    key: "runner"
                )
            ),
        ]));
        using (var host = builder.Build()) {
            await host.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
            await host.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        Assert.Equal(
            actual: events,
            expected: ["start", "stop", "dispose service", "runner"]
        );
    }
    [Fact]
    public void AControlConfigurationWithoutAHostedControlIsRefused() {
        var refusal = Assert.Throws<PuckExtensionException>(testCode: () => new ServiceCollection().AddPuckExtensions(
            controlConfiguration: "remote.json",
            extensions: PuckExtensionSet.Compose(extensions: [new WorldServerExtension()])
        ));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "no installed extension contributes a HostedControl"
        );
    }
    [Fact]
    public void RemoteMcpSelectsOneServicesProviderOnlyWhenServicesAreConfigured() {
        var control = new ProbeControlHost();
        var none = PuckExtensionSet.Compose(extensions: [new McpControlExtension()]);
        var one = PuckExtensionSet.Compose(extensions: [new McpControlExtension(), Providing(name: "alpha")]);
        var two = PuckExtensionSet.Compose(extensions: [Providing(name: "beta"), new McpControlExtension(), Providing(name: "alpha")]);
        var console = McpServicesProvider.Select(
            extensions: one,
            host: control,
            options: McpOptions(services: false)
        );

        Assert.IsNotType<ServicesHost>(@object: console);
        Assert.True(condition: console.IsReady);
        Assert.Equal(
            ["row"],
            control.ReadinessTargets
        );
        Assert.Equal(
            "alpha",
            Assert.IsType<ServicesHost>(@object: McpServicesProvider.Select(
                extensions: one,
                host: control,
                options: McpOptions(services: true)
            )).Name
        );
        Assert.Equal(
            "The MCP configuration names services: no installed extension provides a McpServicesProvider.",
            Assert.Throws<ArgumentException>(testCode: () => McpServicesProvider.Select(
                extensions: none,
                host: control,
                options: McpOptions(services: true)
            )).Message
        );
        Assert.Equal(
            "The MCP configuration names services: more than one installed extension provides a McpServicesProvider ('alpha' from alpha, 'beta' from beta); name one.",
            Assert.Throws<ArgumentException>(testCode: () => McpServicesProvider.Select(
                extensions: two,
                host: control,
                options: McpOptions(services: true)
            )).Message
        );
    }
    [Fact]
    public void TheAzureProviderServesADeploymentThatConfiguresServices() {
        var extensions = PuckExtensionSet.Compose(extensions: [new AzureMcpExtension(), new McpControlExtension()]);

        Assert.Equal(
            ["Puck.Mcp: HostedControl control", "Puck.Mcp.Azure: McpServicesProvider azure"],
            extensions.Describe()
        );

        var host = McpServicesProvider.Select(
            extensions: extensions,
            host: new ProbeControlHost(),
            options: McpOptions(services: true)
        );

        try {
            Assert.Equal(
                "Puck.Mcp.Azure",
                host.GetType().Assembly.GetName().Name
            );
            Assert.Contains(
                collection: host.ServiceTools,
                filter: static tool => (tool.Name == "puck_onboard")
            );
            Assert.True(condition: host.SupportsAttachments);
        } finally { (host as IDisposable)?.Dispose(); }
    }
    [Fact]
    public async Task InstalledMcpExtensionsShareTheirSeamAcrossLoadContexts() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-ext-mcp-");

        try {
            var configuration = Path.Combine(
                path1: root.FullName,
                path2: "remote.json"
            );
            var withAzure = Directory.CreateDirectory(path: Path.Combine(
                path1: root.FullName,
                path2: "azure"
            ));
            var withoutAzure = Directory.CreateDirectory(path: Path.Combine(
                path1: root.FullName,
                path2: "bare"
            ));

            await File.WriteAllTextAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                contents: $$"""
                    {"target":"row","publicUrl":"https://mcp.example.test/mcp","listenUrl":"http://127.0.0.1:8080",
                     "issuer":"https://login.microsoftonline.com/{{Tenant}}/v2.0","audience":"{{Application}}","scope":"user_impersonation",
                     "subjectClaim":"oid","tenantId":"{{Tenant}}","allowedSubjects":[],"services":{{DelegatedSettings}}}
                    """,
                path: configuration
            );
            Install(
                name: "Puck.Mcp",
                root: withAzure
            );
            Install(
                name: "Puck.Mcp.Azure",
                root: withAzure
            );
            Install(
                name: "Puck.Mcp",
                root: withoutAzure
            );

            IPuckHostedService Start(DirectoryInfo directory) {
                var extensions = WorldSiloApplication.ComposeExtensions(directories: [directory.FullName]);
                var services = new ServiceCollection().AddSingleton(implementationInstance: extensions).BuildServiceProvider();

                Assert.True(condition: extensions.TryGet<HostedControl>(
                    contribution: out var control,
                    key: HostedControl.Key
                ));
                return control.Create(
                    arg1: services,
                    arg2: new ProbeControlHost(),
                    arg3: configuration
                );
            }

            // The Azure provider, loaded in its own context, registers the McpServicesProvider type the installed
            // Puck.Mcp reads: the seam resolves across contexts instead of reading as absent.
            using (Start(directory: withAzure)) { }
            Assert.Contains(
                actualString: Assert.Throws<ArgumentException>(testCode: () => Start(directory: withoutAzure)).Message,
                expectedSubstring: "no installed extension provides a McpServicesProvider"
            );
        } finally { Remove(root: root); }
    }
    [Fact]
    public async Task AnInstalledAgentHarnessSelectsAnInstalledChatProviderAcrossLoadContexts() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-ext-agents-");

        try {
            Install(
                name: "Puck.World.AgentHarness",
                root: root
            );
            Install(
                name: "Puck.World.AgentHarness.Azure",
                root: root
            );
            using var fixture = Fixtures.FreshServer();
            var extensions = WorldSiloApplication.ComposeExtensions(directories: [root.FullName]);
            // The Azure provider, loaded in its own context, registers the ChatClientProvider type the installed
            // harness selects: named or not, the seam resolves across contexts instead of reading as absent.
            await using var participant = extensions.Select<Puck.World.Protocol.WorldParticipantType>(
                key: "agent.harness",
                purpose: "Participant type"
            ).Create(
                arg1: new Puck.World.Protocol.WorldParticipantContext {
                    BodyIndex = 0,
                    Channels = () => fixture.Server.Population.Channels,
                    Clock = TimeProvider.System,
                    Extensions = extensions,
                    Link = new Puck.World.Protocol.LoopbackTransport(server: fixture.Server),
                    Name = "guide",
                    Principal = Puck.Commands.Principal.Addon(name: "guide"),
                },
                arg2: JsonDocument.Parse(json: """{ "objective": "Greet visitors.", "providerSettings": { "endpoint": "https://example.openai.azure.com/", "deployment": "chat" } }""").RootElement.Clone()
            );

            Assert.Contains(
                actualString: participant.Describe(),
                expectedSubstring: "provider=azure.openai state=idle"
            );
        } finally { Remove(root: root); }
    }
    [Fact]
    public void DiscoveryRefusesEveryMalformedInstallationByPath() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-ext-malformed-");

        try {
            DirectoryInfo Case(string name) => Directory.CreateDirectory(path: Path.Combine(
                path1: root.FullName,
                path2: name
            ));
            // Every refusal also disposes the built-in the host handed over before discovery failed.
            string Refusal(params DirectoryInfo[] directories) {
                var disposed = new List<string>();
                var message = Assert.Throws<PuckExtensionException>(testCode: () => PuckExtensionDiscovery.Compose(
                    builtIns: [new DisposableExtension(
                        disposed: disposed,
                        name: "built-in"
                    )],
                    directories: directories.Select(selector: static directory => directory.FullName)
                )).Message;

                Assert.Equal(
                    actual: disposed,
                    expected: ["built-in"]
                );
                return message;
            }

            var stray = Case(name: "stray");

            File.Copy(
                destFileName: Path.Combine(
                    path1: stray.FullName,
                    path2: "Puck.Text.dll"
                ),
                sourceFileName: Assembly(name: "Puck.Text")
            );
            Assert.Contains(
                actualString: Refusal(stray),
                expectedSubstring: "sits directly in an extensions directory"
            );

            var unnamed = Case(name: "unnamed");

            Install(
                assembly: Assembly(name: "Puck.Text"),
                name: "Puck.Text",
                root: unnamed
            );
            File.Move(
                destFileName: Path.Combine(
                    path1: unnamed.FullName,
                    path2: "Puck.Text",
                    path3: "Other.dll"
                ),
                sourceFileName: Path.Combine(
                    path1: unnamed.FullName,
                    path2: "Puck.Text",
                    path3: "Puck.Text.dll"
                )
            );
            Assert.Contains(
                actualString: Refusal(unnamed),
                expectedSubstring: "carries no 'Puck.Text.dll'"
            );

            var undeclared = Case(name: "undeclared");

            Install(
                name: "Puck.Text",
                root: undeclared
            );
            Assert.Contains(
                actualString: Refusal(undeclared),
                expectedSubstring: "declares no [PuckExtension] entry type"
            );

            var corrupt = Case(name: "corrupt");
            var corruptDirectory = Directory.CreateDirectory(path: Path.Combine(
                path1: corrupt.FullName,
                path2: "Broken"
            ));

            File.WriteAllText(
                contents: "not an assembly",
                path: Path.Combine(
                    path1: corruptDirectory.FullName,
                    path2: "Broken.dll"
                )
            );
            Assert.Contains(
                actualString: Refusal(corrupt),
                expectedSubstring: "could not load"
            );

            var left = Case(name: "left");
            var right = Case(name: "right");

            Install(
                name: "Puck.World.Embeddings",
                root: left
            );
            Install(
                name: "Puck.World.Embeddings",
                root: right
            );
            Assert.Contains(
                actualString: Refusal(left, right),
                expectedSubstring: "Extension 'Puck.World.Embeddings' is installed twice"
            );
            Assert.Empty(collection: PuckExtensionDiscovery.Compose(
                builtIns: [],
                directories: [Path.Combine(
                    path1: root.FullName,
                    path2: "missing"
                )]
            ).Extensions);
        } finally { Remove(root: root); }
    }
    [Fact]
    public void DiscoveredExtensionsShareHostContractsAndLiveForTheProcess() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-ext-identity-");

        try {
            Install(
                name: "Puck.HumbleGamingBrick.Forge",
                root: root
            );
            var extensions = PuckExtensionDiscovery.Compose(
                builtIns: [],
                directories: [root.FullName]
            );
            var discovered = Assert.Single(collection: extensions.Extensions);
            var context = AssemblyLoadContext.GetLoadContext(assembly: discovered.GetType().Assembly)!;

            Assert.NotSame(
                AssemblyLoadContext.Default,
                context
            );
            Assert.False(condition: context.IsCollectible);
            Assert.NotEqual(
                typeof(HumbleGamingBrickExtension),
                discovered.GetType()
            );

            var catalog = WorldMachineCatalog.From(extensions: extensions);

            // The catalog found the contributions under the host's own IMachineEngine and IMachineContentProvider, so
            // the extension's context shared those contracts rather than loading its own copies.
            Assert.True(condition: catalog.IsRegistered(engineId: "gaming-brick"));
            Assert.True(condition: catalog.ContentProviders.ContainsKey(key: "gaming-brick"));
        } finally { Remove(root: root); }
    }
    [Fact]
    public void TheWorldAndTheSiloComposeOneInstallationIdentically() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-ext-hosts-");

        try {
            var silo = Directory.CreateDirectory(path: Path.Combine(
                path1: root.FullName,
                path2: "silo"
            ));
            var world = Directory.CreateDirectory(path: Path.Combine(
                path1: root.FullName,
                path2: "world"
            ));

            foreach (var name in new[] { "Puck.Mcp", "Puck.Mcp.Azure", "Puck.World.AgentHarness", "Puck.World.AgentHarness.Azure", "Puck.World.Azure", "Puck.World.Embeddings" }) {
                Install(
                    name: name,
                    root: silo
                );
                Install(
                    name: name,
                    root: world
                );
            }
            // The silo installs the Gaming Brick forges; the World carries them as built-ins.
            Install(
                name: "Puck.AdvancedGamingBrick.Forge",
                root: silo
            );
            Install(
                name: "Puck.HumbleGamingBrick.Forge",
                root: silo
            );

            var siloSet = WorldSiloApplication.ComposeExtensions(directories: [silo.FullName]);
            var worldSet = PuckExtensionDiscovery.Compose(
                builtIns: [new AdvancedGamingBrickExtension(), new HumbleGamingBrickExtension(), new WorldServerExtension()],
                directories: [world.FullName]
            );

            Assert.Equal(
                worldSet.Describe(),
                siloSet.Describe()
            );
            Assert.Equal(
                WorldMachineCatalog.From(extensions: worldSet).CompositionFingerprint,
                WorldMachineCatalog.From(extensions: siloSet).CompositionFingerprint
            );
            Assert.Contains(
                collection: siloSet.Describe(),
                expected: "Puck.World.Azure: WorldAuthenticationProvider azure.api-users, WorldExtensionProviderType azure.resource, WorldHealthCheck /livez/azure, WorldSiloRetirementProvider azure.scheduled-events, WorldSiloStorageProvider azure.blob"
            );
            // The agent participant and its identity-authenticated model provider compose the same way in both hosts.
            Assert.Contains(
                collection: siloSet.Describe(),
                expected: "Puck.World.AgentHarness: WorldParticipantType agent.harness"
            );
            Assert.Contains(
                collection: siloSet.Describe(),
                expected: "Puck.World.AgentHarness.Azure: ChatClientProvider azure.openai"
            );
            // Installing a copy of a built-in beside a host is a conflict, never a silent second registration.
            Install(
                name: "Puck.HumbleGamingBrick.Forge",
                root: world
            );
            Assert.Contains(
                actualString: Assert.Throws<PuckExtensionException>(testCode: () => PuckExtensionDiscovery.Compose(
                    builtIns: [new HumbleGamingBrickExtension()],
                    directories: [world.FullName]
                )).Message,
                expectedSubstring: "share one name"
            );
        } finally { Remove(root: root); }
    }

    private sealed class ProbeControlHost : IControlSessionHost {
        public List<string> ReadinessTargets { get; } = [];

        public ValueTask<IControlSession> AttachAsync(string target, ControlIdentity identity, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public ValueTask<ControlCapabilities> DescribeAsync(string target, ControlIdentity identity, CancellationToken cancellationToken) => ValueTask.FromResult(result: new ControlCapabilities(CommandHelp: ""));
        public bool IsReady(string target) {
            ReadinessTargets.Add(item: target);
            return true;
        }
    }
    private sealed class ServicesHost(string name) : RemoteMcpHost {
        public override bool IsReady => true;
        public string Name => name;

        public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
    private sealed class AsyncDisposableExtension(string name, List<string> disposed) : IPuckExtension, IAsyncDisposable {
        public string Name => name;

        public ValueTask DisposeAsync() {
            disposed.Add(item: name);
            return ValueTask.CompletedTask;
        }
        public void Register(IPuckExtensionRegistry registry) { }
    }
    private sealed class DisposableExtension(string name, List<string> disposed, Action<IPuckExtensionRegistry>? register = null, Exception? disposeFailure = null) : IPuckExtension, IDisposable {
        public string Name => name;

        public void Dispose() {
            disposed.Add(item: name);
            if (disposeFailure is not null) { throw disposeFailure; }
        }
        public void Register(IPuckExtensionRegistry registry) => register?.Invoke(obj: registry);
    }
    private sealed class RecordingService(List<string> events) : IPuckHostedService {
        public void Dispose() => events.Add(item: "dispose service");
        public ValueTask DisposeAsync() {
            Dispose();
            return ValueTask.CompletedTask;
        }
        public Task StartAsync(CancellationToken cancellationToken) {
            events.Add(item: "start");
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) {
            events.Add(item: "stop");
            return Task.CompletedTask;
        }
    }
}
