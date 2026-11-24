using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// A frame's passes decided in order and emitted side by side. The recording (RecordPendingFrame) walks the passes once, in
// order, on one thread: it decides every pass (skip, stand, cadence, the barrier tracker, the work ledger's pass, a
// package's decision), records every pass that cannot be emitted apart into the frame's current serial list, and queues
// each package pass whose recorder is an IRenderGraphPackageEmitter as an emission. Consecutive emissions form a run;
// once the walk ends each run is cut into lanes by the weight its passes' emissions took last frame, and each lane emits
// its passes into a command list of its own (EmitLane), on whichever thread the runtime's fork runs it. The frame's lists
// are submitted in the order the walk made them, so the queue sees every pass in pass order whichever thread recorded it.
//
// The invariant the lanes rest on: an emission writes nothing another emission or the decision reads. One door enforces
// the node's side: everything a pass changes in the node (the tracker's overrides, history cursors, aliases, cadence,
// the ledger's pass states, the timing flags, the readback arming) is changed in DecidePackage, before any lane runs, and
// a lane reads only the emission's captured values (the resolved barrier targets, a copy of the pass block, the resolved
// arguments) and the pass's resolved ports, which no decision of the frame rewrites after its own. The ledger counts a
// lane's calls under its pass through GpuWorkLedger.EnterLane. A recorder's side is its own contract
// (IRenderGraphPackageEmitter.Emit), held by review.
//
// GPU lifetimes: each further list of a frame takes the slot's next command pool (FrameSlot.Lists), so every list in flight
// has an allocator of its own, and a slot's pools are reset only by the slot's next recording, after WaitSlot waited the
// slot's previous submission.
public sealed partial class ShaderPipelineRenderNode {
    // A lane is worth cutting off a run only when it holds at least this much of last frame's emission time: below it, the
    // fork's hand-off and the extra list cost more than the side-by-side recording saves.
    private static readonly long MinLaneTicks = (Stopwatch.Frequency / 20_000L);

    // How many lanes one frame's runs may be cut into: the job pool's threads when a runtime records the node, one
    // otherwise, which emits each emitter's pass in order into the frame's serial list as it is decided.
    internal int EmitLaneLimit { get; set; } = 1;

    private readonly List<PassEmission> m_emissions = [];
    private readonly List<EmitOp> m_emitOps = [];
    private byte[] m_emitBlocks = [];
    private int m_emitBlockBytes;
    private readonly List<FrameSegment> m_segments = [];
    private readonly List<EmissionLane> m_lanes = [];
    private int m_laneCount;
    // The frame's current serial list: where a pass that is not emitted apart records, or zero while a run is open.
    private nint m_serialList;
    // Whether the frame being recorded queues its emitters' passes for lanes.
    private bool m_lanesEnabled;
    // Each pass's latest emission time, by its index in the installed graph, which the next frame cuts its runs by.
    private long[] m_passEmitTicks = [];
    // The passes the recording recorded into its serial lists, as against those it queued for lanes.
    private int m_passesRecordedSerially;

    // One emitter pass's decided frame: where its barriers and readback copies lie in m_emitOps, its pass block's copy in
    // m_emitBlocks, and the values its recording carries.
    private struct PassEmission {
        public int Pass;
        public int PreStart;
        public int PreCount;
        public int PostStart;
        public int PostCount;
        public int BlockStart;
        public int BlockLength;
        // Whether the decision drew, so the recorder emits; otherwise only the pass's reads' barriers are recorded.
        public bool Emits;
        // Whether the frame's kernel counters are cleared ahead of this pass, the first the frame records.
        public bool ClearsCounters;
        public bool MayStandIn;
        public IGpuBuffer? Arguments;
        public GpuKernelCounterRow? WorkCounters;
    }
    // One command an emission records apart from its recorder: a planned barrier, or a readback copy and its barrier,
    // resolved to the native objects at the decision.
    private struct EmitOp {
        public ShaderPipelineBarrier Barrier;
        public nint Target;
        public bool Copies;
        public RenderGraphBufferReadback Readback;
        public nint Source;
    }
    // A stretch of the frame's lists: a serial list (RunCount zero), or a run of emissions whose first lane continues the
    // serial list before it.
    private readonly record struct FrameSegment(nint List, int Extra, int RunStart, int RunCount);
    // One lane's emissions and the list it records them into and ends.
    private sealed class EmissionLane {
        public int First;
        public int Count;
        public nint List;
        // The list's place among the frame's further lists, or -1 for the slot's first list.
        public int Extra;
        public bool Emitted;
        public long Ticks;
        public ExceptionDispatchInfo? Error;
    }

