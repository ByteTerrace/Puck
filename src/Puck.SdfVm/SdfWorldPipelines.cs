using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// The compute pipelines an <see cref="SdfWorldEngine"/> records with, for one compiled kernel set on one device:
/// about fourteen of them, the two brick pipelines included. Creating a pipeline is where a driver translates the
/// kernel to native code, which can take seconds per pipeline with its cache cold, so <see cref="SdfWorldPipelineCache"/>
/// builds this set on the thread pool (<see cref="Puck.Hosting.BackgroundBuild{T}"/>) for every node and view that leases
/// it, and each constructs its engine only once the set is ready; the frame thread never waits on the driver's compiler.
/// <para>
/// Building and preparing a reload are safe on any thread: they only create objects on the device and count them into
/// the ledger the set was built with. Everything else runs on the thread that renders with the set. The set is disposed
/// after every engine built from it, and before the device goes away — so a build still in flight must be waited for
/// (<see cref="Puck.Hosting.BackgroundBuild{T}.CancelAndWait"/>) before a device loss or disposal releases the device.
/// </para>
/// </summary>
public sealed class SdfWorldPipelines : IDisposable {
    private readonly IGpuDeviceContext m_device;
    private readonly GpuDeviceServices m_gpu;
    private readonly Slot?[] m_slots;

    private bool m_disposed;
    private SdfWorldKernels m_kernels;

    private SdfWorldPipelines(GpuDeviceServices gpu, IGpuDeviceContext device, SdfWorldKernels kernels, bool includesBrickPipelines, Slot?[] slots) {
        m_device = device;
        m_gpu = gpu;
        m_kernels = kernels;
        m_slots = slots;
        IncludesBrickPipelines = includesBrickPipelines;
    }

    /// <summary>Gets whether the set was built for an engine with a brick pool. A kernel set with no brick kernels still
    /// builds no brick pipeline; the engine then bakes nothing.</summary>
    public bool IncludesBrickPipelines { get; }
    /// <summary>Gets whether the set has been disposed.</summary>
    public bool IsDisposed => m_disposed;
    /// <summary>Gets the kernel set the installed pipelines were created from; a committed reload replaces it.</summary>
    public SdfWorldKernels Kernels => m_kernels;

    private static PipelineVersion CreateVersion(GpuDeviceServices gpu, IGpuDeviceContext device, GpuComputePipelineDescription description, ReadOnlyMemory<byte> bytecode) {
        var shader = gpu.ShaderModuleFactory.Create(
            bytecode: bytecode,
            stage: GpuShaderStage.Compute
        );

        try {
            return new PipelineVersion(
                native: gpu.PipelineFactory.Create(
                    computeShaderModule: shader,
                    description: description
                ),
                shader: shader
            );
        } catch {
            shader.Dispose();
            throw;
        }
    }
    private static ReadOnlyMemory<byte> KernelBytes(in SdfWorldKernels kernels, string name) => name switch {
        "sdf-beam" => kernels.Beam,
        "sdf-instance-cull" => kernels.InstanceCull,
        "sdf-cull-args" => kernels.CullArgs,
        "sdf-world-primary" => kernels.Primary,
        "sdf-world-surface" => kernels.Surface,
        "sdf-world-ambient" => kernels.Ambient,
        "sdf-world-views" => kernels.Views,
        "sdf-world-views-core" => kernels.ViewsCore,
        "sdf-world-views-folds" => kernels.ViewsFolds,
        "sdf-sky" => kernels.Sky,
        "sdf-world-composite" => kernels.Composite,
        "sdf-brick-bake" => kernels.BrickBake,
        "sdf-brick-upload" => kernels.BrickUpload,
        "sdf-frame-upload" => kernels.FrameUpload,
        _ => throw new ArgumentException(
            message: $"Unknown SDF kernel '{name}'.",
            paramName: nameof(name)
        ),
    };

