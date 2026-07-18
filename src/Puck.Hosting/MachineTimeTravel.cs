using System.Runtime.CompilerServices;
using Puck.Abstractions.Machines;

namespace Puck.Hosting;

/// <summary>
/// The machine-neutral time-travel layer — rewind, runahead, and fast-forward built ONCE over the snapshot surface any
/// deterministic <see cref="ITimeTravelMachineCore{TInput}"/> exposes (the binjgb reference design). It is pure HOST
/// state: it only READS the authoritative machine (through raw state capture) or drives a SEPARATE forked lookahead, so
/// it can never perturb the simulation trajectory whether its features are on or off.
/// <para><b>Rewind.</b> Every <see cref="m_interval"/>th captured frame is a full keyframe (the restore anchor); the
/// frames between two keyframes store only the (input, cycle-budget, host-accumulator) that produced each, so a rewind
/// reconstructs by restoring the nearest keyframe and deterministically replaying the intervening inputs — the core is
/// deterministic, so the replay lands bit-exact. The landing also hands back the host tick-to-cycle accumulator phase the
/// landed frame was produced under, so the host restores it atomically with the core (a restored instant plus identical
/// future ticks receives identical budgets). A dual-ended segment ring bounded by a memory budget holds the history; when
/// the budget is full the oldest keyframe span is evicted. Capture runs into pre-sized, reused buffers (keyframes recycle
/// their buffers on eviction, the per-frame record arrays are allocated once per segment), so the steady-state ring
/// allocates nothing. There is no XOR/RLE delta encode: restore is keyframe-plus-replay (bit-exact by construction), so a
/// full-state delta stream would be dead CPU/allocation cost for bytes a result can never read.</para>
/// <para><b>Runahead.</b> ONE persistent lookahead fork (rented from the core's instance pool, never forked per input
/// change) is kept N native frames ahead of the authoritative machine on predicted (currently-held) input; the host
/// presents ITS framebuffer while the real machine stays the tick-locked authority and the only audio source. The layer
/// advances the lookahead by the authority's OWN native-frame delta each submission (catching up to authority + N), so
/// the lead stays exactly N under a mismatched host/native cadence and under fast-forward rather than drifting.</para>
/// <para><b>Fast-forward.</b> A host-level cycle-budget multiplier the host reads and applies to its per-frame budget,
/// with presentation frames skipped — never a timing hack inside the core. Clamped to <see cref="MaxFastForwardFactor"/>
/// so the host multiply can never overflow its checked cycle-budget arithmetic and fault the worker loop.</para>
/// </summary>
/// <typeparam name="TInput">The core's held-input image, recorded per frame and replayed verbatim.</typeparam>
public sealed class MachineTimeTravel<TInput> : IDisposable {
    /// <summary>The most frames the persistent lookahead is ever kept ahead — beyond this the predicted-input divergence
    /// makes the runahead frame a worse guess than the real frame.</summary>
    public const int MaxRunaheadFrames = 10;

    /// <summary>The largest supported fast-forward factor. A verb boundary rejects an over-cap factor cleanly; this layer
    /// additionally clamps here so even a caller that skips its own bound cannot drive the host's checked cycle-budget
    /// multiply to overflow (which would escape into the worker-loop catch and stop the queue).</summary>
    public const int MaxFastForwardFactor = 32;

    private readonly ITimeTravelMachineCore<TInput> m_core;
    private readonly int m_interval;
    private readonly long m_budgetBytes;
    private readonly ulong m_cyclesPerSecond;

    // The keyframe-anchored segment ring (oldest at m_segHead, m_segCount live), sized once the keyframe byte length is
    // known so the whole ring stays within the memory budget.
    private Segment[] m_segments = [];
    private int m_capacity;
    private int m_segHead;
    private int m_segCount;

    // One reused capture buffer (the just-serialized state), sized once to the state image, never per frame.
    private byte[] m_captureScratch = [];
    private int m_snapshotSize = -1;

