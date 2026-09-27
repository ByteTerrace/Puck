using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Hosting;
using Puck.Shaders;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Security;
using Windows.Win32.System.Com;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX.Presentation;

/// <summary>
/// Owns the DXGI flip-model swap chain, back-buffer RTVs, one descriptor pool of the device's shader-visible heaps
/// (<see cref="DirectXShaderVisibleHeaps"/>) holding the display encode's set, its source image, its sampler and its
/// block, and a lease on the encode pipeline, the <see cref="GpuPassPipelineCache"/> entry of <see cref="SurfaceEncoder"/>
/// in the swap chain's format. It creates no shader-visible heap of its own: the pool is admitted through
/// <see cref="IGpuBindings.CanAdmit"/> like every other owner's, and a CPU surface is uploaded through the device's
/// <see cref="IGpuSurfaceUpload"/>. On every frame it:
/// <list type="bullet">
///   <item>resets the per-frame command allocator and command list,</item>
///   <item>delegates recording to the injected <see cref="IDirectXCommandListRecorder"/>,</item>
///   <item>closes, executes, and presents the command list.</item>
/// </list>
/// <para>
/// The encode path (<see cref="Blit"/>) builds a single <see cref="DirectXDrawCommand"/> that encodes one
/// <see cref="Surface"/> fullscreen in the swap chain's <see cref="DisplayOutput"/> at the host's paper-white level. The multi-draw path (<see cref="Present"/>) accepts a caller-supplied
/// list of draw commands so the compositor can be driven for arbitrary compositing scenarios without changing
/// this class.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXSurfaceCompositor : IDisposable {
    private const uint FrameCount = 2;
    // Variable-refresh-rate tearing: the swap chain must be created (and resized) with the ALLOW_TEARING flag and
    // presented with the matching Present flag. Both are UINT bitmasks in the DXGI headers.
    private const uint DxgiSwapChainFlagAllowTearing = 0x00000800; // DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING
    private const uint DxgiPresentAllowTearing = 0x00000200;       // DXGI_PRESENT_ALLOW_TEARING
    // Closed-loop present timing for tearing modes: GetFrameStatistics has no vblank sync-point when presenting at sync
    // interval 0 (Immediate/Adaptive), so those modes drive a FRAME_LATENCY_WAITABLE_OBJECT swap chain and phase-lock to
    // the waitable instead — the DXGI analogue of Vulkan's vkWaitForPresentKHR.
    private const uint DxgiSwapChainFlagFrameLatencyWaitableObject = 0x00000040; // DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT
    private const uint FrameLatencyWaitTimeoutMilliseconds = 100; // bound so a stalled/occluded present pipeline can never hang the pump
    // The name the encode's pool and block are admitted and named under.
    private const string EncodeOwner = "display-encode";

    // The SDR outputs a flip-model swap chain presents on any display: 8-bit unsigned normalized, in either channel order.
    private static readonly DisplayOutput[] SdrOutputs = [
        DisplayOutput.Sdr(format: GpuPixelFormat.B8G8R8A8Unorm),
        DisplayOutput.Sdr(format: GpuPixelFormat.R8G8B8A8Unorm),
    ];

    private readonly IDirectXCommandListRecorder m_commandListRecorder;
    private readonly GpuPassPipelineCache m_pipelines;
    private readonly double m_paperWhiteNits;
    private readonly GpuPixelFormat m_preferredFormat;
    private readonly DisplayColorSpace m_requestedColorSpace;
    private readonly PresentMode m_presentMode;
    private readonly uint m_syncInterval;

    private readonly nint[] m_backBuffers = new nint[FrameCount];
    // One command allocator/list per swap-chain buffer (indexed by IDXGISwapChain3::GetCurrentBackBufferIndex,
    // queried identically in BeginFrame and Present with no intervening Present call between the two reads, so
    // both reads return the same index) — the standard D3D12 multi-frame-in-flight pattern. Each slot's
    // allocator may only be Reset() once the GPU has finished the commands last recorded into it; m_frameFenceValues
    // tracks that per slot so BeginFrame can wait on exactly the slot about to be reused instead of draining the
    // whole device every frame.
    private readonly nint[] m_commandAllocators = new nint[FrameCount];
    private readonly nint[] m_commandLists = new nint[FrameCount];
    private readonly ulong[] m_frameFenceValues = new ulong[FrameCount];

    // The encode pipeline's lease on the device's pass pipelines, held from Initialize to Dispose.
    private GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? m_encodeLease;
    private IGpuBindings? m_bindings;
    // The encode block for the chosen output, written once the output is chosen.
    private IGpuStorageBuffer? m_encodeBlock;
    private DirectXDrawCommand[]? m_encodeDrawCommands;
    // The encode group's pool, a range of the device's heaps, and its one set, whose source image Blit rewrites.
    private nint m_encodePool;
    private nint m_encodeSet;
    private IGpuSurfaceUpload? m_cpuUpload;
    private nint m_frameFence;
    private HANDLE m_frameFenceEvent;

    private ulong m_nextFrameFenceValue = 1;

    private IGpuSurfaceImport? m_surfaceImport;
    private uint m_height;
    private nint m_lastEncodedResource;
    // Set when the swap chain is created: the ALLOW_TEARING swap-chain flag (carried into ResizeBuffers too) and the
    // matching Present flag, both non-zero only for Immediate mode on a display that supports tearing.
    private uint m_presentFlags;
    // What the swap chain presents, chosen when it is created (SelectOutput), and its back buffers' two formats.
    private DisplayOutput m_output;
    private GpuPixelFormat m_surfaceFormat;
    private DXGI_FORMAT m_swapChainFormat;
    private nint m_rtvHeap;
    private uint m_rtvStride;
    private uint m_swapChainFlags;
    private nint m_swapChain;
    private uint m_width;
    // Closed-loop present timing. Vsync/Mailbox read GetFrameStatistics after each present (a TRUE display-scanout
    // timestamp). Immediate/Adaptive instead wait on the frame-latency waitable at the top of each frame (a present-
    // pipeline signal — the prior present being retired — timestamped with Stopwatch, the same QPC clock as SyncQPCTime).
    // Either way the pacer consumes only the inter-sample delta, so the two timestamp meanings phase-lock identically.
    // m_presentTimingAvailable = false signals "no sample" (disjoint stats, or a waitable timeout) → open-loop fallback.
    // All on the single pump thread that presents.
    private uint m_lastPresentCount;
    private long m_lastPresentQpcTicks;
    private bool m_presentTimingAvailable;
    // The frame-latency waitable HANDLE for Immediate/Adaptive; null (the default) for Vsync/Mailbox and before creation.
    private HANDLE m_frameLatencyWaitable;

    /// <summary>Initializes a new instance of the <see cref="DirectXSurfaceCompositor"/> class.</summary>
    /// <param name="commandListRecorder">Records draw commands into the per-frame command list.</param>
    /// <param name="presentationOptions">The neutral present-mode, surface-format, color-space and paper-white
    /// preferences.</param>
    /// <param name="pipelines">The composition's pass pipelines, which the display encode is an entry of.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commandListRecorder"/>, <paramref name="presentationOptions"/> or <paramref name="pipelines"/> is <see langword="null"/>.</exception>
    public DirectXSurfaceCompositor(
        IDirectXCommandListRecorder commandListRecorder,
        PresentationOptions presentationOptions,
        GpuPassPipelineCache pipelines
    ) {
        ArgumentNullException.ThrowIfNull(commandListRecorder);
        ArgumentNullException.ThrowIfNull(presentationOptions);
        ArgumentNullException.ThrowIfNull(pipelines);

        m_commandListRecorder = commandListRecorder;
        m_pipelines = pipelines;
        m_paperWhiteNits = presentationOptions.PaperWhiteNits;
        m_presentMode = presentationOptions.PresentMode;
        m_preferredFormat = presentationOptions.SurfaceFormat;
        m_requestedColorSpace = presentationOptions.ColorSpace;
        // Vsync presents with sync interval 1, the other modes with 0.
        m_syncInterval = ((PresentMode.Vsync == m_presentMode)
            ? 1u
            : 0u
        );
    }

    /// <summary>Gets what the swap chain presents, its format and color space, or <see langword="null"/> while no swap
    /// chain exists.</summary>
    public DisplayOutput? Output => ((0 == m_swapChain)
        ? null
        : m_output
    );

    /// <summary>
    /// Creates the DXGI swap chain, encode pipeline, and all supporting D3D12 objects against the shared device.
    /// Call exactly once when the host has a window handle.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The binding is not a Win32 surface binding.</exception>
    /// <exception cref="DirectXException">A DXGI or Direct3D 12 call failed.</exception>
    public void Initialize(DirectXDeviceContext deviceContext, NativeSurfaceBinding binding, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        if (
            (NativeDisplayKind.Win32 != binding.DisplayKind) ||
            (binding.Win32 is null)
        ) {
            throw new ArgumentException(message: "DirectX presentation requires a Win32 surface binding.");
        }

        m_width = width;
        m_height = height;
        SetOutput(output: Select(
            reported: SdrOutputs,
            requested: DisplayColorSpace.Srgb
        ));

        var device = ((ID3D12Device*)deviceContext.Device.Handle);

        CreateSwapChain(
            commandQueue: deviceContext.CommandQueueHandle,
            windowHandle: binding.Win32.Value.WindowHandle,
            height: height,
            width: width
        );
        SelectOutput();
        CreateRtvHeap(device: device);
        AcquireBackBuffers(device: device);
        var encodePipeline = AcquireEncodePipeline(deviceContext: deviceContext);
        var encodeSet = AllocateEncodeSet(
            deviceContext: deviceContext,
            encodePipeline: encodePipeline
        );
        var heaps = deviceContext.DescriptorHeaps;

        CreateCommandInfrastructure(device: device);

        // The encode draw command is invariant for the compositor's whole activation lifetime: every field it reads is
        // set once, above, and never reassigned. Building it once here (parity with the Vulkan compositor's cached
        // per-set draw-command arrays) removes a per-present heap allocation from Blit.
        m_encodeDrawCommands = [
            new DirectXDrawCommand(
                DrawParameters: new DirectXDrawParameters(
                    instanceCount: 1,
                    vertexCount: FullscreenTriangle.VertexCount
                ),
                Group: DisplayEncodeLayout.Group,
                PipelineLayoutHandle: encodePipeline.LayoutHandle,
                SamplerHeapHandle: heaps.SamplerHeap,
                SamplerTableGpuHandle: encodeSet.SamplerGpuBase,
                ViewHeapHandle: heaps.ViewHeap,
                ViewTableGpuHandle: encodeSet.GpuBase
            ),
        ];
    }
    /// <summary>
    /// Waits only for the ring slot this frame's <see cref="Present"/> is about to reuse (the pipelined replacement
    /// for a full-device drain — see <see cref="WaitForFrameSlot"/>), then resizes the swap chain if the window
    /// dimensions changed.
    /// </summary>
    public void BeginFrame(DirectXDeviceContext deviceContext, uint width, uint height) {
        if (m_swapChain == 0) {
            return;
        }

        // Tearing modes: wait on the frame-latency waitable at the TOP of the frame (the canonical placement) for the
        // PRIOR present to retire, and timestamp it as the present-confirmation sample — mirroring Vulkan's wait on the
        // prior present id. The wait overlaps the frame's own work, so it paces to the display without doubling the
        // host pacer's deadline wait. A no-op for Vsync/Mailbox (which use GetFrameStatistics after present instead).
        CaptureFrameLatencyTiming();

        WaitForFrameSlot();

        // The full-drain WaitIdle this replaced also flushed the D3D12 debug-layer message queue every frame
        // (opt-in via GpuDeviceOptions.DebugLayers); the per-slot wait above has nothing to do with that queue, so the drain
        // is called directly to keep the same per-frame cadence.
        deviceContext.DrainDebugMessages();

        if (
            (width == m_width) &&
            (height == m_height)
        ) {
            return;
        }

        // Resizing tears down and recreates every back buffer, so every ring slot must be idle first — the
        // per-slot wait above only proves the ONE slot this frame would reuse is free.
        deviceContext.WaitIdle();

        ReleaseBackBuffers();

        ((IDXGISwapChain3*)m_swapChain)->ResizeBuffers(
            BufferCount: FrameCount,
            Height: height,
            NewFormat: m_swapChainFormat,
            SwapChainFlags: m_swapChainFlags,
            Width: width
        );

        m_width = width;
        m_height = height;

        AcquireBackBuffers(device: ((ID3D12Device*)deviceContext.Device.Handle));
    }
    /// <summary>
    /// Encodes <paramref name="surface"/> fullscreen onto the current back buffer and presents. Handles
    /// GPU-resident surfaces (via <see cref="DirectXImageView"/> token), imported shared textures, and CPU pixel
    /// surfaces (uploaded via <see cref="DirectXSurfaceUpload"/>). A no-op when the surface is empty.
    /// </summary>
    public void Blit(DirectXDeviceContext deviceContext, Surface surface) {
        if (surface.IsEmpty) {
            return;
        }

        nint sourceView;

        if (surface.IsSameDeviceImage) {
            sourceView = surface.ImageViewHandle;
        } else if (surface.IsCpuPixels) {
            m_cpuUpload ??= deviceContext.Services.SurfaceTransferFactory.CreateUpload();
            // The upload texture is one resource shared by every ring slot: an in-flight frame may still be
            // sampling it, so overwriting it must wait for every presented frame, not just this slot's.
            WaitForAllFrames();
            sourceView = m_cpuUpload.Upload(
                format: surface.Format,
                height: surface.Height,
                pixels: surface.Pixels,
                width: surface.Width
            );
        } else if (surface.IsSharedHandle) {
            m_surfaceImport ??= deviceContext.Services.SurfaceTransferFactory.CreateImport();
            sourceView = m_surfaceImport.Import(
                sharedHandle: surface.SharedHandle,
                format: surface.Format,
                width: surface.Width,
                height: surface.Height
            ).ImageViewHandle;
        } else {
            throw new InvalidOperationException(message: "The surface has an unsupported payload kind.");
        }

        var sourceResource = ((DirectXImageView)GCHandle.FromIntPtr(value: sourceView).Target!).ResourceHandle;

        // Skip rewriting the set's source image when the source resource is unchanged (parity with the Vulkan
        // compositor's last-written-view cache).
        if (sourceResource != m_lastEncodedResource) {
            // The set's view is consumed at command-list execution, so rewriting it while the other ring slot's frame
            // is still in flight would redirect that frame's read mid-execution.
            WaitForAllFrames();
            m_bindings!.WriteSampledImage(
                arrayElement: 0U,
                binding: DisplayEncodeLayout.SourceImageBinding,
                descriptorSetHandle: m_encodeSet,
                imageViewHandle: sourceView
            );

            m_lastEncodedResource = sourceResource;
        }

        Present(
            deviceContext: deviceContext,
            drawCommands: m_encodeDrawCommands!
        );
    }
    /// <summary>
    /// Submits a caller-supplied list of draw commands to the current back buffer and presents. Each command
    /// specifies its own pipeline, group, descriptor heaps and descriptor tables; zero values mean "no change". Commands are replayed in list order (use <see cref="DirectXDrawCommand.SequenceKey"/>
    /// to pre-sort for painter's order).
    /// </summary>
    /// <param name="deviceContext">The shared device context.</param>
    /// <param name="drawCommands">The ordered list of draw commands to execute this frame.</param>
    public void Present(DirectXDeviceContext deviceContext, IReadOnlyList<DirectXDrawCommand> drawCommands) {
        if (m_swapChain == 0) {
            return;
        }

        var swapChain = ((IDXGISwapChain3*)m_swapChain);
        var frameIndex = swapChain->GetCurrentBackBufferIndex();
        var backBuffer = ((ID3D12Resource*)m_backBuffers[frameIndex]);
        var rtvBase = GetCpuHeapStart(heap: ((ID3D12DescriptorHeap*)m_rtvHeap));
        var rtvCpuHandle = ((nint)(rtvBase.ptr + ((nuint)(frameIndex * m_rtvStride))));

        var commandListHandle = m_commandLists[frameIndex];
        var allocator = ((ID3D12CommandAllocator*)m_commandAllocators[frameIndex]);
        var commandList = ((ID3D12GraphicsCommandList*)commandListHandle);

        var calls = DirectXDeviceCommandCalls.Of(deviceContext: deviceContext);

        DirectXCommandCalls.Reset(
            allocator: allocator,
            calls: calls,
            commandList: commandList
        );

        // The present path's display encode as a GPU-capture debug group (PIX event) — the Direct3D 12 peer of the
        // Vulkan "display-encode" debug-utils label.
        DirectXDebugLabel.Begin(
            commandList: commandList,
            label: "display-encode"
        );
        m_commandListRecorder.RecordBackBuffer(
            backBufferHandle: ((nint)backBuffer),
            commandListHandle: commandListHandle,
            drawCommands: drawCommands,
            rtvCpuHandle: rtvCpuHandle,
            viewportHeight: m_height,
            viewportWidth: m_width
        );
        DirectXDebugLabel.End(commandList: commandList);

        DirectXCommandCalls.Close(
            calls: calls,
            commandList: commandList
        );

        var executable = ((ID3D12CommandList*)commandListHandle);
        var commandQueue = ((ID3D12CommandQueue*)deviceContext.CommandQueueHandle);

        commandQueue->ExecuteCommandLists(
            NumCommandLists: 1,
            ppCommandLists: &executable
        );
        swapChain->Present(
            Flags: ((DXGI_PRESENT)m_presentFlags),
            SyncInterval: m_syncInterval
        ).ThrowIfFailed(operation: "IDXGISwapChain3::Present");
        CapturePresentTiming(swapChain: swapChain);

        // Arm this slot's fence value AFTER the queue submit so BeginFrame's next WaitForFrameSlot on this same
        // slot (FrameCount presents from now) proves the GPU is done with the allocator/list just submitted above.
        var fenceValue = m_nextFrameFenceValue;

        DirectXCommandCalls.Signal(
            calls: calls,
            fence: ((ID3D12Fence*)m_frameFence),
            queue: commandQueue,
            value: fenceValue
        );
        m_frameFenceValues[frameIndex] = fenceValue;
        m_nextFrameFenceValue = (fenceValue + 1);
    }

    /// <summary>Blocks until the ring slot <see cref="Present"/> is about to reuse this frame — the slot's
    /// allocator/list from <see cref="FrameCount"/> presents ago — has fully retired on the GPU. This is the
    /// pipelined replacement for a full <see cref="DirectXDeviceContext.WaitIdle"/> drain every frame: the host
    /// can record frame N while the GPU still executes frame N-1 (mirroring the Vulkan compositor's
    /// <c>WaitForFrameSlot</c>). A no-op for a slot that has never been presented into (its allocator was never
    /// recorded against, so it needs no wait before its first use).</summary>
    private void WaitForFrameSlot() {
        var swapChain = ((IDXGISwapChain3*)m_swapChain);
        var frameIndex = swapChain->GetCurrentBackBufferIndex();
        var targetValue = m_frameFenceValues[frameIndex];

        if (0 == targetValue) {
            return;
        }

        var fence = ((ID3D12Fence*)m_frameFence);

        if (fence->GetCompletedValue() < targetValue) {
            fence->SetEventOnCompletion(
                Value: targetValue,
                hEvent: m_frameFenceEvent
            );
            _ = PInvoke.WaitForSingleObject(
                dwMilliseconds: uint.MaxValue,
                hHandle: m_frameFenceEvent
            );
        }
    }
    /// <summary>Blocks until every presented frame has fully retired on the GPU — the guard for the resources the
    /// ring does not duplicate per slot: the single CPU-upload texture, the single encode SRV descriptor, and the
    /// per-slot allocators/lists at teardown. Any write to a cross-slot resource must run behind this, because
    /// <see cref="WaitForFrameSlot"/> only proves one slot idle while the other may still be sampling.</summary>
    private void WaitForAllFrames() {
        var lastSignaled = (m_nextFrameFenceValue - 1);

        if (
            (m_frameFence == 0) ||
            (lastSignaled == 0)
        ) {
            return;
        }

        var fence = ((ID3D12Fence*)m_frameFence);

        if (fence->GetCompletedValue() < lastSignaled) {
            fence->SetEventOnCompletion(
                Value: lastSignaled,
                hEvent: m_frameFenceEvent
            );
            _ = PInvoke.WaitForSingleObject(
                dwMilliseconds: uint.MaxValue,
                hHandle: m_frameFenceEvent
            );
        }
    }

    /// <summary>Gets the last display-confirmed present count and its QPC timestamp (Stopwatch ticks); <see langword="false"/> when unavailable.</summary>
    /// <param name="presentCount">The most recent confirmed present count.</param>
    /// <param name="presentQpcTicks">The QPC timestamp the present was shown at.</param>
    /// <returns><see langword="true"/> when a usable sample is available.</returns>
    internal bool TryGetPresentTiming(out uint presentCount, out long presentQpcTicks) {
        presentCount = m_lastPresentCount;
        presentQpcTicks = m_lastPresentQpcTicks;

        return m_presentTimingAvailable;
    }

    // Reads the display's frame statistics after a present so the host pacer can phase-lock to the real present rhythm.
    // GetFrameStatistics returns DXGI_ERROR_FRAME_STATISTICS_DISJOINT right after creation/resize/mode-change (normal),
    // so ALL failure is caught and treated as "no timing this frame" — present timing is render-side only and must never
    // throw out of the present path.
    private void CapturePresentTiming(IDXGISwapChain3* swapChain) {
        // Tearing modes capture timing from the frame-latency waitable in BeginFrame, not here — GetFrameStatistics has
        // no vblank sync-point at sync interval 0 and would just report a stale/zero SyncQPCTime.
        if (!m_frameLatencyWaitable.IsNull) {
            return;
        }

        try {
            swapChain->GetFrameStatistics(pStats: out var statistics);

            // GetFrameStatistics reflects only frames the display actually scanned out, so two presents faster than the
            // refresh return an UNCHANGED PresentCount (and the same SyncQPCTime). Availability must therefore require the
            // count to ADVANCE — otherwise the seam would report a stale sample as "available", which a consumer that does
            // not change-detect (the pacer does) could mis-read as a fresh present. SyncQPCTime > 0 still gates out the
            // DISJOINT/no-vsync case (e.g. tearing presents at sync interval 0).
            var advanced = (statistics.PresentCount != m_lastPresentCount);

            m_lastPresentCount = statistics.PresentCount;
            m_lastPresentQpcTicks = statistics.SyncQPCTime;
            m_presentTimingAvailable = (advanced && (statistics.SyncQPCTime > 0L));
        } catch {
            m_presentTimingAvailable = false;
        }
    }
    // Tearing modes (Immediate/Adaptive): wait (bounded) on the frame-latency waitable for the prior present to retire,
    // and publish the wait-return instant as the present-confirmation sample. The timestamp is a CPU-side proxy (a
    // pipeline signal, not a display scanout time) — exactly like Vulkan's vkWaitForPresentKHR return — and the pacer
    // uses only the inter-sample delta, so the constant offset cancels. A timeout (stalled/occluded pipeline) reports no
    // sample, so the pacer falls back to open-loop. Called at the top of each frame; a no-op when there is no waitable.
    // FIRST frame only: a waitable swap chain starts signaled (its initial latency credit), so the first wait returns
    // before any present — that one sample is not a retired present, but the pacer's priming discards it (it anchors only
    // from the second observed sample), and consumers read m_lastPresentCount as a delta, so it is harmless.
    private void CaptureFrameLatencyTiming() {
        if (m_frameLatencyWaitable.IsNull) {
            return;
        }

        var wait = PInvoke.WaitForSingleObject(
            dwMilliseconds: FrameLatencyWaitTimeoutMilliseconds,
            hHandle: m_frameLatencyWaitable
        );

        // WAIT_OBJECT_0 == 0: the waitable was signaled (a present retired). Compared numerically to avoid taking a
        // dependency on the WAIT_EVENT enum's namespace, which this project does not surface from its CsWin32 reference.
        if (((uint)wait) == 0u) {
            m_lastPresentQpcTicks = Stopwatch.GetTimestamp();

            unchecked {
                ++m_lastPresentCount;
            }

            m_presentTimingAvailable = true;
        } else {
            m_presentTimingAvailable = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        // The last present may still be executing against this slot's allocator/list — retire everything before
        // any release below.
        WaitForAllFrames();
        m_cpuUpload?.Dispose();
        m_cpuUpload = null;
        m_surfaceImport?.Dispose();
        m_surfaceImport = null;
        m_encodeDrawCommands = null;
        ReleaseBackBuffers();

        for (var i = 0; (i < m_commandLists.Length); i++) {
            Release(pointer: ref m_commandLists[i]);
            Release(pointer: ref m_commandAllocators[i]);
        }

        Release(pointer: ref m_frameFence);

        if (!m_frameFenceEvent.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_frameFenceEvent);
            m_frameFenceEvent = HANDLE.Null;
        }

        // The device's pass pipelines dispose the encode once no other lease holds it.
        m_encodeLease?.Release();
        m_encodeLease = null;
        // The pool's range returns to the device's heaps, and its set with it.
        m_bindings?.DestroyPool(poolHandle: m_encodePool);
        m_encodePool = 0;
        m_encodeSet = 0;
        m_bindings = null;
        m_encodeBlock?.Dispose();
        m_encodeBlock = null;
        m_lastEncodedResource = 0;
        Release(pointer: ref m_rtvHeap);
        Release(pointer: ref m_swapChain);

        // The frame-latency waitable is owned by the caller (per GetFrameLatencyWaitableObject), so close it after the
        // swap chain that produced it is released.
        if (!m_frameLatencyWaitable.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_frameLatencyWaitable);
            m_frameLatencyWaitable = default;
        }
    }

    // Whether this adapter/display supports variable-refresh tearing (needed before requesting an ALLOW_TEARING
    // swap chain for Immediate present). False when the factory predates IDXGIFactory5 or the feature is off.
    private static bool SupportsTearing(IDXGIFactory4* factory) {
        IDXGIFactory5* factory5 = null;
        var iid = IDXGIFactory5.IID_Guid;

        if (((IUnknown*)factory)->QueryInterface(
            ppvObject: ((void**)&factory5),
            riid: &iid
        ).Failed) {
            return false;
        }

        try {
            var allowTearing = 0;

            // CsWin32 generates CheckFeatureSupport as throwing (PreserveSig=false); an unrecognized feature throws.
            factory5->CheckFeatureSupport(
                Feature: DXGI_FEATURE.DXGI_FEATURE_PRESENT_ALLOW_TEARING,
                FeatureSupportDataSize: ((uint)sizeof(int)),
                pFeatureSupportData: &allowTearing
            );

            return (0 != allowTearing);
        } catch {
            return false;
        } finally {
            _ = ((IUnknown*)factory5)->Release();
        }
    }
    private void CreateSwapChain(nint commandQueue, nint windowHandle, uint width, uint height) {
        // Recompute the present/swap-chain flags from scratch — the compositor is a singleton reused across
        // Deactivate(Dispose)->Initialize cycles, and the flags below are OR-accumulated, so resetting here keeps a
        // re-activation from inheriting stale bits if the flag inputs ever vary per activation.
        m_swapChainFlags = 0;
        m_presentFlags = 0;

        void* factory;

        PInvoke.CreateDXGIFactory2(
            Flags: default,
            ppFactory: out factory,
            riid: IDXGIFactory4.IID_Guid
        ).ThrowIfFailed(operation: "CreateDXGIFactory2");

        var dxgiFactory = ((IDXGIFactory4*)factory);

        try {
            var tearingMode = ((PresentMode.Immediate == m_presentMode) || (PresentMode.Adaptive == m_presentMode));

            // The tearing modes drive a frame-latency-waitable swap chain so the host pacer can close the loop on the
            // present pipeline (GetFrameStatistics is dead at sync interval 0). Independent of tearing support, so set it
            // for both Immediate and Adaptive. Vsync/Mailbox leave it off and use GetFrameStatistics.
            if (tearingMode) {
                m_swapChainFlags |= DxgiSwapChainFlagFrameLatencyWaitableObject;
            }

            // Immediate and Adaptive present need an ALLOW_TEARING swap chain (carried into Present too) AND a
            // display that supports tearing; detect once so Vsync/Mailbox are unaffected and the tearing modes degrade
            // to no-vsync when unsupported. Vsync/Mailbox leave both flags zero.
            if (
                tearingMode &&
                SupportsTearing(factory: dxgiFactory)
            ) {
                m_swapChainFlags |= DxgiSwapChainFlagAllowTearing;
                m_presentFlags = DxgiPresentAllowTearing;
            }

            var desc = new DXGI_SWAP_CHAIN_DESC1 {
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_UNSPECIFIED,
                BufferCount = FrameCount,
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                Flags = ((DXGI_SWAP_CHAIN_FLAG)m_swapChainFlags),
                Format = m_swapChainFormat,
                Height = height,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                Width = width,
            };

            IDXGISwapChain1* sc1 = null;

            dxgiFactory->CreateSwapChainForHwnd(
                hWnd: ((HWND)windowHandle),
                pDesc: in desc,
                pDevice: ((IUnknown*)commandQueue),
                pFullscreenDesc: null,
                pRestrictToOutput: null,
                ppSwapChain: &sc1
            );

            // Prevent DXGI from registering its own window-message hooks (alt-enter, WM_SIZE monitoring).
            // Without this, each swap chain creation installs hooks that DXGI defers removing until the
            // message pump runs — causing CreateSwapChainForHwnd to fail with E_ACCESSDENIED on repeated
            // activate/deactivate cycles before the deferred cleanup has been processed.
            dxgiFactory->MakeWindowAssociation(
                Flags: DXGI_MWA_FLAGS.DXGI_MWA_NO_WINDOW_CHANGES | DXGI_MWA_FLAGS.DXGI_MWA_NO_ALT_ENTER,
                WindowHandle: ((HWND)windowHandle)
            );

            try {
                IDXGISwapChain3* sc3 = null;
                var iid = IDXGISwapChain3.IID_Guid;

                ((IUnknown*)sc1)->QueryInterface(
                    ppvObject: ((void**)&sc3),
                    riid: &iid
                )
                    .ThrowIfFailed(operation: "IDXGISwapChain1::QueryInterface(IDXGISwapChain3)");
                m_swapChain = ((nint)sc3);

                // Frame-latency-waitable swap chain (tearing modes only): cap the queue at one frame and grab the
                // waitable the pacer phase-locks to. Closing any prior handle keeps a re-created swap chain leak-free.
                if ((m_swapChainFlags & DxgiSwapChainFlagFrameLatencyWaitableObject) != 0) {
                    if (!m_frameLatencyWaitable.IsNull) {
                        _ = PInvoke.CloseHandle(hObject: m_frameLatencyWaitable);
                        m_frameLatencyWaitable = default;
                    }

                    // CsWin32 generates this as a friendly void overload that throws on a failing HRESULT.
                    sc3->SetMaximumFrameLatency(MaxLatency: 1);
                    m_frameLatencyWaitable = sc3->GetFrameLatencyWaitableObject();
                }
            } finally {
                _ = ((IUnknown*)sc1)->Release();
            }
        } finally {
            _ = ((IUnknown*)factory)->Release();
        }
    }
    private DisplayOutput Select(IReadOnlyCollection<DisplayOutput> reported, DisplayColorSpace requested) {
        // Every list offered holds SdrOutputs, so an output is always chosen.
        _ = DisplayOutput.TrySelect(
            chosen: out var chosen,
            preferredSdrFormat: m_preferredFormat,
            reported: reported,
            requested: requested
        );

        return chosen;
    }
    private void SetOutput(DisplayOutput output) {
        m_output = output;
        m_surfaceFormat = output.Format;
        m_swapChainFormat = DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: output.Format);
    }
    // Chooses what the swap chain, just created in SDR and holding no back buffer yet, presents: the requested HDR output
    // when the display reports it and the swap chain can present its color space, otherwise the SDR it was created in.
    private void SelectOutput() {
        var chosen = Select(
            reported: ReportedOutputs(),
            requested: m_requestedColorSpace
        );

        if (!chosen.IsHdr) {
            return;
        }

        var sdr = m_output;
        var swapChain = ((IDXGISwapChain3*)m_swapChain);
        var colorSpace = DirectXGpuFormats.ToDxgiColorSpace(colorSpace: chosen.ColorSpace);

        SetOutput(output: chosen);
        swapChain->ResizeBuffers(
            BufferCount: FrameCount,
            Height: m_height,
            NewFormat: m_swapChainFormat,
            SwapChainFlags: m_swapChainFlags,
            Width: m_width
        );
        swapChain->CheckColorSpaceSupport(
            ColorSpace: colorSpace,
            pColorSpaceSupport: out var support
        );

        if (0U != (support & ((uint)DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG.DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT))) {
            swapChain->SetColorSpace1(ColorSpace: colorSpace);

            return;
        }

        SetOutput(output: sdr);
        swapChain->ResizeBuffers(
            BufferCount: FrameCount,
            Height: m_height,
            NewFormat: m_swapChainFormat,
            SwapChainFlags: m_swapChainFlags,
            Width: m_width
        );
    }
    // The outputs the display the swap chain presents on reports: SDR always, and both HDR outputs when the display's
    // own color space is HDR10 (IDXGIOutput6::GetDesc1), which is how Windows reports HDR turned on. A display DXGI cannot
    // describe reports SDR alone.
    private List<DisplayOutput> ReportedOutputs() {
        var reported = new List<DisplayOutput>(collection: SdrOutputs);
        IDXGIOutput* output = null;
        IDXGIOutput6* output6 = null;

        try {
            ((IDXGISwapChain3*)m_swapChain)->GetContainingOutput(ppOutput: &output);

            var iid = IDXGIOutput6.IID_Guid;

            if (((IUnknown*)output)->QueryInterface(
                ppvObject: ((void**)&output6),
                riid: &iid
            ).Failed) {
                return reported;
            }

            if (DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 == output6->GetDesc1().ColorSpace) {
                reported.Add(item: new DisplayOutput(
                    ColorSpace: DisplayColorSpace.Hdr10,
                    Format: DisplayOutput.HdrFormatOf(colorSpace: DisplayColorSpace.Hdr10)
                ));
                reported.Add(item: new DisplayOutput(
                    ColorSpace: DisplayColorSpace.ScRgb,
                    Format: DisplayOutput.HdrFormatOf(colorSpace: DisplayColorSpace.ScRgb)
                ));
            }
        } catch (COMException) {
            // No containing output (a window off every display) reports SDR alone.
        } finally {
            if (null != output6) {
                _ = ((IUnknown*)output6)->Release();
            }
            if (null != output) {
                _ = ((IUnknown*)output)->Release();
            }
        }

        return reported;
    }
    private void CreateRtvHeap(ID3D12Device* device) {
        m_rtvHeap = ((nint)DirectXDescriptorHeaps.Create(
            count: FrameCount,
            device: device,
            type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV
        ));
        m_rtvStride = device->GetDescriptorHandleIncrementSize(DescriptorHeapType: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    }
    private void AcquireBackBuffers(ID3D12Device* device) {
        var swapChain = ((IDXGISwapChain3*)m_swapChain);
        var handle = GetCpuHeapStart(heap: ((ID3D12DescriptorHeap*)m_rtvHeap));
        var resourceIid = ID3D12Resource.IID_Guid;

        for (var i = 0u; (i < FrameCount); i++) {
            void* buffer;

            swapChain->GetBuffer(
                Buffer: i,
                ppSurface: &buffer,
                riid: &resourceIid
            );
            device->CreateRenderTargetView(
                DestDescriptor: handle,
                pDesc: null,
                pResource: ((ID3D12Resource*)buffer)
            );
            m_backBuffers[i] = ((nint)buffer);
            handle.ptr += m_rtvStride;
        }
    }
    private void ReleaseBackBuffers() {
        for (var i = 0; (i < m_backBuffers.Length); i++) {
            Release(pointer: ref m_backBuffers[i]);
        }
    }
    // Takes the display encode from the device's pass pipelines, for a render pass of one color attachment in the swap
    // chain's format; its vertex stage draws the fullscreen triangle from SV_VertexID, so the pipeline reads no vertex
    // input. The pool builds it, and the compositor waits for it once, here.
    private GpuPassPipeline AcquireEncodePipeline(DirectXDeviceContext deviceContext) {
        m_encodeLease = m_pipelines.Acquire(
            device: deviceContext,
            key: SurfaceEncoder.Key(
                directX: true,
                renderPass: new GpuRenderPassDescription(Colors: [new GpuColorAttachment(
                    FinalLayout: GpuImageLayout.RenderTarget,
                    Format: m_surfaceFormat,
                    Load: GpuAttachmentLoad.Clear,
                    Store: GpuAttachmentStore.Store
                )])
            )
        );

        return m_encodeLease.Wait(cancellationToken: CancellationToken.None);
    }
    // Creates the encode group's pool as a range of the device's shader-visible heaps, admitted first like every other
    // owner's so a heap that cannot hold it refuses it by name, and allocates its one set, whose sampler and block (the
    // chosen output's, at the paper-white level) never change.
    private DirectXDescriptorSet AllocateEncodeSet(DirectXDeviceContext deviceContext, GpuPassPipeline encodePipeline) {
        var bindings = deviceContext.Services.Bindings;
        GpuDescriptorPoolSizes[] pools = [GpuDescriptorPoolSizes.ForGroups(groups: DisplayEncodeLayout.Layout.Groups)];

        if (!bindings.CanAdmit(
            owner: EncodeOwner,
            pools: pools,
            refusal: out var refusal
        )) {
            throw new GpuDescriptorHeapRefusalException(message: refusal);
        }

        m_bindings = bindings;
        m_encodePool = bindings.CreatePool(
            name: new GpuObjectName(
                owner: EncodeOwner,
                part: "pool"
            ),
            sizes: in pools[0]
        );
        m_encodeSet = bindings.AllocateSet(
            descriptorSetLayoutHandle: encodePipeline.GroupLayoutHandles[((int)DisplayEncodeLayout.Group)],
            name: new GpuObjectName(
                owner: EncodeOwner,
                part: "set"
            ),
            poolHandle: m_encodePool
        );
        bindings.WriteSampler(
            arrayElement: 0U,
            binding: DisplayEncodeLayout.SamplerBinding,
            descriptorSetHandle: m_encodeSet,
            samplerHandle: bindings.CreateSampler(filter: GpuSamplerFilter.Linear)
        );
        m_encodeBlock = SurfaceEncoder.CreateBlock(
            gpu: deviceContext.Services,
            name: new GpuObjectName(
                owner: EncodeOwner,
                part: "block"
            ),
            output: m_output,
            paperWhiteNits: m_paperWhiteNits
        );
        bindings.WriteConstantBuffer(
            arrayElement: 0U,
            binding: DisplayEncodeLayout.BlockBinding,
            bufferHandle: m_encodeBlock.BufferHandle,
            bufferSize: m_encodeBlock.SizeBytes,
            descriptorSetHandle: m_encodeSet
        );
        // A fresh set has no source written yet; the next Blit writes one.
        m_lastEncodedResource = 0;

        return ((DirectXDescriptorSet)GCHandle.FromIntPtr(value: m_encodeSet).Target!);
    }
    private void CreateCommandInfrastructure(ID3D12Device* device) {
        for (var i = 0u; (i < FrameCount); i++) {
            device->CreateCommandAllocator(
                ppCommandAllocator: out var allocator,
                riid: ID3D12CommandAllocator.IID_Guid,
                type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT
            );
            m_commandAllocators[i] = ((nint)allocator);

            device->CreateCommandList(
                nodeMask: 0,
                pCommandAllocator: ((ID3D12CommandAllocator*)allocator),
                pInitialState: null,
                ppCommandList: out var commandList,
                riid: ID3D12GraphicsCommandList.IID_Guid,
                type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT
            );
            m_commandLists[i] = ((nint)commandList);
            DirectXCommandCalls.Close(
                calls: new DirectXDeviceCommandCalls(device: device),
                commandList: ((ID3D12GraphicsCommandList*)commandList)
            );

            // A fresh allocator has nothing recorded against it yet, so its slot needs no wait before first use.
            m_frameFenceValues[i] = 0;
        }

        device->CreateFence(
            Flags: default,
            InitialValue: 0,
            ppFence: out var frameFence,
            riid: ID3D12Fence.IID_Guid
        );
        m_frameFence = ((nint)frameFence);
        m_nextFrameFenceValue = 1;
        m_frameFenceEvent = PInvoke.CreateEvent(
            bInitialState: false,
            bManualReset: false,
            lpEventAttributes: ((SECURITY_ATTRIBUTES*)null),
            lpName: default(PCWSTR)
        );

        if (m_frameFenceEvent.IsNull) {
            throw new DirectXException(
                operation: "CreateEventW",
                result: Marshal.GetHRForLastWin32Error()
            );
        }
    }
}
