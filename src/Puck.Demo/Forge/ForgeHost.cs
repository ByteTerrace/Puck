using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;

namespace Puck.Demo.Forge;

/// <summary>
/// The shared one-shot GPU harness for the forge tool modes. The forge needs a live GPU to render its SDF scenes, and —
/// like the POST battery — cannot bring one up before a host exists, so it builds a trimmed Vulkan host (the window
/// flashes once) and runs the caller's <c>work</c> on the first frame, then exits. The <see cref="RomForge"/> tool
/// modes (the SDF-scene forge and the camera forge) run through here.
/// </summary>
internal static class ForgeHost {
    /// <summary>Builds the host, runs <paramref name="work"/> once with a live GPU device + compute services, and
    /// returns its exit code (defaults to 2 if the work never ran).</summary>
    public static async Task<int> RunAsync(string[] args, Func<IGpuDeviceContext, IGpuComputeServices, int> work) {
        ArgumentNullException.ThrowIfNull(work);

        var result = new ForgeResult();
        var builder = Host.CreateApplicationBuilder(args: args);
        var services = builder.Services;

        services.Configure<NativeWindowOptions>(configureOptions: static options => {
            options.Height = 288;
            options.Mode = NativeWindowMode.PlatformWindow;
            options.Title = "Puck: forge";
            options.Width = 320;
        });
        services.AddSingleton(implementationInstance: new LauncherOptions {
            ExitAfter = TimeSpan.FromSeconds(value: 30),
            TargetRenderRate = 60,
        });
        services.AddSingleton(implementationInstance: new PresentationOptions {
            PresentMode = PresentMode.Vsync,
            SurfaceFormat = SurfaceFormat.R8G8B8A8Unorm,
        });
        services.AddSingleton(implementationFactory: static _ => new BindingCommandSource(bindings: new Dictionary<string, IReadOnlyList<CommandBinding>>()));
        // The shared trimmed-GPU-host block (GpuHostComposition documents the Vulkan-wins-the-compute-seam ORDER rule);
        // the forge always hosts on Vulkan and loads .spv kernels, so DirectX is not contributed as a named backend.
        GpuHostComposition.AddTrimmedGpuHost(services: services, preferredBackend: "vulkan", registerDirectXBackend: false);
        services.AddSingleton<IRenderNode>(implementationFactory: sp => new ForgeNode(services: sp, work: work, result: result));

        await builder.Build().RunAsync();

        return result.ExitCode;
    }

    private sealed class ForgeResult {
        public int ExitCode = 2;
    }
    private sealed class ForgeNode : IRenderNode {
        private readonly NodeDescriptor m_descriptor = new(Name: "forge", SurfaceId: SurfaceId.New());
        private readonly ForgeResult m_result;
        private readonly IServiceProvider m_services;
        private readonly Func<IGpuDeviceContext, IGpuComputeServices, int> m_work;
        private bool m_done;

        public ForgeNode(IServiceProvider services, Func<IGpuDeviceContext, IGpuComputeServices, int> work, ForgeResult result) {
            m_result = result;
            m_services = services;
            m_work = work;
        }

        public NodeDescriptor Descriptor => m_descriptor;

        public void Dispose() { }
        public Surface ProduceFrame(in FrameContext context) {
            if (m_done) {
                return default;
            }

            m_done = true;

            try {
                if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)) {
                    throw new InvalidOperationException(message: "The forge host published no GPU device.");
                }

                m_result.ExitCode = m_work(device, m_services.GetRequiredService<IGpuComputeServices>());
            } catch (Exception exception) {
                Console.Error.WriteLine(value: $"forge | FAILED | {exception.Message}");
                m_result.ExitCode = 2;
            }

            if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
                terminal.RequestExit();
            }

            return default;
        }
    }
}
