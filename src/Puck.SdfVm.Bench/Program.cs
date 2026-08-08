using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Pacing;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.DirectX.Presentation;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Memory;
using Puck.Platform;
using Puck.SdfVm;
using Puck.SdfVm.Bench;
using Puck.SdfVm.Debug;
using Puck.Vulkan.Presentation;

// Puck.SdfVm.Bench — a real GPU/CPU ceiling-measurement harness for contributed dynamic geometry (Phase 3 L1,
// 2026-08-02 mission). Boots the SAME generic Launcher + SdfWorldRenderBuilder assembly Puck.World composes (no game
// glue), drives DynamicMatrixBenchFrameSource through SdfBenchScene's DynamicMatrix ladder, and exits when it
// finishes. --backend vulkan|directx (default vulkan), --width/--height (default 1920x1080, pinned across the whole
// matrix), --warm/--samples (default 20/300) per configuration.
var hostsOnDirectX = string.Equals(a: ReadOption(args: args, name: "--backend", fallback: "vulkan"), b: "directx", comparisonType: StringComparison.OrdinalIgnoreCase);
var width = uint.Parse(s: ReadOption(args: args, name: "--width", fallback: "1920"));
var height = uint.Parse(s: ReadOption(args: args, name: "--height", fallback: "1080"));
var warmFrames = int.Parse(s: ReadOption(args: args, name: "--warm", fallback: "20"));
var sampleFrames = int.Parse(s: ReadOption(args: args, name: "--samples", fallback: "300"));
// A bisection-point single-rung run (--n given): --placement clustered|uniform|far-corners (default clustered),
// --moving true|false (default false). Omitting --n runs the full 30-cell ladder.
(SdfBenchPlacement Placement, bool Moving, int Count)? singleRung = null;
if (ReadOptionOrNull(args: args, name: "--n") is { } nToken) {
    var placementToken = ReadOption(args: args, name: "--placement", fallback: "clustered");
    var placement = placementToken.ToLowerInvariant() switch {
        "uniform" => SdfBenchPlacement.Uniform,
        "far-corners" or "farcorners" => SdfBenchPlacement.FarCorners,
        _ => SdfBenchPlacement.Clustered,
    };
    var moving = bool.Parse(value: ReadOption(args: args, name: "--moving", fallback: "false"));

    singleRung = (placement, moving, int.Parse(s: nToken));
}
if (hostsOnDirectX && !OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)) {
    Console.Error.WriteLine(value: "Direct3D 12 requires Windows 10 or newer; use --backend vulkan on this platform.");

    return 1;
}

