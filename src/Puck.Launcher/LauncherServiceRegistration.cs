using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Attestation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher.Commands;
using Puck.Launcher.Release;

namespace Puck.Launcher;

/// <summary>Explicit, composable service registration for the launcher: every dependency is wired by hand
/// against the Puck.* libraries — no engine-wide bring-up helper. Grouped by concern so the composition
/// root stays readable.</summary>
public static class LauncherServiceRegistration {
    // The registrations both boot shapes share: the terminal baton, the command pump (registry/stdin/shell), and the
    // capability aggregation — everything AddLauncherTerminal registered before it added its own hosted service. Pure
    // state/plumbing: nothing here opens a window, a GPU device, or a swapchain, so it is safe to register
    // unconditionally in both AddLauncherTerminal and AddLauncherHeadlessTerminal.
    private static void AddLauncherTerminalShared(IServiceCollection services) {
        // The terminal's held capabilities, both backed by the one TerminalControl: the baton (terminal
        // ownership/lifecycle) and input focus (the right to receive input). The backend's root host context
        // publishes them as HELD on the root context, so the root engine holds them via HoldsCapability and
        // hosted children do not — the capability-permission system. The window loop drains exit + routes
        // input through them.
        services.TryAddSingleton<LauncherOptions>();

        // The live present-rate control the window pump's display-aware pacer reads: seeded from the configured target rate, then
        // retargeted mid-session by the `present-rate` verb (presentation pacing only — the fixed-step sim is untouched).
        services.TryAddSingleton(implementationFactory: static sp => new PresentPacingControl(initialTargetHertz: sp.GetRequiredService<LauncherOptions>().TargetRenderRate));

        // The in-process CPU frame-timing publish hub: the window loop publishes one FrameTimingSample per iteration
        // while GPU timing is armed, and observers (the [frame-timing] stderr digest, a bench runner) subscribe. A
        // presentation-side diagnostic seam, registered beside the present-pacing control.
        services.TryAddSingleton<FrameTimingHub>();

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
        services.TryAddSingleton<TerminalConsoleSessions>();
        services.TryAddSingleton<IConsoleSessions>(implementationFactory: static sp => sp.GetRequiredService<TerminalConsoleSessions>());
        services.TryAddSingleton<IAlwaysActiveInputBindings, TerminalInputBindings>();

        // Contribute the terminal's held capabilities (the baton + input focus) to the root host context, and
        // register the aggregator that assembles that context from every module's contributions — the device
        // capability from whichever graphics backend is composed in, plus these — so neither side references
        // the other. Registered with TryAdd so a composition root may publish its own root context instead. A
        // headless boot never contributes a device capability, so IHostContext ends up carrying only these two.
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
                    : inheritedCapabilities
                );

