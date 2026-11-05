using System.Diagnostics;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// A frame's record job: preparation reserves a slot (its fence waited, its leases retired, its regions bound, the
// cadence checkpointed), recording fills the slot's command lists, and commit submits them and publishes what they wrote.
// Every path that records into a slot's lists (a frame, a re-presented selection, a capture pin) begins them through
// BeginFrameCommands and hands them over through SubmitCounted, so the slot's lists belong to one recording at a time:
// the recording owns them from their begin until they are submitted or abandoned, and an abandoned recording closes or
// replaces them so the slot's next begin finds them reusable.
public sealed partial class ShaderPipelineRenderNode {
    // The recording that owns the slot's lists, from BeginFrameCommands until SubmitCounted or AbandonRecording.
    private readonly FrameRecording m_recording = new();

    private sealed class FrameRecording {
        public bool Active;
        public int Slot;
        // The slot's frame list and region-copy list while open; zero once ended or never begun.
        public nint Frame;
        public nint Copies;
        // When the job's phases began, in Stopwatch ticks.
        public long PreparedAt;
        public long RecordedAt;
        public long CommittedAt;
    }

    /// <summary>Gets the reason the node's latest frame recording failed and was abandoned, or <see langword="null"/>
    /// once a later recording committed. The node kept its last published image for that frame.</summary>
    public string? LastRecordError { get; private set; }
    /// <summary>Gets the duration of the node's latest committed recording, in <see cref="Stopwatch"/> ticks.</summary>
    public long LastRecordTicks { get; private set; }

    // Takes the slot's lists for a new recording. A node records one frame at a time, so a recording still active is a
    // defect in its caller, refused before any list is touched.
    private void BeginRecording(int slot) {
        if (m_recording.Active) {
            throw new InvalidOperationException(message: $"Instance '{m_name}' began a recording in slot {slot} while slot {m_recording.Slot}'s was neither submitted nor abandoned.");
        }
        m_recording.Active = true;
        m_recording.Slot = slot;
        m_recording.Frame = 0;
        m_recording.Copies = 0;
    }
    // Ends a list the recording began. Once the end is attempted the list is no longer recording, whatever the end
    // reports, so it is forgotten first.
    private void EndRecordedList(nint command) {
        if (m_recording.Frame == command) { m_recording.Frame = 0; }
        if (m_recording.Copies == command) { m_recording.Copies = 0; }
        m_gpu.Recorder.EndCommandBuffer(commandBufferHandle: command);
    }
    // Requires the recording's lists to be ended before their submission and releases the slot's lists to the queue.
    private void HandOverRecording() {
        if (!m_recording.Active || (m_recording.Frame != 0) || (m_recording.Copies != 0)) {
            throw new InvalidOperationException(message: $"Instance '{m_name}' submitted a recording whose lists are not all ended.");
        }
        m_recording.Active = false;
    }
    // Gives up a recording that failed before its submission: nothing it recorded reaches the queue. Each list still
    // open is ended so the slot's next begin can reset it; a list that cannot be ended takes its pool with it, replaced
    // by a fresh one, since the slot's fence was waited before the recording began and nothing pending uses it.
    private void AbandonRecording() {
        if (!m_recording.Active) { return; }

        var slot = m_recording.Slot;

        m_recording.Active = false;
        if (m_recording.Frame is not 0 and var frame) {
            m_recording.Frame = 0;
            if (!TryEnd(command: frame)) {
                m_slots[slot].Commands?.Dispose();
                m_slots[slot].Commands = CommandsOf(gpu: m_gpu, owner: m_name, slot: slot);
            }
        }
        if (m_recording.Copies is not 0 and var copies) {
            m_recording.Copies = 0;
            if (!TryEnd(command: copies) && (m_copyPools is { } pools)) {
                pools[slot].Dispose();
                pools[slot] = m_gpu.CommandPoolFactory.Create(name: new GpuObjectName(index: slot, owner: m_name, part: "region copies"));
            }
        }
        RenderRecordWork.Process.Abandoned();
    }
    private bool TryEnd(nint command) {
        try {
            m_gpu.Recorder.EndCommandBuffer(commandBufferHandle: command);

            return true;
        } catch (Exception error) when (error is not DeviceLostException) {
            return false;
        }
    }
    // A recording that failed before submission is the node's outcome for the frame, not the host's: the node keeps its
    // last image and says why, and the next frame records again. A device loss is the host's to recover from.
    private bool ContainsRecordFailure(Exception error) => (m_recording.Active && (error is not DeviceLostException));
    private void NoteRecordFailure(Exception error) {
        var reason = $"its frame recording failed and was abandoned: {error.Message}";

        if (!string.Equals(a: reason, b: LastRecordError, comparisonType: StringComparison.Ordinal)) {
            Console.Error.WriteLine(value: $"[render.record] instance '{m_name}' {reason}");
        }
        LastRecordError = reason;
    }
    private void NoteCommitted() {
        var now = Stopwatch.GetTimestamp();

        LastRecordError = null;
        LastRecordTicks = (m_recording.CommittedAt - m_recording.RecordedAt);
        RenderRecordWork.Process.Job(
            commitTicks: (now - m_recording.CommittedAt),
            prepareTicks: (m_recording.RecordedAt - m_recording.PreparedAt),
            recordTicks: LastRecordTicks
        );
    }
}
