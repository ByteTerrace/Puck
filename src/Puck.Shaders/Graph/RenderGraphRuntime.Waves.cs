using Puck.Abstractions.Collections;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Memory;
using Puck.Hosting;
using Puck.Hosting.Jobs;

namespace Puck.Shaders;

// The frame's renders in dependency waves. A render's wave is one past the latest wave of a graph node it reads this
// frame (an external producer produces as it is prepared, so its readers may share its wave); every other render of a
// wave reads nothing the wave writes. Each wave is prepared on the frame thread in schedule order (bindings, the slot's
// fence, leases and checkpoint, IRenderGraphPackageRecorder.PrepareRecord), recorded as one fork of the job pool's frame
// class, its recordings' lanes emitted as a second fork (EmitWave), and committed on the frame thread in schedule order,
// so the next wave binds what this one published. Submission order is wave order, then schedule order: every producer
// still reaches the queue before its same-frame readers.
//
// The invariant the first fork rests on: a recording reads and writes only its own instance and the recording domains its
// passes name (ShaderPipelineRenderNode.AddRecordingDomains). One door enforces it: RecordWave groups the wave's jobs by
// shared domain, and a group records on one thread, in schedule order. A package that shares state its recorder does not
// name is caught by its own guards (a residency's upload and an SDF view's resolution refuse to run inside a partition),
// and otherwise only by review. The second fork rests on every recording having decided all its passes before any lane
// runs: a lane changes nothing a decision or another lane reads (IRenderGraphPackageEmitter.Emit).
public sealed partial class RenderGraphRuntime {
    private JobPool? m_jobs;
    // Each instance's wave this frame, or -1 for an instance that does not render.
    private int[] m_waveOf = [];
    private readonly List<WaveJob> m_waveJobs = [];
    private readonly WaveRecording m_waveRecording = new();
    private readonly WaveEmission m_waveEmission = new();
    private readonly Dictionary<object, int> m_waveDomains = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<object> m_jobDomains = [];
    private readonly DisjointSet m_waveGroups = new(count: 0);

    // What a frame's renders share across its waves.
    private struct FrameRenders {
        public RenderGraphSchedule Schedule;
        public RenderGraphHistory Prior;
        public bool HoldingConvergence;
        // The node that submitted last this frame, whose submission follows the host's presentation of the last frame.
        public ShaderPipelineRenderNode? Submitter;
        // The frame's longest single piece of recording (a node's walk over its passes, or one lane of its emissions), the
        // critical path its record jobs keep side by side.
        public long LongestRecord;
    }
    // A graph node's render a wave prepared, whose record job its fork runs and its commit ends.
    private readonly record struct WaveJob(int Index, ShaderPipelineRenderNode Node, SourceGraph? Source, ulong Rendered, long Submitted, RenderGraphConvergence? Converging);

