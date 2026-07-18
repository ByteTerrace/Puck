using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Demo.Ui;
using Puck.Hosting;
using Puck.Input;
using Puck.Scene;

namespace Puck.Demo;

/// <summary>
/// The demo's service composition, housed OUTSIDE <c>Program</c> (whose top-level <c>Main</c> is at its class-coupling
/// ceiling): the platform windowing, both GPU presenters + the named backend switch, the run-document registration,
/// and the command / input modules all register here, so the entry point names one seam instead of the whole
/// composition's type surface. Mirrors the <c>ForgeCliSeams</c> / <c>ScenarioCliSeams</c> escape.
/// </summary>
internal static class DemoHost {
    /// <summary>Registers the launcher terminal, platform windowing, camera capture, allocator, both presenters (Vulkan
    /// first so it wins the shared compute-seam TryAdds), the named backend switch, the run document, and the demo's
    /// command / input modules.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="document">The resolved run document.</param>
    /// <param name="capturePath">The optional one-shot capture path.</param>
    /// <param name="width">The resolved window width.</param>
    /// <param name="height">The resolved window height.</param>
    /// <param name="startWithDirectX">Whether the window hosts on Direct3D 12.</param>
    /// <returns>The parity result the run-document registration produced (a validation gate fills it before exit).</returns>
    public static ParityResult RegisterServices(IServiceCollection services, PuckRunDocument document, string? capturePath, uint width, uint height, bool startWithDirectX) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(document);

        // The stdin text source, registered BEFORE the launcher terminal block so the launcher's TryAdd defers to
        // this one. The unification contract says stdin, the on-screen panel, and stdout are ONE console — so a
        // scripted verb's result must land in the panel scrollback too, not just stdout. Each submitted line echoes
        // into the on-screen history ONLY (stdout already knows what the pipe sent — the assertable stream stays
        // byte-identical), and the result routes through DemoConsole.WriteLine — the SAME sink the panel's enter
        // verb uses — which appends the history row, republishes the overlay frame, strips enrichment for the
        // stdout copy, and redraws any in-progress input line. Presentation-only, frame-thread-drained: no
        // wall-clock, no simulation state.
        services.AddSingleton(implementationFactory: static provider => {
            var console = provider.GetRequiredService<DemoConsole>();
            // The toast tap rides the SAME least-coupled result seam: a scripted verb's result additionally surfaces
            // as a transient on-screen chip (the overlay-panels node suppresses it while the console panel is open —
            // the panel already shows results). The [OK]/[ERR] coloring is a display heuristic only (ToastClassifier).
            var toasts = provider.GetRequiredService<ToastStore>();
            // The tick-transcript recorder's OTHER tap: typed console lines never flow through ICommandObserver (the
            // Submit path returns its result directly instead of notifying observers), so a piped script's verbs
            // need this forward to show up in tick.explain's narration.
            var transcript = provider.GetRequiredService<TickTranscriptRecorder>();

            return new TextCommandSource(
                onResult: (line, result) => {
                    console.EchoSubmittedLine(line: line);
                    transcript.RecordTextCommand(line: line);

                    if (!string.IsNullOrEmpty(value: result.Output)) {
                        console.WriteLine(message: result.Output);
                        toasts.Publish(isError: ToastClassifier.IsError(message: result.Output), message: result.Output);
                    }
                },
                registry: provider.GetRequiredService<CommandRegistry>()
            );
        });

        // The shared trimmed-GPU-host block (windowing, camera, allocator, presenters, backend switch) lives in
        // GpuHostComposition — the ORDER-MATTERS Vulkan-wins-the-compute-seam rule is documented there, once.
        GpuHostComposition.AddTrimmedGpuHost(services: services, preferredBackend: (startWithDirectX ? "directx" : "vulkan"), registerDirectXBackend: true);

        var parityResult = DemoRunRegistrar.RegisterRunDocument(capturePath: capturePath, document: document, height: height, hostsOnDirectX: startWithDirectX, services: services, width: width);

        // The ENGINE BENCHMARK (puck.bench): the harness + the control-plane switch registry as DI singletons, their
        // generic console modules (feature.* / bench.*), and the post-hoc installer that attaches the demo's scenes +
        // the §4 switch roster after composition (the OverworldControlGateInstaller lazy-seam pattern). The demo is the
        // FIRST content provider to register scenes against the content-blind harness.
        services.AddSingleton<Puck.Bench.BenchRuntime>();
        services.AddSingleton<FeatureSwitchRegistry>();
        services.AddSingleton<ICommandModule, FeatureSwitchCommandModule>();
        services.AddSingleton<ICommandModule, Puck.Bench.BenchCommandModule>();
        services.AddHostedService<Puck.Demo.Bench.BenchInstaller>();

