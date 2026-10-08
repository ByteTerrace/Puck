using System.Globalization;
using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The compute pipelines an <see cref="SdfWorldTables"/> records with, for one compiled kernel set on one device: one
/// lease on the composition's pass-pipeline cache (<see cref="GpuPassPipelineCache"/>) a kernel variant, keyed like any
/// pass by its bytecode and description, the brick baker's included when asked for. Creating a pipeline is where a
/// driver translates the kernel to native code, which can take seconds per pipeline with its cache cold, so every entry
/// builds on the thread pool, shared with every other holder of the same kernel on the device, and a residency builds
/// its tables only once the set is ready (<see cref="Poll"/>); the frame thread never waits on the driver's compiler.
/// <para>
/// Acquiring hashes each kernel and starts or joins its background build without waiting for it. Everything else
/// runs on the thread that renders with the set. The set is disposed after every table built from it and before the
/// device goes away; disposal releases every lease, and the last lease on an entry waits only for the creation already
/// in the driver.
/// </para>
/// </summary>
public sealed partial class SdfWorldPipelines : IDisposable {
    // Optional pass builds join this table off-thread; reload preparation and publication share the same gate.
    private readonly Lock m_gate = new();
    private readonly Slot?[] m_slots;

    private bool m_disposed;
    private SdfKernelSet m_kernels;

    private SdfWorldPipelines(SdfKernelSet kernels, bool includesBrickPipelines, Slot?[] slots) {
        m_kernels = kernels;
        m_slots = slots;
        IncludesBrickPipelines = includesBrickPipelines;
    }

    /// <summary>Gets whether the set was acquired for an engine with a brick pool. A kernel set with no brick kernels
    /// still leases no brick pipeline; the engine then bakes nothing.</summary>
    public bool IncludesBrickPipelines { get; }
    /// <summary>Gets whether the set has been disposed.</summary>
    public bool IsDisposed => m_disposed;
    /// <summary>Gets the kernel set the installed pipelines were created from; a committed reload replaces it.</summary>
    public SdfKernelSet Kernels => m_kernels;

    /// <summary>Takes a lease on the up-front pipelines, each an entry of the pass-pipeline cache, joining the entries
    /// other holders lease and starting the rest building on the thread pool. One shadow kernel and the three views
    /// variants serve every shadow fade capacity, so no policy asks for another pipeline. Safe on any thread.</summary>
    /// <param name="cache">The composition's pass-pipeline cache.</param>
    /// <param name="device">The device the pipelines are created on.</param>
    /// <param name="kernels">The compiled kernel set for the device's backend.</param>
    /// <param name="includeBrickPipelines">Whether to lease the brick bake pipeline, for an engine with a brick
    /// pool.</param>
    /// <returns>The set, owned by the caller, which disposes it to release its leases.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/>, <paramref name="device"/> or
    /// <paramref name="kernels"/> is <see langword="null"/>.</exception>
    public static SdfWorldPipelines Acquire(GpuPassPipelineCache cache, IGpuDeviceContext device, SdfKernelSet kernels, bool includeBrickPipelines) {
        ArgumentNullException.ThrowIfNull(argument: cache);
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: kernels);

        var specs = SdfWorldTables.PipelineLayouts.Specs;
        var slots = new Slot?[specs.Length];

        try {
            foreach (var kernel in BuildOrder(kernels: kernels)) {
                var spec = specs[((int)kernel)];
                var bytecode = kernels[kernel];

                if (spec.Brick && (!includeBrickPipelines || bytecode.IsEmpty)) {
                    continue;
                }

                slots[((int)kernel)] = new Slot(
                    description: spec.Description,
                    lease: cache.Acquire(
                        device: device,
                        key: GpuPassPipelineKey.OfCompute(
                            bytecode: bytecode,
                            description: spec.Description
                        )
                    )
                );
            }
        } catch {
            GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>.Release(leases: [.. slots.Select(selector: static slot => slot?.Lease)]);

            throw;
        }

