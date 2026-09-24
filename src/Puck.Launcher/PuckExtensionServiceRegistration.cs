using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>Installs a composed <see cref="PuckExtensionSet"/> into a host's services: the set itself, the
/// <c>world.extensions.catalog</c> read-back, and every <see cref="PuckHostedService"/> contribution. Every host installs
/// its set through this one method.</summary>
public static class PuckExtensionServiceRegistration {
    /// <summary>Adds the set, its catalog verb, and its contributed hosted services, in contribution key order, then the
    /// <see cref="HostedControl"/> when the deployment names a control configuration. The container owns the set: it is
    /// disposed with the container, after the hosted services it started.</summary>
    /// <param name="services">The host's services. A named control configuration requires an
    /// <see cref="IControlSessionHost"/> registration.</param>
    /// <param name="extensions">The host's composed extensions.</param>
    /// <param name="controlConfiguration">The path of the deployment's control configuration file, or
    /// <see langword="null"/> to start no hosted control.</param>
    /// <returns>The same services.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="extensions"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="PuckExtensionException"><paramref name="controlConfiguration"/> is named but no composed extension
    /// contributes a <see cref="HostedControl"/>.</exception>
    public static IServiceCollection AddPuckExtensions(this IServiceCollection services, PuckExtensionSet extensions, string? controlConfiguration = null) {
        ArgumentNullException.ThrowIfNull(argument: services);
        ArgumentNullException.ThrowIfNull(argument: extensions);
        if (
            (controlConfiguration is not null) &&
            !extensions.TryGet<HostedControl>(
                contribution: out _,
                key: HostedControl.Key
            )
        ) {
            throw new PuckExtensionException(message: $"A control configuration ('{controlConfiguration}') is named, but no installed extension contributes a {nameof(HostedControl)}.");
        }
        services.AddSingleton(implementationFactory: _ => extensions);
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => new PuckExtensionCommandModule(extensions: sp.GetRequiredService<PuckExtensionSet>()));
        for (var index = 0; (index < extensions.Contributions<PuckHostedService>().Count); index++) {
            var position = index;

            // Resolving the set before creating the service orders the container's disposal: the service first, the set
            // that owns its extension after it.
            services.AddSingleton(implementationFactory: sp => Host(service: sp.GetRequiredService<PuckExtensionSet>().Contributions<PuckHostedService>()[position].Value.Create(arg: sp)));
        }
        if (controlConfiguration is not null) {
            var path = Path.GetFullPath(path: controlConfiguration);

            services.AddSingleton(implementationFactory: sp => {
                sp.GetRequiredService<PuckExtensionSet>().TryGet<HostedControl>(
                    contribution: out var control,
                    key: HostedControl.Key
                );
                return Host(service: control!.Create(
                    arg1: sp,
                    arg2: sp.GetRequiredService<IControlSessionHost>(),
                    arg3: path
                ));
            });
        }
        return services;
    }
    /// <summary>Adapts a contributed service to the .NET host. A service that already implements
    /// <see cref="IHostedService"/> is returned unchanged, keeping its background-task supervision and lifecycle callbacks;
    /// any other is wrapped so that the host starts, stops, and disposes it.</summary>
    /// <param name="service">The contributed service; the host owns it from here.</param>
    /// <returns>The hosted service the container registers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public static IHostedService Host(IPuckHostedService service) {
        ArgumentNullException.ThrowIfNull(argument: service);
        return ((service as IHostedService) ?? new HostedServiceAdapter(service: service));
    }

    private sealed class HostedServiceAdapter(IPuckHostedService service) : IHostedService, IAsyncDisposable, IDisposable {
        public void Dispose() => service.Dispose();
        public ValueTask DisposeAsync() => service.DisposeAsync();
        public Task StartAsync(CancellationToken cancellationToken) => service.StartAsync(cancellationToken: cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => service.StopAsync(cancellationToken: cancellationToken);
    }
}

/// <summary>The <c>world.extensions.catalog</c> read-back: every composed extension and the contributions it
/// registered, by kind and key.</summary>
internal sealed class PuckExtensionCommandModule(PuckExtensionSet extensions) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            description: "Lists this host's composed extensions and each contribution by kind and key. Configuration may select these keys; it cannot load executable paths.",
            handler: (_, args) => (CommandResult.RequireNoArguments(
                args: args,
                verb: "world.extensions.catalog"
            ) ?? new CommandResult(Output: $"[world.extensions.catalog: {string.Join(
                separator: "; ",
                values: extensions.Describe()
            )}]")),
            name: "world.extensions.catalog"
        );
    }
}
