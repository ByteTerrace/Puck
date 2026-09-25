using System.Runtime.ExceptionServices;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// The one region-copy pipeline each device has (<c>region-copy.comp</c>, created from
/// <see cref="GpuRegion.CopyPipeline"/>), shared by every owner that copies a <see cref="GpuRegion"/> or records the same
/// copy by hand: the SDF engine's table upload and its mesh region among them. An owner takes a
/// <see cref="GpuRegionCopyPipelineLease"/>; the first lease on a device starts the pipeline's build on the thread pool
/// (<see cref="BackgroundBuild{T}"/>), every later lease on that device joins it, each polls it from the frame thread,
/// and the last release disposes it, waiting only for a creation already in the driver.
/// <para>
/// The cache counts the shader module and pipeline it creates into <see cref="Work"/>, a <see cref="GpuWorkLedger"/>
/// named <see cref="WorkSourceName"/>; no owner's ledger counts them. Every device a cache serves runs one backend, so a
/// cache creates every pipeline from one kernel: the one it was built with, or its backend's deployed kernel, read from
/// <see cref="DefaultDirectory"/> on the first acquire and kept for the cache's whole life.
/// </para>
/// <para>
/// Acquiring is safe on any thread, and the first acquire of a cache over a deployed kernel reads it, so an owner on the
/// frame thread acquires from its own background work. A device recreated in place after a loss keeps its identity, so
/// every owner releases its lease on device loss, and a pipeline is never handed to a lease on the recreated device.
/// </para>
/// </summary>
public sealed class GpuRegionCopyPipelineCache {
    /// <summary>The file name stem of the deployed kernel: <c>region-copy.comp</c> and the backend's extension.</summary>
    public const string KernelStem = "region-copy";
    /// <summary>The name a counters report heads <see cref="Work"/>'s section with.</summary>
    public const string WorkSourceName = "gpu.region-copy";

    private readonly string? m_bytecodeExtension;

    private readonly List<GpuRegionCopyPipelineLease.Entry> m_entries = [];
    private readonly Lock m_gate = new();
    private readonly Lock m_kernelGate = new();
    private readonly GpuWorkLedger m_work = new(
        framesInFlight: 1,
        name: WorkSourceName
    );

    private ReadOnlyMemory<byte> m_kernel;

    /// <summary>Initializes a new instance of the <see cref="GpuRegionCopyPipelineCache"/> class over its backend's
    /// deployed kernel, which the first acquire reads.</summary>
    /// <param name="bytecodeExtension">The backend's compiled-kernel extension (<c>".spv"</c> for Vulkan,
    /// <c>".dxil"</c> for Direct3D 12).</param>
    /// <exception cref="ArgumentException"><paramref name="bytecodeExtension"/> is empty.</exception>
    public GpuRegionCopyPipelineCache(string bytecodeExtension) {
        ArgumentException.ThrowIfNullOrEmpty(argument: bytecodeExtension);

        m_bytecodeExtension = bytecodeExtension;
    }
    /// <summary>Initializes a new instance of the <see cref="GpuRegionCopyPipelineCache"/> class that creates every
    /// device's pipeline from one compiled kernel, for a host with its own kernel.</summary>
    /// <param name="kernel">The compiled kernel for the devices the cache serves; not empty.</param>
    /// <exception cref="ArgumentException"><paramref name="kernel"/> is empty.</exception>
    public GpuRegionCopyPipelineCache(ReadOnlyMemory<byte> kernel) {
        if (kernel.IsEmpty) {
            throw new ArgumentException(
                message: "The region-copy kernel is empty.",
                paramName: nameof(kernel)
            );
        }

        m_kernel = kernel;
    }

