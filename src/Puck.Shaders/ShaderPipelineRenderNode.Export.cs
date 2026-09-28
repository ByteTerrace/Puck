using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>An image another device reads, which a node copies its default output into after rendering it, such as a
/// camera view a probe's kernel host samples. The node creates the image through it when it allocates a graph, renders at
/// its extent whatever extent it is asked for, copies into it only on a frame the reader has released it, leaves it in
/// <see cref="GpuImageLayout.External"/> between frames, and ends every write it began.</summary>
public interface IShaderPipelineOutputExport {
    /// <summary>Gets the image's width, in pixels, the width the node renders at.</summary>
    uint Width { get; }
    /// <summary>Gets the image's height, in pixels.</summary>
    uint Height { get; }

    /// <summary>Creates the image on the node's device, in the default output's format. The node owns it from here and
    /// disposes it with the graph it was allocated for; a graph the node builds again creates another.</summary>
    /// <param name="device">The node's device.</param>
    /// <returns>The image.</returns>
    IGpuExportableImage Create(IGpuDeviceContext device);
    /// <summary>Returns whether the reader has released the image, so the node may write it this frame. A node the reader
    /// still holds it from renders its graph as usual and copies nothing.</summary>
    /// <returns><see langword="true"/> when the node may write the image, beginning a write it ends with
    /// <see cref="EndWrite"/>.</returns>
    bool TryBeginWrite();
    /// <summary>Ends a write <see cref="TryBeginWrite"/> began.</summary>
    /// <param name="written">Whether the frame's submission wrote the image.</param>
    /// <param name="image">The image the node copies into, or <see langword="null"/> when it has none.</param>
    /// <param name="writtenValue">The shared fence value the write signals (<see cref="IGpuExportableImage.CompleteWrite"/>),
    /// or zero when nothing was written or the write finished before it was handed off.</param>
    void EndWrite(bool written, IGpuExportableImage? image, ulong writtenValue);
}
// The exported output. A node given an export renders its default output into images of its own, one per frame slot,
// which it publishes and its readers sample like any output, and copies each written frame into the one image the export
// creates, in its own counted pass (ExportCopyPass) at the end of the frame's submission. The export image is taken back
// from its reader before that submission and completed for the reader after, so the reader's own device waits for the
// copy, and nothing on the node's device ever samples it.
public sealed partial class ShaderPipelineRenderNode {
    /// <summary>The label an exported output's copy counts under (<see cref="PassLabels"/>), as
    /// <see cref="WorkClass.Deterministic"/> work: one image copy and its barriers on each frame the reader has released the
    /// image, and nothing on a frame it has not.</summary>
    public const string ExportCopyPass = "export copy";

    private IShaderPipelineOutputExport? m_export;
    private int m_exportCopyPass = -1;

    /// <summary>Gets or sets the export the node copies its default output into, or <see langword="null"/> to export
    /// nothing. Setting another export rebuilds the installed graph beside it at the export's extent, as a resize
    /// does.</summary>
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

