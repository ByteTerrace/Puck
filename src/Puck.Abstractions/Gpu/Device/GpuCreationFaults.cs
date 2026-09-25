using System.Globalization;
using System.Runtime.CompilerServices;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The operator's creation faults for the devices a host creates: which creation of each <see cref="GpuCreationKind"/>
/// fails, and how many of each kind have been made since the faults were last disarmed. A backend wraps the services it
/// creates with its context through <see cref="Wrap"/>, so every creation of a counted kind passes through here first;
/// an armed creation throws <see cref="GpuCreationFaultException"/> instead of reaching the device, and nothing is
/// created. It also holds one armed device loss: a GPU host counts each frame it produces here
/// (<see cref="ThrowIfLossDue"/>), and the armed frame throws <see cref="DeviceLostException"/> inside the frame body
/// its device-loss policy guards. Only the <c>gpu.faults</c> console verb, which answers the operator alone, arms it,
/// so a world document can never reach it.
/// <para>
/// Each kind holds at most one armed fault, counted from the moment it is armed, and a fault fires exactly once. The
/// counts are creation calls, the failed one included, in the order the calls arrive: deterministic by count alone,
/// with no randomness and no clock. One instance serves every device a host creates, and is safe to call from any
/// thread.
/// </para>
/// </summary>
public sealed class GpuCreationFaults {
    /// <summary>The refusal code every <see cref="GpuCreationFaultException"/> message starts with.</summary>
    public const string RefusalCode = "GPU_CREATION_FAULT";

    private const int KindCount = (((int)GpuCreationKind.BindingsPool) + 1);

    private static readonly string[] KindNames = ["pipeline", "buffer", "image", "render-pass", "framebuffer", "shader-module", "command-pool", "bindings-pool"];

    /// <summary>The refusal code every armed device loss's <see cref="DeviceLostException"/> message starts with.</summary>
    public const string LossRefusalCode = "GPU_DEVICE_LOSS_FAULT";

    // Per kind: the one-based creation number the armed fault fails, or zero when none is armed.
    private readonly long[] m_armed = new long[KindCount];
    private readonly Lock m_gate = new();
    private readonly long[] m_seen = new long[KindCount];

    // Frames counted since the last disarm, and the one-based frame the armed loss fires on, or zero.
    private long m_framesSeen;
    private long m_lossArmed;
    private long m_revision;

    /// <summary>Gets every kind, in declaration order.</summary>
    public static ReadOnlySpan<GpuCreationKind> Kinds => [
        GpuCreationKind.Pipeline,
        GpuCreationKind.Buffer,
        GpuCreationKind.Image,
        GpuCreationKind.RenderPass,
        GpuCreationKind.Framebuffer,
        GpuCreationKind.ShaderModule,
        GpuCreationKind.CommandPool,
        GpuCreationKind.BindingsPool,
    ];