    /// <summary>Creates every engine pipeline for a kernel set. Safe on any thread; the token is checked before each
    /// pipeline, so a canceled build stops within one pipeline creation and releases what it had created.</summary>
    /// <param name="device">The device the pipelines are created on; the set creates them through its services, counted
    /// through its own wrapper over <paramref name="ledger"/>.</param>
    /// <param name="kernels">The compiled kernel set for the device's backend.</param>
    /// <param name="includeBrickPipelines">Whether to build the brick bake and upload pipelines, for an engine with a
    /// brick pool.</param>
    /// <param name="ledger">The ledger that counts the shader modules and pipelines created: the pipeline cache's, or a
    /// harness's own.</param>
    /// <param name="cancellationToken">Stops the build between pipelines.</param>
    /// <returns>The built set, owned by the caller.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="ledger"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The build was canceled.</exception>
    public static SdfWorldPipelines Build(IGpuDeviceContext device, SdfWorldKernels kernels, bool includeBrickPipelines, GpuWorkLedger ledger, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(ledger);

        var counted = GpuWorkCounting.Wrap(
            ledger: ledger,
            services: device.Services
        );
        var specs = SdfWorldEngine.PipelineLayouts.Specs;
        var slots = new Slot?[specs.Length];

        try {
            for (var index = 0; (index < specs.Length); index++) {
                cancellationToken.ThrowIfCancellationRequested();

                var spec = specs[index];
                var bytecode = KernelBytes(
                    kernels: kernels,
                    name: spec.Description.Name
                );

                if (spec.Brick && (!includeBrickPipelines || bytecode.IsEmpty)) {
                    continue;
                }

                slots[index] = new Slot(
                    current: CreateVersion(
                        bytecode: bytecode,
                        description: spec.Description,
                        device: device,
                        gpu: counted
                    ),
                    description: spec.Description
                );
            }
        } catch {
            foreach (var slot in slots) {
                slot?.Dispose();
            }

            throw;
        }

        // The driver's cache now holds whatever this build compiled; write it out from this build thread, not the frame's.
        (device as IGpuPipelineCache)?.Persist();

        return new SdfWorldPipelines(
            device: device,
            gpu: counted,
            includesBrickPipelines: includeBrickPipelines,
            kernels: kernels,
            slots: slots
        );
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var slot in m_slots) {
            slot?.Dispose();
        }
    }
    /// <summary>Creates replacements for the pipelines whose bytecode differs between the installed kernels and
    /// <paramref name="kernels"/>. Unchanged bytecode creates nothing. Safe on any thread while no other reload is being
    /// installed; <see cref="SdfWorldEngine.InstallReload"/> puts the result into service on the render thread.</summary>
    /// <param name="kernels">A complete compiled set for the same backend and unchanged host binding ABI.</param>
    /// <param name="cancellationToken">Stops the preparation between pipelines.</param>
    /// <returns>The prepared reload, owned by the caller until it is installed or disposed.</returns>
    /// <exception cref="ObjectDisposedException">The set has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The preparation was canceled.</exception>
    /// <exception cref="ArgumentException">Candidate bytecode is malformed or unsupported by the backend.</exception>
    public SdfWorldPipelineReload PrepareReload(SdfWorldKernels kernels, CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var baseline = m_kernels;
        var replacements = new List<(int Index, PipelineVersion Version)>();

        try {
            for (var index = 0; (index < m_slots.Length); index++) {
                if (m_slots[index] is not { } slot) {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var bytecode = KernelBytes(
                    kernels: kernels,
                    name: slot.Description.Name
                );

                if (bytecode.Span.SequenceEqual(other: KernelBytes(
                    kernels: baseline,
                    name: slot.Description.Name
                ).Span)) {
                    continue;
                }

                replacements.Add(item: (index, CreateVersion(
                    bytecode: bytecode,
                    description: slot.Description,
                    device: m_device,
                    gpu: m_gpu
                )));
            }
        } catch {
            foreach (var (_, version) in replacements) {
                version.Dispose();
            }

            throw;
        }

        (m_device as IGpuPipelineCache)?.Persist();

        return new SdfWorldPipelineReload(
            baseline: baseline,
            kernels: kernels,
            replacements: [.. replacements],
            target: this
        );
    }

    internal void Commit(SdfWorldPipelineReload reload) {
        m_kernels = reload.Kernels;
        reload.Commit();
    }
    internal void Exchange(SdfWorldPipelineReload reload) =>
        reload.Exchange(slots: m_slots);
    internal IGpuComputePipeline? OptionalPipeline(int index) =>
        m_slots[index];
    internal IGpuComputePipeline Pipeline(int index) =>
        (m_slots[index] ?? throw new InvalidOperationException(message: $"The pipeline set has no '{SdfWorldEngine.PipelineLayouts.Specs[index].Description.Name}' pipeline."));
    internal void Rollback(SdfWorldPipelineReload reload) =>
        reload.Rollback(slots: m_slots);
    internal void ThrowIfNotCurrent(SdfWorldPipelineReload reload) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (!ReferenceEquals(
            objA: reload.Target,
            objB: this
        )) {
            throw new InvalidOperationException(message: "The reload was prepared for a different pipeline set.");
        }

        if (!reload.Baseline.Equals(other: m_kernels)) {
            throw new InvalidOperationException(message: "The reload was prepared against kernels that are no longer installed.");
        }
    }

    // One native pipeline and the shader module it was created from; both released together.
    internal sealed class PipelineVersion(IGpuComputePipeline native, IGpuShaderModule shader) : IDisposable {
        public IGpuComputePipeline Native { get; } = native;

        public void Dispose() {
            Native.Dispose();
            shader.Dispose();
        }
    }
    // The engine's record paths and descriptor sets retain these stable slots. A reload changes the native objects
    // behind them between frames, with identically defined layouts, so both backends reuse the allocated sets.
    internal sealed class Slot(GpuComputePipelineDescription description, PipelineVersion current) : IGpuComputePipeline {
        private PipelineVersion m_current = current;

        public GpuComputePipelineDescription Description { get; } = description;

        public nint DescriptorSetLayoutHandle => m_current.Native.DescriptorSetLayoutHandle;
        public nint Handle => m_current.Native.Handle;
        public nint LayoutHandle => m_current.Native.LayoutHandle;

        public void Dispose() =>
            m_current.Dispose();
        public PipelineVersion Exchange(PipelineVersion replacement) {
            var previous = m_current;

            m_current = replacement;

            return previous;
        }
    }
}
/// <summary>
/// Replacement pipelines for the kernels that changed, prepared off the render thread by
/// <see cref="SdfWorldPipelines.PrepareReload"/> and put into service by <see cref="SdfWorldEngine.InstallReload"/>.
/// Disposing a reload that was never installed releases its pipelines.
/// </summary>
public sealed class SdfWorldPipelineReload : IDisposable {
    private readonly (int Index, SdfWorldPipelines.PipelineVersion Version)[] m_replacements;
    private readonly SdfWorldPipelines.PipelineVersion?[] m_retired;

