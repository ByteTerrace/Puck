using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// The compute pipelines an <see cref="SdfWorldEngine"/> records with, for one compiled kernel set on one device:
/// about thirteen of them, the two brick pipelines included. Creating a pipeline is where a driver translates the
/// kernel to native code, which can take seconds per pipeline with its cache cold, so <see cref="SdfWorldPipelineCache"/>
/// builds this set on the thread pool (<see cref="Puck.Hosting.BackgroundBuild{T}"/>) for every node and view that leases
/// it, and each constructs its engine only once the set is ready; the frame thread never waits on the driver's compiler.
/// A build creates up to <see cref="BuildConcurrency"/> pipelines at once and checks its cancel between pipelines.
/// <para>
/// Building and preparing a reload are safe on any thread: they only create objects on the device and count them into
/// the ledger the set was built with. Everything else runs on the thread that renders with the set. The set is disposed
/// after every engine built from it, and before the device goes away — so a build still in flight must be canceled and
/// waited for (<see cref="Puck.Hosting.BackgroundBuild{T}.CancelAndWait"/>) before a device loss or disposal releases the
/// device; the wait covers only the pipelines already in the driver.
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

    private static PipelineVersion CreateVersion(GpuDeviceServices gpu, GpuComputePipelineDescription description, ReadOnlyMemory<byte> bytecode) {
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
    // Creates every item's pipeline, at most BuildConcurrency at once, starting them in list order: this thread is one
    // creator and pool tasks are the others. Each creator claims the next item only after checking the token and the
    // failure flag, so a cancel or a failure waits only for the creations already in the driver. Anything created is
    // released before the cancel or the failures are thrown; a failure among the creations in the driver is never
    // dropped (CreationRun.ThrowIfFailed names every one).
    private static PipelineVersion[] CreateAll(GpuDeviceServices gpu, List<Creation> work, SdfWorldPipelineBuildProgress? progress, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        var created = new PipelineVersion?[work.Count];
        var creators = new CreationRun(
            cancellationToken: cancellationToken,
            created: created,
            gpu: gpu,
            progress: progress,
            work: work
        );
        var helpers = new Task[Math.Max(
            val1: 0,
            val2: (Math.Min(
                val1: BuildConcurrency,
                val2: work.Count
            ) - 1)
        )];

        for (var helper = 0; (helper < helpers.Length); helper++) {
            helpers[helper] = Task.Run(
                action: creators.Run,
                cancellationToken: CancellationToken.None
            );
        }

        creators.Run();

        // A creator catches everything it throws, so the wait never faults.
        Task.WaitAll(tasks: helpers);

        if (!creators.Failed && (Array.IndexOf(
            array: created,
            value: null
        ) < 0)) {
            return created!;
        }

        foreach (var version in created) {
            version?.Dispose();
        }

        creators.ThrowIfFailed();
        cancellationToken.ThrowIfCancellationRequested();

        throw new InvalidOperationException(message: "The pipeline build stopped without a cancel or a failure.");
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
        _ => throw new ArgumentException(
            message: $"Unknown SDF kernel '{name}'.",
            paramName: nameof(name)
        ),
    };

    /// <summary>Gets the most pipelines one build or reload creates at once: one fewer than the machine's processors,
    /// from one to four, so the thread that pumps frames keeps a processor while the driver translates kernels. A cancel
    /// waits for at most this many creations.</summary>
    public static int BuildConcurrency { get; } = Math.Clamp(
        max: 4,
        min: 1,
        value: (Environment.ProcessorCount - 1)
    );

    /// <summary>Creates every engine pipeline for a kernel set, up to <see cref="BuildConcurrency"/> at once: the
    /// calling thread creates one at a time and the thread pool runs the rest beside it. Pipelines start in
    /// <c>SdfWorldEngine.PipelineLayouts.BuildOrder</c>, the three views variants last. Safe on any thread. The token is
    /// checked before each pipeline and never during one, so a canceled build stops once the creations already in the
    /// driver return and releases everything it created; a failed creation stops the rest the same way.</summary>
    /// <param name="device">The device the pipelines are created on; the set creates them through its services, counted
    /// through its own wrapper over <paramref name="ledger"/>.</param>
    /// <param name="kernels">The compiled kernel set for the device's backend.</param>
    /// <param name="includeBrickPipelines">Whether to build the brick bake and upload pipelines, for an engine with a
    /// brick pool.</param>
    /// <param name="ledger">The ledger that counts the shader modules and pipelines created: the pipeline cache's, or a
    /// harness's own. The counts do not depend on the order the pipelines are created in.</param>
    /// <param name="cancellationToken">The token that stops the build between pipelines.</param>
    /// <param name="progress">The progress the build reports each created pipeline to, or <see langword="null"/>.</param>
    /// <returns>The built set, owned by the caller.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="ledger"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The build was canceled.</exception>
    /// <exception cref="AggregateException">A pipeline creation failed. The message names every pipeline whose creation
    /// failed, in build order, and the inner exceptions are those failures in the same order.</exception>
    /// <exception cref="DeviceLostException">The device was lost during a pipeline creation; thrown alone.</exception>
    public static SdfWorldPipelines Build(IGpuDeviceContext device, SdfWorldKernels kernels, bool includeBrickPipelines, GpuWorkLedger ledger, CancellationToken cancellationToken, SdfWorldPipelineBuildProgress? progress = null) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(ledger);

        var counted = GpuWorkCounting.Wrap(
            ledger: ledger,
            services: device.Services
        );
        var specs = SdfWorldEngine.PipelineLayouts.Specs;
        var work = new List<Creation>(capacity: specs.Length);

        foreach (var index in SdfWorldEngine.PipelineLayouts.BuildOrder) {
            var spec = specs[index];
            var bytecode = KernelBytes(
                kernels: kernels,
                name: spec.Description.Name
            );

            if (spec.Brick && (!includeBrickPipelines || bytecode.IsEmpty)) {
                continue;
            }

            work.Add(item: new Creation(
                Bytecode: bytecode,
                Description: spec.Description,
                Index: index
            ));
        }

        progress?.Begin(total: work.Count);

        var created = CreateAll(
            cancellationToken: cancellationToken,
            gpu: counted,
            progress: progress,
            work: work
        );
        var slots = new Slot?[specs.Length];

        for (var item = 0; (item < work.Count); item++) {
            slots[work[item].Index] = new Slot(
                current: created[item],
                description: work[item].Description
            );
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
    /// <exception cref="AggregateException">A replacement's creation failed, for malformed or unsupported bytecode among
    /// other causes. The message names every pipeline whose creation failed, in the set's order, and the inner exceptions
    /// are those failures in the same order.</exception>
    /// <exception cref="DeviceLostException">The device was lost during a creation; thrown alone.</exception>
    public SdfWorldPipelineReload PrepareReload(SdfWorldKernels kernels, CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var baseline = m_kernels;
        var work = new List<Creation>();

        for (var index = 0; (index < m_slots.Length); index++) {
            if (m_slots[index] is not { } slot) {
                continue;
            }

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

            work.Add(item: new Creation(
                Bytecode: bytecode,
                Description: slot.Description,
                Index: index
            ));
        }

        var created = CreateAll(
            cancellationToken: cancellationToken,
            gpu: m_gpu,
            progress: null,
            work: work
        );
        var replacements = new (int Index, PipelineVersion Version)[work.Count];

        for (var item = 0; (item < work.Count); item++) {
            replacements[item] = (work[item].Index, created[item]);
        }

        (m_device as IGpuPipelineCache)?.Persist();

        return new SdfWorldPipelineReload(
            baseline: baseline,
            kernels: kernels,
            replacements: replacements,
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

    // One pipeline a build or reload creates: the slot it fills, its description, and its kernel.
    private readonly record struct Creation(int Index, GpuComputePipelineDescription Description, ReadOnlyMemory<byte> Bytecode);
    // The creators of one CreateAll call, sharing its next unclaimed item and every item's failure. Each item's failure
    // is written only by the creator that claimed it, and read once every creator has returned.
    private sealed class CreationRun(GpuDeviceServices gpu, List<Creation> work, PipelineVersion?[] created, SdfWorldPipelineBuildProgress? progress, CancellationToken cancellationToken) {
        private readonly Exception?[] m_failures = new Exception?[work.Count];

        private int m_failed;

        private int m_next = -1;

        public bool Failed => (Volatile.Read(location: ref m_failed) != 0);

        public void Run() {
            while (!cancellationToken.IsCancellationRequested && !Failed) {
                var item = Interlocked.Increment(location: ref m_next);

                if (item >= work.Count) {
                    return;
                }

                try {
                    created[item] = CreateVersion(
                        bytecode: work[item].Bytecode,
                        description: work[item].Description,
                        gpu: gpu
                    );
                    progress?.Advance();
                } catch (Exception exception) {
                    m_failures[item] = exception;
                    Volatile.Write(
                        location: ref m_failed,
                        value: 1
                    );
                }
            }
        }
        // Throws the run's failures once every creator has returned. A device loss among them is thrown alone, so it
        // reaches the host's recovery; otherwise one exception names every pipeline that failed, in the run's order, with
        // each failure as an inner exception in the same order, whichever creator finished first.
        public void ThrowIfFailed() {
            if (!Failed) {
                return;
            }

            var names = new List<string>();
            var failures = new List<Exception>();

            for (var item = 0; (item < m_failures.Length); item++) {
                if (m_failures[item] is not { } failure) {
                    continue;
                }

                if (failure is DeviceLostException) {
                    ExceptionDispatchInfo.Throw(source: failure);
                }

                names.Add(item: work[item].Description.Name);
                failures.Add(item: failure);
            }

            throw new AggregateException(
                innerExceptions: failures,
                message: $"The SDF pipeline set's build failed creating {string.Join(separator: ", ", values: names)}."
            );
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
        public IReadOnlyList<nint> GroupLayoutHandles => m_current.Native.GroupLayoutHandles;
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
