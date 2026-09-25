using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Hosting;
using Puck.Input;

namespace Puck.Launcher.Tests;

/// <summary>
/// A real windowed launcher host (<see cref="LauncherServiceRegistration.AddLauncherTerminal"/>) over fakes that need
/// no GPU and no display: a window that reports itself already closed, a render root (a node that produces nothing
/// unless the law supplies its own), a device context published as the root host capability the way a backend
/// publishes its own, and a presenter registered where a backend's presenter would be, whose
/// <see cref="ISurfacePresenter.Activate"/> either succeeds or throws a chosen failure.
/// </summary>
internal static class WindowedHostFixture {
    public static readonly TimeSpan HostBudget = TimeSpan.FromSeconds(value: 30);

    public static IHost Build(FakePresenter presenter, IGpuDeviceContext device, IRenderNode? root = null, Action<IServiceCollection>? configure = null) {
        var builder = Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings {
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ISurfacePresenter>(implementationInstance: presenter);
        builder.Services.AddSingleton<INativeWindowFactory, FakeWindowFactory>();
        builder.Services.AddSingleton(implementationInstance: (root ?? new FakeRenderNode()));
        builder.Services.AddSingleton(implementationInstance: new HostCapabilityContribution(
            CapabilityType: typeof(IGpuDeviceContext),
            Instance: device,
            IsHeld: false
        ));
        builder.Services.AddLauncherTerminal();
        configure?.Invoke(obj: builder.Services);

        return builder.Build();
    }
    public static async Task RunAsync(IHost host) => await host.RunAsync(token: Xunit.TestContext.Current.CancellationToken).WaitAsync(
        cancellationToken: Xunit.TestContext.Current.CancellationToken,
        timeout: HostBudget
    );

    /// <summary>A presenter that activates, or throws <see cref="Failure"/> from <see cref="Activate"/> when one is set.</summary>
    public sealed class FakePresenter(Exception? failure) : ISurfacePresenter {
        public int ActivateCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public Exception? Failure { get; } = failure;

        public void Activate(NativeSurfaceBinding binding, uint width, uint height) {
            ActivateCalls++;

            if (Failure is not null) {
                throw Failure;
            }
        }
        public void BeginFrame(uint width, uint height) => throw new InvalidOperationException(message: "The fixture's window is closed; no frame should begin.");
        public void Deactivate() { }
        public void Dispose() => DisposeCalls++;
        public void Present(Surface surface) => throw new InvalidOperationException(message: "The fixture's window is closed; nothing should present.");
    }
    /// <summary>A device context whose drains are counted and fail, as a failing teardown step; it has no services, the
    /// way a device that never came up has none to give a consumer that asks.</summary>
    public sealed class CountingDeviceContext : IGpuDeviceContext {
        public long AdapterLuid => 0L;
        public GpuDeviceCapabilities? Capabilities => null;
        public GpuDeviceIdentity? Identity => null;
        public GpuMemoryProfile MemoryProfile => default;
        public GpuDeviceServices Services => throw new InvalidOperationException(message: "The fixture device was never created.");
        public int WaitIdleCalls { get; private set; }

        public void WaitIdle() {
            WaitIdleCalls++;

            throw new InvalidOperationException(message: "The fixture device was asked to drain.");
        }
    }
    /// <summary>The device context of a renderer whose bring-up failed, as <c>VulkanRenderer</c> presents it: draining
    /// returns at once, and its services are the ones given, which a law makes refuse every call, or absent.</summary>
    public sealed class NeverInitializedDeviceContext(GpuDeviceServices? services = null) : IGpuDeviceContext {
        public long AdapterLuid => 0L;
        public GpuDeviceCapabilities? Capabilities => null;
        public GpuDeviceIdentity? Identity => null;
        public GpuMemoryProfile MemoryProfile => default;
        public GpuDeviceServices Services => (services ?? throw new InvalidOperationException(message: "The renderer must be initialized before its device is used."));

        public void WaitIdle() { }
    }
    /// <summary>A render node that produces nothing, whose <see cref="Dispose"/> throws <c>failure</c> when one is set.</summary>
    public sealed class FakeRenderNode(Exception? failure = null) : IRenderNode {
        public NodeDescriptor Descriptor { get; } = new(
            Name: "windowed-host-fixture",
            SurfaceId: SurfaceId.New()
        );
        public int DisposeCalls { get; private set; }

        public void Dispose() {
            DisposeCalls++;

            if (failure is not null) {
                throw failure;
            }
        }
        public Surface ProduceFrame(in FrameContext context) => default;
    }

    private sealed class FakeWindowFactory : INativeWindowFactory {
        public INativeWindow Create() => new FakeWindow();
    }
    private sealed class FakeWindow : INativeWindow, IWindowInputSource {
        public NativeDisplayKind DisplayKind => default;
        public bool HasPainted => false;
        public uint Height => 32U;
        public bool IsOpen => false;
        public bool IsVisible => false;
        public ulong ResizeCount => 0UL;
        public string Title => "windowed host fixture";
        public uint Width => 32U;

        public void Close() { }
        public NativeSurfaceBinding CreateSurfaceBinding() => default;
        public void Dispose() { }
        public void PollEvents() { }
        public void Show() { }
        public bool TryDequeueInput(out WindowInputEvent inputEvent) {
            inputEvent = default;

            return false;
        }
    }
}