    // Renders every scheduled instance, wave by wave. A render that throws ends the frame: every job the wave prepared and
    // has not committed is abandoned, so nothing it recorded reaches the queue.
    private void RenderWaves(ref FrameRenders rendering, in FrameContext context, in RenderGraphFrame frame) {
        var renders = rendering.Schedule.Renders;
        var waves = PlanWaves(schedule: rendering.Schedule);

        for (var wave = 0; (wave < waves); wave++) {
            var committed = 0;

            m_waveJobs.Clear();
            try {
                for (var position = 0; (position < renders.Count); position++) {
                    if (m_waveOf[renders[position]] == wave) {
                        BeginRender(rendering: ref rendering, context: in context, frame: in frame, index: renders[position]);
                    }
                }
                RecordWave();
                while (committed < m_waveJobs.Count) {
                    var job = m_waveJobs[committed++];

                    FinishRender(rendering: ref rendering, job: in job);
                }
            } catch {
                for (var position = committed; (position < m_waveJobs.Count); position++) {
                    try {
                        AbandonRender(rendering: ref rendering, job: m_waveJobs[position]);
                    } catch (Exception) {
                        // The failure that ended the frame is the one that propagates (a device loss the host recovers
                        // from); a later instance's abandonment failing too changes nothing it reports.
                    }
                }
                m_waveJobs.Clear();
                throw;
            }
            m_waveJobs.Clear();
        }
    }
    // Assigns each render its wave (m_waveOf) from the schedule's same-frame reads, which name every producer a render
    // binds this frame's output of; returns the number of waves.
    private int PlanWaves(RenderGraphSchedule schedule) {
        var renders = schedule.Renders;
        var reads = schedule.Reads;
        var waves = 0;

        ArrayCapacity.EnsureLength(array: ref m_waveOf, required: m_nodes.Length);
        Array.Fill(array: m_waveOf, value: -1);
        for (var position = 0; (position < renders.Count); position++) {
            var index = renders[position];
            var wave = 0;

            schedule.ConsumerReads(consumer: index, start: out var start, count: out var count);
            for (var read = start; (read < (start + count)); read++) {
                var producer = schedule.ReadProducer(readIndex: read);

                if ((producer == index) || (reads[read].Frame != schedule.Frame) || (m_waveOf[producer] < 0)) {
                    continue;
                }
                wave = Math.Max(val1: wave, val2: (m_waveOf[producer] + ((m_producers[producer] is null) ? 1 : 0)));
            }
            m_waveOf[index] = wave;
            waves = Math.Max(val1: waves, val2: (wave + 1));
        }
        return waves;
    }
    // Prepares one render on the frame thread: an external producer produces, an uploaded source writes, and a graph
    // node binds its reads and begins its frame. A node whose frame records joins the wave's jobs; every other render
    // finishes here.
    private void BeginRender(ref FrameRenders rendering, in FrameContext context, in RenderGraphFrame frame, int index) {
        var schedule = rendering.Schedule;
        var prior = rendering.Prior;
        var row = schedule.Instances[index];

        // The display encoder may still be building after the last requested sample. Keep the contributing
        // images alive until readback completes; another render would silently capture a later jitter sample.
        if (rendering.HoldingConvergence && m_convergenceInstances.Contains(item: index)) {
            if (index == m_captureInstance) {
                m_nodes[index]?.PollCapture();
            }
            m_unproduced++;
            schedule.Next.Withdraw(index: index, previous: prior);
            return;
        }

        // An external producer submits through its own ring, at the scheduled extent, before its consumers render. A
        // capture of it moves to it first, and it serves the capture from the next frame it produces. A render it
        // could not produce is withdrawn from the history, so its cadence counts from its last completed frame and a
        // source that renders once is asked again on the next frame.
        if (m_producers[index] is { } producer) {
            var reads = BindExternalReads(
                index: index,
                schedule: schedule
            );

            if (
                (index == m_captureInstance) &&
                (m_capture.PendingPath is not null) &&
                (m_taintedReads[index] is null) &&
                !MarkReadStale(index: index, schedule: schedule)
            ) {
                m_capture.Forward(target: producer);
                m_forwardedCaptureIndex = index;
            }

            var produced = (((row.Width > 0) && (row.Height > 0))
                ? producer.Produce(
                    context: in context,
                    height: ((uint)row.Height),
                    reads: reads,
                    width: ((uint)row.Width)
                )
                : FrameRender.Waiting(reason: "the schedule gave it no extent"));

            reads?.RetireUntaken();

            if (!produced.IsRendered) {
                m_unproduced++;
                MarkProduction(
                    index: index,
                    production: produced
                );
                schedule.Next.Withdraw(
                    index: index,
                    previous: prior
                );
            } else {
                m_historySucceeded[index] = true;
                MarkRendered(index: index, schedule: schedule);
            }

            return;
        }

        var node = m_nodes[index]!;
        var source = m_sources[index];

        // An uploaded source writes its image for the frame's tick into its region; one with no image is withdrawn,
        // as an external producer's render is, so its cadence counts from its last converted frame.
        if (
            (source is not null) &&
            (source.Write(
                node: node,
                tick: frame.Tick
            ) is { IsRendered: false } written)
        ) {
            m_unproduced++;

            if (written.Completion == FrameCompletion.Refused) {
                MarkProduction(
                    index: index,
                    production: written
                );
            } else {
                MarkUnproduced(
                    index: index,
                    node: node,
                    waiting: written.Reason
                );
            }

            schedule.Next.Withdraw(
                index: index,
                previous: prior
            );

            return;
        }
        // A render its package withholds records nothing: its image waits on work the frame's preparation ran,
        // which another recording would only queue GPU work ahead of. Its completed image stands published, a
        // capture already reading it is polled, and the render is withdrawn so the next frame asks again.
        if (
            (LatestOf(index: index, latch: in m_latch).Frame >= 0) &&
            (PackageReasonOf(index: index, kind: PackageReason.Withheld) is { } withheld)
        ) {
            if (index == m_captureInstance) {
                node.PollCapture();
            }
            m_unproduced++;
            MarkStale(
                index: index,
                reason: withheld
            );
            schedule.Next.Withdraw(
                index: index,
                previous: prior
            );

            return;
        }
        if (!Bind(
            index: index,
            schedule: schedule
        )) {
            m_unproduced++;
            if (!MarkReadStale(index: index, schedule: schedule)) {
                MarkStale(
                    index: index,
                    reason: $"the instance '{m_set.Instances[index].Name}' reads a buffer with no completed output"
                );
            }

            return;
        }

        // The reads its graph binds to no version reach its package passes, their taint the instance's; what they
        // leave is retired with the frame.
        node.Reads = BindUnboundReads(
            index: index,
            schedule: schedule
        );
        // A source's graph renders at the extent its descriptor fixed, which it declared to the scheduler.
        if (
            (source is null) &&
            (row.Width > 0) &&
            (row.Height > 0)
        ) {
            node.Resize(
                height: ((uint)row.Height),
                width: ((uint)row.Width)
            );
        }
        // A capture moves to its instance's node only once that node renders a graph over completed inputs, so the
        // frame it produces is the one the capture reads; until then it stays armed here, where
        // UnservedCaptureReasonOf explains it.
        // A node whose published image stands for one that is gone serves no capture until it has rendered again,
        // which a shown one does this frame (WithRerenders).
        if (
            (index == m_captureInstance) &&
            (m_capture.PendingPath is not null) &&
            CanServeConvergence &&
            node.IsReady &&
            (m_standInReads[index] is null) &&
            (m_taintedReads[index] is null) &&
            !HasPackageTaint(index: index) &&
            !m_outputs.HasEnded(index: index, latch: in m_latch) &&
            (m_outputs.StandsFor(index: index).IsOwn || (LatestOf(index: index, latch: in m_latch).Frame >= 0)) &&
            !MarkReadStale(index: index, schedule: schedule)
        ) {
            m_capture.Forward(target: node);
            m_forwardedCaptureIndex = index;
        }

        OfferCaptureSource(
            index: index,
            node: node
        );

        // A node may spread its passes across as many lanes as the pool has threads (EmitWave).
        node.EmitLaneLimit = ((m_jobs is { Serial: false } pool) ? pool.Workers.Count : 1);

        // Readback may complete inline in the frame. That still counts the final rendered sample of this request.
        var job = new WaveJob(
            Converging: (((index == m_captureInstance) && IsConverging(index: index)) ? m_convergence : null),
            Index: index,
            Node: node,
            Rendered: node.FrameCounter,
            Source: source,
            Submitted: node.SubmissionCount
        );

        // The root is shown as the display, at its own extent; every other instance is resampled by what reads it.
        node.ShownAtItsExtent = (index == m_root);
        node.UnreadFrames = m_unreadFrames[index];
        node.TimingFrame = m_readFrame;
        // What the node commits for a deferred submission, and an export it writes, settle once the batch is flushed.
        node.DefersExportCompletion = true;

        bool records;

        try {
            records = node.BeginFrame(context: in context);
        } catch {
            ReleaseRender(rendering: ref rendering, job: in job, schedule: schedule);
            throw;
        }
        if (records) {
            m_waveJobs.Add(item: job);
        } else {
            FinishRender(rendering: ref rendering, job: in job);
        }
    }
    // Records the wave's jobs. Jobs whose recordings name a common domain form one group, recorded on one thread in
    // schedule order; the groups run side by side as the partitions of one fork, the heaviest (by their latest
    // recordings) first, so the longest group starts at once.
    private void RecordWave() {
        var jobs = m_waveJobs.Count;

        if (jobs == 0) {
            return;
        }

        var recording = m_waveRecording;

        m_waveGroups.Reset(count: jobs);
        m_waveDomains.Clear();
        for (var job = 0; (job < jobs); job++) {
            m_jobDomains.Clear();
            m_waveJobs[job].Node.AddRecordingDomains(domains: m_jobDomains);
            foreach (var domain in m_jobDomains) {
                if (m_waveDomains.TryGetValue(key: domain, value: out var holder)) {
                    m_waveGroups.Union(first: holder, second: job);
                } else {
                    m_waveDomains.Add(key: domain, value: job);
                }
            }
        }
        m_jobDomains.Clear();
        m_waveDomains.Clear();

        var partitions = recording.Group(groups: m_waveGroups, jobs: m_waveJobs);

        recording.ForkingThread = Environment.CurrentManagedThreadId;
        RenderRecordWork.Process.Wave(partitions: partitions);
        try {
            ForkFrame(body: recording, partitions: partitions);
        } finally {
            recording.Release();
        }
        EmitWave();
    }
    // Runs a fork of the frame class across the job pool, or its partitions in order on this thread with no pool.
    private void ForkFrame(IJobPartitions body, int partitions) {
        if (m_jobs is { } pool) {
            pool.Fork(body: body, jobClass: JobClass.Frame, partitions: partitions);
            return;
        }
        for (var partition = 0; (partition < partitions); partition++) {
            body.Run(partition: partition);
        }
    }
    // Emits the lanes the wave's recordings left (ShaderPipelineRenderNode.EmitLane) as one fork: every recording has
    // decided all its passes, so a lane reads nothing another lane or a recording writes, and lanes of nodes that share a
    // recording domain run side by side too. The heaviest lanes (by their last emissions) go first.
    private void EmitWave() {
        var emission = m_waveEmission;
        var lanes = emission.Collect(jobs: m_waveJobs);

        if (lanes == 0) {
            return;
        }
        emission.ForkingThread = Environment.CurrentManagedThreadId;
        RenderRecordWork.Process.Lanes(lanes: lanes);
        try {
            ForkFrame(body: emission, partitions: lanes);
        } finally {
            emission.Release();
        }
    }
    // Commits one render's frame and settles what the runtime keeps of it.
    private void FinishRender(ref FrameRenders rendering, in WaveJob job) {
        var schedule = rendering.Schedule;
        var index = job.Index;
        var node = job.Node;
        Surface surface;

        try {
            surface = node.EndFrame();
        } finally {
            ReleaseRender(rendering: ref rendering, job: in job, schedule: schedule);
        }

        if (node.FrameCounter == job.Rendered) {
            m_unproduced++;
            MarkUnproduced(
                index: index,
                node: node
            );
            // A paused node presents its last image as this frame's output on purpose, writing nothing: the frame it
            // was scheduled for is spent, so its refresh, and the demand it passes to its producers, keep their cadence.
            if (Stands(index: index)) {
                m_historySucceeded[index] = true;
                SettleWithdrawal(index: index);
            }

            // A source whose conversion has not built yet is asked again, since its cadence may never ask twice.
            if (job.Source is not null) {
                schedule.Next.Withdraw(
                    index: index,
                    previous: rendering.Prior
                );
            }

            if (node.SubmissionCount == job.Submitted) {
                return;
            }
        }

        // A sample rendered at an extent the node was not asked for is one its capture never reads.
        if ((node.FrameCounter != job.Rendered) && (job.Converging is { } converging) &&
            (m_standInReads[index] is null) && (m_taintedReads[index] is null) && (node.Extent == node.RequestedExtent)) {
            converging.Count();
        }

        MarkRendered(
            index: index,
            schedule: schedule
        );

        rendering.Submitter = node;
        if (node.FrameCounter == job.Rendered) {
            RememberOutput(frame: schedule.Frame, index: index, node: node, rendered: false, schedule: schedule, surface: in surface);
        }
    }
    // Gives up a render whose frame the wave prepared but never committed.
    private void AbandonRender(ref FrameRenders rendering, in WaveJob job) {
        try {
            job.Node.AbandonFrame();
        } finally {
            ReleaseRender(rendering: ref rendering, job: in job, schedule: rendering.Schedule);
        }
    }
    // What the runtime settles after a node's frame whatever its outcome: the deferred export, a rendered frame's output,
    // and the reads its package passes left.
    private void ReleaseRender(ref FrameRenders rendering, in WaveJob job, RenderGraphSchedule schedule) {
        var node = job.Node;
        var index = job.Index;

        node.DefersExportCompletion = false;
        if (node.OwesSettlement) { m_settlementsOwed.Add(item: node); }
        if (node.FrameCounter != job.Rendered) {
            rendering.LongestRecord = Math.Max(val1: rendering.LongestRecord, val2: node.LastLongestRecordTicks);
            m_historySucceeded[index] = true;
            RememberOutput(frame: schedule.Frame, index: index, node: node, rendered: true, schedule: schedule, surface: node.PublishedSurface);
        }
        node.Reads?.RetireUntaken();
        node.Reads = null;
        NoteOwedReadbacks(index: index);
    }