        // DYNAMIC LIGHTING: the keyboard HID LampArray bind legend (presentation-only). The installer opens the
        // lamp arrays and paints the color-coded legend; the ticker drives it at its own ~30 Hz cadence, independent
        // of the render loop. DI disposes the installer at shutdown, restoring the device to autonomous mode.
        services.AddSingleton<LightingInstaller>();
        services.AddHostedService<LightingTickService>();
        // A scored bench.run completion plays its overall score as a tier-colored wave on the keyboard; the
        // light.celebrate verb fires the same show on demand.
        services.AddHostedService<BenchCelebrationInstaller>();
        services.AddSingleton<ICommandModule, LightingCommandModule>();

        services.AddSingleton<ICommandModule, DemoCommandModule>();
        services.AddSingleton<ICommandModule, Puck.Demo.Creator.CreatorCommandModule>();
        services.AddSingleton<ICommandModule, Puck.Demo.Tracker.TrackerCommandModule>();
        services.AddSingleton<ICommandModule, Puck.Demo.World.WorldCommandModule>();
        services.AddSingleton<ICommandModule, Puck.Demo.Creator.CompanionCommandModule>();
        // The deterministic-garden verbs (garden.plant / garden.list / garden.clear): plant a seed, watch it grow
        // over sim ticks (OverworldWorld's own planted-garden pool — real sim state, unlike the companions above).
        services.AddSingleton<ICommandModule, GardenCommandModule>();
        // The RTS scenario verbs operate on OverworldWorld's simulated RTS unit pool.
        services.AddSingleton<ICommandModule, RtsCommandModule>();
        // The gravity scenario verbs operate on OverworldWorld's field walker; the walker steps against a
        // live SdfFieldEvaluator, so gravity IS the rendered field's gradient, not a separate authored quantity.
        services.AddSingleton<ICommandModule, GravityCommandModule>();
        // The persisted-replay verbs (replay.capture / replay.list / replay.verify — the Puck.Replay seed): a
        // deterministic scripted overworld walk, recorded/replayed through the SAME record/replay seams
        // OverworldDeterminism's harness proves, persisted as a real OverworldRecording file. Self-contained (no
        // live root dependency — see ReplayCommandModule's remarks for the scoping call).
        services.AddSingleton<ICommandModule, ReplayCommandModule>();
        // The fullscreen SDF debugger: sdf / sdf.shape / sdf.op / sdf.floor / sdf.info.
        services.AddSingleton<ICommandModule, Puck.Demo.SdfDebug.SdfDebugCommandModule>();
        // The native AGB (ARM7TDMI) debug scene + execution-control verbs (agb.debug / agb.pause / agb.step / agb.frame /
        // agb.until / agb.trace / agb.regs / agb.io / agb.status / agb.bios). The AGB machine state lives in a DI singleton
        // reached by the module directly (the sanctioned IServiceProvider escape) and resolved by the overworld render node
        // for its per-frame step + framebuffer upload — the node and frame source are both at their CA1506 ceilings.
        services.AddSingleton<Puck.Demo.AgbDebug.AgbDebugService>();
        services.AddSingleton<ICommandModule, Puck.Demo.AgbDebug.AgbDebugCommandModule>();
        // The machine-neutral time-travel verb family (rewind / rewind.status / runahead / fastforward): ONE surface
        // routing between the overworld cabinets (index-first, the boot/win/press convention, via IOverworldControlHost)
        // and the AGB debug scene's machine (index-less, via AgbDebugService). Both drive the same MachineTimeTravel layer.
        services.AddSingleton<ICommandModule, TimeTravelCommandModule>();
        // The SM83 debug verb family for the overworld cabinets (hgb.peek/poke/regs/status/pause/resume/step/frame/
        // until/snap/restore/watch/watch.clear/watch.list/dis) — index-first, routed through the same IOverworldControlHost
        // seam, applied by the node between step fan-outs. The forge-authorship + agent-scripting counterpart to agb.*.
        services.AddSingleton<ICommandModule, HgbDebugCommandModule>();
        // The live win/reveal-condition editor ("the recursion"): condition.show/set/clear re-forge a cabinet's exit +
        // victory gates in-session, routed through the same IOverworldControlHost seam the reveal/link/cart verbs use.
        services.AddSingleton<ICommandModule, ConditionCommandModule>();
        // The text-enrichment control plane (text.motion / text.say): the reduced-motion accessibility gate plus a verb
        // that prints BBCode-enriched lines onto the diegetic terminal's CRT. Presentation-only, like every other echo.
        services.AddSingleton<ICommandModule, TextCommandModule>();
        // The live present-rate verb: retargets the window pacer's display-aware cadence to a safe enumerated tier
        // (present-rate sixty|one-twenty|display), the mid-session mirror of the run-doc host.presentRate field.
        // Presentation pacing only — the fixed-step sim is untouched.
        services.AddSingleton<ICommandModule, PresentRateCommandModule>();
        // The scripted-console control plane. Registered as its concrete type AND as an ICommandModule (forwarding to
        // the same singleton), then its step/settle HOLD gate is wired onto the stdin text source by a hosted service
        // AFTER the DI graph is built — the module must NOT take TextCommandSource via its constructor, because
        // TextCommandSource depends on CommandRegistry, which depends on every ICommandModule: that is a cycle.
        services.AddSingleton<OverworldControlCommandModule>();
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => sp.GetRequiredService<OverworldControlCommandModule>());
        services.AddHostedService<OverworldControlGateInstaller>();
        // The `addon` control plane (list / enable / disable): reaches the overworld's loaded WASM addon set through
        // IAddonControlHost, resolved lazily off the render-node root exactly like the overworld control verbs.
        services.AddSingleton<ICommandModule, AddonCommandModule>();
        services.AddSingleton<ICommandObserver, DemoCommandObserver>();
        // The introspection verbs (tick.explain / tick.watch / hash.mark / hash.marks): the engine narrating itself
        // over the pipe. Registered as its concrete type AND as an ICommandObserver (forwarding to the same
        // singleton, like OverworldControlCommandModule's two-registration pattern), so both the pushed-command tap
        // and the module's own verb lookups share one recorder instance.
        services.AddSingleton<TickTranscriptRecorder>();
        services.AddSingleton<ICommandObserver>(implementationFactory: static sp => sp.GetRequiredService<TickTranscriptRecorder>());
        services.AddSingleton<ICommandModule, IntrospectionCommandModule>();
        // The on-screen developer console's state store: DemoConsole publishes to it, the overworld's console overlay renders it.
        services.AddSingleton<Puck.Demo.DevConsole.ConsoleTextStore>();
        services.AddSingleton<DemoConsole>();
        // The overlay panels' stores for the toast, hub, tracker, and plaque surfaces. Each is a thin
        // PublishBuffer wrapper the OverlayPanelsNode reads: the toast is written at the verb-result seam above, the
        // other three are polled by OverlayPanelsFeed (composed in OverlayPanelsNode.TryWrap — the two ceiling types
        // may not name them). The control store carries master visibility + the drag/verb position overrides, and
        // the command module is their scriptable mirror (ui.panels / ui.panel.move / ui.panel.reset).
        services.AddSingleton<ToastStore>();
        services.AddSingleton<HubPanelStore>();
        services.AddSingleton<TrackerPanelStore>();
        services.AddSingleton<GalleryPanelStore>();
        services.AddSingleton<OverlayPanelsControlStore>();
        services.AddSingleton<ICommandModule, OverlayPanelsCommandModule>();
        // The pointer state store for draggable overlay panels. Contributed as a HELD root
        // capability — the same seam a graphics backend uses to publish its device — so the launcher's window
        // pump (engine-agnostic; it knows nothing about the demo) hands it every raw pointer event as it is
        // dequeued. See PointerStore's doc comment for why this bypasses the command-binding vocabulary.
        services.AddSingleton<PointerStore>();
        services.AddSingleton(implementationFactory: static sp => new HostCapabilityContribution(
            CapabilityType: typeof(IPointerInputSink),
            Instance: sp.GetRequiredService<PointerStore>(),
            IsHeld: true
        ));
        services.AddSingleton(implementationFactory: static _ => new BindingCommandSource(
            bindings: new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase) {
                [InputSources.Keyboard.Backspace] = [new(Command: "backspace")],
                [InputSources.Keyboard.Backtick] = [new(Command: "console")],
                [InputSources.Keyboard.Letter(letter: 'c')] = [new(Command: "copy", RequiredModifiers: InputModifiers.Control)],
                [InputSources.Keyboard.Text] = [new(Command: "echo")],
                [InputSources.Keyboard.Enter] = [new(Command: "enter")],
                [InputSources.Keyboard.Escape] = [new(Command: "escape")],
                [InputSources.Keyboard.Function(number: 4)] = [new(Command: "debug.view.cycle")],
                [InputSources.Keyboard.Letter(letter: 'a')] = [new(Command: "select", RequiredModifiers: InputModifiers.Control)],
            }
        ));
        // Controller input: the manager owns device acquisition, the source feeds the command registry (focus-gated
        // like keyboard input), and the hosted service governs device lifetime.
        // Single-drainer discipline: the manager's per-frame drain is destructive per device, so exactly ONE consumer may
        // drain. The live Overworld root drains per-device itself, and a document with gaming-brick / overworld viewport
        // panes drains through the shared pad-routing service — suppress the global gamepad command source for both.
        // Every other mode keeps the global source.
        services.AddDemoGamepad(registerGlobalSource: ((document.Graph is not OverworldNode) && !GamingBrickPadRegistration.UsesPadService(document: document)));
        services.AddBrickPadRouting(document: document);

        return parityResult;
    }
}