        return new SdfWorldPipelines(
            includesBrickPipelines: includeBrickPipelines,
            kernels: kernels,
            slots: slots
        );
    }
    /// <summary>Orders a kernel set's up-front pipelines by when a set starts building them: longest bytecode first, ties
    /// in <see cref="SdfKernel"/> order. A cold driver's translation grows with the kernel, and a set is ready only once its
    /// slowest pipeline is, so the longest starts while the device's threads are free rather than behind the rest.</summary>
    /// <param name="kernels">The compiled kernel set for a device's backend.</param>
    /// <returns>Every kernel a set leases up front (resolve builds on demand), in build order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kernels"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SdfKernel> BuildOrder(SdfKernelSet kernels) {
        ArgumentNullException.ThrowIfNull(argument: kernels);

        return [.. SdfWorldTables.PipelineLayouts.Leased.OrderByDescending(keySelector: kernel => kernels[kernel].Length)];
    }
    /// <summary>Describes how far the set's builds have come as a clause: <c>building (9 of 11 pipelines
    /// created; waiting on sdf-world-surface, sdf-world-views)</c>, naming the pipelines not yet built in
    /// <see cref="SdfKernel"/> order, and then any refused (<see cref="IsBuilt"/>): <c>refused sdf-world-views</c>.</summary>
    /// <returns>The clause.</returns>
    public string Describe() {
        var built = 0;
        var total = 0;
        var waiting = new List<string>();
        var refused = new List<string>();

        foreach (var slot in m_slots) {
            if (slot is null) {
                continue;
            }

            total++;

            if (slot.Lease.Current is not null) {
                built++;
            } else if (slot.Refusal is not null) {
                refused.Add(item: slot.Description.Name);
            } else {
                waiting.Add(item: slot.Description.Name);
            }
        }

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"building ({built} of {total} pipelines created{((waiting.Count == 0) ? string.Empty : $"; waiting on {string.Join(separator: ", ", values: waiting)}")}{((refused.Count == 0) ? string.Empty : $"; refused {string.Join(separator: ", ", values: refused)}")})"
        );
    }
    /// <inheritdoc/>
    /// <remarks>Releases every lease at once (<see cref="GpuBuildLease{TKey, T}.Release(IReadOnlyList{GpuBuildLease{TKey, T}?})"/>):
    /// every build no other holder leases is canceled before any is waited for, so disposal waits only for the creations
    /// already in the driver.</remarks>
    public void Dispose() {
        GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>?[] leases;

        lock (m_gate) {
            if (m_disposed) {
                return;
            }
            m_disposed = true;
            leases = [.. m_slots.Select(selector: static slot => slot?.Lease)];
        }
        GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>.Release(leases: leases);
    }
    /// <summary>Returns whether every pipeline of the set is ready, starting again any build that failed. Allocates
    /// nothing while a build runs or once every pipeline is ready.</summary>
    /// <returns><see langword="true"/> once every pipeline is ready.</returns>
    /// <exception cref="ObjectDisposedException">The set has been disposed.</exception>
    /// <exception cref="AggregateException">A pipeline's creation failed. The message names every pipeline whose
    /// creation failed, in the set's order, and the inner exceptions are those failures in the same order.</exception>
    /// <exception cref="DeviceLostException">The device was lost during a creation; thrown alone.</exception>
    public bool Poll() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        return PollAll(slots: m_slots);
    }
    /// <summary>Returns whether every pipeline the tables are built from is ready: all but the views variants, of which a
    /// residency's views need only the one its program selects or a fuller one (<see cref="IsBuilt"/>), so a program that
    /// selects a stripped variant never waits for the full ISA's translation. Polls every pipeline, the views variants
    /// included; a views variant whose creation failed is refused (<see cref="IsBuilt"/>) rather than thrown, since the
    /// tables are built without it.</summary>
    /// <returns><see langword="true"/> once every pipeline but the views variants is ready.</returns>
    /// <exception cref="ObjectDisposedException">The set has been disposed.</exception>
    /// <exception cref="AggregateException">A pipeline's creation failed, other than a views variant's, named as
    /// <see cref="Poll"/> names it.</exception>
    /// <exception cref="DeviceLostException">The device was lost during a creation; thrown alone.</exception>
    public bool PollRequired() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        return PollAll(
            slots: m_slots,
            viewsRequired: false
        );
    }
    /// <summary>Returns whether one of the set's pipelines is built, taking a build that has just finished. A creation that
    /// failed, other than by a device loss, is refused: it is kept (<see cref="RefusalOf"/>), never thrown, and the
    /// pipeline is not polled again, so its build is not restarted, until a reload replaces it
    /// (<see cref="PrepareReload"/>) or the set is disposed on a device loss. Allocates nothing.</summary>
    /// <param name="kernel">The kernel.</param>
    /// <returns><see langword="true"/> when the set leases the kernel and its pipeline is built.</returns>
    /// <exception cref="DeviceLostException">The device was lost during the creation.</exception>
    public bool IsBuilt(SdfKernel kernel) => ((m_slots[((int)kernel)] is { } slot) && slot.PollRefusing());
    /// <summary>Returns why one of the set's pipelines was refused (<see cref="IsBuilt"/>), or <see langword="null"/> when
    /// it was not.</summary>
    /// <param name="kernel">The kernel.</param>
    /// <returns>The creation's failure, or <see langword="null"/>.</returns>
    public Exception? RefusalOf(SdfKernel kernel) => m_slots[((int)kernel)]?.Refusal;
    /// <summary>Returns a task that completes once every pipeline of the set is ready, for a holder's own background
    /// build, which awaits it holding no thread, or a harness that drives tables directly.</summary>
    /// <param name="cancellationToken">The token that ends the wait; the entries keep building for their other
    /// holders.</param>
    /// <returns>The task, which faults as the exceptions below say.</returns>
    /// <exception cref="ObjectDisposedException">The set has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="AggregateException">A pipeline's creation failed, named as <see cref="Poll"/> names it.</exception>
    /// <exception cref="DeviceLostException">The device was lost during a creation; thrown alone.</exception>
    public Task WaitAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        return WaitAllAsync(
            cancellationToken: cancellationToken,
            slots: m_slots
        );
    }
    /// <summary>Blocks until every pending build of the set has finished, successfully or not, without taking any result,
    /// so the next <see cref="Poll"/> sees what it would had the holder kept polling
    /// (<see cref="GpuBuildLease{TKey, T}.WaitFinished"/>).</summary>
    /// <param name="cancellationToken">The token that ends the wait; the entries keep building.</param>
    /// <returns><see langword="true"/> when any build was pending; <see langword="false"/> when none was.</returns>
    /// <exception cref="ObjectDisposedException">The set has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public bool WaitFinished(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var waited = false;

        foreach (var slot in m_slots) {
            if (slot is not null) {
                waited |= slot.Lease.WaitFinished(cancellationToken: cancellationToken);
            }
        }

        return waited;
    }
    /// <summary>Leases replacements for the pipelines whose bytecode differs between the installed kernels and
    /// <paramref name="kernels"/>, and for each refused pipeline (<see cref="IsBuilt"/>), which the reload builds again;
    /// unchanged bytecode leases nothing otherwise. Before it leases anything it reflects each changed
    /// kernel through <paramref name="reflector"/> and holds it to this host's interface
    /// (<see cref="SdfKernelSet.InterfaceMismatch"/>), so a kernel compiled against another instruction set, or binding
    /// anything the host does not place where the host places it, refuses the whole reload and the set keeps its kernels.
    /// The replacements build on the thread pool like any entry; the reload is ready once
    /// <see cref="SdfWorldPipelineReload.WaitAsync"/> completes, and <see cref="SdfWorldTables.InstallReload"/> puts it into
    /// service on the render thread. Safe on any thread while no other reload is being installed.</summary>
    /// <param name="cache">The composition's pass-pipeline cache the set was acquired from.</param>
    /// <param name="device">The device the set's pipelines were created on.</param>
    /// <param name="kernels">A complete compiled set for the same backend.</param>
    /// <param name="reflector">The reflector that reads each changed kernel's bindings.</param>
    /// <returns>The prepared reload, owned by the caller until it is installed or disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/>, <paramref name="device"/>,
    /// <paramref name="kernels"/> or <paramref name="reflector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The set has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A changed kernel does not read this host's interface, naming every such
    /// kernel and why, or it is a DXIL container no <c>dxcompiler.dll</c> can reflect: the reload is refused and the set
    /// keeps its kernels.</exception>
    /// <exception cref="InvalidDataException">A changed kernel's bytes are not bytecode the reflector reads.</exception>
    public SdfWorldPipelineReload PrepareReload(GpuPassPipelineCache cache, IGpuDeviceContext device, SdfKernelSet kernels, ShaderBytecodeReflector reflector) {
        ArgumentNullException.ThrowIfNull(argument: cache);
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: kernels);
        ArgumentNullException.ThrowIfNull(argument: reflector);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        lock (m_gate) {
            var baseline = m_kernels;
            var changed = new List<(int Index, Slot? Slot, ReadOnlyMemory<byte> Bytecode)>();
            List<string>? refusals = null;

            for (var index = 0; (index < m_slots.Length); index++) {
                var slot = m_slots[index];
                var kernel = ((SdfKernel)index);

                if ((slot is null) && (kernel is not (SdfKernel.Resolve or SdfKernel.ReceiverComparison or SdfKernel.IndirectClassify or SdfKernel.IndirectTrace or SdfKernel.IndirectShade or SdfKernel.LightPrimary or SdfKernel.LightDepth))) {
                    continue;
                }
                var bytecode = kernels[kernel];

                // A refused pipeline is built again from the bytecode it was refused with, which the set already reads.
                if (bytecode.Span.SequenceEqual(other: baseline[kernel].Span)) {
                    if (slot?.Refusal is not null) {
                        changed.Add(item: (index, slot, bytecode));
                    }

                    continue;
                }

                if (SdfKernelSet.InterfaceMismatch(kernel: kernel, reflected: reflector.Read(bytecode: bytecode.Span)) is { } mismatch) {
                    (refusals ??= []).Add(item: $"'{SdfKernelSet.StemOf(kernel: kernel)}': {mismatch}");
                }

                if ((slot is not null) || (kernel is SdfKernel.Resolve or SdfKernel.ReceiverComparison or SdfKernel.IndirectClassify or SdfKernel.IndirectTrace or SdfKernel.IndirectShade)) {
                    changed.Add(item: (index, slot, bytecode));
                }
            }

            if (refusals is not null) {
                throw new InvalidOperationException(message: $"The reloaded kernels do not read this host's interface (instruction set stamp '{SdfWorldInterfaces.Stamp}'), so the set keeps its kernels: {string.Join(separator: " ", values: refusals)}");
            }

            var replacements = new List<(int Index, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> Lease)>();

            try {
                foreach (var (index, slot, bytecode) in changed) {
                    replacements.Add(item: (index, cache.Acquire(
                        device: device,
                        key: GpuPassPipelineKey.OfCompute(
                            bytecode: bytecode,
                            description: (slot?.Description ?? SdfWorldTables.PipelineLayouts.Specs[index].Description)
                        )
                    )));
                }
            } catch {
                GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>.Release(leases: [.. replacements.Select(selector: static replacement => replacement.Lease)]);

                throw;
            }

            return new SdfWorldPipelineReload(
                baseline: baseline,
                kernels: kernels,
                replacements: [.. replacements],
                target: this
            );
        }
    }

    internal void Commit(SdfWorldPipelineReload reload) {
        lock (m_gate) {
            m_kernels = reload.Kernels;
            reload.Commit();
        }
    }
    internal void Exchange(SdfWorldPipelineReload reload) {
        lock (m_gate) {
            reload.Exchange(slots: m_slots);
        }
    }
    internal IGpuComputePipeline? OptionalPipeline(SdfKernel kernel) =>
        (((Volatile.Read(location: ref m_slots[((int)kernel)]) is { } slot) && (slot.Lease.Current is not null)) ? slot : null);
    internal IGpuComputePipeline Pipeline(SdfKernel kernel) =>
        (m_slots[((int)kernel)] ?? throw new InvalidOperationException(message: $"The pipeline set has no '{SdfKernelSet.StemOf(kernel: kernel)}' pipeline."));
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

        if (!ReferenceEquals(objA: reload.Baseline, objB: m_kernels)) {
            throw new InvalidOperationException(message: "The reload was prepared against kernels that are no longer installed.");
        }
    }
    // The views variants, one of which a program dispatches its views with (SdfWorldTables.ViewsPipeline).
    internal static bool IsViews(SdfKernel kernel) => (kernel is (SdfKernel.Views or SdfKernel.ViewsCore or SdfKernel.ViewsFolds));
    // Polls every lease, so each build that failed is named, in the set's order; a device loss is thrown alone. The
    // slots are a set's, in kernel order. Without viewsRequired, a views variant or the comparison receiver still building
    // or refused leaves the set ready: the frame waits for those itself (SdfWorldTables.FrameWaiting).
    internal static bool PollAll(IReadOnlyList<Slot?> slots, bool viewsRequired = true) {
        var ready = true;
        List<(string Name, Exception Failure)>? failures = null;

        for (var index = 0; (index < slots.Count); index++) {
            if (slots[index] is not { } slot) {
                continue;
            }

            // A kernel the tables do without is refused as IsBuilt refuses it, never thrown.
            if (!viewsRequired && (IsViews(kernel: ((SdfKernel)index)) || (((SdfKernel)index) == SdfKernel.ReceiverComparison))) {
                _ = slot.PollRefusing();

                continue;
            }

            try {
                ready &= (slot.Lease.Poll() is not null);
            } catch (Exception failure) {
                (failures ??= []).Add(item: (slot.Description.Name, failure));
            }
        }

        ThrowIfFailed(failures: failures);

        return ready;
    }
    internal static async Task WaitAllAsync(IReadOnlyList<Slot?> slots, CancellationToken cancellationToken) {
        List<(string Name, Exception Failure)>? failures = null;

        for (var index = 0; (index < slots.Count); index++) {
            if (slots[index] is not { } slot) {
                continue;
            }

            try {
                _ = await slot.Lease.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception failure) when ((failure is not OperationCanceledException)) {
                (failures ??= []).Add(item: (slot.Description.Name, failure));
            }
        }

        ThrowIfFailed(failures: failures);
    }

    private static void ThrowIfFailed(List<(string Name, Exception Failure)>? failures) {
        if (failures is null) {
            return;
        }

        foreach (var (_, failure) in failures) {
            if (failure is DeviceLostException) {
                ExceptionDispatchInfo.Throw(source: failure);
            }
        }

        throw new AggregateException(
            innerExceptions: failures.Select(selector: static failed => failed.Failure),
            message: $"The SDF pipeline set's build failed creating {string.Join(separator: ", ", values: failures.Select(selector: static failed => failed.Name))}."
        );
    }

    // The tables' record paths and descriptor sets retain these stable slots. A reload changes the lease behind one
    // between frames, with an identically defined layout, so both backends reuse the allocated sets.
    internal sealed class Slot(GpuComputePipelineDescription description, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> lease) : IGpuComputePipeline {
        public GpuComputePipelineDescription Description { get; } = description;

        private GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> m_lease = lease;

        // The lease's failed creation, which PollRefusing keeps instead of polling the lease again; a reload's exchange
        // clears it with the lease it replaces.
        private Exception? m_refusal;

        public nint DescriptorSetLayoutHandle => Native.DescriptorSetLayoutHandle;
        public IReadOnlyList<nint> GroupLayoutHandles => Native.GroupLayoutHandles;
        public nint Handle => Native.Handle;
        public nint LayoutHandle => Native.LayoutHandle;
        public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> Lease => Volatile.Read(location: ref m_lease);
        public Exception? Refusal => Volatile.Read(location: ref m_refusal);

        private IGpuComputePipeline Native => (Lease.Current?.Compute ?? throw new InvalidOperationException(message: $"The '{Description.Name}' pipeline is not built."));

        public void Dispose() =>
            Lease.Release();
        public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> Exchange(GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> replacement) {
            var replaced = Interlocked.Exchange(location1: ref m_lease, value: replacement);

            Volatile.Write(location: ref m_refusal, value: null);

            return replaced;
        }
        // Whether the lease is built. A creation that failed other than by a device loss is kept as the refusal, and the
        // lease is not polled again, since a poll after a failure starts a fresh build.
        public bool PollRefusing() {
            if (Refusal is not null) {
                return false;
            }

            try {
                return (Lease.Poll() is not null);
            } catch (Exception failure) when ((failure is not DeviceLostException)) {
                Volatile.Write(location: ref m_refusal, value: failure);
                Console.Error.WriteLine(value: $"[{Description.Name}] kernel build refused, retried on a kernel reload or a device loss: {failure.Message}");

                return false;
            }
        }
    }
}
/// <summary>
/// Replacement pipelines for the kernels that changed, leased by <see cref="SdfWorldPipelines.PrepareReload"/> and put
/// into service by <see cref="SdfWorldTables.InstallReload"/> once built (<see cref="WaitAsync"/>). Disposing a reload that
/// was never installed releases its leases; a committed reload releases the leases it replaced.
/// </summary>
public sealed class SdfWorldPipelineReload : IDisposable {
    private readonly (int Index, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> Lease)[] m_replacements;
    private readonly GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>?[] m_retired;