    // One wave's fork: the jobs in groups, each group a partition that records its jobs in schedule order. Grouping and
    // ordering reuse the same arrays every wave, so a steady frame allocates nothing here.
    private sealed class WaveRecording : IJobPartitions {
        private List<WaveJob> m_jobs = [];
        // The jobs by partition: partition p records m_order[m_starts[p]] to m_order[m_starts[p + 1] - 1].
        private int[] m_order = [];
        private int[] m_starts = [];
        // Scratch for grouping: each job's group root, each group's weight and partition.
        private int[] m_roots = [];
        private long[] m_weights = [];
        private int[] m_groups = [];
        private int[] m_cursor = [];

        // The thread that forks, whose share of the partitions is the frame thread's own recording.
        public int ForkingThread;

        // Groups the jobs by their sets' roots and orders the groups heaviest first; returns the partition count.
        public int Group(DisjointSet groups, List<WaveJob> jobs) {
            var count = jobs.Count;

            m_jobs = jobs;
            ArrayCapacity.EnsureScratch(array: ref m_order, required: count);
            ArrayCapacity.EnsureScratch(array: ref m_starts, required: (count + 1));
            ArrayCapacity.EnsureScratch(array: ref m_roots, required: count);
            ArrayCapacity.EnsureScratch(array: ref m_weights, required: count);
            ArrayCapacity.EnsureScratch(array: ref m_groups, required: count);
            ArrayCapacity.EnsureScratch(array: ref m_cursor, required: count);

            // A group is named by its smallest job, its root; m_cursor maps a root to its group's position.
            var partitions = 0;

            for (var job = 0; (job < count); job++) {
                var root = groups.Find(element: job);

                m_roots[job] = root;
                if (root == job) {
                    m_groups[partitions] = root;
                    // Heaviest first: the sort below is ascending, so the weight is negated.
                    m_weights[partitions] = 0L;
                    m_cursor[root] = partitions++;
                }
                m_weights[m_cursor[root]] -= Math.Max(val1: jobs[job].Node.LastWalkTicks, val2: 1L);
            }
            Array.Sort(keys: m_weights, items: m_groups, index: 0, length: partitions);
            // Each group's run in m_order (a counting sort by partition), then the jobs in schedule order into their runs.
            Array.Clear(array: m_starts, index: 0, length: (partitions + 1));
            for (var partition = 0; (partition < partitions); partition++) {
                m_cursor[m_groups[partition]] = partition;
            }
            for (var job = 0; (job < count); job++) {
                m_starts[(m_cursor[m_roots[job]] + 1)]++;
            }
            for (var partition = 0; (partition < partitions); partition++) {
                m_starts[(partition + 1)] += m_starts[partition];
                m_groups[partition] = m_starts[partition];
            }
            for (var job = 0; (job < count); job++) {
                m_order[m_groups[m_cursor[m_roots[job]]]++] = job;
            }
            return partitions;
        }
        public void Run(int partition) {
            var offThread = (Environment.CurrentManagedThreadId != ForkingThread);

            for (var at = m_starts[partition]; (at < m_starts[(partition + 1)]); at++) {
                var node = m_jobs[m_order[at]].Node;

                node.RecordPendingFrame();
                if (offThread) {
                    RenderRecordWork.Process.WorkerJob(passes: node.LastPassesRecordedSerially);
                }
            }
        }
        // Lets go of the wave's jobs once its fork has joined.
        public void Release() => m_jobs = [];
    }
    // One wave's emission fork: every lane its recordings left, heaviest first, a partition each.
    private sealed class WaveEmission : IJobPartitions {
        private ShaderPipelineRenderNode[] m_nodes = [];
        private int[] m_lanes = [];
        private long[] m_weights = [];
        private int[] m_order = [];
        private int m_count;

