namespace Puck.Shaders;

// Presenting the selected output again without rendering a frame: a paused node whose output selection moved.
public sealed partial class ShaderPipelineRenderNode {
    private void PresentSelectedOutput() {
        WaitAll();
        HoldLeases();
        var slot = ((int)((m_frame - 1) % m_inFlight));
        var selected = m_resourceLookup[(m_selectedOutput ?? m_pipeline!.Plan.DefaultOutput)];

        PrepareFrameCommands();
        var commands = m_commands;
        var command = BeginFrameCommands(slot: slot);

        commands.Clear();
        var previewed = NeedsPreview(spec: selected.Spec);

        if (previewed) {
            m_preview!.Record(
                PreviewSource(
                    selected: selected,
                    slot: slot
                ),
                ResolveImage(
                    selected,
                    selected.Spec.Name,
                    HistoryIndex(previous: false, resource: selected, slot: slot)
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
        EndRecordedList(command: command);
        commands.Add(item: command);
        SubmitCounted(
            commands: commands,
            slot: slot
        );
        m_frameLeases.MoveTo(destination: m_slots[slot].Leases);
        // A paused selection has no cadence frame to settle. Publish it after its submission reaches the queue.
        m_slots[slot].Fence!.HandOff();
        if (previewed) { m_preview!.Submitted(slot: slot); }
        var publishedName = PublishSelectedOutput(selected: selected, slot: slot);
        if (selected.Spec.IsExternal && m_externalImages.TryGetValue(key: publishedName, value: out var bound)) {
            m_publishedLabel = (m_publishedLabel.IsClassified ? m_publishedLabel.Union(other: bound.Image.Label) : bound.Image.Label);
        }
        m_outputRefreshRequested = false;
    }
}
