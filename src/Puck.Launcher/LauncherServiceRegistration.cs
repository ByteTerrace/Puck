using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Pacing;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher.Commands;

namespace Puck.Launcher;

/// <summary>Explicit, composable service registration for the launcher: every dependency is wired by hand
/// against the Puck.* libraries — no engine-wide bring-up helper. Grouped by concern so the composition
/// root stays readable.</summary>
public static class LauncherServiceRegistration {
    /// <summary>The backend-neutral terminal: the terminal-control <em>baton</em>, the command pump, and the window
    /// run loop. It carries no graphics backend AND no platform windowing — the run loop drives an
    /// <see cref="ISurfacePresenter"/>, whichever root <see cref="IRenderNode"/> the developer registers, and the
    /// <c>INativeWindowFactory</c> the composition root supplies. The composition root supplies a backend (e.g.
    /// <c>AddVulkanPresenter</c>, which also provides the default root <see cref="IHostContext"/>), the native windowing
    /// (e.g. <c>AddPlatformWindowing</c>), the root <see cref="IRenderNode"/>, a <see cref="BindingCommandSource"/> (the
    /// key bindings the pump reads), and any engine-specific <see cref="ICommandModule"/>s.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddLauncherTerminal(this IServiceCollection services) {
        // The terminal's held capabilities, both backed by the one TerminalControl: the baton (terminal
        // ownership/lifecycle) and input focus (the right to receive input). The backend's root host context
        // publishes them as HELD on the root context, so the root engine holds them via HoldsCapability and
        // hosted children do not — the capability-permission system. The window loop drains exit + routes
        // input through them.
        services.TryAddSingleton<LauncherOptions>();

        // The shared monotonic capture clock: every input backend stamps CaptureTick from this one instance, and
        // the window pump uses it to time-stamp drained input. One origin so all stamps are comparable.
        services.TryAddSingleton<InputClock>(implementationFactory: static _ => InputClock.Start());
        services.TryAddSingleton<IInputClock>(implementationFactory: static sp => sp.GetRequiredService<InputClock>());

        // The genlock (latency phase-align) ingestion seam: external rhythm producers (cameras, capture cards, network
        // feeds) register named sources here, and the HOST's election policy decides which single one the pacer
        // phase-aligns to. TryAdd default = automatic single-source election; a composition root overrides with a
        // document-configured instance. With no publisher (or no election) the pacer is unaffected.
        services.TryAddSingleton<ExternalClockRegistry>();

        services.TryAddSingleton<TerminalControl>();
        services.TryAddSingleton<ITerminalControl>(implementationFactory: static sp => sp.GetRequiredService<TerminalControl>());
        services.TryAddSingleton<IInputFocus>(implementationFactory: static sp => sp.GetRequiredService<TerminalControl>());

        // Contribute the terminal's held capabilities (the baton + input focus) to the root host context, and
        // register the aggregator that assembles that context from every module's contributions — the device
        // capability from whichever graphics backend is composed in, plus these — so neither side references
        // the other. Registered with TryAdd so a composition root may publish its own root context instead.
        services.AddSingleton(implementationFactory: static sp => new HostCapabilityContribution(
            CapabilityType: typeof(IInputFocus),
            Instance: sp.GetRequiredService<IInputFocus>(),
            IsHeld: true
        ));
        services.AddSingleton(implementationFactory: static sp => new HostCapabilityContribution(
            CapabilityType: typeof(ITerminalControl),
            Instance: sp.GetRequiredService<ITerminalControl>(),
            IsHeld: true
        ));
        services.TryAddSingleton<IHostContext>(implementationFactory: static sp => {
            var heldCapabilities = new Dictionary<Type, object>();
            var inheritedCapabilities = new Dictionary<Type, object>();

            foreach (var contribution in sp.GetServices<HostCapabilityContribution>()) {
                var target = (contribution.IsHeld
                    ? heldCapabilities
                    : inheritedCapabilities);

                _ = target.TryAdd(contribution.CapabilityType, contribution.Instance);
            }

            return new HostContext(
                capabilities: inheritedCapabilities,
                heldCapabilities: heldCapabilities
            );
        });

