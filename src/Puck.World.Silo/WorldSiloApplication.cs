using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.World.Machines;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Composes and runs the silo, allowing dynamic host extensions to register capabilities.</summary>
public static class WorldSiloApplication {
    /// <summary>Runs the ordinary silo with dynamic extensions loaded from --extensions-dir.</summary>
    /// <param name="args">The silo's ordinary command-line arguments.</param>
    /// <param name="cancellationToken">Stops the host through its normal lifecycle.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default) {
        var siloOption = new Option<string?>(name: "--silo") {
            DefaultValueFactory = static _ => null,
            Description = "The silo document (puck.silo.def.v1) to load. Required.",
        };
        var extensionsDirOption = new Option<string?>(name: "--extensions-dir") {
            DefaultValueFactory = static _ => null,
            Description = "Optional path to the extensions directory. Defaults to ./extensions and <app>/extensions.",
        };
        var mcpOption = new Option<string?>(name: "--mcp") {
            DefaultValueFactory = static _ => Environment.GetEnvironmentVariable(variable: "PUCK_MCP_CONFIG"),
            Description = "Optional path to MCP deployment configuration (remote.json). Enables dynamic MCP control hosting.",
        };
        var launchCommand = new RootCommand(description: "Puck World Silo") {
            extensionsDirOption,
            mcpOption,
            siloOption,
        };
        var parseResult = launchCommand.Parse(args);
        if (parseResult.GetValue(option: siloOption) is not { Length: > 0 } siloPath) {
            Console.Error.WriteLine(value: "--silo <path> is required.");
        
            return 1;
        }
        var loadedExtensions = LoadDynamicExtensions(explicitDir: parseResult.GetValue(option: extensionsDirOption));
        if (!WorldSiloDefinitionSerialization.TryLoadFile(
            clusteringKinds: WorldSiloExtensions.ClusteringKinds,
            definition: out var definition,
            path: siloPath,
            reason: out var loadReason
        )) {
            Console.Error.WriteLine(value: $"--silo could not be read: {loadReason}");
        
            return 1;
        }
        var builder = Host.CreateApplicationBuilder(args: args);
        // Orleans' own INFO-level startup narration (cluster config dumps, membership chatter) would otherwise bury the
        // engine's own [world.listen: bound …]/[silo.*] lines a driving script polls for.
        builder.Logging.AddFilter(category: "Orleans", level: LogLevel.Warning);
        builder.Logging.AddFilter(category: "Microsoft.Orleans", level: LogLevel.Warning);
        builder.Services.AddSingleton(implementationInstance: definition!);
        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: builder.Services);
        WorldSiloExtensions.Add(builder.Services, definition!);
        builder.Services.AddSingleton<SiloConsoleTagging>();
        // Registered ahead of AddLauncherHeadlessTerminal's own TryAddSingleton<TextCommandSource> below, so this — every
        // line tagged '[silo] ' — is the one that wins; the desktop's own registration (untagged, tape-recording) is
        // exactly what AddLauncherHeadlessTerminal would otherwise add.
        builder.Services.AddSingleton(implementationFactory: static sp => new TextCommandSource(
            onResult: (_, result) => sp.GetRequiredService<SiloConsoleTagging>().WriteTagged(
                result: result,
                tag: "silo"
            ),
            registry: sp.GetRequiredService<CommandRegistry>()
        ));
        builder.Services.AddSingleton(implementationFactory: static sp => new SiloConsoleRouting(
            source: () => sp.GetRequiredService<TextCommandSource>(),
            tagging: sp.GetRequiredService<SiloConsoleTagging>()
        ));
        builder.Services.AddSingleton<IWorldConsoleAuthority, SiloConsoleAuthority>();
        builder.Services.AddSingleton<IControlSessionHost, SiloControlSessionHost>();
        builder.Services.AddSingleton(implementationFactory: static sp => new WorldSiloHost(
            blobStore: sp.GetRequiredService<Puck.Storage.IObjectBlobStore>(),
            definition: sp.GetRequiredService<WorldSiloDefinition>(),
            routing: sp.GetRequiredService<SiloConsoleRouting>(),
            storageTarget: sp.GetRequiredService<Puck.Storage.ObjectStorageTarget>(),
            authentication: WorldSiloExtensions.Authenticate
        ));
        builder.Services.AddSingleton<IWorldWaitGateResolver>(implementationFactory: static sp => sp.GetRequiredService<WorldSiloHost>());
        builder.Services.AddHostedService<WorldSiloActivations>();
        builder.Services.AddSingleton<ICommandModule, SiloCommandModule>();
        builder.Services.AddSingleton<ICommandModule, WorldWaitCommandModule>();
        builder.Services.AddSingleton<ICommandModule, WorldTimingCommandModule>();
        builder.Services.AddSingleton<ICommandModule, WorldNetworkCommandModule>();
        builder.Services.AddSingleton<Puck.World.Protocol.IServerLink, SiloServerLink>();
        builder.Services.AddSingleton<Puck.World.Protocol.WorldDeferredVerbEchoes>();
        builder.Services.AddSingleton<ICommandModule, WorldStateCommandModule>();
        // A bare router/registry: the silo embodies no local seats and drives no physical input, so the bindings and
        // principal resolver below bind nothing and claim no principal — HeadlessTickHostedService still requires exactly
        // one IFixedStepSimulation paired with exactly one InputRouter, so this is the minimal pair that satisfies it
        // without AddFixedStepSimulation's seat-oriented registration.
        builder.Services.AddSingleton<WorldSiloSimulation>();
        builder.Services.AddSingleton<IFixedStepSimulation>(implementationFactory: static sp => sp.GetRequiredService<WorldSiloSimulation>());
        builder.Services.AddSingleton<IInputBindings, WorldSiloBareInputBindings>();
        builder.Services.AddSingleton<ICommandPrincipalResolver, WorldSiloBarePrincipalResolver>();
        builder.Services.AddSingleton(implementationFactory: static sp => new InputRouter(
            bindings: sp.GetRequiredService<IInputBindings>(),
            clock: sp.GetRequiredService<IInputClock>(),
            principalResolver: sp.GetRequiredService<ICommandPrincipalResolver>(),
            registry: sp.GetRequiredService<CommandRegistry>()
        ));
        builder.Services.AddLauncherHeadlessTerminal(readStandardInput: false);
        builder.Services.AddHostedService(implementationFactory: static sp => new SiloStdinRouter(
            administrative: sp.GetRequiredService<TextCommandSource>(),
            host: sp.GetRequiredService<WorldSiloHost>(),
            routing: sp.GetRequiredService<SiloConsoleRouting>()
        ));
        builder.UseOrleans(configureDelegate: siloBuilder => WorldSiloExtensions.ConfigureClustering(builder: siloBuilder, definition: definition!));
        if (definition!.Lifecycle is { } lifecycle) {
            builder.Services.Configure<HostOptions>(configureOptions: options => options.ShutdownTimeout = TimeSpan.FromSeconds((lifecycle.ShutdownSeconds + 5)));
            builder.Services.AddHostedService<WorldSiloLifecycleService>();
        }
        // Every stdout/stderr line a silo run writes from here on carries a '[<row>] '/'[silo] ' prefix (SiloConsoleTagging
        // handles verb output directly; this writer catches engine narration written straight to Console.Out/Error).
        Console.SetOut(newOut: new SiloNarrationWriter(inner: Console.Out));
        Console.SetError(newError: new SiloNarrationWriter(inner: Console.Error));
        if (parseResult.GetValue(option: mcpOption) is { Length: > 0 } mcpConfigPath) {
            if (loadedExtensions.ControlExtensions.Count == 0) {
                Console.Error.WriteLine(value: "[silo] Warning: --mcp was specified, but no IControlExtension (e.g. Puck.Mcp) was found in the extensions directory.");
            } else {
                foreach (var controlExt in loadedExtensions.ControlExtensions) {
                    var controlRegistry = new SiloControlExtensionRegistry();
                    controlExt.Register(registry: controlRegistry);
                    if (controlRegistry.HostedControlFactory is { } factory) {
                        builder.Services.AddHostedService(implementationFactory: sp => {
                            var controlHost = sp.GetRequiredService<IControlSessionHost>();
                            var service = factory(sp, controlHost, mcpConfigPath);
                            return new SiloHostedServiceAdapter(service: service);
                        });
                    }
                }
            }
        }
        foreach (var agentExt in loadedExtensions.AgentExtensions) {
            var agentRegistry = new SiloWorldAgentExtensionRegistry();
            agentExt.Register(registry: agentRegistry);
            if (agentRegistry.AgentRunnerFactory is { } factory) {
                builder.Services.AddHostedService(implementationFactory: sp => {
                    var service = factory(sp);
                    return new SiloHostedServiceAdapter(service: service);
                });
            }
        }
        var host = builder.Build();
        // The silo's own boot-free WorldInstanceHost carries every row this silo ever admits, so one attach here reaches
        // its cross-row/host-level narration for the run's whole lifetime — each admitted row's own WorldServer.Output
        // still narrates unbound (a row activates well after this point; see WorldGrain).
        host.Services.GetRequiredService<WorldSiloHost>().Instances.AttachNarrationSink(sink: new WorldConsoleNarrationSink());
        try {
            await host.RunAsync(cancellationToken);
        } catch (Orleans.Runtime.OrleansLifecycleCanceledException) {
            // A quit that lands before Orleans' own startup lifecycle finishes cancels that lifecycle — an ordinary
            // shutdown race, not a fault; every terminal command already ran on the tick thread before this unwound.
        }
        return Environment.ExitCode;
    }

    private sealed class LoadedExtensions {
        public List<IControlExtension> ControlExtensions { get; } = [];
        public List<Puck.World.Protocol.IWorldAgentExtension> AgentExtensions { get; } = [];
    }

    private sealed class SiloHostedServiceAdapter(Puck.Abstractions.IPuckHostedService service) : IHostedService {
        public Task StartAsync(CancellationToken cancellationToken) => service.StartAsync(cancellationToken: cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => service.StopAsync(cancellationToken: cancellationToken);
    }

    private sealed class SiloControlExtensionRegistry : IControlExtensionRegistry {
        public Func<IServiceProvider, IControlSessionHost, string, Puck.Abstractions.IPuckHostedService>? HostedControlFactory { get; private set; }
        public void RegisterHostedControl(Func<IServiceProvider, IControlSessionHost, string, Puck.Abstractions.IPuckHostedService> factory) {
            HostedControlFactory = factory;
        }
    }

    private sealed class SiloWorldAgentExtensionRegistry : Puck.World.Protocol.IWorldAgentExtensionRegistry {
        public Func<IServiceProvider, Puck.Abstractions.IPuckHostedService>? AgentRunnerFactory { get; private set; }
        public void RegisterAgentRunner(Func<IServiceProvider, Puck.Abstractions.IPuckHostedService> factory) {
            AgentRunnerFactory = factory;
        }
    }

    private static LoadedExtensions LoadDynamicExtensions(string? explicitDir) {
        var serverRegistry = new WorldSiloExtensions.Registry();
        var machineRegistry = new WorldMachineExtensionRegistry();
        var loaded = new LoadedExtensions();

        void Scan(string dir) {
            WorldExtensionLoader.LoadFromDirectory(
                directoryPath: dir,
                serverRegistry: serverRegistry,
                onExtensionLoaded: ext => {
                    if (ext is Puck.GamingBricks.Forge.IGamingBrickExtension brickExtension) {
                        brickExtension.Initialize(registry: machineRegistry);
                    }
                    if (ext is IControlExtension controlExtension) {
                        loaded.ControlExtensions.Add(item: controlExtension);
                    }
                    if (ext is Puck.World.Protocol.IWorldAgentExtension agentExtension) {
                        loaded.AgentExtensions.Add(item: agentExtension);
                    }
                },
                log: static msg => Console.WriteLine(value: msg));
        }

        if (!string.IsNullOrWhiteSpace(value: explicitDir) && Directory.Exists(path: explicitDir)) {
            Scan(dir: explicitDir);
            return loaded;
        }

        var localDir = Path.Combine(Directory.GetCurrentDirectory(), "extensions");

        if (Directory.Exists(path: localDir)) {
            Scan(dir: localDir);
        }

        var appDir = Path.Combine(AppContext.BaseDirectory, "extensions");

        if (Directory.Exists(path: appDir) && !string.Equals(a: localDir, b: appDir, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            Scan(dir: appDir);
        }

        return loaded;
    }
}
