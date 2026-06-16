using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Launcher;
using Puck.Platform;
using Puck.Recursive.Commands;
using Puck.Recursive.Input;
using Puck.SdfVm.Rendering;

namespace Puck.Recursive;

/// <summary>Composes the recursive cross-backend showcase as a consumer of the <see cref="Puck.Launcher"/>
/// terminal: the launcher brings the window, swapchain, blit compositor, baton, command pump, and run loop
/// (<see cref="LauncherServiceRegistration.AddLauncherTerminal"/>); this showcase brings the <em>primary
/// engine</em> — the recursive node tree, its host context (Vulkan + DirectX devices + the baton), and the
/// engine's controls. The terminal drives the tree's root node and blits its surface.</summary>
internal static class RecursiveShowcaseHost {
    /// <summary>Wires the recursive showcase's engine onto the launcher terminal.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="tree">The tree to build: <c>default</c>, <c>vdv</c>, or <c>dvd</c>.</param>
    /// <param name="engineShaderDirectory">The directory holding the SDF engine's compiled shaders.</param>
    public static IServiceCollection AddRecursiveShowcase(this IServiceCollection services, string tree, string engineShaderDirectory) {
        // The terminal: window + swapchain + blit compositor + baton + command pump + run loop. It drives
        // whatever root IRenderNode we register and contributes the `quit` verb itself.
        services.AddLauncherTerminal();

        // The primary engine: the recursive node tree + its host context (Vulkan + DirectX + baton).
        services.AddSingleton(new SdfViewRendererOptions {
            ShaderDirectory = engineShaderDirectory,
        });
        services.AddRecursiveNodeTree(tree: tree);

        // The engine's controls handed to the terminal's pump: the key bindings, the packet→signal adapter,
        // and the engine command module (pause/scene/layout). `quit` comes from the terminal.
        services.AddSingleton(implementationFactory: static _ => new BindingCommandSource(bindings: InputMap.CreateBindings()));
        services.AddSingleton<Func<InputPacket, InputSignal?>>(implementationInstance: InputMap.ToInputSignal);
        services.AddSingleton<ICommandModule, RecursiveCommandModule>();

        return services;
    }
}
