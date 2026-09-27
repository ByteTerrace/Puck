using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>An image another device reads, which a node renders its default output into in place of images of its own,
/// such as a camera view a probe's kernel host samples. The node creates the image through it when it allocates a graph,
/// renders at its extent whatever extent it is asked for, writes it only on a frame the reader has released it, leaves it
/// in <see cref="GpuImageLayout.External"/> between frames, and ends every write it began.</summary>
public interface IShaderPipelineOutputExport {
    /// <summary>Gets the image's width, in pixels, the width the node renders at.</summary>
    uint Width { get; }
    /// <summary>Gets the image's height, in pixels.</summary>
    uint Height { get; }

    /// <summary>Creates the image on the node's device, sampled and storage, in the default output's format. The node owns
    /// it from here and disposes it with the graph it was allocated for; a graph the node builds again creates another.</summary>
    /// <param name="device">The node's device.</param>
    /// <returns>The image.</returns>
    IGpuExportableImage Create(IGpuDeviceContext device);
    /// <summary>Returns whether the reader has released the image, so the node may write it this frame. A node renders
    /// nothing on a frame it may not, and keeps publishing its last image.</summary>
    /// <returns><see langword="true"/> when the node may write the image, beginning a write it ends with
    /// <see cref="EndWrite"/>.</returns>
    bool TryBeginWrite();
    /// <summary>Ends a write <see cref="TryBeginWrite"/> began.</summary>
    /// <param name="written">Whether the frame's submission wrote the image.</param>
    /// <param name="image">The image the node renders into, or <see langword="null"/> when it has none.</param>
    /// <param name="writtenValue">The shared fence value the write signals (<see cref="IGpuExportableImage.CompleteWrite"/>),
    /// or zero when nothing was written or the write finished before it was handed off.</param>
    void EndWrite(bool written, IGpuExportableImage? image, ulong writtenValue);
}
// The exported output. A node given an export renders its default output's storage as one image the export creates, at
// the export's extent, and hands it back in External layout after every frame, taken back from its reader before the
// submission that writes it and completed for the reader after, so the reader's own device waits for the write.
public sealed partial class ShaderPipelineRenderNode {
    private IShaderPipelineOutputExport? m_export;

    /// <summary>Gets or sets the export the node renders its default output into, or <see langword="null"/> to render into
    /// images of its own. Setting another export rebuilds the installed graph beside it at the export's extent, as a
    /// resize does.</summary>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    public IShaderPipelineOutputExport? Export {
        get => m_export;
        set {
            ObjectDisposedException.ThrowIf(
                condition: m_disposed,
                instance: this
            );

            if (ReferenceEquals(
                objA: m_export,
                objB: value
            )) {
                return;
            }

            m_export = value;

            if (value is not null) {
                Resize(
                    height: value.Height,
                    width: value.Width
                );
            }

            ForgetRefusal();
            m_resizePending = (m_ready && (m_pipeline is not null));
        }
    }

    // Whether a planned storage is the one an export takes: the default output's, when the node has an export.
    private bool IsExported(ShaderPipelinePlan plan, ShaderPipelinePlannedStorage storage) => (
        (m_export is not null) &&
        (plan.FindResource(name: plan.DefaultOutput) is { } output) &&
        (output.Storage == storage.Index)
    );

    /// <summary>Gets the image the installed graph renders its exported output into, which the reader on another device
    /// opens by its shared handles, or <see langword="null"/> when the node exports nothing or has installed no graph
    /// since it was given its export. A graph built again renders into another image.</summary>
    public IGpuExportableImage? ExportedImage {
        get {
            if (
                (m_export is null) ||
                !m_ready ||
                (m_pipeline?.Plan.FindResource(name: m_pipeline.Plan.DefaultOutput) is not { } output) ||
                (output.Storage >= m_resources.Length)
            ) {
                return null;
            }

            return (m_resources[output.Storage].Images?[0] as IGpuExportableImage);
        }
    }
}
