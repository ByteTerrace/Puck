using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

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
        // The frame's further lists (FrameSlot.Lists), in the order taken, and whether each is still open. Only the
        // recording's walk takes one; a lane ends its own, writing only its own element.
        public int ExtraCount;
        private nint[] m_extraLists = [];
        private bool[] m_extraOpen = [];
        // When the job's phases began and ended, in Stopwatch ticks. A recording run on another thread may begin well after
        // its preparation ended and end well before its commit begins.
        public long PreparedAt;
        public long PrepareEndedAt;
        public long RecordedAt;
        public long RecordEndedAt;
        public long CommittedAt;

        // Takes a further list, not yet begun, so not yet open: only the walk takes one.
        public void TakeExtra(nint list) {
            ArrayCapacity.EnsureLength(array: ref m_extraLists, required: (ExtraCount + 1));
            ArrayCapacity.EnsureLength(array: ref m_extraOpen, required: (ExtraCount + 1));
            m_extraLists[ExtraCount] = list;
            m_extraOpen[ExtraCount] = false;
            ExtraCount++;
        }
        // Marks a further list open once its begin returned; the thread that began it writes only its own element.
        public void MarkOpen(int extra) => m_extraOpen[extra] = true;
        // Forgets a list as open once its end is attempted: a further list by its place (`extra`), else the slot's frame
        // or copy list.
        public void CloseList(nint list, int extra) {
            if (extra >= 0) {
                m_extraOpen[extra] = false;
                return;
            }
            if (Frame == list) { Frame = 0; }
            if (Copies == list) { Copies = 0; }
        }
        // The place of a further list among the frame's, or -1 for the slot's frame or copy list.
        public int ExtraOf(nint list) {
            for (var extra = 0; (extra < ExtraCount); extra++) {
                if (m_extraLists[extra] == list) { return extra; }
            }
            return -1;
        }
        public bool IsOpen(int extra) => m_extraOpen[extra];
        public nint ExtraList(int extra) => m_extraLists[extra];
        public bool Ended {
            get {
                for (var extra = 0; (extra < ExtraCount); extra++) {
                    if (m_extraOpen[extra]) { return false; }
                }
                return ((Frame == 0) && (Copies == 0));
            }
        }
    }

    // The frame between BeginFrame and EndFrame (or AbandonFrame): what Produce prepared, and what the recording left for
    // the commit. Only the recording itself (RecordPendingFrame) may run on another thread; it writes nothing here but
    // Recorded and Error, which the frame thread reads after the join that ends the fork.
    private OpenFrame m_open;

    private struct OpenFrame {
        public bool Open;
        // Whether Produce prepared a record job; otherwise Surface is the frame's outcome.
        public bool Pending;
        public bool Recorded;
        public FrameContext Context;
        public FrameJob Job;
        public IGpuExportableImage? Exported;
        public bool ExportWritten;
        public ulong ExportValue;
        public Surface Surface;
        // What the recording threw, and what a pass's preparation threw, which the recording throws once its lists began.
        public ExceptionDispatchInfo? Error;
        public ExceptionDispatchInfo? PrepareFailure;
        // The operator's armed recording fault this recording fails partway (GpuCreationFaults.TakeRecordingDue), or zero.
        public long RecordingFault;
    }

    // The passes the latest recording recorded, as against stood or skipped, and those of them it recorded in its walk
    // rather than queued for lanes.
    internal int LastPassesRecorded { get; private set; }
    internal int LastPassesRecordedSerially => m_passesRecordedSerially;

    // A frame in three steps, which ProduceFrame takes in a row and the runtime takes across a wave of instances: BeginFrame
    // on the frame thread prepares it (everything up to the record job, which takes the slot, its leases and its
    // checkpoint), RecordPendingFrame records the slot's lists on whichever thread the runtime's fork runs it, and EndFrame
    // on the frame thread commits it, or abandons it when its recording failed. Returns whether a record job is pending;
    // either way the frame is open until EndFrame or AbandonFrame closes it.
    internal bool BeginFrame(in FrameContext context) {
        if (m_open.Open) {
            throw new InvalidOperationException(message: $"Instance '{m_name}' began a frame while its previous frame was neither ended nor abandoned.");
        }
        // A caller that rendered the previous frame inside a batch settles it after the flush; one that did not is settled
        // here, before anything reads the history that frame committed.
        SettleHandOver();
        var latch = (context.Latch.IsLatched ? context.Latch : DeliveryLatch.Capture());
        var latched = (context.Latch.IsLatched ? context : (context with { Latch = latch }));

        m_frameLatch = latch;
        m_open.Open = true;
        try {
            m_open.Surface = Produce(context: in latched);
            return m_open.Pending;
        } catch {
            CloseFrame(abandon: true);
            throw;
        }
    }
    // Records the open frame's record job. It touches only the instance and the recording domains its passes name
    // (IRenderGraphPackageRecorder.RecordingDomain), and never throws: what its recording throws is kept for EndFrame,
    // which handles it on the frame thread exactly as an inline recording's failure.
    internal void RecordPendingFrame() {
        if (!m_open.Pending || m_open.Recorded) { return; }
        m_open.Recorded = true;
        try {
            RecordFrame(context: in m_open.Context, job: in m_open.Job);
        } catch (Exception error) {
            m_open.Error = ExceptionDispatchInfo.Capture(source: error);
        }
    }
    // Commits the open frame, or abandons its recording when it failed: a failed recording keeps the last image and says
    // why; anything else (a device loss) abandons the frame and propagates to the host's recovery. Returns the node's
    // published surface.
    internal Surface EndFrame() {
        if (!m_open.Open) {
            throw new InvalidOperationException(message: $"Instance '{m_name}' ended a frame it had not begun.");
        }
        if (!m_open.Pending) {
            var surface = m_open.Surface;

            CloseFrame(abandon: false);
            return surface;
        }
        try {
            if (!m_open.Recorded) {
                throw new InvalidOperationException(message: $"Instance '{m_name}' ended a frame whose record job never ran.");
            }
            if (m_open.Error is null) {
                EmitOwedLanes();
                m_open.Error = LaneError();
            }
            if (m_open.Error is { } failed) {
                if (!ContainsRecordFailure(error: failed.SourceException)) {
                    failed.Throw();
                }
                AbandonRecording();
                AbortCadenceFrame();
                NoteRecordFailure(error: failed.SourceException);
                CloseFrame(abandon: false);
                return m_lastSurface;
            }

            var committed = CommitFrame(
                exportValue: out m_open.ExportValue,
                exportWritten: out m_open.ExportWritten,
                job: in m_open.Job
            );

            CloseFrame(abandon: false);
            return committed;
        } catch {
            CloseFrame(abandon: true);
            throw;
        }
    }
    // Adds the state the open frame's recording shares with other instances' recordings: each package pass's recording
    // domain (its package factory unless the recorder names a narrower one) and each host buffer region bound to the
    // instance, which another instance may hold too. Read on the frame thread after BeginFrame prepared the frame.
    internal void AddRecordingDomains(List<object> domains) {
        foreach (var pass in m_passes) {
            if (pass.Package is { } package) {
                domains.Add(item: (package.RecordingDomain ?? (object?)pass.PackageFactory ?? package));
            }
        }
        foreach (var region in m_externalRegions.Values) {
            domains.Add(item: region);
        }
    }
    // Gives up the open frame without committing it, when another instance's failure ends the runtime's frame first:
    // nothing it recorded reaches the queue, its cadence rolls back and its leases retire unsampled.
    internal void AbandonFrame() {
        if (m_open.Open) {
            CloseFrame(abandon: true);
        }
    }
    // Closes the open frame: abandons what it recorded when asked, ends the export write it began (written only by a
    // committed copy, unless the batch owes its completion), and retires every lease the frame bound but no submission
    // holds.
    private void CloseFrame(bool abandon) {
        try {
            if (abandon) {
                AbandonRecording();
                AbortCadenceFrame();
            }
        } finally {
            var exported = m_open.Exported;
            var written = m_open.ExportWritten;
            var value = m_open.ExportValue;

            m_open = default;
            try {
                if ((exported is not null) && (m_exportOwed is null)) {
                    m_export!.EndWrite(
                        image: exported,
                        written: written,
                        writtenValue: value
                    );
                }
            } finally {
                m_frameLatch = default;
                ReleaseUnheldLeases();
            }
        }
    }

    /// <summary>Gets the reason the node's latest frame recording failed and was abandoned, or <see langword="null"/>
    /// once a later recording committed. The node kept its last published image for that frame.</summary>
    public string? LastRecordError { get; private set; }
    /// <summary>Gets the host time the node's latest committed recording took, in <see cref="Stopwatch"/> ticks: its walk
    /// over the passes and every lane that emitted its passes, summed.</summary>
    public long LastRecordTicks { get; private set; }
    // The latest committed recording's walk alone, and its longest single piece: the walk or one lane.
    internal long LastWalkTicks { get; private set; }
    internal long LastLongestRecordTicks { get; private set; }
    // The lanes the latest committed recording emitted its passes in.
    private int m_lastLanes;
    /// <summary>Gets the number of record jobs the node has committed.</summary>
    public long RecordJobs { get; private set; }

    // The node's committed jobs' phases in Stopwatch ticks: the latest job's, the sum over every job and the longest
    // recording. The process-wide RenderRecordWork counts the same jobs; this is the per-instance account of them.
    private long m_lastPrepareTicks;
    private long m_lastCommitTicks;
    private long m_totalPrepareTicks;
    private long m_totalRecordTicks;
    private long m_totalCommitTicks;
    private long m_longestRecordTicks;

    /// <summary>Appends the node's record jobs' host time, in microseconds, as one run of <c>key=value</c> pairs: the
    /// jobs committed, the latest job's prepare, record and commit time, its walk over the passes, the lanes it emitted
    /// passes in and its longest single piece (the walk or a lane), the mean of each phase over every job, and the
    /// longest single recording. The one reading the live budget's instance rows and the graph inspection share, so the
    /// instance behind <c>render.record</c>'s critical path is named wherever the engine already reports a node.
    /// Wall-clock readings: pacing counts, never compared between runs.</summary>
    /// <param name="into">The builder the pairs are appended to, each after a separating space.</param>
    public void AppendRecordTimes(StringBuilder into) {
        ArgumentNullException.ThrowIfNull(into);

        var jobs = Math.Max(val1: RecordJobs, val2: 1L);

        _ = into.Append(value: " record-jobs=").Append(value: RecordJobs)
            .Append(value: " last-prepare-us=").Append(value: Microseconds(ticks: m_lastPrepareTicks))
            .Append(value: " last-record-us=").Append(value: Microseconds(ticks: LastRecordTicks))
            .Append(value: " last-walk-us=").Append(value: Microseconds(ticks: LastWalkTicks))
            .Append(value: " last-lanes=").Append(value: m_lastLanes)
            .Append(value: " last-longest-piece-us=").Append(value: Microseconds(ticks: LastLongestRecordTicks))
            .Append(value: " last-commit-us=").Append(value: Microseconds(ticks: m_lastCommitTicks))
            .Append(value: " mean-prepare-us=").Append(value: Microseconds(ticks: (m_totalPrepareTicks / jobs)))
            .Append(value: " mean-record-us=").Append(value: Microseconds(ticks: (m_totalRecordTicks / jobs)))
            .Append(value: " mean-commit-us=").Append(value: Microseconds(ticks: (m_totalCommitTicks / jobs)))
            .Append(value: " longest-record-us=").Append(value: Microseconds(ticks: m_longestRecordTicks));
    }
    private static long Microseconds(long ticks) => ((ticks * 1_000_000L) / Stopwatch.Frequency);

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
        m_recording.ExtraCount = 0;
    }
    // Ends a list the recording began. Once the end is attempted the list is no longer recording, whatever the end
    // reports, so it is forgotten first.
    private void EndRecordedList(nint command) {
        m_recording.CloseList(extra: m_recording.ExtraOf(list: command), list: command);
        m_gpu.Recorder.EndCommandBuffer(commandBufferHandle: command);
    }
    // Requires the recording's lists to be ended before their submission and releases the slot's lists to the queue.
    private void HandOverRecording() {
        if (!m_recording.Active || !m_recording.Ended) {
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
        for (var extra = 0; (extra < m_recording.ExtraCount); extra++) {
            if (!m_recording.IsOpen(extra: extra)) { continue; }

            var list = m_recording.ExtraList(extra: extra);

            m_recording.CloseList(extra: extra, list: list);
            if (!TryEnd(command: list)) {
                var lists = m_slots[slot].Lists;

                lists[extra].Dispose();
                lists[extra] = CommandsOf(gpu: m_gpu, list: (extra + 1), owner: m_name, slot: slot);
            }
        }
        m_laneCount = 0;
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
        var (laneTicks, longestLane) = LaneTicks();

        LastRecordError = null;
        LastWalkTicks = (m_recording.RecordEndedAt - m_recording.RecordedAt);
        LastRecordTicks = (LastWalkTicks + laneTicks);
        LastLongestRecordTicks = Math.Max(val1: LastWalkTicks, val2: longestLane);
        m_lastLanes = m_laneCount;
        m_lastCommitTicks = (now - m_recording.CommittedAt);
        m_lastPrepareTicks = (m_recording.PrepareEndedAt - m_recording.PreparedAt);
        RecordJobs++;
        m_totalCommitTicks += m_lastCommitTicks;
        m_totalPrepareTicks += m_lastPrepareTicks;
        m_totalRecordTicks += LastRecordTicks;
        m_longestRecordTicks = Math.Max(val1: m_longestRecordTicks, val2: LastRecordTicks);
        RenderRecordWork.Process.Job(
            commitTicks: m_lastCommitTicks,
            prepareTicks: m_lastPrepareTicks,
            recordTicks: LastRecordTicks
        );
    }
}
