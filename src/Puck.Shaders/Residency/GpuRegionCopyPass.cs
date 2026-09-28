using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// The region copy (<c>region-copy.comp</c>, created from <see cref="GpuRegion.CopyPipeline"/>) as a pass of the
/// composition's <see cref="GpuPassPipelineCache"/>: one pipeline per device, shared by every owner that copies a
/// <see cref="GpuRegion"/> or records the same copy by hand, the SDF engine's table upload and its mesh region among
/// them. An owner takes a lease from <see cref="Acquire"/>; the cache builds the pipeline on the thread pool, counts it
/// under <see cref="GpuPassPipelineCache.WorkSourceName"/>, and disposes it when the last lease is released.
/// <para>
/// Every device a composition serves runs one backend, so every lease is on one kernel: the one given, or its backend's
/// deployed kernel, read from <see cref="DefaultDirectory"/> when the pass is constructed, at composition, and kept for the
/// pass's whole life as its <see cref="Key"/>. Acquiring reads no file and is safe on any thread, the frame thread
/// included. Every owner releases its lease on device loss, so a pipeline is never handed to a lease on the recreated
/// device.
/// </para>
/// </summary>
public sealed class GpuRegionCopyPass {
    /// <summary>The file name stem of the deployed kernel: <c>region-copy.comp</c> and the backend's extension.</summary>
    public const string KernelStem = "region-copy";

    /// <summary>Initializes a new instance of the <see cref="GpuRegionCopyPass"/> class over its backend's deployed
    /// kernel, which it reads here.</summary>
    /// <param name="pipelines">The composition's pass pipelines, which the copy pipeline is an entry of.</param>
    /// <param name="bytecodeExtension">The backend's compiled-kernel extension (<c>".spv"</c> for Vulkan,
    /// <c>".dxil"</c> for Direct3D 12).</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipelines"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="bytecodeExtension"/> is empty.</exception>
    /// <exception cref="IOException">The deployed kernel is missing or cannot be read.</exception>
    public GpuRegionCopyPass(GpuPassPipelineCache pipelines, string bytecodeExtension) : this(
        kernel: Load(
            bytecodeExtension: bytecodeExtension,
            directory: DefaultDirectory
        ),
        pipelines: pipelines
    ) {
    }
    /// <summary>Initializes a new instance of the <see cref="GpuRegionCopyPass"/> class that creates every device's
    /// pipeline from one compiled kernel, for a host with its own kernel.</summary>
    /// <param name="pipelines">The composition's pass pipelines, which the copy pipeline is an entry of.</param>
    /// <param name="kernel">The compiled kernel for the devices the pass serves; not empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipelines"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="kernel"/> is empty.</exception>
    public GpuRegionCopyPass(GpuPassPipelineCache pipelines, ReadOnlyMemory<byte> kernel) {
        ArgumentNullException.ThrowIfNull(argument: pipelines);

        Pipelines = pipelines;
        Key = KeyOf(kernel: kernel);
    }

    /// <summary>Gets the standard deploy location of the compiled kernel: <c>Assets/Shaders/Residency</c> next to the
    /// application, where a <c>Puck.Shaders</c> reference copies the bytecode its build compiles.</summary>
    public static string DefaultDirectory => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "Shaders",
        path4: "Residency"
    );
    /// <summary>Gets the copy pipeline's key, over the kernel the pass was constructed with: every lease is on it.</summary>
    public GpuPassPipelineKey Key { get; }
    /// <summary>Gets the pass pipelines the copy pipeline is an entry of.</summary>
    public GpuPassPipelineCache Pipelines { get; }

    /// <summary>Returns the copy pipeline's key for a compiled kernel.</summary>
    /// <param name="kernel">The compiled kernel for the device's backend; not empty.</param>
    /// <returns>The key: the kernel and <see cref="GpuRegion.CopyPipeline"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="kernel"/> is empty.</exception>
    public static GpuPassPipelineKey KeyOf(ReadOnlyMemory<byte> kernel) =>
        GpuPassPipelineKey.OfCompute(
            bytecode: kernel,
            description: GpuRegion.CopyPipeline
        );
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
    /// <summary>Takes a lease on <paramref name="device"/>'s copy pipeline, joining the entry another owner already
    /// leases or starting its build on the thread pool. Safe on any thread, and reads no file.</summary>
    /// <param name="device">The device the pipeline is created on, through its services.</param>
    /// <returns>The lease, whose <see cref="GpuPassPipeline.Compute"/> is the copy pipeline once built, and which the
    /// caller releases once nothing it recorded with the pipeline is in flight.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> Acquire(IGpuDeviceContext device) {
        ArgumentNullException.ThrowIfNull(argument: device);

        return Pipelines.Acquire(
            device: device,
            key: Key
        );
    }
}