    /// <summary>Gets the standard deploy location of the compiled kernel: <c>Assets/Shaders/Residency</c> next to the
    /// application, where a <c>Puck.Shaders</c> reference copies the bytecode its build compiles.</summary>
    public static string DefaultDirectory => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "Shaders",
        path4: "Residency"
    );
    /// <summary>Gets the number of devices with a leased pipeline, built or building.</summary>
    public int LeasedDevices {
        get {
            lock (m_gate) {
                return m_entries.Count;
            }
        }
    }
    /// <summary>Gets the shader modules and pipelines the cache has created, over its whole life.</summary>
    public IWorkCounterSource Work => m_work;

    /// <summary>Creates the region-copy pipeline on a device, on the calling thread: the shader module, then the
    /// pipeline from <see cref="GpuRegion.CopyPipeline"/>, both counted into <paramref name="ledger"/>. The cache builds
    /// through it on the thread pool; a harness that drives an engine directly calls it itself.</summary>
    /// <param name="device">The device the pipeline is created on, through its services.</param>
    /// <param name="kernel">The compiled kernel for the device's backend.</param>
    /// <param name="ledger">The ledger that counts the shader module and pipeline.</param>
    /// <param name="cancellationToken">The token checked before the creation starts; never during it.</param>
    /// <returns>The pipeline, owned by the caller, which disposes it after every owner that records with it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="ledger"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="kernel"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The build was canceled before it started.</exception>
    public static GpuRegionCopyPipeline Build(IGpuDeviceContext device, ReadOnlyMemory<byte> kernel, GpuWorkLedger ledger, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: ledger);

        if (kernel.IsEmpty) {
            throw new ArgumentException(
                message: "The region-copy kernel is empty.",
                paramName: nameof(kernel)
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        var gpu = GpuWorkCounting.Wrap(
            ledger: ledger,
            services: device.Services
        );
        var shader = gpu.ShaderModuleFactory.Create(
            bytecode: kernel,
            stage: GpuShaderStage.Compute
        );

        try {
            var pipeline = new GpuRegionCopyPipeline(
                native: gpu.PipelineFactory.Create(
                    computeShaderModule: shader,
                    description: GpuRegion.CopyPipeline
                ),
                shader: shader
            );

            (device as IGpuPipelineCache)?.Persist();

            return pipeline;
        } catch {
            shader.Dispose();
            throw;
        }
    }
    /// <summary>Reads a backend's compiled kernel from a directory.</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for
    /// Direct3D 12).</param>
    /// <param name="directory">The directory holding <c>region-copy.comp</c> compiled for that backend.</param>
    /// <returns>The kernel's bytecode.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytecodeExtension"/> or <paramref name="directory"/> is
    /// empty.</exception>
    /// <exception cref="IOException">The kernel file is missing or cannot be read.</exception>
    public static ReadOnlyMemory<byte> Load(string bytecodeExtension, string directory) {
        ArgumentException.ThrowIfNullOrEmpty(argument: bytecodeExtension);
        ArgumentException.ThrowIfNullOrEmpty(argument: directory);

        return File.ReadAllBytes(path: Path.Combine(
            path1: directory,
            path2: $"{KernelStem}.comp{bytecodeExtension}"
        ));
    }
    /// <summary>Takes a lease on <paramref name="device"/>'s pipeline, joining the pipeline another owner already
    /// leases or starting its build on the thread pool. Safe on any thread; the first acquire of a cache over a deployed
    /// kernel reads it, so an owner on the frame thread acquires from its own background work.</summary>
    /// <param name="device">The device the pipeline is created on, through its services.</param>
    /// <returns>The lease, which the caller releases once nothing it recorded with the pipeline is in flight.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The deployed kernel is missing or cannot be read.</exception>
    public GpuRegionCopyPipelineLease Acquire(IGpuDeviceContext device) {
        ArgumentNullException.ThrowIfNull(argument: device);

        lock (m_gate) {
            foreach (var entry in m_entries) {
                if (ReferenceEquals(
                    objA: entry.Device,
                    objB: device
                )) {
                    entry.Holders++;

                    return new GpuRegionCopyPipelineLease(
                        cache: this,
                        entry: entry
                    );
                }
            }
        }

        // Read outside the gate: a first read is file I/O, and a lease racing it for the same device finds the entry
        // below rather than creating a second one.
        var kernel = Kernel();

        lock (m_gate) {
            foreach (var entry in m_entries) {
                if (ReferenceEquals(
                    objA: entry.Device,
                    objB: device
                )) {
                    entry.Holders++;

                    return new GpuRegionCopyPipelineLease(
                        cache: this,
                        entry: entry
                    );
                }
            }

            var created = new GpuRegionCopyPipelineLease.Entry(
                device: device,
                kernel: kernel
            );

            m_entries.Add(item: created);
            StartBuild(entry: created);

            return new GpuRegionCopyPipelineLease(
                cache: this,
                entry: created
            );
        }
    }

    internal GpuRegionCopyPipeline? Poll(GpuRegionCopyPipelineLease.Entry entry) {
        lock (m_gate) {
            if (entry.Pipeline is { } ready) {
                return ready;
            }

            if (!entry.Build.IsPending) {
                StartBuild(entry: entry);
            }

            if (!entry.Build.TryTake(
                error: out var error,
                result: out var built
            )) {
                return null;
            }

            // A failed build leaves nothing pending, so the next poll by any owner starts a fresh one.
            if (error is not null) {
                ExceptionDispatchInfo.Throw(source: error);
            }

            entry.Pipeline = built;

            return built;
        }
    }
    internal void Release(GpuRegionCopyPipelineLease.Entry entry) {
        CanceledBuild<GpuRegionCopyPipeline> build;

        lock (m_gate) {
            if (--entry.Holders > 0) {
                return;
            }

            build = entry.Build.Detach();
            _ = m_entries.Remove(item: entry);
        }

        build.Wait(discard: static pipeline => pipeline.Dispose());
        entry.Pipeline?.Dispose();
        entry.Pipeline = null;
    }

    private ReadOnlyMemory<byte> Kernel() {
        lock (m_kernelGate) {
            if (m_kernel.IsEmpty) {
                m_kernel = Load(
                    bytecodeExtension: m_bytecodeExtension!,
                    directory: DefaultDirectory
                );
            }

            return m_kernel;
        }
    }
    private void StartBuild(GpuRegionCopyPipelineLease.Entry entry) =>
        entry.Build.Start(build: token => Build(
            cancellationToken: token,
            device: entry.Device,
            kernel: entry.Kernel,
            ledger: m_work
        ));
}
/// <summary>
/// One owner's share of a device's region-copy pipeline from a <see cref="GpuRegionCopyPipelineCache"/>. The owner polls
/// it from the frame thread until the pipeline is ready, records with it, and releases it on device loss and disposal
/// once nothing it recorded with the pipeline is in flight.
/// </summary>
public sealed class GpuRegionCopyPipelineLease {
    private GpuRegionCopyPipelineCache? m_cache;