    private State m_state;

    internal SdfWorldPipelineReload(SdfWorldPipelines target, SdfWorldKernels baseline, SdfWorldKernels kernels, (int Index, SdfWorldPipelines.PipelineVersion Version)[] replacements) {
        Baseline = baseline;
        Kernels = kernels;
        Target = target;
        m_replacements = replacements;
        m_retired = new SdfWorldPipelines.PipelineVersion?[replacements.Length];
    }

    /// <summary>Gets the number of pipelines whose bytecode changed and that the reload replaces.</summary>
    public int ChangedPipelines => m_replacements.Length;
    /// <summary>Gets the kernel set the reload installs.</summary>
    public SdfWorldKernels Kernels { get; }

    internal SdfWorldKernels Baseline { get; }
    internal SdfWorldPipelines Target { get; }

    internal void Commit() {
        if (m_state == State.Exchanged) {
            foreach (var retired in m_retired) {
                retired?.Dispose();
            }
        }

        m_state = State.Committed;
    }
    internal void Exchange(SdfWorldPipelines.Slot?[] slots) {
        for (var index = 0; (index < m_replacements.Length); index++) {
            var (slot, version) = m_replacements[index];

            m_retired[index] = slots[slot]!.Exchange(replacement: version);
        }

        m_state = State.Exchanged;
    }
    internal void Rollback(SdfWorldPipelines.Slot?[] slots) {
        for (var index = 0; (index < m_replacements.Length); index++) {
            _ = slots[m_replacements[index].Index]!.Exchange(replacement: m_retired[index]!);
            m_retired[index] = null;
        }

        m_state = State.Prepared;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_state == State.Prepared) {
            foreach (var (_, version) in m_replacements) {
                version.Dispose();
            }
        }

        m_state = State.Disposed;
    }

    private enum State : byte {
        Prepared = 0,
        Exchanged = 1,
        Committed = 2,
        Disposed = 3,
    }
}