    // Records the planned barrier of a pass's access, or a readback copy, wherever the pass's commands go: into a command
    // list now, or into the pass's emission for its lane.
    private interface IRecordSink {
        void Barrier(ShaderPipelineBarrier barrier, RuntimeResource resource, int instance);
        void Copy(in RenderGraphBufferReadback readback, IGpuBuffer source);
    }
    private readonly struct CommandSink(ShaderPipelineRenderNode node, nint command, IGpuRecorder recorder) : IRecordSink {
        public void Barrier(ShaderPipelineBarrier barrier, RuntimeResource resource, int instance) =>
            EmitBarrier(barrier: barrier, command: command, recorder: recorder, target: node.BarrierTarget(barrier: barrier, instance: instance, resource: resource));
        public void Copy(in RenderGraphBufferReadback readback, IGpuBuffer source) => EmitCopy(command: command, readback: in readback, recorder: recorder, source: source.BufferHandle);
    }
    private readonly struct CaptureSink(ShaderPipelineRenderNode node) : IRecordSink {
        public void Barrier(ShaderPipelineBarrier barrier, RuntimeResource resource, int instance) {
            if (barrier.Kind != ShaderPipelineBarrierKind.None) {
                node.m_emitOps.Add(item: new EmitOp { Barrier = barrier, Target = node.BarrierTarget(barrier: barrier, instance: instance, resource: resource) });
            }
        }
        public void Copy(in RenderGraphBufferReadback readback, IGpuBuffer source) =>
            node.m_emitOps.Add(item: new EmitOp { Copies = true, Readback = readback, Source = source.BufferHandle });
    }

    // The image or buffer a barrier names, resolved now: a history cursor a later pass's decision moves must not move the
    // barrier with it.
    private nint BarrierTarget(ShaderPipelineBarrier barrier, RuntimeResource resource, int instance) => barrier.Kind switch {
        ShaderPipelineBarrierKind.Image => ResolveImage(resource, resource.Spec.Name, instance).ImageHandle,
        ShaderPipelineBarrierKind.Buffer => ResolveBuffer(resource, resource.Spec.Name, instance).BufferHandle,
        _ => 0,
    };
    private static void EmitBarrier(ShaderPipelineBarrier barrier, nint target, nint command, IGpuRecorder recorder) {
        switch (barrier.Kind) {
            case ShaderPipelineBarrierKind.Image:
                recorder.TransitionImageLayout(command, target, barrier.OldLayout, barrier.NewLayout, barrier.SourceAccess, barrier.DestinationAccess,
                    barrier.SourceStage, barrier.DestinationStage);
                break;
            case ShaderPipelineBarrierKind.Memory:
                recorder.MemoryBarrier(command, barrier.SourceAccess, barrier.DestinationAccess, barrier.SourceStage, barrier.DestinationStage);
                break;
            case ShaderPipelineBarrierKind.Buffer:
                recorder.TransitionBuffer(command, target, barrier.SourceAccess, barrier.DestinationAccess, barrier.SourceStage, barrier.DestinationStage);
                break;
        }
    }
    private static void EmitCopy(in RenderGraphBufferReadback readback, nint source, nint command, IGpuRecorder recorder) {
        recorder.CopyBuffer(commandBufferHandle: command, destinationBufferHandle: readback.Destination.BufferHandle,
            sourceBufferHandle: source, sizeBytes: readback.SizeBytes, sourceOffsetBytes: readback.SourceOffsetBytes,
            destinationOffsetBytes: readback.DestinationOffsetBytes);
        GpuReadbackCopy.MakeHostVisible(recorder: recorder, commandBuffer: command, destinationBufferHandle: readback.Destination.BufferHandle);
    }

