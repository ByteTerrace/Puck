using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Puck.Platform.Windows.Interop;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using WinRT;

namespace Puck.Platform.Windows;

/// <summary>
/// A compositor-owned Windows Graphics Capture feed with two transports off the same free-threaded callback: a CPU
/// path (a cadence-gated staging readback, atomically published into a triple-buffer ring so the buffer returned by
/// TryCapture is never written by the producer) and, when GPU targets are attached, a zero-copy path that copies each
/// captured frame straight into a consumer-provisioned D3D12-shared texture on the capture adapter and publishes the
/// completed slot. The GPU path is the D3D12 render host's transport; the CPU path stays live (at a reduced cadence) for
/// the Vulkan host and the POST probe.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class Win32GraphicsCaptureFeed : INativeImageCaptureFeed {
    private const long DisposeTimeoutMilliseconds = 2000L;
    private const int FramePoolBufferCount = 2;
    private const long LivenessCheckIntervalMilliseconds = 100L;
    private const int MaximumDimension = 8192;
    private const long MaximumSourcePixels = 67_108_864L;
    private const int Running = 0;
    private const int Stopped = 2;
    private const int Stopping = 1;
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>Creates and starts a fully owned window feed, or returns false without retaining native resources.</summary>
    public static bool TryCreate(nint windowHandle, int width, int height, double refreshRateHz, [NotNullWhen(true)] out Win32GraphicsCaptureFeed? feed, long? adapterLuid = null) {
        return TryCreateCore(targetKind: CaptureTargetKind.Window, targetHandle: windowHandle, width: width, height: height, refreshRateHz: refreshRateHz, feed: out feed, adapterLuid: adapterLuid);
    }

    /// <summary>Creates and starts a fully owned monitor feed, or returns false without retaining native resources.</summary>
    public static bool TryCreateForMonitor(nint monitorHandle, int width, int height, double refreshRateHz, [NotNullWhen(true)] out Win32GraphicsCaptureFeed? feed, long? adapterLuid = null) {
        return TryCreateCore(targetKind: CaptureTargetKind.Monitor, targetHandle: monitorHandle, width: width, height: height, refreshRateHz: refreshRateHz, feed: out feed, adapterLuid: adapterLuid);
    }

    private static bool TryCreateCore(CaptureTargetKind targetKind, nint targetHandle, int width, int height, double refreshRateHz, [NotNullWhen(true)] out Win32GraphicsCaptureFeed? feed, long? adapterLuid) {
        feed = null;
        ValidateOutputExtent(width: width, height: height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: refreshRateHz);
        if (!double.IsFinite(refreshRateHz)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(refreshRateHz), actualValue: refreshRateHz, message: "The refresh rate must be finite.");
        }

        if (targetHandle == 0) {
            return false;
        }

        try {
            feed = new Win32GraphicsCaptureFeed(targetKind: targetKind, targetHandle: targetHandle, width: width, height: height, refreshRateHz: refreshRateHz, adapterLuid: adapterLuid);
            if (feed.IsEnded) {
                feed.Dispose();
                feed = null;
                return false;
            }

            return true;
        } catch {
            feed?.Dispose();
            feed = null;
            return false;
        }
    }

    private readonly object m_callbackGate = new();
    private readonly TypedEventHandler<Direct3D11CaptureFramePool, object> m_frameArrivedHandler;
    private readonly object m_lifetimeGate = new();
    private readonly uint m_ownerProcessId;
    private readonly uint m_ownerThreadId;
    private readonly object m_publicationGate = new();
    private readonly long m_refreshPeriodTicks;
    private readonly TypedEventHandler<GraphicsCaptureItem, object> m_targetClosedHandler;
    private readonly nint m_targetHandle;
    private readonly int m_targetHeight;
    private readonly CaptureTargetKind m_targetKind;
    private readonly int m_targetWidth;
    private byte[] m_consumerPixels;
    private byte[] m_publishedPixels;
    private byte[] m_workingPixels;
    private int m_activeCallbacks;
    private GraphicsCaptureItem? m_captureItem;
    private GraphicsCaptureSession? m_captureSession;
    private long m_consumedRevision;
    private int m_cleanupQueued;
    private int m_cpuReadbackCounter;
    private Win32GraphicsCaptureDevice? m_device;
    private Direct3D11CaptureFramePool? m_framePool;
    private volatile GpuTargetSet? m_gpuTargets;
    private long m_gpuRevision;
    private bool m_hasFrame;
    private volatile bool m_isEnded;
    private long m_lastLivenessCheckTicks;
    private volatile int m_latestGpuSlot = -1;
    private long m_nextCaptureTicks;
    private long m_publishedRevision;
    private int m_sourceHeight;
    private int m_sourceWidth;
    private int m_state = Stopped;

    /// <inheritdoc/>
    public bool IsEnded {
        get {
            // GraphicsCaptureItem.Closed is the fast path, but Win32 destruction can precede that callback by an
            // unbounded compositor delay. Consumers poll this property, so also retire a window feed once its owner
            // window is gone. The HWND alone is unreliable — Win32 recycles handles — so the fallback also matches the
            // owning process/thread, and is rate-limited to keep the per-poll syscall cost off the hot path. A monitor
            // target has no owner window; disconnect surfaces through GraphicsCaptureItem.Closed, which latches m_isEnded.
            if (m_isEnded) {
                return true;
            }

            var now = Environment.TickCount64;
            if ((now - m_lastLivenessCheckTicks) >= LivenessCheckIntervalMilliseconds) {
                m_lastLivenessCheckTicks = now;
                if (!IsTargetAlive()) {
                    m_isEnded = true;
                }
            }

            return m_isEnded;
        }
    }

    /// <inheritdoc/>
    public int SourceWidth => Volatile.Read(location: ref m_sourceWidth);

    /// <inheritdoc/>
    public int SourceHeight => Volatile.Read(location: ref m_sourceHeight);

    /// <inheritdoc/>
    public int LatestGpuSlot => m_latestGpuSlot;

    /// <inheritdoc/>
    public long GpuRevision => Interlocked.Read(location: ref m_gpuRevision);

    /// <inheritdoc/>
    public bool GpuTargetsOutdated {
        get {
            // A resize latches the new extent into m_source* under the callback gate; the attached set keeps its
            // creation extent, so a mismatch means GPU publishing has paused until a matching AttachGpuTargets.
            var targets = m_gpuTargets;
            return (targets is not null) && ((targets.Width != Volatile.Read(location: ref m_sourceWidth)) || (targets.Height != Volatile.Read(location: ref m_sourceHeight)));
        }
    }

    /// <inheritdoc/>
    public void AttachGpuTargets(NativeImageGpuCaptureTargets targets) {
        ArgumentNullException.ThrowIfNull(argument: targets);
        var handles = targets.SharedTargetHandles;
        ArgumentNullException.ThrowIfNull(argument: handles);
        if (handles.Count < 2) {
            throw new ArgumentException(message: "At least two shared targets are required so the writer stays off the slot a consumer is sampling.", paramName: nameof(targets));
        }
        ValidateOutputExtent(width: targets.Width, height: targets.Height);

        var device = m_device ?? throw new ObjectDisposedException(objectName: nameof(Win32GraphicsCaptureFeed));

        // Open every shared handle once, off the callback gate (the capture device is multithread-protected). A partial
        // failure releases what opened so no target leaks.
        var slotTextures = new nint[handles.Count];
        var opened = 0;
        try {
            for (; opened < slotTextures.Length; opened++) {
                slotTextures[opened] = device.OpenSharedTarget(sharedHandle: handles[opened], expectedWidth: targets.Width, expectedHeight: targets.Height);
            }
        } catch {
            for (var i = 0; i < opened; i++) {
                Win32GraphicsCaptureDevice.ReleaseTexture(texture: slotTextures[i]);
            }

            throw;
        }

        var newTargets = new GpuTargetSet(slotTextures: slotTextures, width: targets.Width, height: targets.Height, cpuReadbackDivisor: targets.CpuReadbackDivisor);
        GpuTargetSet? oldTargets;
        // The pump copies under m_callbackGate, so swapping the set there guarantees no in-flight copy references the
        // outgoing textures; the new set (fresh handles) restarts the published slot.
        lock (m_callbackGate) {
            oldTargets = m_gpuTargets;
            m_gpuTargets = newTargets;
            m_latestGpuSlot = -1;
            m_cpuReadbackCounter = 0;
        }

        ReleaseTargetSet(targets: oldTargets);
    }

    private bool IsTargetAlive() {
        if (m_targetKind == CaptureTargetKind.Monitor) {
            return !m_isEnded;
        }

        if (!User32.IsWindow(windowHandle: m_targetHandle)) {
            return false;
        }

        var threadId = User32.GetWindowThreadProcessId(windowHandle: m_targetHandle, processId: out var processId);
        return (threadId == m_ownerThreadId) && (processId == m_ownerProcessId);
    }

    private Win32GraphicsCaptureFeed(CaptureTargetKind targetKind, nint targetHandle, int width, int height, double refreshRateHz, long? adapterLuid) {
        m_targetHandle = targetHandle;
        m_targetHeight = height;
        m_targetKind = targetKind;
        m_targetWidth = width;
        if (targetKind == CaptureTargetKind.Window) {
            m_ownerThreadId = User32.GetWindowThreadProcessId(windowHandle: targetHandle, processId: out m_ownerProcessId);
        }
        var outputByteLength = checked((width * height) * 4);
        m_consumerPixels = GC.AllocateUninitializedArray<byte>(length: outputByteLength);
        m_publishedPixels = GC.AllocateUninitializedArray<byte>(length: outputByteLength);
        m_workingPixels = GC.AllocateUninitializedArray<byte>(length: outputByteLength);
        m_refreshPeriodTicks = Math.Max(val1: 1L, val2: (long)Math.Round(Stopwatch.Frequency / refreshRateHz));
        m_frameArrivedHandler = OnFrameArrived;
        m_targetClosedHandler = OnTargetClosed;

        try {
            m_device = new Win32GraphicsCaptureDevice(adapterLuid: adapterLuid);
            m_captureItem = CreateCaptureItem(targetKind: targetKind, targetHandle: targetHandle);
            var initialSize = m_captureItem.Size;
            ValidateSourceExtent(width: initialSize.Width, height: initialSize.Height);
            m_sourceHeight = initialSize.Height;
            m_sourceWidth = initialSize.Width;
            m_device.RecreateReadbacks(width: m_sourceWidth, height: m_sourceHeight);
            m_framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device: m_device.RuntimeDevice,
                pixelFormat: DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: FramePoolBufferCount,
                size: initialSize
            );
            m_captureSession = m_framePool.CreateCaptureSession(item: m_captureItem);
            m_captureSession.IsCursorCaptureEnabled = false;
            // IsBorderRequired suppresses the Win11 (22000+) yellow capture highlight. The setter can be access-gated,
            // so a failed suppression must not fail capture.
            if (OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 22000)) {
                try {
                    m_captureSession.IsBorderRequired = false;
                } catch {
                }
            }
            m_captureItem.Closed += m_targetClosedHandler;
            m_framePool.FrameArrived += m_frameArrivedHandler;
            m_state = Running;
            m_captureSession.StartCapture();
        } catch {
            m_state = Stopped;
            DetachEvents();
            ReleaseResources();
            throw;
        }
    }

    /// <inheritdoc/>
    public bool TryCapture(out Surface surface) {
        lock (m_publicationGate) {
            if (!m_hasFrame || (m_consumedRevision == m_publishedRevision)) {
                surface = default;
                return false;
            }

            // Triple-buffer ring: the callback only mutates working and PublishWorkingFrame swaps working<->published,
            // so swapping published<->consumer here hands the consumer the latest frame without a full-buffer copy. The
            // returned buffer stays the consumer's until the next TryCapture swaps it back out.
            (m_consumerPixels, m_publishedPixels) = (m_publishedPixels, m_consumerPixels);
            m_consumedRevision = m_publishedRevision;
        }

        surface = new Surface(
            ImageViewHandle: 0,
            Width: checked((uint)m_targetWidth),
            Height: checked((uint)m_targetHeight),
            Format: SurfaceFormat.B8G8R8A8Unorm,
            Pixels: m_consumerPixels
        );
        return true;
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (m_lifetimeGate) {
            while (m_state == Stopping) {
                Monitor.Wait(obj: m_lifetimeGate);
            }

            if (m_state == Stopped) {
                return;
            }

            m_state = Stopping;
            m_isEnded = true;
        }

        DetachEvents();

        // Dispose runs on the render thread. A FrameArrived callback stalled in native D3D11 (driver hang/TDR) must not
        // block teardown forever, so the drain is bounded. On timeout the stalled callback may still touch the native
        // resources and buffers, so they are abandoned (deliberately leaked) rather than freed — a leak on a hung driver
        // beats a use-after-free. Events are already detached and the state is already Stopping.
        var callbacksDrained = true;
        lock (m_lifetimeGate) {
            var deadline = Environment.TickCount64 + DisposeTimeoutMilliseconds;
            while (m_activeCallbacks != 0) {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0L) {
                    callbacksDrained = false;
                    break;
                }

                Monitor.Wait(obj: m_lifetimeGate, millisecondsTimeout: (int)remaining);
            }
        }

        if (callbacksDrained) {
            ReleaseResources();
        }

        lock (m_lifetimeGate) {
            m_state = Stopped;
            Monitor.PulseAll(obj: m_lifetimeGate);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object _) {
        if (!TryAcquireCallback()) {
            return;
        }

        var ended = false;
        try {
            if (!Monitor.TryEnter(obj: m_callbackGate)) {
                return;
            }

            try {
                PumpFrame(sender: sender);
            } finally {
                Monitor.Exit(obj: m_callbackGate);
            }
        } catch {
            ended = true;
        } finally {
            ReleaseCallback();
        }

        if (ended) {
            EndAndScheduleDispose();
        }
    }

    private void OnTargetClosed(GraphicsCaptureItem _, object __) {
        EndAndScheduleDispose();
    }

    // Never close WGC objects from inside one of their own event invocations. The owner can still dispose immediately
    // after observing IsEnded; this queued cleanup guarantees release when no consumer samples the ended feed.
    private void EndAndScheduleDispose() {
        m_isEnded = true;
        if (Interlocked.Exchange(location1: ref m_cleanupQueued, value: 1) == 0) {
            _ = ThreadPool.UnsafeQueueUserWorkItem(
                callBack: static (Win32GraphicsCaptureFeed feed) => feed.Dispose(),
                state: this,
                preferLocal: false
            );
        }
    }

    private void PumpFrame(Direct3D11CaptureFramePool sender) {
        var resize = false;
        var contentSize = default(SizeInt32);
        var frame = sender.TryGetNextFrame();
        if (frame is null) {
            return;
        }
        try {
            contentSize = frame.ContentSize;
            // Minimized/occluded windows may transiently report no drawable content. That is an unavailable frame,
            // not an ended capture item; target closure is reported separately by GraphicsCaptureItem.Closed.
            if ((contentSize.Width <= 0) || (contentSize.Height <= 0)) {
                return;
            }

            ValidateSourceExtent(width: contentSize.Width, height: contentSize.Height);
            resize = ((contentSize.Width != m_sourceWidth) || (contentSize.Height != m_sourceHeight));
            if (!resize) {
                ProcessCurrentSizeFrame(frame: frame);
            }
        } finally {
            CloseFrameAndRelease(frame: frame);
        }

        if (resize) {
            RecreateForSourceSize(size: contentSize);
        }
    }

    private void ProcessCurrentSizeFrame(Direct3D11CaptureFrame frame) {
        if (m_device!.TryReadCompleted(destination: m_workingPixels, targetWidth: m_targetWidth, targetHeight: m_targetHeight)) {
            PublishWorkingFrame();
        }

        var now = Stopwatch.GetTimestamp();
        if (now < m_nextCaptureTicks) {
            return;
        }

        m_nextCaptureTicks += m_refreshPeriodTicks;
        if ((m_nextCaptureTicks <= now) || ((now - m_nextCaptureTicks) > m_refreshPeriodTicks)) {
            m_nextCaptureTicks = now + m_refreshPeriodTicks;
        }

        var surface = frame.Surface;
        try {
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();
            try {
                var texture = access.GetInterface(iid: ID3D11Texture2D.IID_Guid);
                try {
                    var runCpuReadback = true;
                    var gpuTargets = m_gpuTargets;
                    if (gpuTargets is not null) {
                        PublishGpuFrame(gpuTargets: gpuTargets, sourceTexture: texture);
                        // With GPU mode active, throttle the CPU readback to every Nth tick (or off entirely). The
                        // divisor keeps the glow and probe fed without paying the full staging-readback cost each frame.
                        runCpuReadback = ShouldRunCpuReadback(divisor: gpuTargets.CpuReadbackDivisor);
                    }

                    if (runCpuReadback) {
                        _ = m_device.TryQueueReadback(sourceTexture: texture);
                    }
                } finally {
                    _ = Marshal.Release(pUnk: texture);
                }
            } finally {
                _ = Marshal.ReleaseComObject(access);
            }
        } finally {
            ((IWinRTObject)surface).NativeObject.Dispose();
        }
    }

    // Copies the captured frame into the next round-robin GPU slot and publishes it. A source/target extent mismatch
    // (a resize between attach and now) pauses GPU publishing — GpuTargetsOutdated reports it — until a matching
    // AttachGpuTargets. Runs under m_callbackGate, so the attached set cannot be swapped mid-copy.
    private void PublishGpuFrame(GpuTargetSet gpuTargets, nint sourceTexture) {
        if ((gpuTargets.Width != m_sourceWidth) || (gpuTargets.Height != m_sourceHeight)) {
            return;
        }

        var slot = gpuTargets.NextSlot;
        m_device!.CopyToSharedTargetAndDrain(targetTexture: gpuTargets.SlotTextures[slot], sourceTexture: sourceTexture);
        gpuTargets.NextSlot = (slot + 1) % gpuTargets.SlotTextures.Length;
        m_latestGpuSlot = slot;
        _ = Interlocked.Increment(location: ref m_gpuRevision);
    }

    private bool ShouldRunCpuReadback(int divisor) {
        if (divisor <= 0) {
            return false;
        }

        if (++m_cpuReadbackCounter < divisor) {
            return false;
        }

        m_cpuReadbackCounter = 0;
        return true;
    }

    private void RecreateForSourceSize(SizeInt32 size) {
        ValidateSourceExtent(width: size.Width, height: size.Height);
        m_device!.RecreateReadbacks(width: size.Width, height: size.Height);
        m_framePool!.Recreate(
            device: m_device.RuntimeDevice,
            pixelFormat: DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: FramePoolBufferCount,
            size: size
        );
        m_sourceHeight = size.Height;
        m_sourceWidth = size.Width;
    }

    private void PublishWorkingFrame() {
        lock (m_publicationGate) {
            (m_publishedPixels, m_workingPixels) = (m_workingPixels, m_publishedPixels);
            m_hasFrame = true;
            m_publishedRevision++;
        }
    }

    private bool TryAcquireCallback() {
        lock (m_lifetimeGate) {
            if (m_state != Running) {
                return false;
            }

            m_activeCallbacks++;
            return true;
        }
    }

    private void ReleaseCallback() {
        lock (m_lifetimeGate) {
            m_activeCallbacks--;
            if (m_activeCallbacks == 0) {
                Monitor.PulseAll(obj: m_lifetimeGate);
            }
        }
    }

    private void DetachEvents() {
        if (m_framePool is not null) {
            try {
                m_framePool.FrameArrived -= m_frameArrivedHandler;
            } catch {
            }
        }

        if (m_captureItem is not null) {
            try {
                m_captureItem.Closed -= m_targetClosedHandler;
            } catch {
            }
        }
    }

    private void ReleaseResources() {
        var targetExists = IsTargetAlive();
        if (targetExists) {
            CloseAndRelease(value: m_captureSession);
        } else {
            ReleaseProjection(value: m_captureSession);
        }
        m_captureSession = null;

        if (targetExists) {
            CloseAndRelease(value: m_framePool);
        } else {
            ReleaseProjection(value: m_framePool);
        }
        m_framePool = null;

        if (m_captureItem is IWinRTObject captureItem) {
            try {
                captureItem.NativeObject.Dispose();
            } catch (ObjectDisposedException) {
            }
        }
        m_captureItem = null;

        // Shared targets were opened on the capture device; release them before it. Only reached once callbacks have
        // drained (or from the constructor's failure path), so no copy can still reference a slot texture.
        ReleaseTargetSet(targets: m_gpuTargets);
        m_gpuTargets = null;

        m_device?.Dispose();
        m_device = null;
    }

    private static void ReleaseTargetSet(GpuTargetSet? targets) {
        if (targets is null) {
            return;
        }

        foreach (var texture in targets.SlotTextures) {
            Win32GraphicsCaptureDevice.ReleaseTexture(texture: texture);
        }
    }

    private static void ReleaseProjection(object? value) {
        if (value is IWinRTObject projection) {
            try {
                projection.NativeObject.Dispose();
            } catch (ObjectDisposedException) {
            }
        }
    }

    private static void CloseAndRelease(IDisposable? value) {
        if (value is null) {
            return;
        }

        try {
            value.Dispose();
        } catch {
        } finally {
            ReleaseProjection(value: value);
        }
    }

    private static void CloseFrameAndRelease(Direct3D11CaptureFrame frame) {
        try {
            frame.Dispose();
        } finally {
            ReleaseProjection(value: frame);
        }
    }

    private static GraphicsCaptureItem CreateCaptureItem(CaptureTargetKind targetKind, nint targetHandle) {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemPointer = targetKind == CaptureTargetKind.Monitor
            ? interop.CreateForMonitor(monitor: targetHandle, iid: GraphicsCaptureItemGuid)
            : interop.CreateForWindow(window: targetHandle, iid: GraphicsCaptureItemGuid);
        try {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        } finally {
            _ = Marshal.Release(pUnk: itemPointer);
        }
    }

    private static bool ExceedsResourceBudget(int width, int height) {
        return (width > MaximumDimension) || (height > MaximumDimension) || (((long)width * height) > MaximumSourcePixels);
    }

    private static void ValidateOutputExtent(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);
        if (ExceedsResourceBudget(width: width, height: height)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(width), message: $"Capture extents are limited to {MaximumDimension} per dimension and {MaximumSourcePixels} pixels.");
        }

        _ = checked((width * height) * 4);
    }

    private static void ValidateSourceExtent(int width, int height) {
        if ((width <= 0) || (height <= 0) || ExceedsResourceBudget(width: width, height: height)) {
            throw new InvalidOperationException(message: $"The capture source extent {width}x{height} exceeds the supported resource budget.");
        }
    }

    private enum CaptureTargetKind {
        Window,
        Monitor,
    }

    // The attached GPU targets: the opened shared slot textures plus the extent they were sized to and the CPU-readback
    // divisor. NextSlot is the round-robin write cursor, mutated only by the pump under m_callbackGate.
    private sealed class GpuTargetSet {
        public GpuTargetSet(nint[] slotTextures, int width, int height, int cpuReadbackDivisor) {
            CpuReadbackDivisor = cpuReadbackDivisor;
            Height = height;
            SlotTextures = slotTextures;
            Width = width;
        }

        public int CpuReadbackDivisor { get; }
        public int Height { get; }
        public int NextSlot { get; set; }
        public nint[] SlotTextures { get; }
        public int Width { get; }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IDirect3DDxgiInterfaceAccess {
        nint GetInterface(in Guid iid);
    }
}