    // Runahead: a persistent bare lookahead kept m_runaheadFrames NATIVE frames ahead on the last predicted input. The
    // lead is read from the FORK ITSELF (m_lookahead.NativeFrameIndex - m_core.NativeFrameIndex), never a synthetic
    // per-RunFrame counter — an instruction's overshoot can cross an extra native-frame boundary, so only the fork's own
    // index reports (and drives to) the true lead.
    private ITimeTravelLookahead<TInput>? m_lookahead;
    private int m_runaheadFrames;
    private TInput m_lastPrediction = default!;
    private bool m_lookaheadPrimed;

    private bool m_enabled;
    private int m_fastForward = 1;

    /// <summary>Creates a time-travel layer over one core.</summary>
    /// <param name="core">The authoritative core this layer captures, restores, and forks lookaheads from.</param>
    /// <param name="keyframeIntervalFrames">The number of captured frames per keyframe span (a keyframe plus that many
    /// less one deltas).</param>
    /// <param name="memoryBudgetBytes">The approximate memory ceiling for the ring; the oldest keyframe span is evicted
    /// once the budget is full.</param>
    /// <param name="cyclesPerSecond">The core's representative master-clock rate, used only to render the history span in
    /// seconds for status (presentation-only).</param>
    public MachineTimeTravel(ITimeTravelMachineCore<TInput> core, int keyframeIntervalFrames = 120, long memoryBudgetBytes = (48L * 1024L * 1024L), ulong cyclesPerSecond = 1UL) {
        ArgumentNullException.ThrowIfNull(argument: core);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: keyframeIntervalFrames, other: 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: memoryBudgetBytes, other: 1L);

