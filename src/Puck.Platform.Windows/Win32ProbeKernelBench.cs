using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Puck.Platform.Probes;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.Platform.Windows;

/// <summary>The Direct3D 11 device a camera graph runs its kernels on, with the critical section a multi-call
/// sequence on its immediate context must hold.</summary>
internal unsafe interface IProbeKernelDevice {
    ID3D11DeviceContext* Context { get; }
    ID3D11Device* Device { get; }
    ID3D11Device1* Device1 { get; }

    void Enter();
    void Leave();
}
/// <summary>Resolves a sensor's converted frame to the shader-resource view a kernel binds, or zero while that
/// frame has not been produced yet.</summary>
internal interface IProbeInputResolver {
    /// <summary>Gets the shader-resource view over a sensor's current (or, strobing, previous/unlit) converted
    /// frame, or zero while unresolved.</summary>
    nint Resolve(CameraSensor sensor, bool previous);
    /// <summary>Gets the converted extent of a sensor's stream.</summary>
    (int Width, int Height) Extent(CameraSensor sensor);
}
/// <summary>The kernels attached to one camera graph. Attachment is thread-safe; everything else runs on the graph's
/// worker after a frame converts: a pending kernel opens its <see cref="ProbeKernelInput.Ring"/> sockets and compiles
/// once every <see cref="ProbeKernelInput.Sensor"/>/<see cref="ProbeKernelInput.StrobePair"/> socket resolves,
/// kernels triggered by that frame's sensor run — a ring socket with no published slot yet runs unbound that cycle —
/// and detached kernels release their native objects on this thread.</summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed unsafe class Win32ProbeKernelBench {
    private readonly List<Attachment> m_attached = [];
    private readonly ConcurrentQueue<Attachment> m_pending = new();

    private bool m_closed;

    public IProbeKernelRun Attach(in ProbeKernelRequest request, ProbeReadingRing ring) {
        var attachment = new Attachment(request: request, ring: ring);

        if (Volatile.Read(location: ref m_closed)) {
            attachment.End(fault: "the camera graph has ended");
        } else {
            m_pending.Enqueue(item: attachment);
        }

        return attachment;
    }
    public void OnFrame(CameraSensor sensor, long captureTimestamp, IProbeKernelDevice device, IProbeInputResolver resolver) {
        while (m_pending.TryDequeue(result: out var pending)) {
            m_attached.Add(item: pending);
        }

        var views = stackalloc nint[ProbeKernelInputLimits.MaxRegisters];
        var ringSlots = stackalloc int[ProbeKernelInputLimits.MaxInputs];

        for (var index = m_attached.Count - 1; (index >= 0); index--) {
            var attachment = m_attached[index];

            if (attachment.Detached) {
                attachment.Kernel?.Dispose();
                attachment.Kernel = null;
                attachment.CloseRingResources();
                m_attached.RemoveAt(index: index);

                continue;
            }
            if (attachment.Ended || (attachment.Request.Trigger != sensor)) {
                continue;
            }

            var inputs = attachment.Request.Inputs;

            if (inputs.Count > ProbeKernelInputLimits.MaxInputs) {
                attachment.End(fault: $"a probe kernel binds at most {ProbeKernelInputLimits.MaxInputs} sockets");

                continue;
            }

            if (!attachment.RingResourcesOpened) {
                try {
                    attachment.OpenRingResources(device: device);
                } catch (Exception exception) {
                    attachment.End(fault: DescribeFault(exception: exception));

                    continue;
                }
            }

            for (var input = 0; (input < inputs.Count); input++) {
                ringSlots[input] = -1;
            }

            try {
                var ready = true;
                var boundMask = 0u;
                var cursor = 0;

                for (var input = 0; (input < inputs.Count); input++) {
                    switch (inputs[input]) {
                        case ProbeKernelInput.Sensor sensorInput: {
                            var view = resolver.Resolve(sensor: sensorInput.Kind, previous: false);

                            views[cursor] = view;
                            cursor += 1;

                            if (view != 0) {
                                boundMask |= (1u << input);
                            } else {
                                ready = false;
                            }

                            break;
                        }
                        case ProbeKernelInput.StrobePair strobeInput: {
                            var lit = resolver.Resolve(sensor: strobeInput.Kind, previous: false);
                            var unlit = resolver.Resolve(sensor: strobeInput.Kind, previous: true);

                            views[cursor] = lit;
                            views[cursor + 1] = unlit;
                            cursor += 2;

                            if ((lit != 0) && (unlit != 0)) {
                                boundMask |= (1u << input);
                            } else {
                                ready = false;
                            }

                            break;
                        }
                        case ProbeKernelInput.Ring ringInput: {
                            var ringViews = attachment.RingViews(index: input);

                            if ((ringViews is not null) && ringInput.Slots.TryAcquireLatest(out var slot)) {
                                ringSlots[input] = slot;
                                views[cursor] = ringViews[slot];
                                boundMask |= (1u << input);
                            } else {
                                views[cursor] = 0;
                            }

                            cursor += 1;

                            break;
                        }
                        case ProbeKernelInput.Unbound: {
                            views[cursor] = 0;
                            cursor += 1;

                            break;
                        }
                    }
                }

                if (!ready) {
                    continue;
                }

                try {
                    if (attachment.Kernel is null) {
                        var (width, height) = resolver.Extent(sensor: attachment.Request.Trigger);

                        attachment.Kernel = new Win32D3D11ProbeKernel(
                            context: ((nint)device.Context),
                            device: ((nint)device.Device),
                            device1: ((nint)device.Device1),
                            request: in attachment.Request,
                            ring: attachment.Ring,
                            triggerHeight: height,
                            triggerWidth: width
                        );
                        attachment.ApplyPendingConstants();
                    }

                    device.Enter();

                    try {
                        _ = attachment.Kernel.TryRun(views: new ReadOnlySpan<nint>(views, cursor), boundMask: boundMask, captureTimestamp: captureTimestamp);
                    } finally {
                        device.Leave();
                    }
                } catch (Exception exception) {
                    attachment.Kernel?.Dispose();
                    attachment.Kernel = null;
                    attachment.End(fault: DescribeFault(exception: exception));
                }
            } finally {
                for (var input = 0; (input < inputs.Count); input++) {
                    if ((ringSlots[input] >= 0) && (inputs[input] is ProbeKernelInput.Ring ringInput)) {
                        ringInput.Slots.Release(slot: ringSlots[input]);
                    }
                }
            }
        }
    }
    // The fault a probe reports: the message plus the first frame inside this assembly, so a refused native call names
    // the kernel step it came from rather than only the HRESULT text.
    private static string DescribeFault(Exception exception) {
        foreach (var frame in new System.Diagnostics.StackTrace(e: exception).GetFrames()) {
            if ((frame.GetMethod() is { DeclaringType: { } type } method) && (type.Assembly == typeof(Win32ProbeKernelBench).Assembly) && (type.Namespace?.StartsWith(value: "Windows.Win32", comparisonType: StringComparison.Ordinal) != true)) {
                return $"{exception.Message} ({type.Name}.{method.Name})";
            }
        }

        return exception.Message;
    }
    /// <summary>Ends every attachment and releases its kernel; called on the worker as the graph closes.</summary>
    public void Close() {
        Volatile.Write(location: ref m_closed, value: true);

        while (m_pending.TryDequeue(result: out var pending)) {
            m_attached.Add(item: pending);
        }

        foreach (var attachment in m_attached) {
            attachment.Kernel?.Dispose();
            attachment.Kernel = null;
            attachment.CloseRingResources();
            attachment.End(fault: "the camera graph ended");
        }

        m_attached.Clear();
    }

    private static void ReleaseRingResources(nint[]?[] textures, nint[]?[] views) => ReleaseRingResources(
        release: static resource => Win32D3D11VideoDevice.ReleaseTexture(texture: resource),
        textures: textures,
        views: views
    );

    // Release views first because each view retains its texture. Each released slot is zeroed in place so a caller
    // holding the same array (Attachment publishes textures/views before it knows whether the open will succeed) can
    // read the release back. The injected release action is the smallest pure seam that lets a law prove partially
    // populated acquisition arrays drain completely and in dependency order.
    private static void ReleaseRingResources(nint[]?[] textures, nint[]?[] views, Action<nint> release) {
        foreach (var slots in views) {
            if (slots is null) {
                continue;
            }

            for (var index = 0; (index < slots.Length); index++) {
                if (slots[index] != 0) {
                    release(obj: slots[index]);
                    slots[index] = 0;
                }
            }
        }

        foreach (var slots in textures) {
            if (slots is null) {
                continue;
            }

            for (var index = 0; (index < slots.Length); index++) {
                if (slots[index] != 0) {
                    release(obj: slots[index]);
                    slots[index] = 0;
                }
            }
        }
    }

    private sealed class Attachment(in ProbeKernelRequest request, ProbeReadingRing ring) : IProbeKernelRun {
        private volatile bool m_detached;
        private volatile bool m_ended;
        private string? m_fault;
        private byte[]? m_pendingConstants;
        private bool m_ringResourcesOpened;
        private nint[]?[] m_ringTextures = [];
        private nint[]?[] m_ringViews = [];

        public readonly ProbeKernelRequest Request = request;
        public readonly ProbeReadingRing Ring = ring;
        public Win32D3D11ProbeKernel? Kernel;

        public bool Detached => m_detached;
        public bool Ended => m_ended;
        public bool RingResourcesOpened => m_ringResourcesOpened;
        public long Cycles => (Kernel?.Cycles ?? 0L);
        public long Drops => (Kernel?.Drops ?? 0L);
        public string? Fault => Volatile.Read(location: ref m_fault);
        public bool IsEnded => m_ended;

        public void ApplyPendingConstants() {
            if ((Interlocked.Exchange(location1: ref m_pendingConstants, value: null) is { } pending) && (Kernel is { } kernel)) {
                kernel.SetConstants(constants: pending);
            }
        }
        /// <summary>Opens every declared <see cref="ProbeKernelInput.Ring"/> socket's shared targets and their
        /// shader-resource views on the graph's device — once per attachment, regardless of readiness.</summary>
        public void OpenRingResources(IProbeKernelDevice device) {
            var inputs = Request.Inputs;
            var textures = new nint[]?[inputs.Count];
            var views = new nint[]?[inputs.Count];

            // Published before the first native call (not only on success): RingViews/RingTextures then observe a
            // partial acquisition's arrays too, zeroed in place by ReleaseRingResources's catch below when the open
            // fails partway through.
            m_ringTextures = textures;
            m_ringViews = views;

            try {
                for (var index = 0; (index < inputs.Count); index++) {
                    if (inputs[index] is not ProbeKernelInput.Ring ringInput) {
                        continue;
                    }

                    var handles = ringInput.SharedTargetHandles;
                    var format = ToDxgiFormat(format: ringInput.Format);
                    var openedTextures = new nint[handles.Count];
                    var openedViews = new nint[handles.Count];

                    // Publish the partial-acquisition arrays before the first native call so the outer catch owns
                    // every COM pointer this ring opens, even when validation or view creation fails partway through.
                    textures[index] = openedTextures;
                    views[index] = openedViews;

                    for (var slot = 0; (slot < handles.Count); slot++) {
                        using var handle = new SafeFileHandle(ownsHandle: false, preexistingHandle: handles[slot]);

                        void* opened = null;

                        try {
                            device.Device1->OpenSharedResource1(hResource: handle, ppResource: out opened, returnedInterface: ID3D11Texture2D.IID_Guid);
                        } catch (Exception exception) {
                            throw new InvalidOperationException(message: $"opening ring socket {index} slot {slot} on the graph's device failed: {exception.Message}", innerException: exception);
                        } finally {
                            openedTextures[slot] = ((nint)opened);
                        }

                        var texture = ((ID3D11Texture2D*)opened);
                        var description = default(D3D11_TEXTURE2D_DESC);

                        texture->GetDesc(pDesc: &description);

                        if ((description.Width != ringInput.Width) || (description.Height != ringInput.Height) || (description.Format != format)) {
                            throw new NotSupportedException(message: $"a probe ring socket target is {description.Width}x{description.Height} {description.Format}; expected {ringInput.Width}x{ringInput.Height} {format}");
                        }

                        ID3D11ShaderResourceView* view = null;

                        try {
                            device.Device->CreateShaderResourceView(pResource: ((ID3D11Resource*)texture), pDesc: null, ppSRView: &view);
                        } catch (Exception exception) {
                            throw new InvalidOperationException(message: $"viewing ring socket {index} slot {slot} ({description.Format}, bind {description.BindFlags}, misc {description.MiscFlags}) failed: {exception.Message}", innerException: exception);
                        } finally {
                            openedViews[slot] = ((nint)view);
                        }
                    }
                }
            } catch {
                ReleaseRingResources(textures: textures, views: views);

                throw;
            }

            m_ringResourcesOpened = true;
        }
        /// <summary>Gets the opened shared-resource views for the ring socket at <paramref name="index"/> — zeroed
        /// entries once <see cref="RingResourcesOpened"/> reports <see langword="false"/> after a partial-open
        /// failure released them — or <see langword="null"/> when that socket is not a ring or nothing has opened
        /// yet.</summary>
        public nint[]? RingViews(int index) => m_ringViews[index];
        /// <summary>Gets the opened textures for the ring socket at <paramref name="index"/>, with the same
        /// zeroed-after-release contract as <see cref="RingViews"/>.</summary>
        public nint[]? RingTextures(int index) => m_ringTextures[index];
        /// <summary>Releases every opened ring socket's shared resources.</summary>
        public void CloseRingResources() {
            ReleaseRingResources(textures: m_ringTextures, views: m_ringViews);
            m_ringTextures = [];
            m_ringViews = [];
            m_ringResourcesOpened = false;
        }
        public void Dispose() => m_detached = true;
        public void End(string fault) {
            _ = Interlocked.CompareExchange(location1: ref m_fault, value: fault, comparand: null);
            m_ended = true;
        }
        public void SetConstants(ReadOnlyMemory<byte> constants) {
            if (Kernel is { } kernel) {
                kernel.SetConstants(constants: constants);
            } else {
                _ = Interlocked.Exchange(location1: ref m_pendingConstants, value: constants.ToArray());
            }
        }

        private static DXGI_FORMAT ToDxgiFormat(SurfaceFormat format) => (format switch {
            SurfaceFormat.R8G8B8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            SurfaceFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            _ => throw new NotSupportedException(message: $"probe ring format {format} is unsupported"),
        });
    }
}