    /// <summary>Returns a kind's one spelling: <c>pipeline</c>, <c>buffer</c>, <c>image</c>, <c>render-pass</c>,
    /// <c>framebuffer</c>, <c>shader-module</c>, <c>command-pool</c> or <c>bindings-pool</c>.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The kind's name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a declared kind.</exception>
    public static string NameOf(GpuCreationKind kind) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: KindCount,
            value: ((int)kind)
        );

        return KindNames[((int)kind)];
    }
    /// <summary>Reads a kind from its one spelling (<see cref="NameOf"/>), ordinally.</summary>
    /// <param name="name">The spelled kind.</param>
    /// <param name="kind">The kind named, or <see cref="GpuCreationKind.Pipeline"/> when none is.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a kind.</returns>
    public static bool TryParseKind(ReadOnlySpan<char> name, out GpuCreationKind kind) {
        for (var index = 0; (index < KindCount); index++) {
            if (name.SequenceEqual(other: KindNames[index])) {
                kind = ((GpuCreationKind)index);

                return true;
            }
        }

        kind = default;

        return false;
    }
    /// <summary>Returns a set whose creating members pass through <paramref name="faults"/> before they forward to
    /// <paramref name="services"/>: the pipeline, buffer, image, render-pass (render passes and framebuffers),
    /// shader-module and command-pool factories, and the bindings' descriptor pools. The recorder, the queue submitter,
    /// the surface-transfer factory and the rest of the bindings are passed through unwrapped. Objects created are the
    /// backend's own, unwrapped. A backend calls this once, where it creates its services.</summary>
    /// <param name="services">The services a backend creates with its device context.</param>
    /// <param name="faults">The host's faults, or <see langword="null"/> when the host arms none.</param>
    /// <returns><paramref name="services"/> itself when <paramref name="faults"/> is <see langword="null"/>; otherwise the
    /// wrapped set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A member of <paramref name="services"/> already passes through a set of
    /// faults.</exception>
    public static GpuDeviceServices Wrap(GpuDeviceServices services, GpuCreationFaults? faults) {
        ArgumentNullException.ThrowIfNull(services);

        if (faults is null) {
            return services;
        }

        return new GpuDeviceServices {
            Bindings = new FaultingBindings(
                faults: faults,
                inner: Guard(instance: services.Bindings)
            ),
            BufferFactory = new FaultingBufferFactory(
                faults: faults,
                inner: Guard(instance: services.BufferFactory)
            ),
            CommandPoolFactory = new FaultingCommandPoolFactory(
                faults: faults,
                inner: Guard(instance: services.CommandPoolFactory)
            ),
            ImageFactory = new FaultingImageFactory(
                faults: faults,
                inner: Guard(instance: services.ImageFactory)
            ),
            PipelineFactory = new FaultingPipelineFactory(
                faults: faults,
                inner: Guard(instance: services.PipelineFactory)
            ),
            QueueSubmitter = services.QueueSubmitter,
            Recorder = services.Recorder,
            RenderPassFactory = new FaultingRenderPassFactory(
                faults: faults,
                inner: Guard(instance: services.RenderPassFactory)
            ),
            ShaderModuleFactory = new FaultingShaderModuleFactory(
                faults: faults,
                inner: Guard(instance: services.ShaderModuleFactory)
            ),
            SurfaceTransferFactory = services.SurfaceTransferFactory,
        };
    }

    // Refuses a service that already passes through faults, whose creations would be counted twice.
    private static T Guard<T>(T instance, [CallerArgumentExpression(parameterName: nameof(instance))] string? paramName = null) where T : class {
        ArgumentNullException.ThrowIfNull(
            argument: instance,
            paramName: paramName
        );

        if (instance is FaultingWrapper) {
            throw new ArgumentException(
                message: "The service already passes through creation faults; wrapping it again would count every creation twice.",
                paramName: paramName
            );
        }

        return instance;
    }

    /// <summary>Arms the <paramref name="nth"/> creation of <paramref name="kind"/> from now to fail, replacing any
    /// fault already armed for that kind.</summary>
    /// <param name="kind">The kind whose creation fails.</param>
    /// <param name="nth">The one-based number, counted from now, of the creation that fails: 1 fails the next.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a declared kind, or
    /// <paramref name="nth"/> is less than 1.</exception>
    public void Arm(GpuCreationKind kind, int nth = 1) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: KindCount,
            value: ((int)kind)
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: nth
        );

        lock (m_gate) {
            m_armed[((int)kind)] = (m_seen[((int)kind)] + nth);
            m_revision++;
        }
    }
    /// <summary>Arms a device loss on the <paramref name="nth"/> frame a GPU host produces from now, replacing any loss
    /// already armed: the host's frame throws <see cref="DeviceLostException"/> (<see cref="ThrowIfLossDue"/>) and
    /// recovers through its device-loss policy exactly as from a real loss, on a device that is still healthy.</summary>
    /// <param name="nth">The one-based number, counted from now, of the frame that loses the device: 1 loses the
    /// next.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nth"/> is less than 1.</exception>
    public void ArmLoss(int nth = 1) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: nth
        );

        lock (m_gate) {
            m_lossArmed = (m_framesSeen + nth);
            m_revision++;
        }
    }
    /// <summary>Clears every armed fault, the armed loss, every kind's count of creations seen and the count of frames
    /// seen.</summary>
    public void Disarm() {
        lock (m_gate) {
            Array.Clear(array: m_armed);
            Array.Clear(array: m_seen);
            m_framesSeen = 0L;
            m_lossArmed = 0L;
            m_revision++;
        }
    }
    /// <summary>Counts one frame a GPU host is about to produce, and throws when it is the frame the armed loss fires
    /// on, clearing the loss as it fires. A host calls this once per frame, inside the frame body its device-loss
    /// policy guards.</summary>
    /// <exception cref="DeviceLostException">This is the frame the armed loss fires on.</exception>
    public void ThrowIfLossDue() {
        long fired;

        lock (m_gate) {
            var seen = ++m_framesSeen;

            if (m_lossArmed != seen) {
                return;
            }

            m_lossArmed = 0L;
            m_revision++;
            fired = seen;
        }

        throw new DeviceLostException(message: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{LossRefusalCode}: gpu.faults lost the device on frame {fired}, counted since the faults were last disarmed."
        ));
    }
    /// <summary>Reads how many frames remain before the armed loss fires.</summary>
    /// <param name="remaining">The one-based number, counted from now, of the frame that loses the device: 1 is the
    /// next; zero when none is armed.</param>
    /// <returns><see langword="true"/> when a loss is armed.</returns>
    public bool TryGetArmedLoss(out long remaining) {
        lock (m_gate) {
            remaining = ((m_lossArmed == 0L)
                ? 0L
                : (m_lossArmed - m_framesSeen)
            );

            return (m_lossArmed != 0L);
        }
    }
    /// <summary>Gets how many frames GPU hosts have counted since the faults were last disarmed.</summary>
    public long FramesSeen {
        get {
            lock (m_gate) {
                return m_framesSeen;
            }
        }
    }
    /// <summary>Gets a number that changes whenever the faults do: an arm, a disarm, or a fault or loss that fires and
    /// clears. A consumer that stays refused until one of its recorded inputs changes records this as one of them, so
    /// the operator's arming or clearing of a fault is a change it retries on.</summary>
    public long Revision {
        get {
            lock (m_gate) {
                return m_revision;
            }
        }
    }

    // Counts one creation of the kind and throws when it is the armed one, clearing the fault as it fires. A decorator
    // calls this before it forwards the creation.
    internal void Enter(GpuCreationKind kind) {
        long fired;

        lock (m_gate) {
            var seen = ++m_seen[((int)kind)];

            if (m_armed[((int)kind)] != seen) {
                return;
            }

            m_armed[((int)kind)] = 0L;
            m_revision++;
            fired = seen;
        }

        throw new GpuCreationFaultException(
            creation: fired,
            kind: kind
        );
    }

    /// <summary>Reads how many creations of <paramref name="kind"/> remain before its armed fault fires.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="remaining">The one-based number, counted from now, of the creation that fails: 1 is the next;
    /// zero when none is armed.</param>
    /// <returns><see langword="true"/> when a fault is armed for <paramref name="kind"/>.</returns>
    public bool TryGetArmed(GpuCreationKind kind, out long remaining) {
        lock (m_gate) {
            var armed = m_armed[((int)kind)];

            remaining = ((armed == 0L)
                ? 0L
                : (armed - m_seen[((int)kind)])
            );

            return (armed != 0L);
        }
    }
    /// <summary>Gets how many creations of <paramref name="kind"/> have been attempted since the faults were last
    /// disarmed, failed ones included.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The count.</returns>
    public long SeenOf(GpuCreationKind kind) {
        lock (m_gate) {
            return m_seen[((int)kind)];
        }
    }
}