    private State m_state;

    internal SdfWorldPipelineReload(SdfWorldPipelines target, SdfKernelSet baseline, SdfKernelSet kernels, (int Index, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> Lease)[] replacements) {
        Baseline = baseline;
        Kernels = kernels;
        Target = target;
        m_replacements = replacements;
        m_retired = new GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>?[replacements.Length];
    }

    /// <summary>Gets the number of pipelines whose bytecode changed and that the reload replaces.</summary>
    public int ChangedPipelines => m_replacements.Length;
    /// <summary>Gets the kernel set the reload installs.</summary>
    public SdfKernelSet Kernels { get; }

    internal SdfKernelSet Baseline { get; }
    internal SdfWorldPipelines Target { get; }

    /// <summary>Returns a task that completes once every replacement is built, for the holder's own background build,
    /// which awaits it holding no thread.</summary>
    /// <param name="cancellationToken">The token that ends the wait.</param>
    /// <returns>The task, which faults as the exceptions below say.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="AggregateException">A replacement's creation failed, for malformed or unsupported bytecode among
    /// other causes. The message names every pipeline whose creation failed, and the inner exceptions are those
    /// failures in the same order.</exception>
    /// <exception cref="DeviceLostException">The device was lost during a creation; thrown alone.</exception>
    public Task WaitAsync(CancellationToken cancellationToken) =>
        SdfWorldPipelines.WaitAllAsync(
            cancellationToken: cancellationToken,
            slots: [.. m_replacements.Select(selector: replacement => new SdfWorldPipelines.Slot(
                description: SdfWorldTables.PipelineLayouts.Specs[replacement.Index].Description,
                lease: replacement.Lease
            ))]
        );
    /// <inheritdoc/>
    public void Dispose() {
        if (m_state == State.Prepared) {
            GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>.Release(leases: [.. m_replacements.Select(selector: static replacement => replacement.Lease)]);
        }

        m_state = State.Disposed;
    }

    internal void Commit() {
        if (m_state == State.Exchanged) {
            foreach (var retired in m_retired) {
                retired?.Release();
            }
        }

        m_state = State.Committed;
    }
    internal void Exchange(SdfWorldPipelines.Slot?[] slots) {
        for (var index = 0; (index < m_replacements.Length); index++) {
            var (slot, lease) = m_replacements[index];

            if (slots[slot] is { } previous) {
                m_retired[index] = previous.Exchange(replacement: lease);
            } else {
                slots[slot] = new SdfWorldPipelines.Slot(description: SdfWorldTables.PipelineLayouts.Specs[slot].Description, lease: lease);
            }
        }

        m_state = State.Exchanged;
    }

    private enum State : byte {
        Prepared = 0,
        Exchanged = 1,
        Committed = 2,
        Disposed = 3,
    }
}