    // Starts a frame's walk over its passes: the slot's first list is the first serial list, and lanes are cut only when
    // the runtime offers more than one and nothing the walk records must precede every pass (an initialization's clears).
    private void BeginEmissions(nint command) {
        m_emissions.Clear();
        m_emitOps.Clear();
        m_segments.Clear();
        m_emitBlockBytes = 0;
        m_laneCount = 0;
        m_passesRecordedSerially = 0;
        m_serialList = command;
        m_segments.Add(item: new FrameSegment(Extra: -1, List: command, RunCount: 0, RunStart: 0));
        m_lanesEnabled = ((EmitLaneLimit > 1) && !m_initializationPending);
        ArrayCapacity.EnsureLength(array: ref m_passEmitTicks, required: m_passes.Length);
    }
    // The list a pass that is not emitted apart records into: the current serial list, or a new one after a run.
    private nint SerialList(int slot) {
        if (m_serialList != 0) {
            return m_serialList;
        }

        var (list, extra) = BeginExtraList(slot: slot);

        m_segments.Add(item: new FrameSegment(Extra: extra, List: list, RunCount: 0, RunStart: 0));
        m_serialList = list;
        return list;
    }
    // Takes the slot's next further command pool, creating it the first time a frame needs this many lists, and begins
    // its list. The further lists are begun uncounted: how many a frame takes follows the lanes' measured weights, which
    // the ledger's deterministic counts must not, so render.record.lists counts them instead.
    private (nint List, int Extra) BeginExtraList(int slot) {
        var lists = m_slots[slot].Lists;
        var extra = m_recording.ExtraCount;

        if (extra == lists.Count) {
            lists.Add(item: CommandsOf(gpu: m_gpu, list: (extra + 1), owner: m_name, slot: slot));
        }

        var list = lists[extra].CommandBufferHandle;

        m_device.Services.Recorder.BeginCommandBuffer(commandBufferHandle: list);
        m_recording.OpenExtra(list: list);
        return (list, extra);
    }
    // Queues an emitter pass, decided by DecidePackage, into the run the walk is in, opening one after a serial list.
    private void QueueEmission(in PassEmission emission) {
        if (m_serialList != 0) {
            var serial = m_segments[^1];

            m_segments[^1] = serial with { RunStart = m_emissions.Count };
            m_serialList = 0;
        }
        m_emissions.Add(item: emission);
        m_segments[^1] = m_segments[^1] with { RunCount = (m_segments[^1].RunCount + 1) };
    }
    // Reserves a pass block's copy for an emission. It holds whatever an earlier frame's block left: FillPassBlock
    // writes every byte of it before anything reads one.
    private Span<byte> ReserveEmitBlock(int bytes, out int start) {
        start = m_emitBlockBytes;
        ArrayCapacity.EnsureLength(array: ref m_emitBlocks, required: (start + bytes));
        m_emitBlockBytes += bytes;

        return m_emitBlocks.AsSpan(start: start, length: bytes);
    }
    // Decides an emitter pass in the walk: everything RecordPackage does but record, which its emission does on its lane.
    private void DecidePackage(RuntimePass pass, int index, int slot, bool clearsCounters) {
        var emitter = ((IRenderGraphPackageEmitter)pass.Package!);
        var region = pass.PassRegion!;
        var passBlock = ReserveEmitBlock(bytes: checked((int)region.ByteCount), start: out var blockStart);
        var sink = new CaptureSink(node: this);
        var preStart = m_emitOps.Count;

        ResolvePackagePorts(pass: pass, slot: slot);
        FillPassBlock(block: passBlock, pass: pass);

        var mayStandIn = MayStandIn(pass: pass, slot: slot);
        var emission = new PassEmission {
            BlockLength = passBlock.Length,
            BlockStart = blockStart,
            ClearsCounters = clearsCounters,
            MayStandIn = mayStandIn,
            Pass = index,
            PreStart = preStart,
        };

        if (mayStandIn && (pass.Package is not IRenderGraphPackageReadback) && emitter.DrawsNothing(passBlock: passBlock)) {
            RecordAccessBarriers(pass: pass, readsOnly: true, sink: ref sink, slot: slot);
            _ = region.Write(bytes: passBlock, offset: 0);
            region.Flush(slot: slot);
            ApplyOutcome(outcome: RenderGraphPackageOutcome.DrewNothing, pass: pass, slot: slot);
            QueueEmission(emission: emission with { PreCount = (m_emitOps.Count - preStart), PostStart = m_emitOps.Count });
            return;
        }
        RecordAccessBarriers(pass: pass, readsOnly: false, sink: ref sink, slot: slot);
        emission.Arguments = PackageArgumentsOf(pass: pass, slot: slot);
        emission.WorkCounters = WorkCountersOf(pass: pass, slot: slot);

        var preEnd = m_emitOps.Count;
        var outcome = emitter.Decide(recording: RecordingOf(command: 0, emission: in emission, passBlock: passBlock, reads: Reads, slot: slot));

        RecordPackageReadback(pass: pass, sink: ref sink, slot: slot);
        _ = region.Write(bytes: passBlock, offset: 0);
        region.Flush(slot: slot);
        ApplyOutcome(outcome: outcome, pass: pass, slot: slot);
        pass.Drew = (outcome == RenderGraphPackageOutcome.Drew);
        QueueEmission(emission: emission with {
            Emits = pass.Drew,
            PostCount = (m_emitOps.Count - preEnd),
            PostStart = preEnd,
            PreCount = (preEnd - preStart),
        });
    }
    // The recording a package pass's recorder decides or emits with. An emission's carries the pass block's copy, no
    // external reads (its decision took what it samples) and the frame's leases only for the decision.
    private RenderGraphPackageRecording RecordingOf(nint command, in PassEmission emission, Span<byte> passBlock, RenderGraphExternalReads? reads, int slot) {
        var pass = m_passes[emission.Pass];

        return new RenderGraphPackageRecording(
            Arguments: emission.Arguments,
            CommandBuffer: command,
            Context: m_open.Context,
            FrameHeight: m_height,
            FrameWidth: m_width,
            Height: pass.Height,
            Inputs: pass.PackageInputs!,
            Leases: m_frameLeases,
            MayStandIn: emission.MayStandIn,
            Outputs: pass.PackageOutputs!,
            PassBlock: passBlock,
            Reads: reads,
            Recorder: m_gpu.Recorder,
            RenderHeight: m_renderHeight,
            RenderWidth: m_renderWidth,
            Slot: slot,
            UnreadFrames: UnreadFrames,
            Width: pass.Width,
            WorkCounters: emission.WorkCounters,
            WorkDetailRow: pass.WorkDetailRow
        );
    }
    // Ends the walk: cuts each run into lanes and lays the frame's lists out in submission order, then returns the list
    // the frame's tail (region copies aside, the preview, the outputs and the timings) records into: the last serial list,
    // or a new one after a closing run.
    private nint PlanEmissions(int slot, List<nint> commands) {
        var tail = SerialList(slot: slot);
        var lanes = 0;

        foreach (var segment in m_segments) {
            if (segment.RunCount > 0) { lanes++; }
        }

        var budget = Math.Max(val1: EmitLaneLimit, val2: lanes);
        var total = 0L;

        foreach (var emission in m_emissions) {
            total += m_passEmitTicks[emission.Pass];
        }

        var target = Math.Max(val1: (total / budget), val2: MinLaneTicks);

        foreach (var segment in m_segments) {
            if (segment.RunCount == 0) {
                commands.Add(item: segment.List);
                continue;
            }
            CutRun(segment: segment, slot: slot, target: target, commands: commands);
        }
        return tail;
    }
    // Cuts one run into lanes of about `target` weight each, its first lane continuing the serial list before it.
    private void CutRun(FrameSegment segment, int slot, long target, List<nint> commands) {
        var weight = 0L;

        for (var at = segment.RunStart; (at < (segment.RunStart + segment.RunCount)); at++) {
            weight += m_passEmitTicks[m_emissions[at].Pass];
        }

        var count = ((int)Math.Clamp(value: (weight / Math.Max(val1: target, val2: 1L)), min: 1L, max: Math.Min(val1: segment.RunCount, val2: EmitLaneLimit)));
        var share = (weight / count);
        var first = segment.RunStart;
        var end = (segment.RunStart + segment.RunCount);

        for (var lane = 0; (lane < count); lane++) {
            var last = (lane == (count - 1));
            var stop = first + 1;
            var held = m_passEmitTicks[m_emissions[first].Pass];

            // Leave at least one emission for each lane after this one.
            while (!last && (stop < (end - (count - lane - 1))) && (held < share)) {
                held += m_passEmitTicks[m_emissions[stop].Pass];
                stop++;
            }
            if (last) { stop = end; }

            var (list, extra) = ((lane == 0) ? (segment.List, segment.Extra) : BeginExtraList(slot: slot));

            commands.Add(item: list);
            AddLane(count: (stop - first), extra: extra, first: first, list: list);
            first = stop;
        }
    }
    private void AddLane(int first, int count, nint list, int extra) {
        if (m_laneCount == m_lanes.Count) { m_lanes.Add(item: new EmissionLane()); }

        var lane = m_lanes[m_laneCount++];

        lane.First = first;
        lane.Count = count;
        lane.List = list;
        lane.Extra = extra;
        lane.Emitted = false;
        lane.Ticks = 0L;
        lane.Error = null;
    }