        // Command pump: the registry, the stdin text source (results echoed to stdout so scripted runs are
        // assertable), and the per-frame shell. The keyboard binding source and the input adapter are
        // developer-supplied (they encode the engine's controls), so the pump stays engine-agnostic.
        services.TryAddSingleton<CommandRegistry>();
        services.TryAddSingleton(implementationFactory: static provider => new TextCommandSource(
            onResult: static (line, result) => {
                if (!string.IsNullOrEmpty(value: result.Output)) {
                    Console.Out.WriteLine(value: result.Output);
                }
            },
            registry: provider.GetRequiredService<CommandRegistry>()
        ));
        services.TryAddSingleton(implementationFactory: static sp => new CommandShell(
            // Any source registered as ICommandSource (e.g. the gamepad source) is pulled each frame alongside
            // the keyboard and text sources; the keyboard/text sources are registered by concrete type, so they
            // are not double-added here.
            additionalSources: sp.GetServices<ICommandSource>(),
            bindingSource: sp.GetRequiredService<BindingCommandSource>(),
            registry: sp.GetRequiredService<CommandRegistry>(),
            textSource: sp.GetRequiredService<TextCommandSource>()
        ));

        // The terminal's own command surface (just `quit`, which drives the baton) and the two hosted
        // services: the window/run loop and the stdin reader.
        services.AddSingleton<ICommandModule, TerminalCommandModule>();
        services.AddHostedService<LauncherWindowHostedService>();
        services.AddHostedService(implementationFactory: static sp => new StandardInputReaderService(
            source: sp.GetRequiredService<TextCommandSource>(),
            threadName: "Puck.Launcher Stdin Reader"
        ));

        return services;
    }

    /// <summary>Registers the generic backend switch: it fronts every contributed <see cref="SurfacePresenterDescriptor"/>
    /// through a <see cref="BackendSwitcher"/> (preferring <paramref name="preferredBackend"/>), replaces the root
    /// <see cref="ISurfacePresenter"/> with it so the run loop presents through the active backend, and adds the
    /// <c>backend</c> toggle command. The composition root contributes one <see cref="SurfacePresenterDescriptor"/> per
    /// backend, so the launcher itself never names a concrete backend.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="preferredBackend">The display name of the backend to start active; falls back to the first contributed when absent.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="preferredBackend"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddBackendSwitcher(this IServiceCollection services, string preferredBackend) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(preferredBackend);

        services.AddSingleton(implementationFactory: sp => {
            var descriptors = new List<SurfacePresenterDescriptor>(collection: sp.GetServices<SurfacePresenterDescriptor>());

            if (descriptors.Count == 0) {
                throw new InvalidOperationException(message: "No surface presenters were registered; contribute at least one SurfacePresenterDescriptor before calling AddBackendSwitcher.");
            }

            var preferred = (descriptors.Find(match: descriptor => string.Equals(descriptor.Name, preferredBackend, StringComparison.OrdinalIgnoreCase)) ?? descriptors[0]);
            var other = descriptors.Find(match: descriptor => !ReferenceEquals(descriptor, preferred));

            return (other is null)
                ? new BackendSwitcher(current: preferred.Presenter, currentName: preferred.Name, other: null, otherName: null)
                : new BackendSwitcher(current: preferred.Presenter, currentName: preferred.Name, other: other.Presenter, otherName: other.Name);
        });
        services.Replace(ServiceDescriptor.Singleton<ISurfacePresenter>(implementationFactory: static sp => sp.GetRequiredService<BackendSwitcher>()));
        services.AddSingleton<ICommandModule, BackendCommandModule>();

        return services;
    }
}
