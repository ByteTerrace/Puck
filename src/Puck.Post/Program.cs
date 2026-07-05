using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.DirectX.Presentation;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Memory;
using Puck.Platform;
using Puck.Post;
using Puck.Vulkan.Presentation;

// The POST has no rich CLI surface — the battery is a fixed, ordered list in code. Knobs are parsed by hand to avoid
// pulling in a command-line library: where artifacts land, an optional tier/name/stage subset for iterating, an
// override for the fuzz stage's fixed deterministic seed, and the internal --probe mode the Tier-D stages relaunch
// this executable in (a live multi-frame run instead of the battery).
var artifactsDirectory = ArgValue(args: args, name: "--artifacts") ?? Path.Combine(path1: "artifacts", path2: "post");
var tierFilter = ArgValue(args: args, name: "--tier");
var nameFilter = ArgValue(args: args, name: "--filter");
var stageFilter = ArgValue(args: args, name: "--stage");
var fuzzSeedValue = ArgValue(args: args, name: "--fuzz-seed");
var fuzzSeed = ((fuzzSeedValue is null) ? ((int?)null) : int.Parse(s: fuzzSeedValue));
var probeMode = ArgValue(args: args, name: "--probe");

var stages = PostStages.Create(fuzzSeed: fuzzSeed)
    .Where(predicate: stage => TierMatches(stage: stage, tierFilter: tierFilter))
    .Where(predicate: stage => NameMatches(stage: stage, nameFilter: nameFilter))
    .Where(predicate: stage => StageMatches(stage: stage, stageFilter: stageFilter))
    .ToArray();

// The POST always hosts offscreen on Vulkan and runs the battery on the first frame, then exits — so the window the
// launcher opens just flashes. Mirror the demo's composition root, minus the live producers and the game.
var builder = Host.CreateApplicationBuilder(args: args);
var services = builder.Services;

services.Configure<NativeWindowOptions>(configureOptions: static options => {
    options.Height = 600;
    options.Mode = NativeWindowMode.PlatformWindow;
    options.Title = "Puck: POST";
    options.Width = 960;
});
services.AddSingleton(implementationInstance: new LauncherOptions {
    ExitAfter = TimeSpan.FromSeconds(value: 30),
    TargetRenderRate = 60,
});
services.AddSingleton(implementationInstance: new PresentationOptions {
    PresentMode = PresentMode.Vsync,
    SurfaceFormat = SurfaceFormat.R8G8B8A8Unorm,
});
// The launcher's command pump requires a binding source (the keys it reads each frame); the POST drives nothing from
// the keyboard — it runs the battery on the first frame and exits — so an empty binding set satisfies the contract.
services.AddSingleton(implementationFactory: static _ => new BindingCommandSource(bindings: new Dictionary<string, IReadOnlyList<CommandBinding>>()));
services.AddLauncherTerminal();
services.AddPlatformWindowing();
// The camera-share stage (Tier C) resolves the platform camera-capture service to decide its environment-lenient
// skip (the null implementation means the camera seam has nothing to prove on this machine).
services.AddCameraCapture();
services.AddPuckAllocator();
// ORDER MATTERS: AddVulkanPresenter MUST precede AddDirectXPresenter — both chain the neutral compute seam into this
// one container and the Vulkan-hosted stages need the Vulkan device's adapters to win (same rule as the demo's root).
// The Direct3D 12 presenter exists as the SECOND named backend for the Tier-D hot-switch probe; battery runs are
// unaffected (the switcher prefers vulkan and only the probe ever toggles it).
services.AddVulkanPresenter();
if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
    services.AddDirectXPresenter();
}
services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(Name: "vulkan", Presenter: sp.GetRequiredService<VulkanSurfacePresenter>()));
if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
    services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(
        Name: "directx",
        Presenter: (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
            ? sp.GetRequiredService<DirectXSurfacePresenter>()
            : throw new PlatformNotSupportedException())));
}
services.AddBackendSwitcher(preferredBackend: "vulkan");

var runResult = new PostRunResult();

if (probeMode is not null) {
    // A Tier-D probe child: host the live probe node instead of the battery.
    services.AddSingleton<IRenderNode>(implementationFactory: sp => new PostProbeNode(
        mode: probeMode,
        runResult: runResult,
        services: sp
    ));
} else {
    var battery = new PostBattery(stages: stages);

    services.AddSingleton<IRenderNode>(implementationFactory: sp => new PostBatteryNode(
        artifactsDirectory: artifactsDirectory,
        battery: battery,
        runResult: runResult,
        services: sp
    ));
}

await builder.Build().RunAsync();

// The node fills runResult before requesting exit; propagate it. It defaults to 2, so a run that never reached the
// battery (the node never produced a frame) fails loudly.
return runResult.ExitCode;

static string? ArgValue(string[] args, string name) {
    for (var index = 0; (index < (args.Length - 1)); index++) {
        if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return args[index + 1];
        }
    }

    return null;
}

static bool TierMatches(IPostStage stage, string? tierFilter) {
    return (string.IsNullOrEmpty(value: tierFilter) || string.Equals(a: stage.Tier.ToString(), b: tierFilter, comparisonType: StringComparison.OrdinalIgnoreCase));
}

static bool NameMatches(IPostStage stage, string? nameFilter) {
    return (string.IsNullOrEmpty(value: nameFilter) || stage.Name.Contains(value: nameFilter, comparisonType: StringComparison.OrdinalIgnoreCase));
}

static bool StageMatches(IPostStage stage, string? stageFilter) {
    return (string.IsNullOrEmpty(value: stageFilter) || string.Equals(a: stage.Name, b: stageFilter, comparisonType: StringComparison.OrdinalIgnoreCase));
}
