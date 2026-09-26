using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>One view's output image, acquired from <see cref="SdfWorldEngine.TryAcquireViewOutput"/>: the image the
/// view's dispatch set last rendered, which rests in <see cref="SdfWorldEngine.OutputLayout"/> between the engine's
/// submissions. The engine keeps it alive until the acquisition is released
/// (<see cref="SdfWorldEngine.ReleaseViewOutput"/>).</summary>
/// <param name="ImageHandle">The native image handle.</param>
/// <param name="ImageViewHandle">The native image-view handle.</param>
/// <param name="Width">The image's width in pixels: the view's render extent.</param>
/// <param name="Height">The image's height in pixels.</param>
/// <param name="Identity">The image's identity, unique across every engine in the process, which releases the
/// acquisition.</param>
public readonly record struct SdfViewOutput(nint ImageHandle, nint ImageViewHandle, uint Width, uint Height, int Identity);
public sealed partial class SdfWorldEngine {
    // The debug-name detail of each view slot's descriptor sets and output image.
    private static readonly string[] ViewDetails = ["view0", "view1", "view2", "view3", "view4"];

    // The last identity handed to an output image, over every engine in the process, so a lease names one image.
    private static int OutputIdentity;

    // Per view slot: the output image its dispatch set writes, created at the view's extent by the frame that first
    // renders it and replaced only when that extent changes.
    private readonly ViewOutput?[] m_viewOutputs;
    // Per view slot: the extent a host asked the view to render at (RequestViewExtent), or zero for none.
    private readonly (uint Width, uint Height)[] m_requestedViewExtents;
    // Per ring slot and view slot: the output view that slot's views set binds, zero when it must be rewritten.
    private readonly nint[][] m_boundOutputViews;
    // Replaced output images, each disposed once no acquisition holds it and the ring has retired every submission
    // that wrote it.
    private readonly List<ViewOutput> m_retiredViewOutputs = [];

    // Whether this frame replaced a view's output, whose new image holds nothing yet, so the frame renders.
    private bool m_viewOutputReplaced;

    /// <summary>Gets the acquisitions of the engine's view outputs not yet released, over its current outputs and every
    /// replaced output it still keeps.</summary>
    public int ViewOutputHolds {
        get {
            var holds = 0;

            foreach (var output in m_viewOutputs) {
                holds += (output?.Holds ?? 0);
            }
            foreach (var output in m_retiredViewOutputs) {
                holds += output.Holds;
            }

            return holds;
        }
    }