    private readonly Entry m_entry;

    internal GpuRegionCopyPipelineLease(GpuRegionCopyPipelineCache cache, Entry entry) {
        m_cache = cache;
        m_entry = entry;
    }

    /// <summary>Gets the ready pipeline, or <see langword="null"/> before its build has completed and after the lease is
    /// released.</summary>
    public GpuRegionCopyPipeline? Current => ((m_cache is null)
        ? null
        : m_entry.Pipeline
    );
    /// <summary>Gets whether the lease has been released.</summary>
    public bool IsReleased => (m_cache is null);

    /// <summary>Returns the ready pipeline, or <see langword="null"/> while its build runs. A build that failed rethrows
    /// its exception here, on the frame thread, so a device loss reaches the host's recovery; the next poll starts a
    /// fresh build. Allocates nothing while the build runs or once the pipeline is ready.</summary>
    /// <returns>The ready pipeline, or <see langword="null"/> while it builds.</returns>
    /// <exception cref="ObjectDisposedException">The lease has been released.</exception>
    public GpuRegionCopyPipeline? Poll() {
        var cache = m_cache;

        ObjectDisposedException.ThrowIf(
            condition: (cache is null),
            instance: this
        );

        return cache!.Poll(entry: m_entry);
    }
    /// <summary>Gives up the lease. The last lease on a device waits out a build still in flight, discarding its result,
    /// and disposes the pipeline; call it once nothing recorded with the pipeline is in flight and before the device goes
    /// away. Releasing twice does nothing.</summary>
    public void Release() {
        if (Interlocked.Exchange(
            location1: ref m_cache,
            value: null
        ) is { } cache) {
            cache.Release(entry: m_entry);
        }
    }

    // One device's pipeline and its holders. Every mutable member is read and written under the cache's gate.
    internal sealed class Entry(IGpuDeviceContext device, ReadOnlyMemory<byte> kernel) {
        public BackgroundBuild<GpuRegionCopyPipeline> Build { get; } = new();
        public IGpuDeviceContext Device { get; } = device;
        public int Holders { get; set; } = 1;
        public ReadOnlyMemory<byte> Kernel { get; } = kernel;

        public GpuRegionCopyPipeline? Pipeline { get; set; }
    }
}
/// <summary>
/// A device's region-copy pipeline and the shader module it was created from, released together. It records like any
/// compute pipeline (<see cref="IGpuComputePipeline"/>); its owner is the <see cref="GpuRegionCopyPipelineCache"/> or
/// the harness that built it, never an owner recording with it.
/// </summary>
public sealed class GpuRegionCopyPipeline : IGpuComputePipeline {
    private readonly IGpuComputePipeline m_native;
    private readonly IGpuShaderModule m_shader;

    private bool m_disposed;

    internal GpuRegionCopyPipeline(IGpuComputePipeline native, IGpuShaderModule shader) {
        m_native = native;
        m_shader = shader;
    }

    /// <inheritdoc/>
    public nint DescriptorSetLayoutHandle => m_native.DescriptorSetLayoutHandle;
    /// <inheritdoc/>
    public IReadOnlyList<nint> GroupLayoutHandles => m_native.GroupLayoutHandles;
    /// <inheritdoc/>
    public nint Handle => m_native.Handle;
    /// <inheritdoc/>
    public nint LayoutHandle => m_native.LayoutHandle;

    /// <summary>Releases the pipeline and its shader module. Disposing twice does nothing.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_native.Dispose();
        m_shader.Dispose();
    }
}
