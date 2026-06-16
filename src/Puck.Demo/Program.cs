using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Commands;
using Puck.Demo;
using Puck.Demo.Commands;
using Puck.Demo.DirectX;
using Puck.Demo.Input;
using Puck.Demo.Rendering;
using Puck.Demo.Scene;
using Puck.Platform;
using Puck.Recursive;
using Puck.Vulkan;

var builder = Host.CreateApplicationBuilder(args: args);
var services = builder.Services;
var shaderDirectory = Path.Combine(
    path1: AppContext.BaseDirectory,
    path2: "Assets",
    path3: "Shaders",
    path4: "Sdf"
);

// Pick the rendering backend: --backend vulkan (default), --backend directx, or --backend recursive — the
// cross-backend recursive-hosting showcase relocated from the launcher (the launcher is now the purest
// terminal). The vulkan/directx backends share the data-driven scene + command system below; recursive
// carries its own isolated stack (its own renderer, node tree, scene, and command surface) and returns.
var backend = (builder.Configuration["backend"] ?? "vulkan").ToLowerInvariant();
if (backend == "recursive") {
    // Which node tree to host: default (Vulkan SDF + DirectX child), vdv (Vulkan → DirectX → Vulkan), or
    // dvd (DirectX → Vulkan → DirectX). The nested trees demonstrate arbitrary cross-backend hosting.
    var tree = (builder.Configuration["tree"] ?? "default").ToLowerInvariant();

    services.Configure<NativeWindowOptions>(static options => {
        options.Height = 600;
        options.Mode = NativeWindowMode.PlatformWindow;
        options.Title = "Puck: Demo (Recursive)";
        options.Width = 960;
    });
    services.AddRecursiveShowcase(
        engineShaderDirectory: shaderDirectory,
        tree: tree
    );
    await builder.Build().RunAsync();
    return 0;
}
var useDirectX = (backend == "directx");

// Composable bring-up: each library is wired explicitly, no engine-wide helper (see
// DemoServiceRegistration). The demo opens a native window, raymarches a data-driven SDF scene through a
// first-class camera (Puck.Vulkan + the demo's SDF VM), and lets the Puck.Commands system steer it.
services.Configure<NativeWindowOptions>(options => {
    options.Height = 600;
    options.Mode = NativeWindowMode.PlatformWindow;
    options.Title = (useDirectX
        ? "Puck: Demo (DirectX)"
        : "Puck: Demo");
    options.Width = 960;
});
services.AddDemoPlatformWindowing();

// Backend-agnostic model + command system: one command module, the registry, and the two sources that
// drive it — the keyboard binding source and the stdin text source (results echoed to stdout so scripted
// runs are assertable).
services.AddSingleton<DemoScene>();
services.AddSingleton<IDemoExitSignal, DemoExitSignal>();
services.AddSingleton<ICommandModule, DemoCommandModule>();
services.AddSingleton<CommandRegistry>();
services.AddSingleton(implementationFactory: static _ => new BindingCommandSource(bindings: DemoInputMap.CreateBindings()));
services.AddSingleton(implementationFactory: static provider => new TextCommandSource(
    onResult: static (line, result) => {
        if (!string.IsNullOrEmpty(value: result.Output)) {
            Console.Out.WriteLine(value: result.Output);
        }
    },
    registry: provider.GetRequiredService<CommandRegistry>()
));
services.AddSingleton<DemoShell>();
services.AddHostedService(implementationFactory: static sp => new StandardInputReaderService(
    source: sp.GetRequiredService<TextCommandSource>(),
    threadName: "Puck.Demo Stdin Reader"
));
if (useDirectX) {
    // Direct3D 12 backend (Windows 10+). Phase 1: a window-bound clear-and-present loop.
    if (!OperatingSystem.IsWindowsVersionAtLeast(
        10,
        0,
        10240
    )) {
        throw new PlatformNotSupportedException(message: "The DirectX backend requires Windows 10 (build 10240) or later.");
    }

    services.AddHostedService<DirectXDemoHostedService>();
} else {
    // Vulkan backend: the native APIs + factories, the renderer, the SDF view, and the window loop.
    services
        .AddDemoVulkan();
    services.AddSingleton(new VulkanRendererOptions {
        ApplicationName = "Puck.Demo",
    });
    services.AddSingleton(new SdfViewRendererOptions {
        ShaderDirectory = shaderDirectory,
    });
    services.AddSingleton<VulkanRenderer>();
    services.AddSingleton<VulkanDescriptorAllocator>();
    services.AddSingleton<VulkanQueueSubmitter>();
    services.AddSingleton<SdfViewRenderer>();
    services.AddHostedService<DemoWindowHostedService>();
}
await builder.Build().RunAsync();
return 0;