[SupportedOSPlatform("windows10.0.19041")]
internal sealed unsafe class Win32GraphicsCaptureDevice : IDisposable {
    private const int DxgiErrorWasStillDrawing = unchecked((int)0x887A000A);
    private readonly ReadbackSlot[] m_readbacks = [new(), new(), new()];
    private ID3D11DeviceContext* m_context;
    private ID3D11Device* m_device;
    private ID3D11Device1* m_device1;
    private ulong[]? m_downscaleAccumulators;
    private ID3D11Query* m_gpuCopyQuery;
    private IDirect3DDevice? m_runtimeDevice;
    private long m_sequence;
    private int m_sourceHeight;
    private int m_sourceWidth;

    public IDirect3DDevice RuntimeDevice => m_runtimeDevice ?? throw new ObjectDisposedException(nameof(Win32GraphicsCaptureDevice));

    // A LUID pins the device to the render host's adapter so its shared-target opens succeed (cross-adapter shared-handle
    // opens fail); the default (null) keeps the CPU-only path adapter-agnostic. An explicit adapter forces UNKNOWN driver
    // type. device1 carries OpenSharedResource1 and the event query drains each GPU copy.
    public Win32GraphicsCaptureDevice(long? adapterLuid = null) {
        IDXGIAdapter1* adapter = null;
        if (adapterLuid is long luid) {
            adapter = Win32D3D11.FindAdapterByLuid(adapterLuid: luid);
            if (adapter is null) {
                throw new InvalidOperationException(message: $"no DXGI adapter was found with LUID 0x{luid:X16}");
            }
        }

        try {
            Win32D3D11.CreateMultithreadedDevice(
                adapter: (IDXGIAdapter*)adapter,
                driverType: adapter is null ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                flags: D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                device: out var device,
                context: out var context
            );
            m_context = context;
            m_device = device;

            var device1Iid = ID3D11Device1.IID_Guid;
            Win32D3D11.ThrowIfFailed(hr: ((IUnknown*)device)->QueryInterface(in device1Iid, out var device1), operation: "QueryInterface(ID3D11Device1)");
            m_device1 = (ID3D11Device1*)device1;

            var queryDesc = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };
            ID3D11Query* query;
            device->CreateQuery(pQueryDesc: &queryDesc, ppQuery: &query);
            m_gpuCopyQuery = query;

            var dxgiIid = IDXGIDevice.IID_Guid;
            Win32D3D11.ThrowIfFailed(hr: ((IUnknown*)device)->QueryInterface(in dxgiIid, out var dxgiDevice), operation: "QueryInterface(IDXGIDevice)");
            try {
                Win32D3D11.ThrowIfFailed(hr: new HRESULT(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice: (nint)dxgiDevice, graphicsDevice: out var inspectableDevice)), operation: "CreateDirect3D11DeviceFromDXGIDevice");
                try {
                    m_runtimeDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectableDevice);
                } finally {
                    _ = Marshal.Release(pUnk: inspectableDevice);
                }
            } finally {
                _ = ((IUnknown*)dxgiDevice)->Release();
            }
        } catch {
            Dispose();
            throw;
        } finally {
            if (adapter is not null) {
                _ = ((IUnknown*)adapter)->Release();
            }
        }
    }

    // Opens a consumer-provisioned shared texture (a D3D12 CreateSharedHandle NT handle) on this device; the caller owns
    // the returned ID3D11Texture2D* and releases it via ReleaseTexture. Device-level and safe off the callback gate
    // (the device is multithread-protected). Rejects a target whose format or extent CopyResource would silently drop:
    // the capture pool is B8G8R8A8_UNORM, and a cross-format/extent CopyResource is a release-build no-op, not an error.
    public nint OpenSharedTarget(nint sharedHandle, int expectedWidth, int expectedHeight) {
        using var handle = new SafeFileHandle(preexistingHandle: sharedHandle, ownsHandle: false);

        m_device1->OpenSharedResource1(
            hResource: handle,
            returnedInterface: ID3D11Texture2D.IID_Guid,
            ppResource: out var texture
        );

        D3D11_TEXTURE2D_DESC description;
        ((ID3D11Texture2D*)texture)->GetDesc(pDesc: &description);
        if ((description.Format != DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM)
            || (description.Width != (uint)expectedWidth)
            || (description.Height != (uint)expectedHeight)) {
            ReleaseTexture(texture: (nint)texture);
            throw new ArgumentException(
                message: $"The shared target must be a {expectedWidth}x{expectedHeight} B8G8R8A8_UNORM texture; got {description.Width}x{description.Height} {description.Format}.",
                paramName: nameof(sharedHandle)
            );
        }

        return (nint)texture;
    }

    // Copies the captured frame into a shared target and blocks (on the callback thread, at the readback cadence) until
    // the copy has completed on the GPU, so the slot is safe for another device to sample. Mirrors the camera GPU tier's
    // event-query drain; no D3D11 fence exists in this codebase and none is needed.
    public void CopyToSharedTargetAndDrain(nint targetTexture, nint sourceTexture) {
        m_context->CopyResource(pDstResource: (ID3D11Resource*)targetTexture, pSrcResource: (ID3D11Resource*)sourceTexture);
        m_context->End(pAsync: (ID3D11Asynchronous*)m_gpuCopyQuery);
        m_context->Flush();

        BOOL done = false;
        while (!done) {
            m_context->GetData(pAsync: (ID3D11Asynchronous*)m_gpuCopyQuery, pData: &done, DataSize: (uint)sizeof(BOOL), GetDataFlags: 0);
            if (!done) {
                Thread.SpinWait(iterations: 64);
            }
        }
    }

    // Releases a COM pointer obtained from this device (an opened shared target); zero is ignored.
    public static void ReleaseTexture(nint texture) {
        if (0 != texture) {
            _ = ((IUnknown*)texture)->Release();
        }
    }

    public void RecreateReadbacks(int width, int height) {
        ReleaseReadbacks();
        var description = new D3D11_TEXTURE2D_DESC {
            Width = checked((uint)width),
            Height = checked((uint)height),
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = (D3D11_BIND_FLAG)0,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = (D3D11_RESOURCE_MISC_FLAG)0,
        };

        try {
            foreach (var readback in m_readbacks) {
                ID3D11Texture2D* texture;
                m_device->CreateTexture2D(pDesc: &description, pInitialData: null, ppTexture2D: &texture);
                if (texture is null) {
                    throw new InvalidOperationException(message: "D3D11 staging texture creation returned no texture.");
                }

                readback.Texture = texture;
            }
        } catch {
            ReleaseReadbacks();
            throw;
        }

        m_sourceHeight = height;
        m_sourceWidth = width;
    }

    public bool TryQueueReadback(nint sourceTexture) {
        ReadbackSlot? slot = null;
        foreach (var candidate in m_readbacks) {
            if (!candidate.Pending) {
                slot = candidate;
                break;
            }
        }

        if ((slot is null) || (slot.Texture is null)) {
            return false;
        }

        m_context->CopyResource(pDstResource: (ID3D11Resource*)slot.Texture, pSrcResource: (ID3D11Resource*)sourceTexture);
        m_context->Flush();
        slot.Pending = true;
        slot.Sequence = ++m_sequence;
        return true;
    }

    public bool TryReadCompleted(byte[] destination, int targetWidth, int targetHeight) {
        var sequenceCeiling = long.MaxValue;
        for (var attempt = 0; attempt < m_readbacks.Length; attempt++) {
            ReadbackSlot? candidate = null;
            foreach (var readback in m_readbacks) {
                if (readback.Pending && (readback.Sequence < sequenceCeiling) && ((candidate is null) || (readback.Sequence > candidate.Sequence))) {
                    candidate = readback;
                }
            }

            if (candidate is null) {
                return false;
            }

            if (TryMap(readback: candidate, destination: destination, targetWidth: targetWidth, targetHeight: targetHeight)) {
                foreach (var readback in m_readbacks) {
                    if (readback.Pending && (readback.Sequence <= candidate.Sequence)) {
                        readback.Pending = false;
                    }
                }

                return true;
            }

            sequenceCeiling = candidate.Sequence;
        }

        return false;
    }

    private bool TryMap(ReadbackSlot readback, byte[] destination, int targetWidth, int targetHeight) {
        var mapped = new D3D11_MAPPED_SUBRESOURCE();
        var vtable = *(nint**)m_context;
        var map = (delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, ID3D11Resource*, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, HRESULT>)vtable[14];
        var hr = map(
            m_context,
            (ID3D11Resource*)readback.Texture,
            0,
            D3D11_MAP.D3D11_MAP_READ,
            (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT,
            &mapped
        );
        if (hr.Value == DxgiErrorWasStillDrawing) {
            return false;
        }

        Win32D3D11.ThrowIfFailed(hr: hr, operation: "ID3D11DeviceContext::Map");
        m_downscaleAccumulators ??= new ulong[targetWidth * 4];
        try {
            ScaleMapped(
                source: (byte*)mapped.pData,
                sourceRowPitch: mapped.RowPitch,
                sourceWidth: m_sourceWidth,
                sourceHeight: m_sourceHeight,
                destination: destination,
                targetWidth: targetWidth,
                targetHeight: targetHeight,
                accumulators: m_downscaleAccumulators
            );
        } finally {
            m_context->Unmap(pResource: (ID3D11Resource*)readback.Texture, Subresource: 0);
        }

        return true;
    }

    private static void ScaleMapped(byte* source, uint sourceRowPitch, int sourceWidth, int sourceHeight, byte[] destination, int targetWidth, int targetHeight, ulong[] accumulators) {
        var target = MemoryMarshal.Cast<byte, uint>(span: destination.AsSpan());
        if ((sourceWidth == targetWidth) && (sourceHeight == targetHeight)) {
            for (var y = 0; y < targetHeight; y++) {
                var sourceRow = source + ((long)y * sourceRowPitch);
                var targetRow = y * targetWidth;
                for (var x = 0; x < targetWidth; x++) {
                    target[targetRow + x] = Unsafe.ReadUnaligned<uint>(sourceRow + ((long)x * 4)) | 0xFF000000u;
                }
            }

            return;
        }

        if ((sourceWidth < targetWidth) || (sourceHeight < targetHeight)) {
            for (var y = 0; y < targetHeight; y++) {
                var sourceY = ((long)y * sourceHeight) / targetHeight;
                var sourceRow = source + (sourceY * sourceRowPitch);
                var targetRow = y * targetWidth;
                for (var x = 0; x < targetWidth; x++) {
                    var sourceX = ((long)x * sourceWidth) / targetWidth;
                    target[targetRow + x] = Unsafe.ReadUnaligned<uint>(sourceRow + (sourceX * 4)) | 0xFF000000u;
                }
            }

            return;
        }

        // Downscale: box-filter area average. Floor division partitions the source rows into target row bands and the
        // source columns into target column buckets, so a single row-major pass over the source accumulates each pixel
        // into exactly one target bucket. Every bucket receives at least one source pixel because neither dimension is
        // upscaled, so the divisor is always non-zero. Accumulators are 64-bit: one bucket can absorb the entire source
        // extent budget (MaximumSourcePixels * 255 overflows a 32-bit sum).
        var accumulatorSpan = accumulators.AsSpan();
        for (var y = 0; y < targetHeight; y++) {
            accumulatorSpan.Clear();
            var sourceY0 = ((long)y * sourceHeight) / targetHeight;
            var sourceY1 = ((long)(y + 1) * sourceHeight) / targetHeight;
            for (var sy = sourceY0; sy < sourceY1; sy++) {
                var sourceRow = source + (sy * sourceRowPitch);
                for (var sx = 0; sx < sourceWidth; sx++) {
                    var pixel = Unsafe.ReadUnaligned<uint>(sourceRow + ((long)sx * 4));
                    var bucket = ((sx * targetWidth) / sourceWidth) * 4;
                    accumulatorSpan[bucket] += pixel & 0xFFu;
                    accumulatorSpan[bucket + 1] += (pixel >> 8) & 0xFFu;
                    accumulatorSpan[bucket + 2] += (pixel >> 16) & 0xFFu;
                    accumulatorSpan[bucket + 3]++;
                }
            }

            var targetRow = y * targetWidth;
            for (var x = 0; x < targetWidth; x++) {
                var bucket = x * 4;
                var count = accumulatorSpan[bucket + 3];
                var b = (uint)(accumulatorSpan[bucket] / count);
                var g = (uint)(accumulatorSpan[bucket + 1] / count);
                var r = (uint)(accumulatorSpan[bucket + 2] / count);
                target[targetRow + x] = b | (g << 8) | (r << 16) | 0xFF000000u;
            }
        }
    }

    public void Dispose() {
        ReleaseReadbacks();
        if (m_runtimeDevice is not null) {
            try {
                m_runtimeDevice.Dispose();
            } finally {
                ((IWinRTObject)m_runtimeDevice).NativeObject.Dispose();
            }
            m_runtimeDevice = null;
        }

        if (m_gpuCopyQuery is not null) {
            _ = ((IUnknown*)m_gpuCopyQuery)->Release();
            m_gpuCopyQuery = null;
        }

        if (m_device1 is not null) {
            _ = ((IUnknown*)m_device1)->Release();
            m_device1 = null;
        }

        if (m_context is not null) {
            _ = ((IUnknown*)m_context)->Release();
            m_context = null;
        }

        if (m_device is not null) {
            _ = ((IUnknown*)m_device)->Release();
            m_device = null;
        }
    }

    private void ReleaseReadbacks() {
        foreach (var readback in m_readbacks) {
            if (readback.Texture is not null) {
                _ = ((IUnknown*)readback.Texture)->Release();
                readback.Texture = null;
            }

            readback.Pending = false;
            readback.Sequence = 0;
        }

        m_sourceHeight = 0;
        m_sourceWidth = 0;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    private sealed class ReadbackSlot {
        public bool Pending { get; set; }
        public long Sequence { get; set; }
        public ID3D11Texture2D* Texture { get; set; }
    }
}
