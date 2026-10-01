using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// An export over fake images (<see cref="IShaderPipelineOutputExport"/>): it creates each image on the fake device,
/// notes every write the node ends, and releases the image to the node until told otherwise.
/// </summary>
/// <param name="gpu">The fake device the images are created on.</param>
/// <param name="width">The export's width, in pixels.</param>
/// <param name="height">The export's height, in pixels.</param>
/// <param name="format">The format of the images it creates: the exported output's.</param>
internal sealed class FakeOutputExport(FakePipelineGpu gpu, uint width, uint height, GpuPixelFormat format = GpuPixelFormat.R8G8B8A8Unorm) : IShaderPipelineOutputExport {
    /// <summary>Gets every image the export created, in creation order.</summary>
    public List<Image> Created { get; } = [];
    /// <summary>Gets every write the node ended, in order.</summary>
    public List<(bool Written, ulong Value)> Ended { get; } = [];
    /// <inheritdoc/>
    public uint Height => height;
    /// <summary>Gets or sets whether the reader has released the image.</summary>
    public bool Released { get; set; } = true;
    /// <inheritdoc/>
    public uint Width => width;

    /// <inheritdoc/>
    public IGpuExportableImage Create(IGpuDeviceContext device) {
        var image = new Image(inner: gpu.Services.ImageFactory.Create(
            format: format,
            height: Height,
            name: new GpuObjectName(owner: "export", part: "image"),
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: Width
        ));

        Created.Add(item: image);

        return image;
    }
    /// <inheritdoc/>
    public void EndWrite(bool written, IGpuExportableImage? image, ulong writtenValue) => Ended.Add(item: (written, writtenValue));
    /// <inheritdoc/>
    public bool TryBeginWrite() => Released;

    /// <summary>A fake exportable image: a fake image whose writes count the fence values they signal, and which notes
    /// each take-back from its reader.</summary>
    /// <param name="inner">The fake image it wraps.</param>
    internal sealed class Image(IGpuImage inner) : IGpuExportableImage {
        /// <inheritdoc/>
        public GpuPixelFormat Format => inner.Format;
        /// <inheritdoc/>
        public uint Height => inner.Height;
        /// <inheritdoc/>
        public nint ImageHandle => inner.ImageHandle;
        /// <inheritdoc/>
        public nint ImageViewHandle => inner.ImageViewHandle;
        /// <inheritdoc/>
        public nint SharedFenceHandle => 1;
        /// <inheritdoc/>
        public nint SharedHandle => 1;
        /// <inheritdoc/>
        public GpuImageUsage Usage => inner.Usage;
        /// <inheritdoc/>
        public uint Width => inner.Width;
        /// <summary>Gets how many writes began.</summary>
        public int Begun { get; private set; }
        /// <summary>Gets how many writes completed.</summary>
        public int Writes { get; private set; }

        /// <inheritdoc/>
        public void BeginWrite() => Begun++;
        /// <inheritdoc/>
        public ulong CompleteWrite() {
            if (RefuseCompletion) { throw new InvalidOperationException(message: "Injected post-submit failure."); }
            return ((ulong)++Writes);
        }

        /// <summary>Gets or sets whether completion fails after the node's submission succeeded.</summary>
        public bool RefuseCompletion { get; set; }

        /// <inheritdoc/>
        public void Dispose() => inner.Dispose();
    }
}