    /// <summary>Returns the extent a view renders at when no host asked for one: its rect at its render scale, as a
    /// fraction of the engine's extent, quantized as a render graph quantizes a footprint
    /// (<see cref="RenderGraphExtent"/>), so a view the graph has not yet scheduled renders at the extent its first
    /// schedule names.</summary>
    /// <param name="view">The view.</param>
    /// <param name="width">The engine's extent width in pixels.</param>
    /// <param name="height">The engine's extent height in pixels.</param>
    /// <returns>The extent, at least one pixel and at most the engine's extent on each axis.</returns>
    public static (uint Width, uint Height) DefaultViewExtent(SdfViewSnapshot view, uint width, uint height) {
        var scale = (((view.RenderScale > 0f) && (view.RenderScale < 1f))
            ? view.RenderScale
            : 1f);

        return (
            Width: AxisExtent(
                display: width,
                fraction: (view.Region.Width * scale)
            ),
            Height: AxisExtent(
                display: height,
                fraction: (view.Region.Height * scale)
            )
        );
    }
    /// <summary>Asks a view to render at an extent from the next frame on, such as the extent a render graph scheduled
    /// for it; the engine clamps it to its own extent. In export mode view 0 always renders at the engine's
    /// extent.</summary>
    /// <param name="view">The view slot.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="view"/> is past the viewport capacity, or an extent
    /// is zero.</exception>
    public void RequestViewExtent(int view, uint width, uint height) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: view);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: m_requestedViewExtents.Length,
            value: view
        );
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);

        m_requestedViewExtents[view] = (width, height);
    }
    /// <summary>Gets whether a view slot's output holds a rendered frame.</summary>
    /// <param name="view">The view slot.</param>
    /// <returns><see langword="true"/> once a submitted frame has rendered the view into its current output.</returns>
    public bool HasViewOutput(int view) => (
        (((uint)view) < ((uint)m_viewOutputs.Length)) &&
        (m_viewOutputs[view] is { Rendered: true })
    );
    /// <summary>Acquires a view's current output, holding its image until <see cref="ReleaseViewOutput"/> releases the
    /// acquisition, so a replaced image outlives the consumers still sampling it.</summary>
    /// <param name="view">The view slot.</param>
    /// <param name="output">The output, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when the view has not rendered into its current output.</returns>
    public bool TryAcquireViewOutput(int view, out SdfViewOutput output) {
        if (
            (((uint)view) >= ((uint)m_viewOutputs.Length)) ||
            (m_viewOutputs[view] is not { Rendered: true } current)
        ) {
            output = default;

            return false;
        }

        current.Holds++;
        output = current.Describe();

        return true;
    }
    /// <summary>Releases one acquisition of an output image.</summary>
    /// <param name="identity">The acquired output's <see cref="SdfViewOutput.Identity"/>.</param>
    /// <returns><see langword="true"/> when the image is one of this engine's.</returns>
    public bool ReleaseViewOutput(int identity) {
        foreach (var output in m_viewOutputs) {
            if (output?.Identity == identity) {
                output.Holds--;

                return true;
            }
        }
        foreach (var output in m_retiredViewOutputs) {
            if (output.Identity == identity) {
                output.Holds--;

                return true;
            }
        }

        return false;
    }

    private static uint AxisExtent(float fraction, uint display) => Math.Min(
        val1: display,
        val2: ((uint)RenderGraphExtent.Pixels(
            display: ((int)display),
            fraction: RenderGraphExtent.Quantize(fraction: Math.Max(
                val1: 0d,
                val2: fraction
            ))
        ))
    );
    private static int NextOutputIdentity() => Interlocked.Increment(location: ref OutputIdentity);
    // Binds each view's output into the current ring slot's views set for it; a set keeps its binding until the view's
    // output is replaced, since the engine owns the image.
    private void BindViewOutputs(uint viewportCount) {
        var bound = m_boundOutputViews[m_currentSlot];

        for (var view = 0; (view < ((int)viewportCount)); view++) {
            var imageView = m_viewOutputs[view]!.Image.ImageViewHandle;

            if (bound[view] == imageView) {
                continue;
            }

            m_bindings.WriteStorageImage(
                arrayElement: 0,
                binding: ViewOutputBindingIndex,
                descriptorSetHandle: m_viewsSets[m_currentSlot][view],
                imageViewHandle: imageView
            );
            bound[view] = imageView;
        }
    }
    // Disposes the view outputs, current and replaced, after the device has drained.
    private void DisposeViewOutputs() {
        foreach (var output in m_viewOutputs) {
            output?.Image.Dispose();
        }
        foreach (var output in m_retiredViewOutputs) {
            output.Image.Dispose();
        }

        m_retiredViewOutputs.Clear();
    }
    // Sizes each of this frame's views' outputs at its extent: the extent a host asked for, or the default, clamped to the
    // engine's extent. A view whose extent moved gets a new image, and the replaced one retires. Called after the slot's
    // fence wait, which also lets a retired image whose last writer has completed and whose last acquisition was
    // released be disposed.
    private void EnsureViewOutputs(SdfFrame frame, uint viewportCount) {
        for (var index = (m_retiredViewOutputs.Count - 1); (index >= 0); index--) {
            var retired = m_retiredViewOutputs[index];

            // The last frame that wrote it was the one before its retirement, whose slot fence this frame's or an earlier
            // wait has passed once the ring has advanced past it.
            if (
                (retired.Holds == 0) &&
                (m_ringFrame > (retired.RetiredAt + 1))
            ) {
                retired.Image.Dispose();
                m_retiredViewOutputs.RemoveAt(index: index);
            }
        }

        m_viewOutputReplaced = false;

        for (var view = 0; (view < ((int)viewportCount)); view++) {
            var (width, height) = ViewExtentOf(
                frame: frame,
                view: view
            );

            if (m_viewOutputs[view] is { } current) {
                if (
                    ((current.Width == width) && (current.Height == height)) ||
                    ((view == 0) && m_exportMode)
                ) {
                    continue;
                }

                current.RetiredAt = m_ringFrame;
                m_retiredViewOutputs.Add(item: current);
            }

            m_viewOutputs[view] = new ViewOutput(
                height: height,
                identity: NextOutputIdentity(),
                image: m_gpu.ImageFactory.Create(
                    format: Format,
                    height: height,
                    name: NameOf(detail: ViewDetails[view], part: "output"),
                    usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
                    width: width
                ),
                width: width
            );
            m_viewOutputReplaced = true;

            foreach (var bound in m_boundOutputViews) {
                bound[view] = 0;
            }
        }
    }
    // The extent a view renders at this frame.
    private (uint Width, uint Height) ViewExtentOf(SdfFrame frame, int view) {
        if ((view == 0) && m_exportMode) {
            return (m_width, m_height);
        }

        var (width, height) = m_requestedViewExtents[view];

        if ((width == 0) || (height == 0)) {
            return DefaultViewExtent(
                height: m_height,
                view: frame.Views[view],
                width: m_width
            );
        }

        return (
            Width: Math.Min(val1: width, val2: m_width),
            Height: Math.Min(val1: height, val2: m_height)
        );
    }

    // One view slot's output image and what the engine knows of it.
    private sealed class ViewOutput(IGpuImage image, uint width, uint height, int identity) {
        public uint Height { get; } = height;

        public int Holds { get; set; }

        public int Identity { get; } = identity;
        public IGpuImage Image { get; } = image;

        // Whether a recorded frame has moved it out of Undefined, so it rests in OutputLayout between submissions.
        public bool Initialized { get; set; }
        // Whether a submitted frame has rendered into it.
        public bool Rendered { get; set; }
        // The ring frame count when it was replaced.
        public ulong RetiredAt { get; set; }

        public uint Width { get; } = width;

        public SdfViewOutput Describe() => new(
            Height: Height,
            Identity: Identity,
            ImageHandle: Image.ImageHandle,
            ImageViewHandle: Image.ImageViewHandle,
            Width: Width
        );
    }
}
