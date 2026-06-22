using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.DirectX.Messages;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX.Presentation;

/// <summary>
/// Owns the DXGI flip-model swap chain, back-buffer RTVs, a shader-visible SRV slot for the blit texture,
/// and the blit pipeline (root signature + PSO). On every frame it:
/// <list type="bullet">
///   <item>resets the per-frame command allocator and command list,</item>
///   <item>delegates recording to the injected <see cref="IDirectXCommandListRecorder"/>,</item>
///   <item>closes, executes, and presents the command list.</item>
/// </list>
/// <para>
/// The blit path (<see cref="Blit"/>) builds a single <see cref="DirectXDrawCommand"/> that blits one
/// <see cref="Surface"/> fullscreen. The multi-draw path (<see cref="Present"/>) accepts a caller-supplied
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
    private const string BlitVertexHlsl = """
        struct VSOutput {
            float4 Position : SV_Position;
            float2 TexCoord : TEXCOORD0;
        };
        VSOutput main(uint id : SV_VertexID) {
            float2 uv = float2(id == 1u ? 2.0 : 0.0, id == 2u ? 2.0 : 0.0);
            VSOutput o;
            o.TexCoord = uv;
            o.Position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
            return o;
        }
        """;
    private const string BlitPixelHlsl = """
        Texture2D<float4> g_Source : register(t0);
        SamplerState g_Sampler : register(s0);
        float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
            return g_Source.Sample(g_Sampler, uv);
        }
        """;

    private static byte[]? s_blitPixelBytecode;
    private static byte[]? s_blitVertexBytecode;
    private readonly IDirectXCommandListRecorder m_commandListRecorder;
    private readonly IDirectXShaderCompilerApi m_shaderCompiler;
    private readonly DXGI_FORMAT m_swapChainFormat;
    private readonly PresentMode m_presentMode;
    private readonly uint m_syncInterval;
    private readonly nint[] m_backBuffers = new nint[FrameCount];
    private GCHandle m_blitLayoutToken;
    private nint m_commandAllocator;
    private nint m_commandList;
    private DirectXSurfaceUpload? m_cpuUpload;
    private uint m_height;
    private nint m_lastBlitResource;
    // Set when the swap chain is created: the ALLOW_TEARING swap-chain flag (carried into ResizeBuffers too) and the
    // matching Present flag, both non-zero only for Immediate mode on a display that supports tearing.
    private uint m_presentFlags;
    private nint m_rtvHeap;
    private uint m_rtvStride;
    private nint m_srvHeap;
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
    /// <param name="presentationOptions">The neutral present-mode and surface-format preferences.</param>
    /// <param name="shaderCompiler">Compiles the blit vertex and pixel shaders to bytecode.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commandListRecorder"/>, <paramref name="presentationOptions"/>, or <paramref name="shaderCompiler"/> is <see langword="null"/>.</exception>
    public DirectXSurfaceCompositor(IDirectXCommandListRecorder commandListRecorder, PresentationOptions presentationOptions, IDirectXShaderCompilerApi shaderCompiler) {
        ArgumentNullException.ThrowIfNull(commandListRecorder);
        ArgumentNullException.ThrowIfNull(presentationOptions);
        ArgumentNullException.ThrowIfNull(shaderCompiler);

        m_commandListRecorder = commandListRecorder;
        m_shaderCompiler = shaderCompiler;
        m_presentMode = presentationOptions.PresentMode;
        // Map the neutral surface format to the back-buffer DXGI format (both are valid flip-model formats);
        // Vsync presents with sync interval 1, the other modes with 0.
        m_swapChainFormat = presentationOptions.SurfaceFormat switch {
            SurfaceFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            _ => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        };
        m_syncInterval = ((PresentMode.Vsync == m_presentMode) ? 1u : 0u);
    }

    /// <summary>Gets the blit pipeline layout handle (a <see cref="GCHandle"/>-as-<see cref="nint"/> token to
    /// the internal <see cref="DirectXPipelineLayout"/>), valid after <see cref="Initialize"/>. Callers that
    /// build their own <see cref="DirectXDrawCommand"/> lists can reference it as the default blit pipeline.</summary>
    public nint BlitPipelineLayoutHandle => m_blitLayoutToken.IsAllocated
        ? GCHandle.ToIntPtr(m_blitLayoutToken)
        : 0;

    /// <summary>Gets the GPU descriptor handle (<c>D3D12_GPU_DESCRIPTOR_HANDLE.ptr</c>) for the compositor's
    /// single SRV slot; valid after <see cref="Initialize"/>. The handle points at whatever texture was last
    /// written via <see cref="Blit"/>.</summary>
    public ulong BlitDescriptorGpuHandle { get; private set; }

    /// <summary>Gets the native <c>ID3D12DescriptorHeap*</c> for the compositor's shader-visible SRV heap;
    /// valid after <see cref="Initialize"/>.</summary>
    public nint BlitDescriptorHeapHandle => m_srvHeap;

    /// <summary>
    /// Creates the DXGI swap chain, blit pipeline, and all supporting D3D12 objects against the shared device.
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

        var device = (ID3D12Device*)deviceContext.DeviceHandle;

        CreateSwapChain(
            commandQueue: deviceContext.CommandQueueHandle,
            windowHandle: binding.Win32.Value.WindowHandle,
            height: height,
            width: width
        );
        CreateRtvHeap(device: device);
        AcquireBackBuffers(device: device);
        CreateSrvHeap(device: device);
        CreateBlitPipeline(device: device);
        CreateCommandInfrastructure(device: device);
    }

    /// <summary>
    /// Waits for the GPU to become idle (draining the previous frame), then resizes the swap chain if the
    /// window dimensions changed.
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

        deviceContext.WaitIdle();

        if (
            (width == m_width) &&
            (height == m_height)
        ) {
            return;
        }

        ReleaseBackBuffers();

        ((IDXGISwapChain3*)m_swapChain)->ResizeBuffers(
            FrameCount,
            width,
            height,
            m_swapChainFormat,
            m_swapChainFlags
        );

        m_width = width;
        m_height = height;

        AcquireBackBuffers(device: (ID3D12Device*)deviceContext.DeviceHandle);
    }

    /// <summary>
    /// Blits <paramref name="surface"/> fullscreen onto the current back buffer and presents. Handles
    /// GPU-resident surfaces (via <see cref="DirectXImageView"/> token) and CPU pixel surfaces (uploaded via
    /// <see cref="DirectXSurfaceUpload"/>). A no-op when the surface is empty.
    /// </summary>
    public void Blit(DirectXDeviceContext deviceContext, Surface surface) {
        if (surface.IsEmpty) {
            return;
        }

        var device = (ID3D12Device*)deviceContext.DeviceHandle;

        nint sourceResource;
        DXGI_FORMAT sourceFormat;

        if (surface.ImageViewHandle != 0) {
            var view = (DirectXImageView)GCHandle.FromIntPtr(surface.ImageViewHandle).Target!;

            sourceResource = view.ResourceHandle;
            sourceFormat = view.Format;
        } else if (surface.IsCpuPixels) {
            m_cpuUpload ??= new DirectXSurfaceUpload(deviceContext: deviceContext);
            m_cpuUpload.Upload(
                pixels: surface.Pixels.Span,
                width: surface.Width,
                height: surface.Height,
                format: surface.Format switch {
                    SurfaceFormat.B8G8R8A8Unorm => DirectXPixelFormat.B8G8R8A8Unorm,
                    SurfaceFormat.R8G8B8A8Unorm => DirectXPixelFormat.R8G8B8A8Unorm,
                    _ => throw new InvalidOperationException(message: "The surface format has no DirectX mapping."),
                }
            );
            sourceResource = m_cpuUpload.TextureHandle;
            sourceFormat = m_cpuUpload.TextureFormat;
        } else {
            return;
        }

        // Skip rewriting the single SRV descriptor when the source resource is unchanged (parity with the
        // Vulkan compositor's last-written-view cache).
        if (sourceResource != m_lastBlitResource) {
            WriteSrv(device: device, resource: (ID3D12Resource*)sourceResource, format: sourceFormat);

            m_lastBlitResource = sourceResource;
        }

        Present(
            deviceContext: deviceContext,
            drawCommands: [
                new DirectXDrawCommand(
                    DrawParameters: new DirectXDrawParameters(
                        instanceCount: 1,
                        vertexCount: 3
                    ),
                    DescriptorHeapHandle: m_srvHeap,
                    DescriptorTableGpuHandle: BlitDescriptorGpuHandle,
                    PipelineLayoutHandle: BlitPipelineLayoutHandle
                ),
            ]
        );
    }

    /// <summary>
    /// Submits a caller-supplied list of draw commands to the current back buffer and presents. Each command
    /// specifies its own pipeline, descriptor heap, descriptor table, vertex buffer, and root constants; zero
    /// values mean "no change". Commands are replayed in list order (use <see cref="DirectXDrawCommand.SequenceKey"/>
    /// to pre-sort for painter's order).
    /// </summary>
    /// <param name="deviceContext">The shared device context.</param>
    /// <param name="drawCommands">The ordered list of draw commands to execute this frame.</param>
    public void Present(DirectXDeviceContext deviceContext, IReadOnlyList<DirectXDrawCommand> drawCommands) {
        if (m_swapChain == 0) {
            return;
        }

        var swapChain = (IDXGISwapChain3*)m_swapChain;
        var frameIndex = swapChain->GetCurrentBackBufferIndex();
        var backBuffer = (ID3D12Resource*)m_backBuffers[frameIndex];
        var rtvBase = GetCpuHeapStart((ID3D12DescriptorHeap*)m_rtvHeap);
        var rtvCpuHandle = (nint)(rtvBase.ptr + (nuint)(frameIndex * m_rtvStride));

        var allocator = (ID3D12CommandAllocator*)m_commandAllocator;
        var commandList = (ID3D12GraphicsCommandList*)m_commandList;

        allocator->Reset();
        commandList->Reset(pAllocator: allocator, pInitialState: null);

        m_commandListRecorder.RecordBackBuffer(
            commandListHandle: m_commandList,
            backBufferHandle: (nint)backBuffer,
            rtvCpuHandle: rtvCpuHandle,
            viewportWidth: m_width,
            viewportHeight: m_height,
            drawCommands: drawCommands
        );

        commandList->Close();

        var executable = (ID3D12CommandList*)m_commandList;

        ((ID3D12CommandQueue*)deviceContext.CommandQueueHandle)->ExecuteCommandLists(1, &executable);
        swapChain->Present(m_syncInterval, (DXGI_PRESENT)m_presentFlags).ThrowIfFailed(operation: "IDXGISwapChain3::Present");
        CapturePresentTiming(swapChain: swapChain);
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
            swapChain->GetFrameStatistics(out var statistics);

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

        var wait = PInvoke.WaitForSingleObject(hHandle: m_frameLatencyWaitable, dwMilliseconds: FrameLatencyWaitTimeoutMilliseconds);

        // WAIT_OBJECT_0 == 0: the waitable was signaled (a present retired). Compared numerically to avoid taking a
        // dependency on the WAIT_EVENT enum's namespace, which this project does not surface from its CsWin32 reference.
        if ((uint)wait == 0u) {
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
        m_cpuUpload?.Dispose();
        m_cpuUpload = null;
        ReleaseBackBuffers();
        Release(pointer: ref m_commandList);
        Release(pointer: ref m_commandAllocator);

        if (m_blitLayoutToken.IsAllocated) {
            var layout = (DirectXPipelineLayout)m_blitLayoutToken.Target!;

            if (0 != layout.PsoHandle) {
                _ = ((IUnknown*)layout.PsoHandle)->Release();
                layout.PsoHandle = 0;
            }

            if (0 != layout.RootSignatureHandle) {
                _ = ((IUnknown*)layout.RootSignatureHandle)->Release();
                layout.RootSignatureHandle = 0;
            }

            m_blitLayoutToken.Free();
        }

        Release(pointer: ref m_srvHeap);
        Release(pointer: ref m_rtvHeap);
        Release(pointer: ref m_swapChain);

        // The frame-latency waitable is owned by the caller (per GetFrameLatencyWaitableObject), so close it after the
        // swap chain that produced it is released.
        if (!m_frameLatencyWaitable.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_frameLatencyWaitable);
            m_frameLatencyWaitable = default;
        }
    }

    private static D3D12_CPU_DESCRIPTOR_HANDLE GetCpuHeapStart(ID3D12DescriptorHeap* heap) {
        D3D12_CPU_DESCRIPTOR_HANDLE handle;
        var vtable = *(void***)heap;

        ((delegate* unmanaged[Stdcall]<ID3D12DescriptorHeap*, D3D12_CPU_DESCRIPTOR_HANDLE*, void>)vtable[GetCpuDescriptorHandleSlot])(
            heap,
            &handle
        );

        return handle;
    }
    private static D3D12_GPU_DESCRIPTOR_HANDLE GetGpuHeapStart(ID3D12DescriptorHeap* heap) {
        D3D12_GPU_DESCRIPTOR_HANDLE handle;
        var vtable = *(void***)heap;

        ((delegate* unmanaged[Stdcall]<ID3D12DescriptorHeap*, D3D12_GPU_DESCRIPTOR_HANDLE*, void>)vtable[GetGpuDescriptorHandleSlot])(
            heap,
            &handle
        );

        return handle;
    }
    private static void Release(ref nint pointer) {
        if (0 != pointer) {
            _ = ((IUnknown*)pointer)->Release();
            pointer = 0;
        }
    }
    // Whether this adapter/display supports variable-refresh tearing (needed before requesting an ALLOW_TEARING
    // swap chain for Immediate present). False when the factory predates IDXGIFactory5 or the feature is off.
    private static bool SupportsTearing(IDXGIFactory4* factory) {
        IDXGIFactory5* factory5 = null;
        var iid = IDXGIFactory5.IID_Guid;

        if (((IUnknown*)factory)->QueryInterface(&iid, (void**)&factory5).Failed) {
            return false;
        }

        try {
            var allowTearing = 0;

            // CsWin32 generates CheckFeatureSupport as throwing (PreserveSig=false); an unrecognized feature throws.
            factory5->CheckFeatureSupport(
                DXGI_FEATURE.DXGI_FEATURE_PRESENT_ALLOW_TEARING,
                &allowTearing,
                (uint)sizeof(int)
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
            riid: IDXGIFactory4.IID_Guid,
            ppFactory: out factory
        ).ThrowIfFailed(operation: "CreateDXGIFactory2");

        var dxgiFactory = (IDXGIFactory4*)factory;

        try {
            var tearingMode = ((PresentMode.Immediate == m_presentMode) || (PresentMode.Adaptive == m_presentMode));

            // The tearing modes drive a frame-latency-waitable swap chain so the host pacer can close the loop on the
            // present pipeline (GetFrameStatistics is dead at sync interval 0). Independent of tearing support, so set it
            // for both Immediate and Adaptive. Vsync/Mailbox leave it off and use GetFrameStatistics.
            if (tearingMode) {
                m_swapChainFlags |= DxgiSwapChainFlagFrameLatencyWaitableObject;
            }

            // Immediate and Adaptive (VRR) present need an ALLOW_TEARING swap chain (carried into Present too) AND a
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
                Flags = (DXGI_SWAP_CHAIN_FLAG)m_swapChainFlags,
                Format = m_swapChainFormat,
                Height = height,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                Width = width,
            };

            IDXGISwapChain1* sc1 = null;

            dxgiFactory->CreateSwapChainForHwnd(
                pDevice: (IUnknown*)commandQueue,
                hWnd: (HWND)windowHandle,
                pDesc: in desc,
                pFullscreenDesc: null,
                pRestrictToOutput: null,
                ppSwapChain: &sc1
            );

            // Prevent DXGI from registering its own window-message hooks (alt-enter, WM_SIZE monitoring).
            // Without this, each swap chain creation installs hooks that DXGI defers removing until the
            // message pump runs — causing CreateSwapChainForHwnd to fail with E_ACCESSDENIED on repeated
            // activate/deactivate cycles before the deferred cleanup has been processed.
            dxgiFactory->MakeWindowAssociation(
                WindowHandle: (HWND)windowHandle,
                Flags: DXGI_MWA_FLAGS.DXGI_MWA_NO_WINDOW_CHANGES | DXGI_MWA_FLAGS.DXGI_MWA_NO_ALT_ENTER
            );

            try {
                IDXGISwapChain3* sc3 = null;
                var iid = IDXGISwapChain3.IID_Guid;

                ((IUnknown*)sc1)->QueryInterface(&iid, (void**)&sc3)
                    .ThrowIfFailed(operation: "IDXGISwapChain1::QueryInterface(IDXGISwapChain3)");
                m_swapChain = (nint)sc3;

                // Frame-latency-waitable swap chain (tearing modes only): cap the queue at one frame and grab the
                // waitable the pacer phase-locks to. Closing any prior handle keeps a re-created swap chain leak-free.
                if ((m_swapChainFlags & DxgiSwapChainFlagFrameLatencyWaitableObject) != 0) {
                    if (!m_frameLatencyWaitable.IsNull) {
                        _ = PInvoke.CloseHandle(hObject: m_frameLatencyWaitable);
                        m_frameLatencyWaitable = default;
                    }

                    // CsWin32 generates this as a friendly void overload that throws on a failing HRESULT.
                    sc3->SetMaximumFrameLatency(1);
                    m_frameLatencyWaitable = sc3->GetFrameLatencyWaitableObject();
                }
            } finally {
                _ = ((IUnknown*)sc1)->Release();
            }
        } finally {
            _ = ((IUnknown*)factory)->Release();
        }
    }
    private void CreateRtvHeap(ID3D12Device* device) {
        var heapDesc = new D3D12_DESCRIPTOR_HEAP_DESC {
            Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_NONE,
            NumDescriptors = FrameCount,
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
        };

        device->CreateDescriptorHeap(
            pDescriptorHeapDesc: in heapDesc,
            riid: ID3D12DescriptorHeap.IID_Guid,
            ppvHeap: out var rtvHeap
        );
        m_rtvHeap = (nint)rtvHeap;
        m_rtvStride = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    }
    private void AcquireBackBuffers(ID3D12Device* device) {
        var swapChain = (IDXGISwapChain3*)m_swapChain;
        var handle = GetCpuHeapStart((ID3D12DescriptorHeap*)m_rtvHeap);
        var resourceIid = ID3D12Resource.IID_Guid;

        for (var i = 0u; (i < FrameCount); i++) {
            void* buffer;

            swapChain->GetBuffer(i, &resourceIid, &buffer);
            device->CreateRenderTargetView((ID3D12Resource*)buffer, null, handle);
            m_backBuffers[i] = (nint)buffer;
            handle.ptr += m_rtvStride;
        }
    }
    private void ReleaseBackBuffers() {
        for (var i = 0; (i < m_backBuffers.Length); i++) {
            Release(pointer: ref m_backBuffers[i]);
        }
    }
    private void CreateSrvHeap(ID3D12Device* device) {
        // One SRV: the blit samples a SINGLE source texture into the swapchain back buffer. `WriteSrv` always writes
        // slot 0. This is deliberate scope — the compositor is a single-source present, not a multi-layer compositor;
        // adding more source layers would require sizing this heap from the layer count and a per-slot WriteSrv.
        var heapDesc = new D3D12_DESCRIPTOR_HEAP_DESC {
            Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
            NumDescriptors = 1,
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
        };

        device->CreateDescriptorHeap(
            pDescriptorHeapDesc: in heapDesc,
            riid: ID3D12DescriptorHeap.IID_Guid,
            ppvHeap: out var srvHeap
        );
        m_srvHeap = (nint)srvHeap;
        BlitDescriptorGpuHandle = GetGpuHeapStart(heap: (ID3D12DescriptorHeap*)srvHeap).ptr;
        // A fresh SRV heap has no descriptor written yet; force the next Blit to write one.
        m_lastBlitResource = 0;
    }
    private void CreateBlitPipeline(ID3D12Device* device) {
        s_blitVertexBytecode ??= CompileShaderToBytes(source: BlitVertexHlsl, sourceName: "blit.vs", entryPoint: "main", target: "vs_5_0");
        s_blitPixelBytecode ??= CompileShaderToBytes(source: BlitPixelHlsl, sourceName: "blit.ps", entryPoint: "main", target: "ps_5_0");

        var rootSig = CreateBlitRootSignature(device: device);
        nint pso;

        fixed (byte* pVs = s_blitVertexBytecode)
        fixed (byte* pPs = s_blitPixelBytecode) {
            pso = CreateBlitPso(
                device: device,
                rootSignature: rootSig,
                renderTargetFormat: m_swapChainFormat,
                vsHandle: (nint)pVs,
                vsLength: (nuint)s_blitVertexBytecode.Length,
                psHandle: (nint)pPs,
                psLength: (nuint)s_blitPixelBytecode.Length
            );
        }

        m_blitLayoutToken = GCHandle.Alloc(new DirectXPipelineLayout {
            DescriptorTableParamIndex = 0,
            // One SRV at table slot 0 (the source texture). The compositor writes that descriptor into its own heap
            // directly rather than through the shared allocator, but the map is filled for consistency so a future
            // AllocateSet against this layout resolves binding 0 to slot 0 like every other layout.
            DescriptorSlotCount = 1,
            PsoHandle = pso,
            RootSignatureHandle = rootSig,
            SlotByBinding = [0],
        });
    }
    private byte[] CompileShaderToBytes(string source, string sourceName, string entryPoint, string target) {
        using var bytecode = m_shaderCompiler.Compile(request: new DirectXShaderCompileRequest(
            HlslSource: source,
            SourceName: sourceName,
            EntryPoint: entryPoint,
            Target: target
        ));

        var bytes = new byte[(int)bytecode.BufferLength];

        fixed (byte* destination = bytes) {
            Buffer.MemoryCopy(
                source: (void*)bytecode.BufferPointer,
                destination: destination,
                destinationSizeInBytes: bytes.Length,
                sourceBytesToCopy: bytes.Length
            );
        }

        return bytes;
    }
    private static nint CreateBlitRootSignature(ID3D12Device* device) {
        var srvRange = new D3D12_DESCRIPTOR_RANGE {
            BaseShaderRegister = 0,
            NumDescriptors = 1,
            OffsetInDescriptorsFromTableStart = 0,
            RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
            RegisterSpace = 0,
        };
        var tableParam = new D3D12_ROOT_PARAMETER {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL,
        };

        tableParam.Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE {
            NumDescriptorRanges = 1,
            pDescriptorRanges = &srvRange,
        };

        var staticSampler = new D3D12_STATIC_SAMPLER_DESC {
            AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            BorderColor = D3D12_STATIC_BORDER_COLOR.D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK,
            ComparisonFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_NEVER,
            Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR,
            MaxAnisotropy = 0,
            MaxLOD = float.MaxValue,
            MinLOD = 0f,
            MipLODBias = 0f,
            RegisterSpace = 0,
            ShaderRegister = 0,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL,
        };
        var rootSigDesc = new D3D12_ROOT_SIGNATURE_DESC {
            Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT,
            NumParameters = 1,
            NumStaticSamplers = 1,
            pParameters = &tableParam,
            pStaticSamplers = &staticSampler,
        };

        ID3DBlob* sigBlob = null;
        ID3DBlob* errBlob = null;

        try {
            PInvoke.D3D12SerializeRootSignature(
                in rootSigDesc,
                D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1,
                &sigBlob,
                &errBlob
            ).ThrowIfFailed(operation: "D3D12SerializeRootSignature");

            void* rootSig;
            var rootSigIid = ID3D12RootSignature.IID_Guid;

            device->CreateRootSignature(
                0,
                sigBlob->GetBufferPointer(),
                sigBlob->GetBufferSize(),
                in rootSigIid,
                out rootSig
            );

            return (nint)rootSig;
        } finally {
            if (sigBlob is not null) { _ = sigBlob->Release(); }
            if (errBlob is not null) { _ = errBlob->Release(); }
        }
    }
    private static nint CreateBlitPso(
        ID3D12Device* device,
        nint rootSignature,
        DXGI_FORMAT renderTargetFormat,
        nint vsHandle,
        nuint vsLength,
        nint psHandle,
        nuint psLength
    ) {
        var psoDesc = new D3D12_GRAPHICS_PIPELINE_STATE_DESC {
            BlendState = new D3D12_BLEND_DESC {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = false,
            },
            DepthStencilState = new D3D12_DEPTH_STENCIL_DESC {
                DepthEnable = false,
                StencilEnable = false,
            },
            InputLayout = new D3D12_INPUT_LAYOUT_DESC {
                NumElements = 0,
                pInputElementDescs = null,
            },
            NumRenderTargets = 1,
            PS = new D3D12_SHADER_BYTECODE {
                BytecodeLength = psLength,
                pShaderBytecode = (void*)psHandle,
            },
            PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE,
            RasterizerState = new D3D12_RASTERIZER_DESC {
                AntialiasedLineEnable = false,
                ConservativeRaster = D3D12_CONSERVATIVE_RASTERIZATION_MODE.D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF,
                CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE,
                DepthBias = 0,
                DepthBiasClamp = 0f,
                DepthClipEnable = true,
                FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID,
                ForcedSampleCount = 0,
                FrontCounterClockwise = false,
                MultisampleEnable = false,
                SlopeScaledDepthBias = 0f,
            },
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            SampleMask = uint.MaxValue,
            VS = new D3D12_SHADER_BYTECODE {
                BytecodeLength = vsLength,
                pShaderBytecode = (void*)vsHandle,
            },
            pRootSignature = (ID3D12RootSignature*)rootSignature,
        };

        psoDesc.BlendState.RenderTarget._0 = new D3D12_RENDER_TARGET_BLEND_DESC {
            BlendEnable = false,
            BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD,
            BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD,
            DestBlend = D3D12_BLEND.D3D12_BLEND_ZERO,
            DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_ZERO,
            LogicOp = D3D12_LOGIC_OP.D3D12_LOGIC_OP_NOOP,
            LogicOpEnable = false,
            RenderTargetWriteMask = 15,
            SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE,
            SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE,
        };
        psoDesc.RTVFormats._0 = renderTargetFormat;

        void* pso;
        var psoIid = ID3D12PipelineState.IID_Guid;

        device->CreateGraphicsPipelineState(in psoDesc, in psoIid, out pso);

        return (nint)pso;
    }
    private void CreateCommandInfrastructure(ID3D12Device* device) {
        device->CreateCommandAllocator(
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            riid: ID3D12CommandAllocator.IID_Guid,
            ppCommandAllocator: out var allocator
        );
        m_commandAllocator = (nint)allocator;

        device->CreateCommandList(
            nodeMask: 0,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            pCommandAllocator: (ID3D12CommandAllocator*)allocator,
            pInitialState: null,
            riid: ID3D12GraphicsCommandList.IID_Guid,
            ppCommandList: out var commandList
        );
        m_commandList = (nint)commandList;
        ((ID3D12GraphicsCommandList*)commandList)->Close();
    }
    private void WriteSrv(ID3D12Device* device, ID3D12Resource* resource, DXGI_FORMAT format) {
        var srvDesc = new D3D12_SHADER_RESOURCE_VIEW_DESC {
            Format = format,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D,
        };

        srvDesc.Anonymous.Texture2D.MipLevels = 1;

        device->CreateShaderResourceView(
            pResource: resource,
            pDesc: &srvDesc,
            DestDescriptor: GetCpuHeapStart((ID3D12DescriptorHeap*)m_srvHeap)
        );
    }
}