                _ = target.TryAdd(
                    key: contribution.CapabilityType,
                    value: contribution.Instance
                );
            }

            return new HostContext(
                capabilities: inheritedCapabilities,
                heldCapabilities: heldCapabilities
            );
        });

        // Command pump: the registry and the stdin text source (results echoed to stdout so scripted runs are
        // assertable). The text source owns both the queue and its per-frame Collect drain. The keyboard binding
        // source and the input adapter are developer-supplied (they encode the engine's controls), so the pump stays
        // engine-agnostic.
        services.TryAddSingleton<CommandRegistry>();
        // The once-per-frame buffered stdout the pump flushes after each Collect (see BufferedConsoleOutput): the result
        // echoes append into it instead of each paying an AutoFlush syscall, collapsing a piped burst to one write.
        services.TryAddSingleton<BufferedConsoleOutput>();
        services.TryAddEnumerable(descriptor: ServiceDescriptor.Singleton<ICommandObserver, SimulationCommandOutputObserver>());
        services.TryAddSingleton(implementationFactory: static provider => {
            var output = provider.GetRequiredService<BufferedConsoleOutput>();
            var holdGates = provider.GetServices<ITextCommandHoldGate>().ToArray();

            return new TextCommandSource(
                onResult: (line, result) => {
                    // Windowed hosts attach their per-seat bank after registry composition. Mirror every operator
                    // exchange into the displayed seat-one tape; headless roots simply leave the stable proxy
                    // unattached while stdout/stderr remain the authoritative script streams.
                    provider.GetRequiredService<TerminalConsoleSessions>().RecordAdministrative(
                        line: line,
                        result: result
                    );

                    if (string.IsNullOrEmpty(value: result.Output)) {
                        return;
                    }

                    // A REFUSED line goes to stderr; an accepted one to the buffered stdout. This is the only place a
                    // submitted line and its verdict are both in scope, so it is where the two are separated — without
                    // it a rejection is byte-shaped like success on the same stream and a scripted run reads green.
                    if (result.IsError) {
                        output.WriteErrorLine(value: result.Output);
                    } else {
                        output.WriteLine(value: result.Output);
                    }
                },
                registry: provider.GetRequiredService<CommandRegistry>()
            ) {
                HoldGate = ((holdGates.Length == 0)
                ? null
                : () => IsAnyTextCommandGateHolding(gates: holdGates)),
            };
        });
        // The terminal's own command surface (just `quit`, which drives the baton). The fixed-step/tick hosted
        // service AND the stdin reader are added by the CALLER (AddLauncherTerminal / AddLauncherHeadlessTerminal),
        // in that relative order — the generic host starts IHostedServices in REGISTRATION order, and the fixed-step
        // pump starting before the stdin reader (not after) is the ORIGINAL AddLauncherTerminal's own order; adding
        // the reader here (ahead of the caller's own hosted service) would flip it.
        services.AddSingleton<ICommandModule, TerminalCommandModule>();
    }
    // The stdin reader — registered by both AddLauncherTerminal and AddLauncherHeadlessTerminal, AFTER their own
    // fixed-step/tick hosted service, so host startup order matches AddLauncherTerminal's original shape exactly.
    private static void AddStandardInputReader(IServiceCollection services) {
        services.AddHostedService(implementationFactory: static sp => new StandardInputReaderService(
            source: sp.GetRequiredService<TextCommandSource>(),
            threadName: "Puck.Launcher Stdin Reader"
        ));
    }
    private static bool IsAnyTextCommandGateHolding(IReadOnlyList<ITextCommandHoldGate> gates) {
        for (var index = 0; (index < gates.Count); index++) {
            if (gates[index].IsHolding()) {
                return true;
            }
        }

        return false;
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

            var preferred = (descriptors.Find(match: descriptor => string.Equals(
                a: descriptor.Name,
                b: preferredBackend,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) ?? descriptors[0]);
            var other = descriptors.Find(match: descriptor => !ReferenceEquals(
                objA: descriptor,
                objB: preferred
            ));

            return ((other is null)
                ? new BackendSwitcher(
                    current: preferred.Presenter,
                    currentName: preferred.Name,
                    other: null,
                    otherName: null
                )
                : new BackendSwitcher(
                    current: preferred.Presenter,
                    currentName: preferred.Name,
                    other: other.Presenter,
                    otherName: other.Name
                )
            );
        });
        services.Replace(descriptor: ServiceDescriptor.Singleton<ISurfacePresenter>(implementationFactory: static sp => sp.GetRequiredService<BackendSwitcher>()));
        services.AddSingleton<ICommandModule, BackendCommandModule>();

        return services;
    }
    /// <summary>Registers self-update: an app that does not call this has no update path at all. Wires
    /// <see cref="UpdateService"/> (backing <c>update.status</c>/<c>update.check</c>) over
    /// <paramref name="releaseSource"/>, an <see cref="AttestationReleaseVerifier"/> pinned to
    /// <paramref name="options"/>'s <see cref="UpdateOptions.TrustAnchor"/>, a durable
    /// <see cref="FileReleaseSequenceStore"/>, and a <see cref="ContentAddressedUpdateStager"/> rooted at
    /// <paramref name="options"/>'s <see cref="UpdateOptions.CacheRoot"/>. The composition root supplies
    /// <paramref name="releaseSource"/> directly — <see cref="HttpReleaseSource"/> normally,
    /// <see cref="DirectoryReleaseSource"/> for laws and canaries — so this method never selects a transport
    /// itself.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The resolved operational configuration plus the build-pinned trust anchor.</param>
    /// <param name="releaseSource">The transport to fetch the channel manifest and payload files through.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any parameter is <see langword="null"/>.</exception>
    public static IServiceCollection AddSelfUpdate(this IServiceCollection services, UpdateOptions options, IReleaseSource releaseSource) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(releaseSource);

        services.TryAddSingleton(implementationFactory: _ => options);
        services.TryAddSingleton(implementationFactory: _ => releaseSource);
        services.TryAddSingleton<IReleaseSequenceStore>(implementationFactory: _ => new FileReleaseSequenceStore(
            filePath: Path.Combine(path1: options.CacheRoot, path2: "state", path3: "sequence-mark")
        ));
        services.TryAddSingleton<IReleaseVerifier>(implementationFactory: sp => {
            if (options.TrustAnchor.IsPlaceholder) {
                return new PlaceholderReleaseVerifier();
            }

            var trustList = new TrustList(
                entries: [options.TrustAnchor.ToTrustListEntry(
                    maximumAge: null,
                    reach: new HashSet<string>(comparer: StringComparer.Ordinal) { AttestationReleaseVerifier.Slot }
                )],
                defaultMaximumAge: null,
                replayAcceptanceHorizon: (options.ReplayAcceptanceHorizon ?? AddSelfUpdateDefaults.ReplayAcceptanceHorizon)
            );

            return new AttestationReleaseVerifier(
                codec: new CborAttestationCodec(),
                sequenceStore: sp.GetRequiredService<IReleaseSequenceStore>(),
                trustList: trustList
            );
        });
        services.TryAddSingleton(implementationFactory: _ => new ContentAddressedStore(root: Path.Combine(path1: options.CacheRoot, path2: "objects")));
        services.TryAddSingleton<IUpdateStager>(implementationFactory: sp => new ContentAddressedUpdateStager(
            cache: sp.GetRequiredService<ContentAddressedStore>(),
            cacheRoot: options.CacheRoot,
            source: sp.GetRequiredService<IReleaseSource>()
        ));
        services.TryAddSingleton<IUpdateApplier, FileUpdateApplier>();
        services.TryAddSingleton(implementationFactory: sp => new UpdateService(
            applier: sp.GetRequiredService<IUpdateApplier>(),
            options: options,
            source: sp.GetRequiredService<IReleaseSource>(),
            stager: sp.GetRequiredService<IUpdateStager>(),
            verifier: sp.GetRequiredService<IReleaseVerifier>()
        ));
        services.AddSingleton<ICommandModule, UpdateCommandModule>();
        services.AddHostedService<SelfUpdateHealthGateHostedService>();

        return services;
    }
    /// <summary>Registers the launcher's deterministic fixed-step easy path: one consumer, one slot-aware input router,
    /// and one command snapshot per host-owned fixed tick. The ordinary terminal registration still supplies the window,
    /// clock, command registry, and run loop.</summary>
    /// <typeparam name="TSimulation">The composition root's authoritative simulation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="bindings">The physical-input bindings folded into snapshots.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddFixedStepSimulation<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSimulation
    >(this IServiceCollection services, IInputBindings bindings)
        where TSimulation : class, IFixedStepSimulation {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IInputBindings>(instance: bindings);
        services.TryAddSingleton<TSimulation>();
        services.TryAddSingleton<IFixedStepSimulation>(implementationFactory: static sp => sp.GetRequiredService<TSimulation>());
        services.TryAddSingleton(implementationFactory: static sp => {
            var slotResolver = sp.GetService<IInputSlotResolver>();
            var bindings = sp.GetRequiredService<IInputBindings>();
            var clock = sp.GetRequiredService<IInputClock>();
            // REQUIRED, not optional: the mixer stamps every captured entry with the lane's acting identity, and there
            // is no defensible fallback — synthesizing a seat from the slot number would attribute a claimant's action
            // to the seat it displaced. A root that drives a simulation declares who its slots are.
            var principalResolver = sp.GetRequiredService<ICommandPrincipalResolver>();
            var registry = sp.GetRequiredService<CommandRegistry>();
            var alwaysActiveBindings = sp.GetService<IAlwaysActiveInputBindings>();

            return ((slotResolver is null)
                ? new InputRouter(
                    bindings: bindings,
                    clock: clock,
                    principalResolver: principalResolver,
                    registry: registry,
                    alwaysActiveBindings: alwaysActiveBindings
                )
                : new InputRouter(
                    alwaysActiveBindings: alwaysActiveBindings,
                    bindings: bindings,
                    clock: clock,
                    principalResolver: principalResolver,
                    registry: registry,
                    slotResolver: slotResolver
                )
            );
        });

        return services;
    }
    /// <summary>The headless twin of <see cref="AddLauncherTerminal"/>: the SAME command pump and terminal baton, but
    /// <see cref="HeadlessTickHostedService"/> paces the fixed step instead of <see cref="LauncherWindowHostedService"/>
    /// — no graphics backend, no platform windowing, no <see cref="IRenderNode"/> ever resolved. A composition root
    /// calls this INSTEAD OF <see cref="AddLauncherTerminal"/> (never both) when its boot shape has no presentation
    /// composed.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="readStandardInput">Whether to register <see cref="StandardInputReaderService"/> against the
    /// single administrative <see cref="TextCommandSource"/>. <see langword="true"/> (the default) is every existing
    /// caller's shape unchanged. A composition root hosting several independently-addressed sessions over one stdin
    /// (<c>Puck.World.Silo</c>'s tagged <c>@&lt;key&gt;</c> routing) passes <see langword="false"/> and registers its
    /// own stdin reader instead — the command pump and terminal baton are still wired identically either way.</param>
    public static IServiceCollection AddLauncherHeadlessTerminal(this IServiceCollection services, bool readStandardInput = true) {
        AddLauncherTerminalShared(services: services);
        services.AddHostedService<HeadlessTickHostedService>();

        if (readStandardInput) {
            AddStandardInputReader(services: services);
        }

        return services;
    }
    /// <summary>The offscreen twin of <see cref="AddLauncherTerminal"/>: the SAME command pump and terminal baton, but
    /// <see cref="OffscreenTickHostedService"/> paces the fixed step AND produces one composed frame per iteration —
    /// no <see cref="Puck.Abstractions.Presentation.ISurfacePresenter"/>, no platform windowing registered here. The
    /// composition root supplies the GPU backend, the root <see cref="IRenderNode"/>, and an
    /// <see cref="OffscreenRenderOptions"/> registration; it calls this INSTEAD OF <see cref="AddLauncherTerminal"/>
    /// or <see cref="AddLauncherHeadlessTerminal"/> (never more than one) when its boot shape composes a GPU device
    /// with no window.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddLauncherOffscreenTerminal(this IServiceCollection services) {
        AddLauncherTerminalShared(services: services);
        services.AddHostedService<OffscreenTickHostedService>();
        AddStandardInputReader(services: services);

        return services;
    }
    /// <summary>The backend-neutral terminal: the terminal-control <em>baton</em>, the command pump, and the window
    /// run loop. It carries no graphics backend AND no platform windowing — the run loop drives an
    /// <see cref="ISurfacePresenter"/>, whichever root <see cref="IRenderNode"/> the developer registers, and the
    /// <c>INativeWindowFactory</c> the composition root supplies. The composition root supplies a backend (e.g.
    /// <c>AddVulkanPresenter</c>, which also provides the default root <see cref="IHostContext"/>), the native windowing
    /// (e.g. <c>AddPlatformWindowing</c>), the root <see cref="IRenderNode"/>, the <see cref="IInputBindings"/> the
    /// router folds physical input through, and any engine-specific <see cref="ICommandModule"/>s.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddLauncherTerminal(this IServiceCollection services) {
        AddLauncherTerminalShared(services: services);
        // Hosting owns the generic session/cadence controller; the windowed launcher owns its placement between root
        // production and presentation. Register one idle controller for every windowed host so composition roots need
        // only arm it with their chosen ICaptureSink (PNG sequence, recording session, verifier, and so on).
        services.TryAddSingleton<FrameCaptureController>();
        services.AddHostedService<LauncherWindowHostedService>();
        AddStandardInputReader(services: services);

        return services;
    }
}