    // Whether a planned storage is the one an export copies: the default output's image, when the node has an export.
    private bool IsExported(ShaderPipelinePlan plan, ShaderPipelinePlannedStorage storage) => (
        (m_export is not null) &&
        (storage.Declaration.Kind == ShaderPipelineResourceKind.Image) &&
        !storage.Declaration.IsExternal &&
        (plan.FindResource(name: plan.DefaultOutput) is { } output) &&
        (output.Storage == storage.Index)
    );
    // The bytes of the image an export creates for a plan's default output, or zero when the node exports nothing.
    private ulong ExportBytes(ShaderPipelinePlan plan) => (((m_export is { } export) && plan.Storages.Any(predicate: storage => IsExported(
        plan: plan,
        storage: storage
    )))
        ? ImageBytes(
            format: plan.FindResource(name: plan.DefaultOutput)!.Declaration.Format,
            height: export.Height,
            width: export.Width
        )
        : 0UL);
    // Creates the export's image beside the default output's storage, which must match it in format and extent, since the
    // copy moves the whole image.
    private void CreateExportImage(RuntimeResource resource, (uint Width, uint Height) extent) {
        var export = m_export!;
        var image = export.Create(device: m_device);

        resource.Export = image;
        resource.ExportOwner = export;

        var format = ParseFormat(format: resource.Spec.Format);

        if (
            (image.Format != format) ||
            (image.Width != extent.Width) ||
            (image.Height != extent.Height)
        ) {
            throw new InvalidDataException(message: $"The export's image is {image.Format} {image.Width}x{image.Height}, but the default output '{resource.Spec.Name}' it copies is {format} {extent.Width}x{extent.Height}.");
        }
    }
    // The storage whose export image the installed graph copies into for the current export, or null when it has none.
    private RuntimeResource? ExportedResource() {
        if (
            (m_export is null) ||
            !m_ready ||
            (m_pipeline?.Plan.FindResource(name: m_pipeline.Plan.DefaultOutput) is not { } output) ||
            (output.Storage >= m_resources.Length)
        ) {
            return null;
        }

        var resource = m_resources[output.Storage];

        return (ReferenceEquals(
            objA: resource.ExportOwner,
            objB: m_export
        )
            ? resource
            : null);
    }

    /// <summary>Gets the image the installed graph copies its default output into, which the reader on another device
    /// opens by its shared handles, or <see langword="null"/> when the node exports nothing or has installed no graph since
    /// it was given its export. A graph built again copies into another image. The node never publishes or samples
    /// it.</summary>
    public IGpuExportableImage? ExportedImage => ExportedResource()?.Export;

    // Copies this frame's default output into the export's image, inside the export's counted pass: the output moves to
    // the copy's source layout (the presentation after moves it on), and the image, from the reader's handoff layout or,
    // on its first write, from nothing, to the copy's destination layout and back to the handoff layout.
    private void RecordExportCopy(nint command, IGpuRecorder recorder, int slot, IGpuExportableImage target) {
        var exported = ExportedResource()!;

        var (source, name, instance) = PublicationOf(
            selected: exported,
            slot: slot
        );
        var image = ResolveImage(
            index: instance,
            name: name,
            resource: source
        );

        if (
            (image.Format != target.Format) ||
            (image.Width != target.Width) ||
            (image.Height != target.Height)
        ) {
            throw new InvalidOperationException(message: $"The default output '{name}' is {image.Format} {image.Width}x{image.Height} this frame, but the export's image it copies into is {target.Format} {target.Width}x{target.Height}.");
        }

        m_work.EnterPass(pass: m_exportCopyPass);
        BeginTiming(command: command, pass: m_exportCopyPass, slot: slot);

        try {
            RecordBarrier(
                barrier: Present(
                    instance: instance,
                    resource: source,
                    use: new ShaderPipelineAccessState(
                        Access: GpuAccess.TransferRead,
                        Layout: GpuImageLayout.TransferSource,
                        Stage: GpuStage.Transfer
                    )
                ),
                command: command,
                instance: instance,
                recorder: recorder,
                resource: source
            );
            recorder.TransitionImageLayout(
                command,
                target.ImageHandle,
                (exported.ExportWritten
                    ? GpuImageLayout.External
                    : GpuImageLayout.Undefined),
                GpuImageLayout.TransferDestination,
                GpuAccess.None,
                GpuAccess.TransferWrite,
                GpuStage.TopOfPipe,
                GpuStage.Transfer
            );
            recorder.CopyImage(
                command,
                image.ImageHandle,
                target.ImageHandle,
                target.Width,
                target.Height
            );
            recorder.TransitionImageLayout(
                command,
                target.ImageHandle,
                GpuImageLayout.TransferDestination,
                GpuImageLayout.External,
                GpuAccess.TransferWrite,
                GpuAccess.None,
                GpuStage.Transfer,
                GpuStage.Transfer
            );
            exported.ExportWritten = true;
            EndTiming(command: command, pass: m_exportCopyPass, slot: slot);
        } finally {
            m_work.LeavePass();
        }
    }
}