// The bench harness arms GPU timing PROGRAMMATICALLY — the documented highest-precedence source (see
// GpuTimingControl's type remarks: "the bench harness arms it at suite start") — before the host boots, so every
// produced frame from frame 1 onward is timed and no live console switch is needed.
GpuTimingControl.Shared.SetArmed(armed: true);
var frameSource = new DynamicMatrixBenchFrameSource(warmFrames: warmFrames, sampleFrames: sampleFrames, backendIsDirectX: hostsOnDirectX, singleRung: singleRung);
var builder = Host.CreateApplicationBuilder(args: args);
var services = builder.Services;
services.Configure<NativeWindowOptions>(configureOptions: options => {
    options.Height = height;
    options.Mode = NativeWindowMode.PlatformWindow;
    options.Title = "Puck.SdfVm.Bench";
    options.Width = width;
});
// Uncapped target rate + immediate present: the matrix should burn through its ~9000-frame budget (30 configs ×
// (warm + samples)) as fast as the GPU/CPU actually allow, not throttled to a display's vsync interval.
services.AddSingleton(implementationInstance: new LauncherOptions {
    TargetRenderRate = null,
});
services.AddSingleton(implementationInstance: new PresentationOptions {
    PresentMode = PresentMode.Immediate,
});
services.AddSingleton(implementationInstance: new ExternalClockRegistry(electionPolicy: null));
services.AddLauncherTerminal();
services.AddPlatformWindowing();
services.AddPuckAllocator();
if (hostsOnDirectX) {
    services.AddDirectXPresenter();
    services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(
        Name: "directx",
        Presenter: (OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)
            ? sp.GetRequiredService<DirectXSurfacePresenter>()
            : throw new PlatformNotSupportedException(message: "Direct3D 12 requires Windows 10 or newer."))
    ));
} else {
    services.AddVulkanPresenter();
    services.TryAddSingleton<IGpuDeviceContext>(implementationFactory: static sp => sp.GetRequiredService<VulkanRenderer>());
    services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(
        Name: "vulkan",
        Presenter: sp.GetRequiredService<VulkanSurfacePresenter>()
    ));
}
services.AddBackendSwitcher(preferredBackend: (hostsOnDirectX ? "directx" : "vulkan"));
services.AddSingleton<IRenderNode>(implementationFactory: sp => {
    var viewGpuServices = new SdfViewGpuServices(
        Gpu: sp.GetRequiredService<IGpuComputeServices>(),
        TimingFactory: (sp.GetService(serviceType: typeof(IGpuTimingPoolFactory)) as IGpuTimingPoolFactory),
        TimingRecorder: (sp.GetService(serviceType: typeof(IGpuTimingRecorder)) as IGpuTimingRecorder)
    );

    var (probeWords, probeInstances, probeDynamicTransforms) = MeasureWorstCase();

    var render = SdfWorldRenderBuilder.Build(
        services: viewGpuServices,
        spec: new SdfWorldRenderSpec(
            FrameSource: frameSource,
            Width: width,
            Height: height
        ) {
            DynamicTransformCapacity = probeDynamicTransforms,
            HostsOnDirectX = hostsOnDirectX,
            InstanceCapacity = probeInstances,
            ProgramWordCapacity = probeWords,
            Timing = true,
        }
    );

    // Mirrors Puck.World's own `probe.Node = render.Producer` pattern: the frame source reads this back, from inside
    // its own CaptureFrame, to feed SdfBenchScene.Advance the previous frame's timings.
    frameSource.Node = render.Producer;

    return render.Root;
});
var host = builder.Build();
frameSource.Terminal = host.Services.GetRequiredService<ITerminalControl>();
Console.Error.WriteLine(value: $"[sdf-bench] backend={(hostsOnDirectX ? "directx" : "vulkan")} {width}x{height} warm={warmFrames} samples={sampleFrames}{((singleRung is { } r) ? $" single-rung={r.Placement} moving={r.Moving} n={r.Count}" : " matrix (30 cells)")}");
await host.RunAsync();
return (frameSource.ExitRequested ? 0 : 1);

// Measures EVERY rung of the DynamicMatrix ladder (cheap — CPU-only program builds, never rendered) and takes the
// per-axis MAX, so the engine's frozen buffers (constructed ONCE, off the first captured frame) are guaranteed to fit
// every later rung without an UploadProgram rejection. Static and dynamic instances do NOT pack to the same word
// count per instance (measured: a static 16384-instance rung packs MORE program words than the equivalent dynamic
// one), so guessing a single "obviously worst" rung is unsound — this measures the whole ladder instead.
static (int Words, int Instances, int DynamicTransforms) MeasureWorstCase() {
    var renderer = new SdfDebugRenderer();
    var words = 0;
    var instances = 0;
    var dynamicTransforms = 0;

    foreach (var config in SdfBenchWorkloads.BuildDynamicMatrixLadder()) {
        var probeBuilder = new SdfProgramBuilder();

        renderer.EmitBench(builder: probeBuilder, config: config);

        var probeProgram = probeBuilder.Build();

        words = Math.Max(val1: words, val2: probeProgram.Words.Length);
        instances = Math.Max(val1: instances, val2: probeProgram.Instances.Count);
        dynamicTransforms = Math.Max(val1: dynamicTransforms, val2: probeProgram.RequiredDynamicTransformCapacity);
    }

    return (words, instances, dynamicTransforms);
}
static string ReadOption(string[] args, string name, string fallback) =>
    (ReadOptionOrNull(args: args, name: name) ?? fallback);
static string? ReadOptionOrNull(string[] args, string name) {
    for (var index = 0; (index < (args.Length - 1)); index++) {
        if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return args[(index + 1)];
        }
    }

    return null;
}
