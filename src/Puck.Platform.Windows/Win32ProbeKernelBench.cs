using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Puck.Platform.Probes;
using Windows.Win32.Graphics.Direct3D11;

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
/// <summary>Resolves a kernel input to the shader-resource view over the graph's converted frame for it, or zero
/// while that frame has not been produced yet.</summary>
internal interface IProbeInputResolver {
    nint Resolve(ProbeKernelInput input);
    /// <summary>Gets the converted extent of a sensor's stream.</summary>
    (int Width, int Height) Extent(CameraSensor sensor);
}
/// <summary>The kernels attached to one camera graph. Attachment is thread-safe; everything else runs on the graph's
/// worker after a frame converts: pending kernels compile once every input they read exists, kernels triggered by
/// that frame's sensor run, and detached kernels release their native objects on this thread.</summary>
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

        var views = stackalloc nint[ProbeReadingLimits.MaxChannels];

        for (var index = m_attached.Count - 1; (index >= 0); index--) {
            var attachment = m_attached[index];

            if (attachment.Detached) {
                attachment.Kernel?.Dispose();
                attachment.Kernel = null;
                m_attached.RemoveAt(index: index);

                continue;
            }
            if (attachment.Ended || (attachment.Request.Trigger != sensor)) {
                continue;
            }

            var inputs = attachment.Request.Inputs;

            if (inputs.Count > ProbeReadingLimits.MaxChannels) {
                attachment.End(fault: $"a probe kernel binds at most {ProbeReadingLimits.MaxChannels} inputs");

                continue;
            }

            var ready = true;

            for (var input = 0; (input < inputs.Count); input++) {
                views[input] = resolver.Resolve(input: inputs[input]);
                ready &= (views[input] != 0);
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
                    _ = attachment.Kernel.TryRun(inputViews: new ReadOnlySpan<nint>(views, inputs.Count), captureTimestamp: captureTimestamp);
                } finally {
                    device.Leave();
                }
            } catch (Exception exception) {
                attachment.Kernel?.Dispose();
                attachment.Kernel = null;
                attachment.End(fault: exception.Message);
            }
        }
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
            attachment.End(fault: "the camera graph ended");
        }

        m_attached.Clear();
    }

    private sealed class Attachment(in ProbeKernelRequest request, ProbeReadingRing ring) : IProbeKernelRun {
        private volatile bool m_detached;
        private volatile bool m_ended;
        private string? m_fault;
        private byte[]? m_pendingConstants;

        public readonly ProbeKernelRequest Request = request;
        public readonly ProbeReadingRing Ring = ring;
        public Win32D3D11ProbeKernel? Kernel;

        public bool Detached => m_detached;
        public bool Ended => m_ended;
        public long Cycles => (Kernel?.Cycles ?? 0L);
        public long Drops => (Kernel?.Drops ?? 0L);
        public string? Fault => Volatile.Read(location: ref m_fault);
        public bool IsEnded => m_ended;

        public void ApplyPendingConstants() {
            if ((Interlocked.Exchange(location1: ref m_pendingConstants, value: null) is { } pending) && (Kernel is { } kernel)) {
                kernel.SetConstants(constants: pending);
            }
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
    }
}
