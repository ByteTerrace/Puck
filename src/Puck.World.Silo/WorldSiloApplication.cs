using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.World.Machines;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Composes and runs the silo over its built-in and installed extensions.</summary>
public static class WorldSiloApplication {
    /// <summary>Composes the silo's extensions: its built-in <see cref="WorldServerExtension"/> and every extension
    /// installed in the given directories.</summary>
    /// <param name="directories">The extensions directories to search.</param>
    /// <returns>The silo's composed set.</returns>
    /// <exception cref="PuckExtensionException">An installation or composition conflict.</exception>
    public static PuckExtensionSet ComposeExtensions(IEnumerable<string> directories) => PuckExtensionDiscovery.Compose(
        builtIns: [new WorldServerExtension()],
        directories: directories
    );
    /// <summary>Attaches the silo console to its pending-verb table the way the World host does: a late verdict and
    /// an evicted line print, and an eviction counts in <c>wire.errors</c>. The silo hands the result to
    /// <see cref="WorldSiloHost.AnswerDeferredVerbs"/>, whose rows' echoes count a late refusal there too.</summary>
    /// <param name="services">The built silo's services, holding its <see cref="Puck.World.Protocol.WorldDeferredVerbEchoes"/>
    /// and <see cref="CommandRegistry"/>.</param>
    /// <returns>The attached answers.</returns>
    public static WorldDeferredVerbAnswers AnswerDeferredVerbs(IServiceProvider services) => WorldDeferredVerbAnswers.Attach(
        echoes: services.GetRequiredService<Puck.World.Protocol.WorldDeferredVerbEchoes>(),
        registry: services.GetRequiredService<CommandRegistry>()
    );
    /// <summary>Runs the ordinary silo with the extensions installed in --extensions-dir, or in the default
    /// directories when it is absent.</summary>
    /// <param name="args">The silo's ordinary command-line arguments.</param>
    /// <param name="cancellationToken">Stops the host through its normal lifecycle.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default) {
        var siloOption = new Option<string?>(name: "--silo") {
            DefaultValueFactory = static _ => null,
            Description = "The silo document (puck.silo.configuration.v1) to load. Required.",
        };
        var extensionsDirOption = new Option<string?>(name: "--extensions-dir") {
            DefaultValueFactory = static _ => null,
            Description = "Optional path to the one extensions directory to search. Defaults to ./extensions and <app>/extensions.",
        };
        var mcpOption = new Option<string?>(name: "--mcp") {
            DefaultValueFactory = static _ => null,
            Description = "Optional path to the control configuration (remote.json) the installed hosted control reads. Refused when no installed extension contributes one.",
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
        var extensionsDirectory = parseResult.GetValue(option: extensionsDirOption);

        if (
            (extensionsDirectory is not null) &&
            !Directory.Exists(path: extensionsDirectory)
        ) {
            Console.Error.WriteLine(value: $"--extensions-dir '{extensionsDirectory}' does not exist.");

            return 1;
        }

        PuckExtensionSet extensions;
        WorldMachineCatalog machineCatalog;

        try {
            extensions = ComposeExtensions(directories: ((extensionsDirectory is null)
                ? PuckExtensionDiscovery.DefaultDirectories()
                : [extensionsDirectory]
            ));
            machineCatalog = WorldMachineCatalog.From(extensions: extensions);
        } catch (Exception error) when ((error is PuckExtensionException or ArgumentException)) {
            Console.Error.WriteLine(value: $"[silo] extensions refused: {error.Message}");

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
        // Standard output carries the tagged console answers a driving script reads; every log line goes to standard
        // error beside the narration.
        builder.Logging.AddConsole(configure: static options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        // Orleans' own INFO-level startup narration (cluster config dumps, membership chatter) would otherwise bury the
        // engine's own [world.listen: bound …]/[silo.*] lines a driving script polls for.
        builder.Logging.AddFilter(
            category: "Orleans",
            level: LogLevel.Warning
        );
        builder.Logging.AddFilter(
            category: "Microsoft.Orleans",
            level: LogLevel.Warning
        );
        builder.Services.AddSingleton(implementationInstance: definition!);
        builder.Services.AddSingleton(implementationInstance: machineCatalog);
        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: builder.Services);
        try {
            WorldSiloExtensions.Add(
                definition: definition!,
                extensions: extensions,
                services: builder.Services
            );
            builder.Services.AddPuckExtensions(
                controlConfiguration: ((parseResult.GetValue(option: mcpOption) is { Length: > 0 } mcpConfigPath)
                    ? mcpConfigPath
                    : null),
                extensions: extensions
            );
        } catch (Exception error) when ((error is PuckExtensionException or ArgumentException)) {
            Console.Error.WriteLine(value: $"[silo] extensions refused: {error.Message}");

            return 1;
        }
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
            machineCatalog: sp.GetRequiredService<WorldMachineCatalog>(),
            authentication: (selection, federation, clock) => WorldSiloExtensions.Authenticate(
                clock: clock,
                extensions: sp.GetRequiredService<PuckExtensionSet>(),
                federation: federation,
                selection: selection
            ),
            contentAdmissionPolicy: sp.GetService<IMachineContentAdmissionPolicy>(),
            extensions: sp.GetRequiredService<PuckExtensionSet>()
        ));
        builder.Services.AddSingleton<IWorldWaitGateResolver>(implementationFactory: static sp => sp.GetRequiredService<WorldSiloHost>());
        builder.Services.AddHostedService<WorldSiloActivations>();
        builder.Services.AddSingleton<ICommandModule, SiloCommandModule>();
        builder.Services.AddSingleton<ICommandModule, WorldWaitCommandModule>();
        builder.Services.AddSingleton<ICommandModule, WorldNetworkCommandModule>();
        builder.Services.AddSingleton<Puck.World.Protocol.IServerLink, SiloServerLink>();
        builder.Services.AddSingleton<Puck.World.Protocol.WorldDeferredVerbEchoes>();
        builder.Services.AddSingleton<ICommandModule, WorldStateCommandModule>();
        builder.Services.AddSingleton<ICommandModule, WorldExtensionsCommandModule>();
        builder.Services.AddSingleton<ICommandModule, WorldMachineCommandModule>();
        // A bare router/registry: the silo embodies no local seats and drives no physical input, so the bindings and
        // principal resolver below bind nothing and claim no principal — HeadlessTickHostedService still requires exactly
        // one IFixedStepSimulation paired with exactly one InputRouter, so this is the minimal pair that satisfies it
        // without AddFixedStepSimulation's seat-oriented registration.
        builder.Services.AddSingleton<WorldSiloSimulation>();
        builder.Services.AddSingleton<IFixedStepSimulation>(implementationFactory: static sp => sp.GetRequiredService<WorldSiloSimulation>());
        builder.Services.AddSingleton<IInputBindings, WorldSiloBareInputBindings>();
        builder.Services.AddSingleton<IPrincipalResolver, WorldSiloBarePrincipalResolver>();
        builder.Services.AddSingleton(implementationFactory: static sp => new InputRouter(
            bindings: sp.GetRequiredService<IInputBindings>(),
            clock: sp.GetRequiredService<IInputClock>(),
            principalResolver: sp.GetRequiredService<IPrincipalResolver>(),
            registry: sp.GetRequiredService<CommandRegistry>()
        ));
        builder.Services.AddLauncherHeadlessTerminal(readStandardInput: false);
        builder.Services.AddHostedService(implementationFactory: static sp => new SiloStdinRouter(
            administrative: sp.GetRequiredService<TextCommandSource>(),
            host: sp.GetRequiredService<WorldSiloHost>(),
            routing: sp.GetRequiredService<SiloConsoleRouting>()
        ));
        builder.UseOrleans(configureDelegate: siloBuilder => WorldSiloExtensions.ConfigureClustering(
            builder: siloBuilder,
            definition: definition!
        ));
        if (definition!.Lifecycle is { } lifecycle) {
            builder.Services.Configure<HostOptions>(configureOptions: options => options.ShutdownTimeout = TimeSpan.FromSeconds(seconds: (lifecycle.ShutdownSeconds + 5)));
            builder.Services.AddHostedService<WorldSiloLifecycleService>();
        }
        // Every stdout/stderr line a silo run writes from here on carries a '[<row>] '/'[silo] ' prefix (SiloConsoleTagging
        // handles verb output directly; this writer catches engine narration written straight to Console.Out/Error).
        Console.SetOut(newOut: new SiloNarrationWriter(inner: Console.Out));
        Console.SetError(newError: new SiloNarrationWriter(inner: Console.Error));
        var host = builder.Build();
        // The silo's own boot-free WorldInstanceHost carries every row this silo ever admits, so one attach here reaches
        // its cross-row/host-level narration for the run's whole lifetime — each admitted row's own WorldServer.Output
        // still narrates unbound (a row activates well after this point; see WorldGrain).
        host.Services.GetRequiredService<WorldSiloHost>().Instances.AttachNarrationSink(sink: new WorldConsoleNarrationSink());
        BackgroundService[] backgroundServices;