        // The thread that forks, whose lanes are the frame thread's own recording.
        public int ForkingThread;

        // Gathers the jobs' lanes and orders them heaviest first; returns the partition count.
        public int Collect(List<WaveJob> jobs) {
            var count = 0;

            foreach (var job in jobs) {
                count += job.Node.EmitLanes;
            }
            if (count == 0) {
                return 0;
            }
            ArrayCapacity.EnsureScratch(array: ref m_nodes, required: count);
            ArrayCapacity.EnsureScratch(array: ref m_lanes, required: count);
            ArrayCapacity.EnsureScratch(array: ref m_weights, required: count);
            ArrayCapacity.EnsureScratch(array: ref m_order, required: count);

            var at = 0;

            foreach (var job in jobs) {
                var node = job.Node;

                for (var lane = 0; (lane < node.EmitLanes); lane++, at++) {
                    m_nodes[at] = node;
                    m_lanes[at] = lane;
                    // Heaviest first: the sort below is ascending, so the weight is negated.
                    m_weights[at] = -node.LaneWeight(lane: lane);
                    m_order[at] = at;
                }
            }
            Array.Sort(keys: m_weights, items: m_order, index: 0, length: count);
            m_count = count;
            return count;
        }
        public void Run(int partition) {
            var at = m_order[partition];
            var node = m_nodes[at];
            var lane = m_lanes[at];

            node.EmitLane(lane: lane);
            RenderRecordWork.Process.Lane(passes: node.LanePasses(lane: lane), worker: (Environment.CurrentManagedThreadId != ForkingThread));
        }
        // Lets go of the wave's nodes once its fork has joined.
        public void Release() {
            Array.Clear(array: m_nodes, index: 0, length: m_count);
            m_count = 0;
        }
    }
}
