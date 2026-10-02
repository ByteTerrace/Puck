using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

// Presenting the selected output again without rendering a frame: a paused node whose output selection moved.
public sealed partial class ShaderPipelineRenderNode {
    private void PresentSelectedOutput() {
        WaitAll();
        HoldLeases();
        var slot = ((int)((m_frame - 1) % m_inFlight));
        var selected = m_resourceLookup[(m_selectedOutput ?? m_pipeline!.Plan.DefaultOutput)];

        m_latestSlot = slot;
        var commands = m_commands;
        var command = BeginFrameCommands(slot: slot);

        commands.Clear();
        if (NeedsPreview(spec: selected.Spec)) {
            m_preview!.Record(
                PreviewSource(
                    selected: selected,
                    slot: slot
                ),
                ResolveImage(
                    selected,
                    selected.Spec.Name,
                    slot
                ),
                slot,
                command
            );
        }
        FinalizeOutputs(
            command: command,
            exported: null,
            slot: slot
        );
        m_gpu.Recorder.EndCommandBuffer(commandBufferHandle: command);
        commands.Add(item: command);
        SubmitCounted(
            commands: commands,
            fence: m_slots[slot].Fence!
        );
        m_frameLeases.MoveTo(destination: m_slots[slot].Leases);
        Publish(surface: Output(slot: slot));
        m_outputRefreshRequested = false;
    }
    private RuntimeResource PresentationResource(RuntimeResource selected) {
        var selectedFormat = ParseFormat(format: selected.Spec.Format);

        if (Surface.IsImageFormat(format: selectedFormat)) {
            return selected;
        }
        throw new InvalidDataException(message: $"Selected output '{selected.Spec.Name}' is {selectedFormat}, which no surface carries.");
    }
}
