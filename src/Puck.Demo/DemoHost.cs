using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
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

        // The shared trimmed-GPU-host block (windowing, camera, allocator, presenters, backend switch) lives in
        // GpuHostComposition — the ORDER-MATTERS Vulkan-wins-the-compute-seam rule is documented there, once.
        GpuHostComposition.AddTrimmedGpuHost(services: services, preferredBackend: (startWithDirectX ? "directx" : "vulkan"), registerDirectXBackend: true);

        var parityResult = DemoRunRegistrar.RegisterRunDocument(capturePath: capturePath, document: document, height: height, hostsOnDirectX: startWithDirectX, services: services, width: width);

        services.AddSingleton<ICommandModule, DemoCommandModule>();
        services.AddSingleton<ICommandModule, Puck.Demo.Creator.CreatorCommandModule>();
        services.AddSingleton<ICommandModule, Puck.Demo.Tracker.TrackerCommandModule>();
        services.AddSingleton<ICommandModule, Puck.Demo.World.WorldCommandModule>();
        services.AddSingleton<ICommandModule, Puck.Demo.Creator.CompanionCommandModule>();
        // The fullscreen SDF-debug tool (the SDF-VM accuracy arc's debugger): sdf / sdf.shape / sdf.op / sdf.floor / sdf.info.
        services.AddSingleton<ICommandModule, Puck.Demo.SdfDebug.SdfDebugCommandModule>();
        // The native GBA (ARM7TDMI) debug scene + execution-control verbs (agb.debug / agb.pause / agb.step / agb.frame /
        // agb.until / agb.trace / agb.regs / agb.io / agb.status / agb.bios). The AGB machine state lives in a DI singleton
        // reached by the module directly (the sanctioned IServiceProvider escape) and resolved by the overworld render node
        // for its per-frame step + framebuffer upload — the node and frame source are both at their CA1506 ceilings.
        services.AddSingleton<Puck.Demo.AgbDebug.AgbDebugService>();
        services.AddSingleton<ICommandModule, Puck.Demo.AgbDebug.AgbDebugCommandModule>();
        // The live win/reveal-condition editor ("the recursion"): condition.show/set/clear re-forge a cabinet's exit +
        // victory gates in-session, routed through the same IOverworldControlHost seam the reveal/link/cart verbs use.
        services.AddSingleton<ICommandModule, ConditionCommandModule>();
        // The scripted-console control plane. Registered as its concrete type AND as an ICommandModule (forwarding to
        // the same singleton), then its step/settle HOLD gate is wired onto the stdin text source by a hosted service
        // AFTER the DI graph is built — the module must NOT take TextCommandSource via its constructor, because
        // TextCommandSource depends on CommandRegistry, which depends on every ICommandModule: that is a cycle.
        services.AddSingleton<OverworldControlCommandModule>();
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => sp.GetRequiredService<OverworldControlCommandModule>());
        services.AddHostedService<OverworldControlGateInstaller>();
        services.AddSingleton<ICommandObserver, DemoCommandObserver>();
        // The on-screen developer console's state store: DemoConsole publishes to it, the overworld's console overlay renders it.
        services.AddSingleton<Puck.Demo.DevConsole.ConsoleTextStore>();
        services.AddSingleton<DemoConsole>();
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