    // The lanes the open frame's recording left to emit, each a partition the runtime's fork runs.
    internal int EmitLanes => ((m_open.Pending && (m_open.Error is null)) ? m_laneCount : 0);
    // The emissions one lane holds, and its weight by last frame's emissions, which the runtime orders its fork by.
    internal int LanePasses(int lane) => m_lanes[lane].Count;
    internal long LaneWeight(int lane) {
        var held = m_lanes[lane];
        var weight = 0L;

        for (var at = held.First; (at < (held.First + held.Count)); at++) {
            weight += m_passEmitTicks[m_emissions[at].Pass];
        }
        return Math.Max(val1: weight, val2: 1L);
    }
    // Emits one lane's passes into its list and ends the list, on any thread, beside the node's other lanes. It never
    // throws: what it throws is kept for EndFrame, which handles it as a failed recording.
    internal void EmitLane(int lane) {
        var held = m_lanes[lane];

        if (held.Emitted) { return; }
        held.Emitted = true;

        var started = Stopwatch.GetTimestamp();
        var slot = m_open.Job.Slot;
        var recorder = m_gpu.Recorder;

        try {
            for (var at = held.First; (at < (held.First + held.Count)); at++) {
                var begun = Stopwatch.GetTimestamp();
                var emission = m_emissions[at];

                EmitPass(command: held.List, emission: in emission, recorder: recorder, slot: slot);
                m_passEmitTicks[emission.Pass] = (Stopwatch.GetTimestamp() - begun);
            }
        } catch (Exception error) {
            held.Error = ExceptionDispatchInfo.Capture(source: error);
        }
        try {
            m_device.Services.Recorder.EndCommandBuffer(commandBufferHandle: held.List);
            m_recording.CloseList(list: held.List, extra: held.Extra);
        } catch (Exception error) {
            held.Error ??= ExceptionDispatchInfo.Capture(source: error);
        }
        held.Ticks = (Stopwatch.GetTimestamp() - started);
    }
    // Emits one decided pass inside its ledger pass: the counters' clear when it is the frame's first, its debug group and
    // timestamps, its reads' and writes' barriers, the recorder's emission and its readback copies, in RecordPasses' order.
    private void EmitPass(nint command, in PassEmission emission, IGpuRecorder recorder, int slot) {
        var pass = m_passes[emission.Pass];
        var ops = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_emitOps);

