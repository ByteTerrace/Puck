using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Composes and runs the silo, allowing an outer distribution to install optional host extensions.</summary>
public static class WorldSiloApplication {
    /// <summary>Runs the ordinary silo with optional service registrations supplied by its trusted composition owner.</summary>
    /// <param name="args">The silo's ordinary command-line arguments.</param>
    /// <param name="configureBuilder">Optional host extensions, registered before building the host.</param>
    /// <param name="cancellationToken">Stops the host through its normal lifecycle.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, Action<IHostApplicationBuilder>? configureBuilder = null,
        CancellationToken cancellationToken = default) {
        var siloOption = new Option<string?>(name: "--silo") {
            DefaultValueFactory = static _ => null,
            Description = "The silo document (puck.silo.def.v1) to load. Required.",
        };
        var launchCommand = new RootCommand(description: "Puck World Silo") {
            siloOption,
        };
        var parseResult = launchCommand.Parse(args);
        if (parseResult.GetValue(option: siloOption) is not { Length: > 0 } siloPath) {
            Console.Error.WriteLine(value: "--silo <path> is required.");
        
            return 1;
        }
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
        configureBuilder?.Invoke(builder);
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
}