// The one shape every faulting decorator shares: it asks the faults before forwarding a creation, and a service of this
// type is refused as the inner of another decorator.
file abstract class FaultingWrapper(GpuCreationFaults faults) {
    protected void Enter(GpuCreationKind kind) =>
        faults.Enter(kind: kind);
}
file sealed class FaultingBindings(IGpuBindings inner, GpuCreationFaults faults) : FaultingWrapper(faults: faults), IGpuBindings {
    public nint AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) =>
        inner.AllocateSet(
            descriptorSetLayoutHandle: descriptorSetLayoutHandle,
            poolHandle: poolHandle
        );

    public long HeapReleaseRevision => inner.HeapReleaseRevision;

    public bool CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) =>
        inner.CanAdmit(
            owner: owner,
            pools: pools,
            refusal: out refusal
        );
    public nint CreatePool(in GpuDescriptorPoolSizes sizes) {
        Enter(kind: GpuCreationKind.BindingsPool);

        return inner.CreatePool(sizes: in sizes);
    }
    public nint CreateSampler(GpuSamplerFilter filter = GpuSamplerFilter.Linear) =>
        inner.CreateSampler(filter: filter);
    public void DestroyPool(nint poolHandle) =>
        inner.DestroyPool(poolHandle: poolHandle);
    public void DestroySampler(nint samplerHandle) =>
        inner.DestroySampler(samplerHandle: samplerHandle);
    public void WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) =>
        inner.WriteBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            elementStride: elementStride,
            kind: kind
        );
    public void WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) =>
        inner.WriteCombinedImageSampler(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            imageViewHandle: imageViewHandle,
            samplerHandle: samplerHandle
        );
    public void WriteConstantBuffer(nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize) =>
        inner.WriteConstantBuffer(
            arrayElement: arrayElement,
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle
        );
    public void WriteSampledImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) =>
        inner.WriteSampledImage(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            imageViewHandle: imageViewHandle
        );
    public void WriteSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle) =>
        inner.WriteSampler(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            samplerHandle: samplerHandle
        );
    public void WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) =>
        inner.WriteStorageImage(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            imageViewHandle: imageViewHandle
        );
}
file sealed class FaultingBufferFactory(IGpuBufferFactory inner, GpuCreationFaults faults) : FaultingWrapper(faults: faults), IGpuBufferFactory {
    public IGpuBuffer CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) {
        Enter(kind: GpuCreationKind.Buffer);

        return inner.CreateDeviceLocal(
            sizeBytes: sizeBytes,
            usage: usage
        );
    }
    public IGpuStorageBuffer CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) {
        Enter(kind: GpuCreationKind.Buffer);

        return inner.CreateHostVisible(
            sizeBytes: sizeBytes,
            usage: usage
        );
    }
    public IGpuStorageBuffer CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) {
        Enter(kind: GpuCreationKind.Buffer);

        return inner.CreateHostVisible(
            data: data,
            usage: usage
        );
    }
}
file sealed class FaultingCommandPoolFactory(IGpuCommandPoolFactory inner, GpuCreationFaults faults) : FaultingWrapper(faults: faults), IGpuCommandPoolFactory {
    public IGpuCommandPool Create() {
        Enter(kind: GpuCreationKind.CommandPool);

        return inner.Create();
    }
}
file sealed class FaultingImageFactory(IGpuImageFactory inner, GpuCreationFaults faults) : FaultingWrapper(faults: faults), IGpuImageFactory {
    public IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {
        Enter(kind: GpuCreationKind.Image);

        return inner.Create(
            format: format,
            height: height,
            usage: usage,
            width: width
        );
    }
}
file sealed class FaultingPipelineFactory(IGpuPipelineFactory inner, GpuCreationFaults faults) : FaultingWrapper(faults: faults), IGpuPipelineFactory {
    public IGpuComputePipeline Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
        Enter(kind: GpuCreationKind.Pipeline);

        return inner.Create(
            computeShaderModule: computeShaderModule,
            description: description
        );
    }
    public IGpuPipeline Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) {
        Enter(kind: GpuCreationKind.Pipeline);

        return inner.Create(
            description: description,
            fragmentShaderModule: fragmentShaderModule,
            renderPass: renderPass,
            vertexShaderModule: vertexShaderModule
        );
    }
}
file sealed class FaultingRenderPassFactory(IGpuRenderPassFactory inner, GpuCreationFaults faults) : FaultingWrapper(faults: faults), IGpuRenderPassFactory {
    public IGpuRenderPass Create(GpuRenderPassDescription description) {
        Enter(kind: GpuCreationKind.RenderPass);

        return inner.Create(description: description);
    }
    public IGpuFramebuffer CreateFramebuffer(IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) {
        Enter(kind: GpuCreationKind.Framebuffer);

        return inner.CreateFramebuffer(
            colors: colors,
            depth: depth,
            renderPass: renderPass
        );
    }
}
file sealed class FaultingShaderModuleFactory(IGpuShaderModuleFactory inner, GpuCreationFaults faults) : FaultingWrapper(faults: faults), IGpuShaderModuleFactory {
    public IGpuShaderModule Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) {
        Enter(kind: GpuCreationKind.ShaderModule);

        return inner.Create(
            bytecode: bytecode,
            stage: stage
        );
    }
}