        m_core = core;
        m_interval = keyframeIntervalFrames;
        m_budgetBytes = memoryBudgetBytes;
        m_cyclesPerSecond = Math.Max(val1: 1UL, val2: cyclesPerSecond);
    }

    /// <summary>Gets whether the rewind ring is armed and capturing history.</summary>
    public bool Enabled => m_enabled;

    /// <summary>Gets whether the lookahead machine is live (runahead armed with a forked sibling).</summary>
    public bool RunaheadActive => ((m_runaheadFrames > 0) && (m_lookahead is not null));

    /// <summary>Gets the fast-forward factor — the host-level per-frame cycle-budget multiplier (1 = realtime). The host
    /// reads this and scales the budget it advances the core by; presentation frames are skipped.</summary>
    public int FastForwardFactor => m_fastForward;

    // The newest live segment (valid only while m_segCount > 0).
    private Segment Newest => m_segments[(((m_segHead + m_segCount) - 1) % m_capacity)];

    /// <summary>Arms or disarms the rewind ring; disarming clears the captured history (the lookahead is untouched).</summary>
    /// <param name="enabled">Whether to capture rewind history.</param>
    public void SetRewindEnabled(bool enabled) {
        if (m_enabled == enabled) {
            return;
        }

        m_enabled = enabled;

        if (!enabled) {
            ClearRing();
        }
    }

    /// <summary>Sets the fast-forward factor, clamped to <c>[1, <see cref="MaxFastForwardFactor"/>]</c>. A verb boundary
    /// rejects an over-cap value cleanly before reaching here; this clamp is the defensive floor/ceiling that keeps the
    /// host's checked per-frame multiply bounded regardless.</summary>
    /// <param name="factor">The cycle-budget multiplier.</param>
    public void SetFastForward(int factor) =>
        m_fastForward = Math.Clamp(value: factor, min: 1, max: MaxFastForwardFactor);

    // ---- Capture ------------------------------------------------------------------------------------------------

    /// <summary>Records the machine's current instant into the ring — once per stepped frame. The first frame of each
    /// keyframe span is stored whole (the restore anchor); the rest store only the (input, cycle-budget, host-accumulator)
    /// that produced them, replayed onto the keyframe to reconstruct. Reads the machine only (through raw state capture),
    /// so recording never perturbs the simulation. A no-op while the ring is disarmed.</summary>
    /// <param name="input">The held-input image latched for the frame just stepped.</param>
    /// <param name="budget">The master-cycle budget that frame advanced.</param>
    /// <param name="hostAccumulator">The host's tick-to-cycle accumulator phase AFTER this frame produced its budget —
    /// the value that seeds the next frame's budget. Stored with the frame and handed back at the landing point on a
    /// rewind so the host restores it atomically with the core.</param>
    public void Record(in TInput input, long budget, ulong hostAccumulator) {
        if (!m_enabled) {
            return;
        }

        var cycle = m_core.CycleCount;
        var nativeFrame = m_core.NativeFrameIndex;

        if ((m_segCount == 0) || (Newest.DeltaCount >= (m_interval - 1))) {
            // Keyframe boundary — the ONLY per-frame full serialize. A delta frame stores just (input, budget,
            // accumulator): restore is keyframe-plus-replay, so an intervening frame's whole-state image is never read
            // (a delta never compares or restores against it). Capturing it every frame and discarding all but the length
            // was pure waste; the keyframe capture below is the sole state serialization the ring consumes.
            var length = m_core.CaptureState(buffer: ref m_captureScratch);

            EnsureScratch(size: length);
            StartSegment(length: length, cycle: cycle, nativeFrame: nativeFrame, hostAccumulator: hostAccumulator);
        } else {
            AppendDelta(cycle: cycle, nativeFrame: nativeFrame, input: in input, budget: budget, hostAccumulator: hostAccumulator);
        }
    }

    private void StartSegment(int length, long cycle, long nativeFrame, ulong hostAccumulator) {
        EnsureRing(baseLength: length);

        int slot;

        if (m_segCount < m_capacity) {
            slot = ((m_segHead + m_segCount) % m_capacity);
            ++m_segCount;
        } else {
            // Ring full (budget reached): evict the oldest keyframe span in place (its reused buffers survive).
            slot = m_segHead;
            m_segHead = ((m_segHead + 1) % m_capacity);
        }

        var segment = m_segments[slot];

        if ((segment.Base.Length < length)) {
            segment.Base = new byte[length];
        }

        m_captureScratch.AsSpan(start: 0, length: length).CopyTo(destination: segment.Base);
        segment.BaseLength = length;
        segment.BaseCycle = cycle;
        segment.BaseNativeFrame = nativeFrame;
        segment.BaseAccumulator = hostAccumulator;
        segment.DeltaCount = 0;
    }

    private void AppendDelta(long cycle, long nativeFrame, in TInput input, long budget, ulong hostAccumulator) {
        var segment = Newest;
        var index = segment.DeltaCount;

        segment.Cycle[index] = cycle;
        segment.NativeFrame[index] = nativeFrame;
        segment.Input[index] = input;
        segment.Budget[index] = budget;
        segment.Accumulator[index] = hostAccumulator;
        segment.DeltaCount = (index + 1);
    }

    // ---- Rewind -------------------------------------------------------------------------------------------------

    /// <summary>Rewinds the authoritative machine backward by up to <paramref name="frames"/> native frames, clamped to
    /// the oldest captured frame, by restoring the target frame's keyframe and replaying the intervening inputs, then
    /// truncating the abandoned future so play resumes cleanly forward. A no-op when the ring is empty or disarmed.</summary>
    /// <param name="frames">The number of native frames to move backward.</param>
    /// <param name="hostAccumulator">Receives the host tick-to-cycle accumulator phase the landed frame was produced
    /// under — the caller restores its own accumulator to this atomically with the core so identical future ticks buy
    /// identical budgets. Left at 0 when nothing was rewound.</param>
    /// <returns>The number of native frames actually rewound.</returns>
    public int RewindBy(int frames, out ulong hostAccumulator) {
        hostAccumulator = 0UL;

        if (!m_enabled || (m_segCount == 0) || (frames <= 0)) {
            return 0;
        }

        var current = m_core.NativeFrameIndex;
        var target = (current - frames);

        Locate(target: target, slot: out var slot, frameIndex: out var frameIndex);

        var segment = m_segments[slot];

        m_core.RestoreState(buffer: segment.Base, length: segment.BaseLength);

        for (var i = 0; (i < frameIndex); ++i) {
            var input = segment.Input[i];

            m_core.ApplyInput(input: in input);
            m_core.RunCycles(cycles: segment.Budget[i]);
        }

        // The landed frame is the keyframe itself (frameIndex 0) or the last replayed delta; hand back the accumulator
        // phase it was recorded under so the host's next tick reproduces the recorded budget instead of the abandoned
        // future's phase.
        hostAccumulator = ((frameIndex == 0) ? segment.BaseAccumulator : segment.Accumulator[frameIndex - 1]);

        Truncate(slot: slot, frameIndex: frameIndex);

        m_lookaheadPrimed = false; // the real machine jumped; force a runahead resync next advance

        return (int)Math.Max(val1: 0L, val2: (current - m_core.NativeFrameIndex));
    }

    // Find the frame with the largest native-frame index not exceeding target; clamp to the oldest captured frame.
    private void Locate(long target, out int slot, out int frameIndex) {
        slot = m_segHead;
        frameIndex = 0;

        for (var s = 0; (s < m_segCount); ++s) {
            var index = ((m_segHead + s) % m_capacity);
            var segment = m_segments[index];

            if (segment.BaseNativeFrame <= target) {
                slot = index;
                frameIndex = 0;
            }

            for (var i = 0; (i < segment.DeltaCount); ++i) {
                if (segment.NativeFrame[i] <= target) {
                    slot = index;
                    frameIndex = (i + 1);
                }
            }
        }
    }

    // Drop every captured frame newer than the landing (later deltas in the landing segment, and all newer segments).
    private void Truncate(int slot, int frameIndex) {
        m_segments[slot].DeltaCount = frameIndex;
        m_segCount = ((((slot - m_segHead) + m_capacity) % m_capacity) + 1);
    }

    // ---- Runahead -----------------------------------------------------------------------------------------------

    /// <summary>Arms (or, with 0 or less, disarms) runahead: a persistent lookahead fork kept N frames ahead on predicted
    /// input. The real machine is never touched, so it stays the sole audio source and its trajectory is identical
    /// whether runahead is on or off.</summary>
    /// <param name="frames">The number of frames to run ahead (clamped to <see cref="MaxRunaheadFrames"/>), or 0 to
    /// disarm.</param>
    public void SetRunahead(int frames) {
        if (frames <= 0) {
            DisableRunahead();

            return;
        }

        m_runaheadFrames = Math.Min(val1: frames, val2: MaxRunaheadFrames);
        m_lookahead?.Dispose();
        m_lookahead = m_core.CreateLookahead();
        m_lookaheadPrimed = false;
    }

    /// <summary>Advances the lookahead to stay exactly N native frames ahead of the authoritative machine — called after
    /// every stepped frame. On a prediction change (or the first advance) it rebases from the real machine's current
    /// state; then, whether primed or held, it advances the lookahead until it sits at the authority's current native
    /// frame plus N. Because it tracks the authority's OWN native-frame position (not a fixed one-frame-per-call step),
    /// the lead stays N under a mismatched host/native cadence and under fast-forward, where the authority advances
    /// several native frames per submission, rather than drifting ahead or behind. A no-op when runahead is disarmed.
    /// Reads the real machine only (a raw state capture).</summary>
    /// <param name="predicted">The predicted (currently-held) input image.</param>
    public void AdvanceLookahead(in TInput predicted) {
        if ((m_lookahead is not { } lookahead) || (m_runaheadFrames <= 0)) {
            return;
        }

        var realFrame = m_core.NativeFrameIndex;

        if (!m_lookaheadPrimed || !EqualityComparer<TInput>.Default.Equals(x: predicted, y: m_lastPrediction)) {
            // Rebase: restore the lookahead to the authority's current instant (its native frame becomes the authority's),
            // then the catch-up loop below re-advances it the configured N frames on the new prediction.
            var length = m_core.CaptureState(buffer: ref m_captureScratch);

            lookahead.RestoreState(buffer: m_captureScratch, length: length);

            m_lastPrediction = predicted;
            m_lookaheadPrimed = true;
        }

        // Catch the lookahead up until ITS OWN native-frame index reaches the authority's current native frame plus N. On
        // a prediction hold this advances by the authority's native-frame delta since the last call (0 when no native
        // frame completed, several under fast-forward); right after a rebase it advances the full N. Driving on the fork's
        // real index (not a per-RunFrame counter) reports the fork's TRUE lead — N, or N+1 in the instant the
        // boundary-reaching frame's instruction overshoots one past the target (it self-corrects to N the next call). The
        // fork is genuinely that many native frames ahead, rather than merely N RunFrame calls deep as the old counter
        // claimed.
        var target = (realFrame + m_runaheadFrames);

        while (lookahead.NativeFrameIndex < target) {
            lookahead.ApplyInput(input: in predicted);
            lookahead.RunFrame();
        }
    }

    /// <summary>Exposes the lookahead's framebuffer for display when runahead is live and primed — the host presents this
    /// instead of the authoritative machine's own framebuffer.</summary>
    /// <param name="framebuffer">The lookahead's framebuffer, when available.</param>
    /// <returns><see langword="true"/> when the caller should present the lookahead framebuffer.</returns>
    public bool TryGetDisplayFramebuffer(out ReadOnlySpan<uint> framebuffer) {
        if (RunaheadActive && m_lookaheadPrimed) {
            framebuffer = m_lookahead!.Framebuffer;

            return true;
        }

        framebuffer = default;

        return false;
    }

    private void DisableRunahead() {
        m_runaheadFrames = 0;
        m_lookaheadPrimed = false;
        m_lookahead?.Dispose();
        m_lookahead = null;
    }

    // ---- Status / lifecycle -------------------------------------------------------------------------------------

    /// <summary>Gets a one-instant read of the ring depth/footprint and the live runahead/fast-forward settings.</summary>
    /// <returns>The status.</returns>
    public TimeTravelStatus GetStatus() {
        var frames = 0;
        var oldest = long.MaxValue;
        var newest = long.MinValue;

        for (var s = 0; (s < m_segCount); ++s) {
            var segment = m_segments[((m_segHead + s) % m_capacity)];

            frames += (1 + segment.DeltaCount);
            oldest = Math.Min(val1: oldest, val2: segment.BaseCycle);
            newest = Math.Max(val1: newest, val2: segment.BaseCycle);

            for (var i = 0; (i < segment.DeltaCount); ++i) {
                newest = Math.Max(val1: newest, val2: segment.Cycle[i]);
            }
        }

        // Honest RETAINED footprint: EnsureRing eagerly constructs ALL m_capacity slots and each one's per-frame record
        // arrays up front (and grows every keyframe buffer to the state image as the ring fills), so the pinned memory is
        // the whole allocated capacity, not the live m_segCount — reporting the live count would undercount the reserved
        // slots until the ring filled. m_capacity is 0 before EnsureRing, so this reads 0 with no history.
        var bytes = ((long)m_capacity * RetainedBytesPerSegment());
        var spanSeconds = ((m_segCount == 0) ? 0.0 : ((double)(newest - oldest) / m_cyclesPerSecond));
        var lead = ((m_lookaheadPrimed && (m_lookahead is { } lookahead)) ? (int)(lookahead.NativeFrameIndex - m_core.NativeFrameIndex) : 0);

        return new TimeTravelStatus(
            RewindEnabled: m_enabled,
            DepthFrames: frames,
            SegmentCount: m_segCount,
            ByteFootprint: bytes,
            SpanSeconds: spanSeconds,
            RunaheadFrames: m_runaheadFrames,
            RunaheadLeadFrames: lead,
            FastForwardFactor: m_fastForward
        );
    }

    // The memory one live segment pins: its grown keyframe buffer plus the once-allocated per-frame record arrays
    // (cycle/native-frame/input/budget/accumulator, each sized interval-1). Sized against the current keyframe length so
    // the accounting tracks the real image, matching the per-span cost EnsureRing budgets the ring against.
    private long RetainedBytesPerSegment() =>
        ((long)m_snapshotSize + PerFrameRecordBytes());

    /// <summary>Clears the ring and tears down any lookahead — called on every content swap/reboot, since a fresh machine
    /// identity invalidates all captured history and the lookahead. Leaves the enabled/fast-forward settings intact.</summary>
    public void Reset() {
        ClearRing();
        DisableRunahead();
    }

    /// <summary>Integrates an authority advance the frame-oriented ring cannot replay — an instruction-granular sub-frame
    /// step, whose exact cycle boundary the (keyframe + whole-frame-input) reconstruction cannot express. Drops the
    /// captured rewind history so a later rewind never replays across the unrecorded instructions, and un-primes the
    /// runahead lookahead WITHOUT disarming it, so the pane falls back to the real machine until the next
    /// <see cref="AdvanceLookahead"/> rebases the fork from the now-advanced authority (the pane never serves the stale
    /// fork in the meantime). The rewind arming and the runahead configuration both survive; only the stale captured
    /// state is invalidated — the same drop-honestly hook class a poke or an out-of-band restore uses, minus the full
    /// runahead teardown, because rebase-on-next-advance is sound after an in-place instruction step.</summary>
    public void InvalidateForUnreplayableAdvance() {
        ClearRing();

        m_lookaheadPrimed = false;
    }

    /// <inheritdoc/>
    public void Dispose() => DisableRunahead();

    private void ClearRing() {
        m_segHead = 0;
        m_segCount = 0;

        for (var s = 0; (s < m_segments.Length); ++s) {
            m_segments[s].BaseLength = 0;
            m_segments[s].DeltaCount = 0;
        }
    }

    // Build (once) the segment ring sized so the whole ring stays within the memory budget. A keyframe span costs its
    // keyframe length PLUS its once-allocated per-frame record arrays (input/budget/accumulator/cycle/native-frame); the
    // ring never grows those after this, so budget / true-per-span-cost spans keep the retained footprint under budget.
    // A one-span ring is the documented degraded floor (it still rewinds within the most recent keyframe span); the ring
    // is never forced to two spans it cannot afford. A budget too small for even a single span is a configuration error,
    // rejected loudly rather than silently overrun.
    private void EnsureRing(int baseLength) {
        if (m_segments.Length != 0) {
            return;
        }

        var perSpan = Math.Max(val1: (baseLength + PerFrameRecordBytes()), val2: 1L);

        if (m_budgetBytes < perSpan) {
            throw new InvalidOperationException(
                message: $"The rewind memory budget ({m_budgetBytes} B) cannot hold a single {perSpan} B keyframe span; raise memoryBudgetBytes or lower keyframeIntervalFrames."
            );
        }

        var capacity = (int)Math.Clamp(value: (m_budgetBytes / perSpan), min: 1L, max: 4096L);

        m_capacity = capacity;
        m_segments = new Segment[capacity];

        for (var i = 0; (i < capacity); ++i) {
            m_segments[i] = new Segment(deltaCapacity: (m_interval - 1));
        }
    }

    // The retained size of one segment's per-frame record arrays: cycle + native-frame + budget + accumulator (four
    // 8-byte lanes) plus the held-input image, each sized interval-1. Fixed per segment; allocated once in the ctor.
    private long PerFrameRecordBytes() =>
        ((long)(m_interval - 1) * (32L + Unsafe.SizeOf<TInput>()));

    private void EnsureScratch(int size) {
        if (m_snapshotSize == size) {
            return;
        }

        m_snapshotSize = size;
    }

    /// <summary>One keyframe-anchored ring segment: a whole keyframe image (the restore anchor) plus up to
    /// <c>interval-1</c> per-frame records — the (input, cycle-budget, host-accumulator) each intervening frame was
    /// produced under, replayed onto the keyframe to reconstruct that frame. The keyframe buffer is reused (grow-only)
    /// and the record arrays are allocated once across segment recycles.</summary>
    private sealed class Segment {
        public Segment(int deltaCapacity) {
            Cycle = new long[deltaCapacity];
            NativeFrame = new long[deltaCapacity];
            Input = new TInput[deltaCapacity];
            Budget = new long[deltaCapacity];
            Accumulator = new ulong[deltaCapacity];
        }

        public byte[] Base = [];
        public int BaseLength;
        public long BaseCycle;
        public long BaseNativeFrame;
        public ulong BaseAccumulator;
        public readonly long[] Cycle;
        public readonly long[] NativeFrame;
        public readonly TInput[] Input;
        public readonly long[] Budget;
        public readonly ulong[] Accumulator;
        public int DeltaCount;
    }
}
