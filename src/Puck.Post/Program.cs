using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Memory;
using Puck.Platform;
using Puck.Post;
using Puck.Vulkan.Presentation;

// The POST has no rich CLI surface — the battery is a fixed, ordered list in code. Only three knobs, parsed by hand to
// avoid pulling in a command-line library: where artifacts land, and an optional tier/name subset for iterating.
var artifactsDirectory = ArgValue(args: args, name: "--artifacts") ?? Path.Combine(path1: "artifacts", path2: "post");
var tierFilter = ArgValue(args: args, name: "--tier");
var nameFilter = ArgValue(args: args, name: "--filter");

var stages = PostStages.Create()
    .Where(predicate: stage => TierMatches(stage: stage, tierFilter: tierFilter))
    .Where(predicate: stage => NameMatches(stage: stage, nameFilter: nameFilter))
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
services.AddPuckAllocator();
services.AddVulkanPresenter();
services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(Name: "vulkan", Presenter: sp.GetRequiredService<VulkanSurfacePresenter>()));
services.AddBackendSwitcher(preferredBackend: "vulkan");

var runResult = new PostRunResult();
var battery = new PostBattery(stages: stages);

services.AddSingleton<IRenderNode>(implementationFactory: sp => new PostBatteryNode(
    artifactsDirectory: artifactsDirectory,
    battery: battery,
    runResult: runResult,
    services: sp
));

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