        if (emission.ClearsCounters) {
            m_passes[0].KernelCounters!.RecordClear(commandBuffer: command, recorder: recorder, slot: slot);
        }
        m_work.EnterLane(pass: emission.Pass);
        try {
            recorder.BeginDebugGroup(commandBufferHandle: command, label: pass.Label);
            BeginTiming(command: command, pass: emission.Pass, slot: slot);
            if (emission.PreCount > 0) {
                recorder.BeginBarriers(commandBufferHandle: command);
                try {
                    foreach (ref readonly var op in ops.Slice(start: emission.PreStart, length: emission.PreCount)) {
                        EmitBarrier(barrier: op.Barrier, command: command, recorder: recorder, target: op.Target);
                    }
                } finally {
                    recorder.EndBarriers(commandBufferHandle: command);
                }
            }
            if (emission.Emits) {
                ((IRenderGraphPackageEmitter)pass.Package!).Emit(recording: RecordingOf(
                    command: command,
                    emission: in emission,
                    passBlock: m_emitBlocks.AsSpan(start: emission.BlockStart, length: emission.BlockLength),
                    reads: null,
                    slot: slot
                ));
            }
            foreach (ref readonly var op in ops.Slice(start: emission.PostStart, length: emission.PostCount)) {
                if (op.Copies) {
                    EmitCopy(command: command, readback: in op.Readback, recorder: recorder, source: op.Source);
                } else {
                    EmitBarrier(barrier: op.Barrier, command: command, recorder: recorder, target: op.Target);
                }
            }
            EndTiming(command: command, mark: false, pass: emission.Pass, slot: slot);
            recorder.EndDebugGroup(commandBufferHandle: command);
        } finally {
            m_work.LeaveLane();
        }
    }
    // Emits every lane the runtime did not, inline, so a frame ended outside a runtime's wave commits whole.
    private void EmitOwedLanes() {
        for (var lane = 0; (lane < m_laneCount); lane++) {
            EmitLane(lane: lane);
        }
    }
    // The first failure a lane kept, by lane order, or null.
    private ExceptionDispatchInfo? LaneError() {
        for (var lane = 0; (lane < m_laneCount); lane++) {
            if (m_lanes[lane].Error is { } error) { return error; }
        }
        return null;
    }
    // The open frame's lanes' summed emission time and its longest lane, once they have all run.
    private (long Sum, long Longest) LaneTicks() {
        var (sum, longest) = (0L, 0L);

        for (var lane = 0; (lane < m_laneCount); lane++) {
            sum += m_lanes[lane].Ticks;
            longest = Math.Max(val1: longest, val2: m_lanes[lane].Ticks);
        }
        return (sum, longest);
    }
}