        // Resolving the hosted services runs each contributed factory, which is where a hosted control reads its
        // configuration and selects among the installed providers.
        try { backgroundServices = [.. host.Services.GetServices<IHostedService>().OfType<BackgroundService>()]; } catch (Exception error) when ((error is PuckExtensionException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)) {
            // The narration writer installed above tags this line.
            Console.Error.WriteLine(value: $"extensions refused: {error.Message}");
            if (host is IAsyncDisposable disposable) { await disposable.DisposeAsync(); } else { host.Dispose(); }

            return 1;
        }
        var silo = host.Services.GetRequiredService<WorldSiloHost>();

        silo.AnswerDeferredVerbs(answers: AnswerDeferredVerbs(services: host.Services));
        HostResourceUnavailableException? unavailable = null;

        try {
            await host.RunAsync(token: cancellationToken);
        } catch (Orleans.Runtime.OrleansLifecycleCanceledException) {
            // A quit that lands before Orleans' own startup lifecycle finishes cancels that lifecycle — an ordinary
            // shutdown race, not a fault; every terminal command already ran on the tick thread before this unwound.
        } catch (Exception failure) when ((LauncherHostRun.FindUnavailable(exception: failure) is { } found)) {
            unavailable = found;
        }
        var faulted = backgroundServices.Where(predicate: static service => (service.ExecuteTask?.IsFaulted == true)).ToArray();

        // A listener this host cannot bind ends a failed run as an unsupported environment, the shape World's own host
        // reports. A row door's failure reaches the faulted activation across a grain call, which need not preserve its
        // type, so the silo's own copy is read before the faults themselves. A run that ended normally reports nothing.
        if (faulted.Length > 0) {
            unavailable ??= (silo.HostUnavailable ?? faulted
                .Select(selector: static service => LauncherHostRun.FindUnavailable(exception: service.ExecuteTask!.Exception!))
                .FirstOrDefault(predicate: static found => (found is not null)));
        }
        if (unavailable is not null) {
            return LauncherHostRun.ReportUnsupported(
                error: Console.Error,
                label: "silo",
                unavailable: unavailable
            );
        }
        // StopHost supervises background failures but does not set the process exit code. A failed configured
        // service must remain a failed worker to Docker/systemd even when shutdown itself drains successfully.
        return ((faulted.Length > 0)
            ? 1
            : Environment.ExitCode
        );
    }
}
