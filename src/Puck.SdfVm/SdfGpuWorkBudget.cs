using System.Globalization;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// One presentation's budget of background GPU work, which every residency's service draws on
/// (<see cref="SdfGpuWork"/>) in place of a budget each: a share of the GPU's time that follows what the frame's own work
/// leaves free (<see cref="Share"/>), by each job's measured GPU time, and at most
/// <see cref="SdfGpuWork.UploadBytesPerSecond"/> of host payload staged, for the residencies together.
/// <para>
/// The share follows the frame (<see cref="Observe"/>). The presentation reports, each frame, the display's period and the
/// GPU time the frame's own submissions took (every view's, nested views among them, read back from their timestamps);
/// every <see cref="FollowFrames"/> frames the budget sets its share to what the frames' mean leaves of the period, short
/// of <see cref="HeadroomShare"/> of it, between <see cref="LeastFrameSeconds"/> of each period and
/// <see cref="SdfGpuWork.GpuShare"/>. A frame that needs more of the period lowers the share at once; a frame that
/// needs less raises it by at most <see cref="RiseShare"/> a step, and not on the step after a fall, so a share that
/// fell does not climb back into the frame that pushed it down. With no reading (no display period, or no timed
/// submission: a device without timestamps, or one not read back yet) the share stands at
/// <see cref="SdfGpuWork.GpuShare"/> of a reference 60 Hz period and the budget says why (<see cref="Describe"/>).
/// Background work never shows in the frame's reading (its jobs are timed apart), so it cannot push its own share down,
/// and dynamic resolution, which reads the same views, scales the render grid only once their passes alone overrun the
/// period: optional background work gives way first.
/// </para>
/// <para>
/// One job of background work runs whole before the frame submitted after it, so the allowance a client cuts each job to
/// is one frame's: <see cref="FrameAllowanceSeconds"/>, the share of the period. An average share cannot protect one
/// frame from a long job queued ahead of it; a job cut to the frame allowance can.
/// </para>
/// <para>
/// Invariant: the jobs queued ahead of one frame together fit one frame allowance. Every service on the presentation's
/// queue shares this budget, and each job is admitted (<see cref="AllowanceOf"/>) only when its priced length and the
/// priced length of every charged job still in flight fit <see cref="FrameAllowanceSeconds"/>, so neither several slots
/// of allowance-sized jobs nor credit banked over a long gap (up to <see cref="MostBankSeconds"/> of the share) queue more
/// than an allowance ahead of a frame. The one exception keeps work progressing: a job with nothing queued ahead of it is
/// admitted whatever its price. The first service whose job does not fit what is left holds the budget
/// (<see cref="Hold"/>): every other budgeted job waits, so the queue drains, and the holder's next take then runs alone.
/// While the frame is over its period (<see cref="FrameOver"/>) the frame itself is what the job would queue behind, so
/// the holder's job runs only once the share has paid for it: the credit banks its price at the share's rate. A job past
/// the allowance is so admitted at most at the share's pace, and never not at all: a unit costlier than what is left (a
/// bake lattice, which is never split) would otherwise wait for good while another residency's service keeps the queue
/// busy or the frames stay over their period, and the bake behind it would never settle. The work the frame owes
/// (<see cref="OwedAllowance"/>) is not held, since it is the frame's own. The queued length moves only through a charge (<see cref="ChargeGpu"/>, which enters the
/// job) and that charge's leaving (<see cref="Leave"/>, once its job completed or was abandoned); the service leaves each
/// charge at the one place a slot gives up its job.
/// </para>
/// <para>
/// The claim that background work never costs a frame is counted (<see cref="FramesMissedBehind"/>). The service hands
/// over each job's span on the queue's timestamp clock (<see cref="Ran"/>) and the presentation each frame's
/// (<see cref="Observe"/>); a frame is behind background work when background work ran on the queue directly before its
/// first submission (no idle gap past <see cref="QueueGapSeconds"/>) or between its submissions, and it may have been missed
/// for it when that work and the frame's own GPU time together exceed the display's period. The count is an upper bound:
/// the queue's clock does not show when the frame reached the queue, so the whole run of background work ahead of it is
/// charged as its wait.
/// </para>
/// <para>
/// Credit is a rate of wall time. It accrues by the time since the last accrual, whichever service made it, at the live
/// share, so the work a second does not fall with the frame rate: a service that found the budget empty marks it wanted
/// (<see cref="Want"/>), and the next accrual then banks the whole gap, up to <see cref="MostBankSeconds"/> of it. An
/// accrual nobody waited on banks at most one frame allowance, so the frame after an idle spell carries no more
/// background work than any other. A service that is serviced while its host waits asks when the budget next holds
/// (<see cref="UntilHolds"/>) and comes back then.
/// </para>
/// <para>
/// Invariant: the GPU credit lies between a bank's worth and <see cref="MostDebtSeconds"/> of debt, whatever a job
/// reports. Two things hold it, both here: a settled charge is at most the wall time its job was in flight
/// (<see cref="MeasuredWithin"/>), since no job ran longer than that, and the credit is floored at the debt
/// (<see cref="ChargeGpu"/>, <see cref="SettleGpu"/> are the only writes that lower it). So one faulty measurement
/// (a timestamp pair read end before start wraps to nearly the counter's whole range) holds the work for at most
/// <see cref="MostDebtSeconds"/> over the share's seconds, never for good. A settlement replaces what the job's charge
/// actually debited (<see cref="ChargeGpu"/> returns it), never the estimate: the part of an estimate the floor dropped
/// was never taken, so it is never given back, and no settlement raises the credit above where the job's charge found
/// it.
/// </para>
/// <para>
/// Work the frame owes (an arrival's bake lattices: a group that draws nothing yet, or the first detail over the floor
/// it landed first, <see cref="SdfMeshPathBake.OwedGroups"/>) is frame work, not optional
/// background work, and has its own allowance (<see cref="OwedAllowance"/>): the GPU time the frame does not need, read
/// from the frames' measured interval less their own GPU time (<see cref="OwedFrameSecondsOf"/>), never less than
/// <see cref="SdfGpuWork.GpuShare"/> of the period and never more than one period. Owed jobs enter the queue ahead of
/// the next frame as every charged job does, so the jobs queued ahead of one frame fit the owed allowance and the
/// ordinary jobs still fit <see cref="FrameAllowanceSeconds"/>; they never draw on the credit, so the ordinary work
/// (landing, lighting) keeps its share beside them (<see cref="ChargeGpu"/>). The landing of a mesh a staged draw waits for
/// and the first light a shown view reads before its volume publishes (<see cref="SdfMeshIrradiance.OwesFirstLight"/>) are
/// an arrival's work too: one rule, the work a shown view displays missing, takes the GPU the frame leaves idle.
/// </para>
/// <para>
/// Invariant: the services draw on the budget in proportion to what the player sees of their residencies, never in the
/// order they are pumped. Each service holds a turn (<see cref="Join"/>) weighted by its residency's share of the
/// display's pixels (<see cref="Weigh"/>, at least <see cref="LeastWeight"/>), and every charged job, owed or not, moves
/// its turn's virtual time by its GPU seconds over that weight (<see cref="ChargeGpu"/>, <see cref="SettleGpu"/>). A
/// service takes a budgeted job only while its turn is no more than one frame allowance ahead of the least virtual time
/// among the other turns that took work within <see cref="ActiveSeconds"/> (<see cref="Takes"/>), so two services that
/// both have work divide the GPU by their weights, and one alone takes everything. A turn that comes back after taking
/// nothing joins the others' pace and banks none of the time it left unused. The one door is <see cref="Takes"/>, which
/// the service asks before any budgeted take; a held frame's own work is unbudgeted and takes no turn. It stops at the
/// weight: the service is weighed by its residency's latest captured frame, so a glass that leaves the view is weighed
/// at its old share until the residency next captures or goes a frame unread.
/// </para>
/// <para>Thread-safe.</para>
/// </summary>
/// <param name="time">The clock the credit accrues by (<see cref="IGpuDeviceContext.Time"/>); <see langword="null"/> reads
/// <see cref="TimeProvider.System"/>.</param>
public sealed class SdfGpuWorkBudget(TimeProvider? time = null) {
    /// <summary>The longest gap between accruals whose GPU time is banked whole when a service waited on the budget: a
    /// tenth of a second, the slowest pump the budget holds its rate across.</summary>
    public const double MostBankSeconds = 0.1;
    /// <summary>The most GPU time, in seconds, the budget owes: half a second. Charges past it are dropped and
    /// counted.</summary>
    public const double MostDebtSeconds = 0.5;
    /// <summary>The least GPU time, in seconds, each display period grants background work however full the frame is: one
    /// millisecond, so lighting and the bake never starve while the frame's own passes fill the period. Past that the
    /// frame keeps its time, and the lighting's age grows.</summary>
    public const double LeastFrameSeconds = 0.001;
    /// <summary>The share of the display period the budget leaves the frame beyond the GPU time its submissions measured:
    /// a tenth, for the time between them that no timestamp covers (the present, the queue's gaps) and for frame-to-frame
    /// variation the mean does not show.</summary>
    public const double HeadroomShare = 0.1;
    /// <summary>The period, in seconds, the allowance is read against while no display period is known: a 60 Hz
    /// frame.</summary>
    public const double ReferencePeriodSeconds = (1.0 / 60.0);
    /// <summary>The frames whose own GPU time one step of the share reads: eight, so the mean spans a view that renders
    /// on alternate frames and a step comes several times a second.</summary>
    public const int FollowFrames = 8;
    /// <summary>The most one step raises the share by: two hundredths, so the share climbs from the least to
    /// <see cref="SdfGpuWork.GpuShare"/> over about ten steps.</summary>
    public const double RiseShare = 0.02;
    /// <summary>The idle gap, in seconds of the queue's clock, past which a frame's first submission is taken to have
    /// waited for nothing that ran before it: a tenth of a millisecond, above the queue's own gap between two
    /// submissions.</summary>
    public const double QueueGapSeconds = 1e-4;
    /// <summary>The background jobs' queue spans the budget keeps to judge frames by: the latest 64, several frames'
    /// worth of every slot of several services.</summary>
    public const int KeptSpans = 64;
    /// <summary>The frames' readings a frame's judgement waits for after its own: two, so the background jobs that ran
    /// before it on the queue, whose fences signaled before its timestamps were written, have been answered and their spans
    /// handed over.</summary>
    public const int JudgeAfter = 2;
    /// <summary>The least weight a turn takes (<see cref="Weigh"/>): a sixty-fourth, so a residency no view shows still
    /// brings the meshes and lighting it owes before its glass shows, at a sixty-fourth of the pace of a residency the
    /// display shows whole.</summary>
    public const double LeastWeight = (1.0 / 64.0);
    /// <summary>The wall time, in seconds, since a turn's last charge during which it holds the other turns to its pace
    /// (<see cref="Takes"/>) unless it found nothing to take since (<see cref="Idle"/>): a tenth of a second, several
    /// frames, so a service whose next job waits on a deadline or a fence keeps its place.</summary>
    public const double ActiveSeconds = 0.1;

    // The host payload bytes the budget banks at most: a full slot ring of the largest jobs.
    private const double MostUploadCreditBytes = (SdfGpuWork.SlotCount * ((double)SdfGpuWork.MostUploadJobBytes));
    // The frames awaiting judgement at most; an older one is judged early rather than dropped.
    private const int MostPendingFrames = 8;

    private readonly Lock m_gate = new();
    private readonly TimeProvider m_time = (time ?? TimeProvider.System);
    private double m_credit = (SdfGpuWork.GpuShare * ReferencePeriodSeconds);
    private double m_uploadCredit = MostUploadCreditBytes;

    private long m_at;
    private bool m_wanted;
    private double m_charged;
    private double m_granted;
    private double m_uploaded;
    private long m_wants;
    private long m_clamped;
    private long m_floored;

    // The live share and the period it is read against, why it stands where it does, and the steps that lowered and
    // raised it. The window of frames the next step reads, the mean the last step read and whether the step after a
    // fall is still held.
    private double m_share = SdfGpuWork.GpuShare;
    private double m_period = ReferencePeriodSeconds;
    private string m_follows = "unread";
    private long m_falls;
    private long m_rises;
    private int m_windowFrames;
    private double m_windowSeconds;
    private double m_frameSeconds;
    private bool m_over;
    private bool m_holding;
    // The wall time between the frames' readings: when the latest came, the window's sum, and the mean the last step
    // read; and the owed allowance that step set (OwedFrameSecondsOf), with the owed jobs charged and their measured GPU
    // time.
    private long m_readingAt;
    private double m_windowWall;
    private int m_windowIntervals;
    private double m_interval;
    private double m_owedFrame = (SdfGpuWork.GpuShare * ReferencePeriodSeconds);
    private long m_owedJobs;
    private double m_owedSeconds;

    // The priced length of the charged jobs still in flight and how many they are: what is queued ahead of the next
    // frame. The length reads zero whenever the count does, so rounding left by entries and leavings never holds work.
    private double m_queued;
    private int m_queuedJobs;
    // The service holding the budget while the frame is over its period (Hold), the held job's priced length, when the
    // holder last asked, and the holds begun.
    private SdfGpuWorkTurn? m_holder;
    private double m_heldSeconds;
    private long m_heldAt;
    private long m_holds;
    // Every service's turn (Join), in the order they joined.
    private readonly List<SdfGpuWorkTurn> m_turns = [];

    // The latest background jobs' spans on the queue's clock (a ring), the frames awaiting judgement with the reading
    // they were taken at, and what the judgements counted.
    private readonly (double Start, double End)[] m_spans = new (double Start, double End)[KeptSpans];
    private int m_spanNext;
    private int m_spanCount;
    private readonly Queue<PendingFrame> m_pendingFrames = new();
    private long m_readings;
    private long m_framesJudged;
    private long m_framesBehind;
    private long m_framesMissed;
    private double m_behindSeconds;
    private double m_mostBehindSeconds;

    /// <summary>Gets whether the budget holds credit of both kinds, so a service may take its next job.</summary>
    public bool Holds {
        get {
            lock (m_gate) { return ((m_credit > 0.0) && (m_uploadCredit > 0.0)); }
        }
    }
    /// <summary>Gets how long from the last accrual until the budget holds credit of both kinds at its rates: zero when
    /// it holds now. A service its host services between frames runs again after it.</summary>
    public TimeSpan UntilHolds {
        get {
            lock (m_gate) {
                var gpu = ((m_credit > 0.0) ? 0.0 : (-m_credit / m_share));
                var upload = ((m_uploadCredit > 0.0) ? 0.0 : (-m_uploadCredit / SdfGpuWork.UploadBytesPerSecond));

                return TimeSpan.FromSeconds(value: Math.Max(val1: gpu, val2: upload));
            }
        }
    }
    /// <summary>Gets the most host payload bytes the next job may stage: what the upload budget holds, from
    /// <see cref="SdfGpuWork.UploadChunkBytes"/>, which the budget may overdraw once, to
    /// <see cref="SdfGpuWork.MostUploadJobBytes"/>.</summary>
    public int UploadAllowance {
        get {
            lock (m_gate) { return ((int)Math.Clamp(max: SdfGpuWork.MostUploadJobBytes, min: SdfGpuWork.UploadChunkBytes, value: m_uploadCredit)); }
        }
    }
    /// <summary>Gets the GPU credit the budget holds, in seconds; negative while it owes.</summary>
    public double GpuCredit {
        get {
            lock (m_gate) { return m_credit; }
        }
    }
    /// <summary>Gets the share of the GPU's time background work takes now: what the frame's own work leaves of the
    /// display's period, between <see cref="LeastFrameSeconds"/> a period and <see cref="SdfGpuWork.GpuShare"/>.</summary>
    public double Share {
        get {
            lock (m_gate) { return m_share; }
        }
    }
    /// <summary>Gets the GPU time, in seconds, one job of background work may take: the share of one display period. A
    /// job runs whole before the frame submitted after it, so every client cuts its jobs to this at its kind's measured
    /// rate (<see cref="SdfGpuWorkRate.UnitsWithin"/>). Safe to read from any thread.</summary>
    public double FrameAllowanceSeconds {
        get {
            lock (m_gate) { return (m_share * m_period); }
        }
    }
    /// <summary>Returns what a service's next budgeted job may take before the frame's deadline
    /// (<see cref="SdfGpuWorkAllowance"/>): the frame allowance less the priced length of the charged jobs in flight while
    /// any is; the whole allowance, admitting a job whatever its price, with nothing queued and room in the frame. While
    /// the frame is over its period with nothing queued, the job must fit the allowance, except the holder's
    /// (<see cref="Hold"/>) once the credit has banked its held job's price, which is admitted whatever its price. While a
    /// hold stands every other service's job waits (no seconds), so the queue drains for the holder. Safe to call from any
    /// thread.</summary>
    /// <param name="turn">The asking service's turn (<see cref="Join"/>).</param>
    /// <returns>The allowance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="turn"/> is <see langword="null"/>.</exception>
    public SdfGpuWorkAllowance AllowanceOf(SdfGpuWorkTurn turn) {
        ArgumentNullException.ThrowIfNull(argument: turn);

        var now = m_time.GetTimestamp();

        lock (m_gate) {
            var allowance = (m_share * m_period);
            var holder = HolderAt(now: now);

            if ((holder is not null) && !ReferenceEquals(objA: holder, objB: turn)) { return new(Queued: true, Seconds: 0.0); }
            if (m_queuedJobs > 0) { return new(Queued: true, Seconds: Math.Max(val1: 0.0, val2: (allowance - m_queued))); }
            if (!m_over || ((holder is not null) && (m_credit >= m_heldSeconds))) { return new(Queued: false, Seconds: allowance); }
            return new(Queued: true, Seconds: allowance);
        }
    }
    /// <summary>Holds the budget for a service whose next job, priced at <paramref name="seconds"/>, does not fit what is
    /// left of the allowance: every other service's budgeted job waits, so the jobs queued ahead drain, and with nothing
    /// queued the holder's take is admitted whatever its price, at once while the frame has room, and while the frame is
    /// over its period once the credit, which banks the price at the share's rate, covers it (<see cref="AllowanceOf"/>).
    /// Without it, a job past what is left waits for good while another service keeps the queue busy, or while the frame
    /// stays over its period. The hold stands while the holder asks again within <see cref="ActiveSeconds"/>, and ends at
    /// the holder's next charge, or when it finds nothing to take (<see cref="Idle"/>) or parts (<see cref="Part"/>). A
    /// service that asks while another holds holds nothing.</summary>
    /// <param name="turn">The service's turn.</param>
    /// <param name="seconds">The held job's priced GPU time, in seconds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="turn"/> is <see langword="null"/>.</exception>
    public void Hold(SdfGpuWorkTurn turn, double seconds) {
        ArgumentNullException.ThrowIfNull(argument: turn);
        if (!double.IsFinite(d: seconds) || !(seconds > 0.0)) { return; }

        var now = m_time.GetTimestamp();

        lock (m_gate) {
            if (HolderAt(now: now) is { } holder && !ReferenceEquals(objA: holder, objB: turn)) { return; }
            if (m_holder is null) { m_holds++; }
            m_holder = turn;
            m_heldSeconds = seconds;
            m_heldAt = now;
        }
    }
    // The hold standing at a time, under the gate: the holder while it asked again within ActiveSeconds; otherwise none,
    // and a lapsed hold is let go.
    private SdfGpuWorkTurn? HolderAt(long now) {
        if (m_holder is null) { return null; }
        if ((now - m_heldAt) <= ((long)(ActiveSeconds * m_time.TimestampFrequency))) { return m_holder; }
        LetGo();
        return null;
    }
    // Ends the hold, under the gate.
    private void LetGo() {
        m_holder = null;
        m_heldSeconds = 0.0;
    }
    /// <summary>Gets whether the frames' own GPU time, as the last step of the share read it, fills the display's period
    /// short of <see cref="HeadroomShare"/> (<see cref="FrameIsOver"/>): the budget then holds even an unqueued job to the
    /// frame allowance. Safe to read from any thread.</summary>
    public bool FrameOver {
        get {
            lock (m_gate) { return m_over; }
        }
    }
    /// <summary>Gets how long a service whose next job the deadline holds waits before it offers the job again: the priced
    /// length of the charged jobs in flight, and while the frame is over its period at least one period, since nothing
    /// queued will complete to wake it. Safe to read from any thread.</summary>
    public double DeadlineRetrySeconds {
        get {
            lock (m_gate) { return (m_over ? Math.Max(val1: m_queued, val2: m_period) : m_queued); }
        }
    }
    /// <summary>Returns whether a frame whose own submissions take <paramref name="frameSeconds"/> of GPU time each
    /// <paramref name="periodSeconds"/> leaves background work no room: it fills the period short of
    /// <see cref="HeadroomShare"/> of it, the point past which <see cref="ShareOf"/> falls to its least.</summary>
    /// <param name="periodSeconds">The display's period, in seconds; positive.</param>
    /// <param name="frameSeconds">The frame's own GPU time, in seconds.</param>
    /// <returns><see langword="true"/> when the frame is over its period.</returns>
    public static bool FrameIsOver(double periodSeconds, double frameSeconds) =>
        (double.IsFinite(d: frameSeconds) && (frameSeconds >= ((1.0 - HeadroomShare) * periodSeconds)));
    /// <summary>Gets what the next job the frame owes may take before the frame's deadline: the owed frame
    /// allowance (<see cref="OwedFrameSeconds"/>) less the priced length of every charged job in flight, owed or not, and
    /// whether any is. Safe to read from any thread.</summary>
    public SdfGpuWorkAllowance OwedAllowance {
        get {
            lock (m_gate) {
                return ((m_queuedJobs == 0) ? new(Queued: false, Seconds: m_owedFrame) : new(Queued: true, Seconds: Math.Max(val1: 0.0, val2: (m_owedFrame - m_queued))));
            }
        }
    }
    /// <summary>Gets the GPU time, in seconds, the jobs the frame owes may queue ahead of one frame, as the last
    /// step of the share set it (<see cref="OwedFrameSecondsOf"/>). Safe to read from any thread.</summary>
    public double OwedFrameSeconds {
        get {
            lock (m_gate) { return m_owedFrame; }
        }
    }
    /// <summary>Returns the GPU time, in seconds, the work the frame owes may queue ahead of one frame: what the
    /// frames leave idle of the time between them (their measured interval, no shorter than the display's period, short of
    /// <see cref="HeadroomShare"/> of it, less their own GPU time), never less than <see cref="SdfGpuWork.GpuShare"/> of the
    /// period, which ordinary background work takes beside an unloaded frame, and never more than one period, so a frame
    /// waits at most that long behind it. The interval counts what owed work delayed a frame by, but only short of the
    /// headroom share, so the allowance it sets settles rather than feeding on itself.</summary>
    /// <param name="periodSeconds">The display's period, in seconds; positive.</param>
    /// <param name="intervalSeconds">The frames' mean wall time between readings, in seconds; zero or less when not
    /// measured, read as the period.</param>
    /// <param name="frameSeconds">The frames' own GPU time, in seconds.</param>
    /// <returns>The owed allowance, in seconds.</returns>
    public static double OwedFrameSecondsOf(double periodSeconds, double intervalSeconds, double frameSeconds) {
        var least = (SdfGpuWork.GpuShare * periodSeconds);
        var idle = (((1.0 - HeadroomShare) * Math.Max(val1: periodSeconds, val2: (double.IsFinite(d: intervalSeconds) ? intervalSeconds : 0.0))) - Math.Max(val1: 0.0, val2: frameSeconds));

        return (double.IsNaN(d: idle) ? least : Math.Clamp(max: periodSeconds, min: least, value: idle));
    }
    /// <summary>Gets the priced length, in seconds, of the charged jobs still in flight: the background work queued ahead
    /// of the next frame.</summary>
    public double QueuedSeconds {
        get {
            lock (m_gate) { return m_queued; }
        }
    }
    /// <summary>Gets the frames judged so far that background work may have cost: those behind background work whose own
    /// GPU time and that work together exceeded the display's period. An upper bound; zero supports the claim that
    /// background work never costs a frame.</summary>
    public long FramesMissedBehind {
        get {
            lock (m_gate) { return m_framesMissed; }
        }
    }
    /// <summary>Gets the frames judged so far that background work ran directly ahead of, or between the submissions
    /// of, whether or not it cost them their period.</summary>
    public long FramesBehind {
        get {
            lock (m_gate) { return m_framesBehind; }
        }
    }

    /// <summary>Returns the GPU time, in seconds, a job is charged for a measurement: the measurement when it lies
    /// between zero and the wall time the job was in flight, and otherwise the job's estimate held to that time, since
    /// a job never ran longer than it was in flight.</summary>
    /// <param name="estimate">The seconds the job was estimated to take.</param>
    /// <param name="measured">The seconds its timestamps read.</param>
    /// <param name="inFlight">The wall time, in seconds, between the job's submission and its answer.</param>
    /// <param name="clamped">Whether the measurement was not one the job could have made.</param>
    /// <returns>The seconds to charge, never negative.</returns>
    public static double MeasuredWithin(double estimate, double measured, double inFlight, out bool clamped) {
        var most = Math.Max(val1: 0.0, val2: inFlight);

        clamped = !((measured >= 0.0) && (measured <= most));

        return (clamped ? Math.Clamp(max: most, min: 0.0, value: (double.IsNaN(d: estimate) ? 0.0 : estimate)) : measured);
    }
    /// <summary>Returns the share of the GPU's time background work may take beside a frame whose own submissions take
    /// <paramref name="frameSeconds"/> of GPU time each <paramref name="periodSeconds"/>: what they leave of the period
    /// short of <see cref="HeadroomShare"/>, between <see cref="LeastFrameSeconds"/> of the period and
    /// <see cref="SdfGpuWork.GpuShare"/>.</summary>
    /// <param name="periodSeconds">The display's period, in seconds; positive.</param>
    /// <param name="frameSeconds">The frame's own GPU time, in seconds.</param>
    /// <returns>The share.</returns>
    public static double ShareOf(double periodSeconds, double frameSeconds) {
        var least = Math.Min(val1: (LeastFrameSeconds / periodSeconds), val2: SdfGpuWork.GpuShare);
        var free = ((((1.0 - HeadroomShare) * periodSeconds) - Math.Max(val1: 0.0, val2: frameSeconds)) / periodSeconds);

        return (double.IsNaN(d: free) ? least : Math.Clamp(max: SdfGpuWork.GpuShare, min: least, value: free));
    }
    /// <summary>Takes one presented frame's reading: the display's period and the GPU time the frame's own submissions
    /// took, read back since the previous frame (zero for a frame that rendered nothing new). Every
    /// <see cref="FollowFrames"/> readings it moves the share to <see cref="ShareOf"/> their mean: down at once, up by at
    /// most <see cref="RiseShare"/> and not on the step after a fall. Lockstep and held frames never read the share
    /// (their work is unbudgeted), so a drain's result does not depend on it. A reading that carries the frame's span on
    /// the queue's clock is judged against the background jobs' spans <see cref="JudgeAfter"/> readings later
    /// (<see cref="FramesMissedBehind"/>).</summary>
    /// <param name="periodSeconds">The display's period, in seconds; positive.</param>
    /// <param name="frameSeconds">The frame's own GPU time read back since the previous reading, in seconds.</param>
    /// <param name="queueStartSeconds">When the frame's first submission read back began, in seconds on the queue's
    /// timestamp clock (the clock <see cref="Ran"/> reads).</param>
    /// <param name="queueEndSeconds">When its last submission read back ended, on the same clock; no later than
    /// <paramref name="queueStartSeconds"/> for a reading with no span, which is not judged.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="periodSeconds"/> is not positive.</exception>
    public void Observe(double periodSeconds, double frameSeconds, double queueStartSeconds = 0.0, double queueEndSeconds = 0.0) {
        if (!(periodSeconds > 0.0)) { throw new ArgumentOutOfRangeException(paramName: nameof(periodSeconds), actualValue: periodSeconds, message: "A display period is positive."); }

        var now = m_time.GetTimestamp();

        lock (m_gate) {
            if ((m_readingAt != 0) && (now > m_readingAt)) {
                m_windowWall += ((now - m_readingAt) / ((double)m_time.TimestampFrequency));
                m_windowIntervals++;
            }
            m_readingAt = now;
            if (queueEndSeconds > queueStartSeconds) {
                if (m_pendingFrames.Count >= MostPendingFrames) { Judge(frame: m_pendingFrames.Dequeue()); }
                m_pendingFrames.Enqueue(item: new PendingFrame(End: queueEndSeconds, Own: (double.IsFinite(d: frameSeconds) ? Math.Max(val1: 0.0, val2: frameSeconds) : 0.0), Period: periodSeconds, Reading: m_readings, Start: queueStartSeconds));
            }
            m_readings++;
            while ((m_pendingFrames.Count > 0) && ((m_readings - m_pendingFrames.Peek().Reading) > JudgeAfter)) { Judge(frame: m_pendingFrames.Dequeue()); }

            if (m_follows != "frame") {
                // A reading after none starts a window of its own.
                m_follows = "frame";
                m_windowFrames = 0;
                m_windowSeconds = 0.0;
                m_windowWall = 0.0;
                m_windowIntervals = 0;
            }
            m_period = periodSeconds;
            m_windowFrames++;
            m_windowSeconds += (double.IsFinite(d: frameSeconds) ? Math.Max(val1: 0.0, val2: frameSeconds) : 0.0);
            if (m_windowFrames < FollowFrames) { return; }
            m_frameSeconds = (m_windowSeconds / m_windowFrames);
            m_over = FrameIsOver(periodSeconds: periodSeconds, frameSeconds: m_frameSeconds);
            m_interval = ((m_windowIntervals > 0) ? (m_windowWall / m_windowIntervals) : 0.0);
            m_owedFrame = OwedFrameSecondsOf(frameSeconds: m_frameSeconds, intervalSeconds: m_interval, periodSeconds: periodSeconds);
            m_windowFrames = 0;
            m_windowSeconds = 0.0;
            m_windowWall = 0.0;
            m_windowIntervals = 0;

            var target = ShareOf(frameSeconds: m_frameSeconds, periodSeconds: periodSeconds);

            if (target < m_share) {
                m_share = target;
                m_holding = true;
                m_falls++;
            } else if (m_holding) {
                m_holding = false;
            } else if (target > m_share) {
                m_share = Math.Min(val1: target, val2: (m_share + RiseShare));
                m_rises++;
            }
        }
    }
    /// <summary>Takes a frame with no reading: the share and the owed allowance stand at <see cref="SdfGpuWork.GpuShare"/>
    /// of the display's period, or of <see cref="ReferencePeriodSeconds"/> when none is known, and the budget names
    /// why.</summary>
    /// <param name="periodSeconds">The display's period, in seconds; zero or less when none is known.</param>
    /// <param name="reason">Why there is no reading, one word for <see cref="Describe"/>.</param>
    public void Unread(double periodSeconds, string reason) {
        ArgumentNullException.ThrowIfNull(argument: reason);

        lock (m_gate) {
            m_follows = reason;
            m_over = false;
            LetGo();
            m_period = ((periodSeconds > 0.0) ? periodSeconds : ReferencePeriodSeconds);
            m_share = SdfGpuWork.GpuShare;
            m_owedFrame = (SdfGpuWork.GpuShare * m_period);
            m_holding = false;
            m_windowFrames = 0;
            m_windowSeconds = 0.0;
            m_windowWall = 0.0;
            m_windowIntervals = 0;
            m_readingAt = 0;
        }
    }
    /// <summary>Banks the GPU time and the upload bytes their rates allow since the last accrual by any service.</summary>
    public void Accrue() {
        var now = m_time.GetTimestamp();

        lock (m_gate) {
            var seconds = ((m_at != 0) ? Math.Max(val1: 0.0, val2: ((now - m_at) / ((double)m_time.TimestampFrequency))) : 0.0);
            // The cap bounds what an accrual adds, never what the budget already holds: several services accrue in one
            // frame, and all but the first find no time has passed. A hold banks up to its job's price.
            var cap = (m_wanted ? (MostBankSeconds * m_share) : (m_share * m_period));

            if (m_over && (HolderAt(now: now) is not null)) { cap = Math.Max(val1: cap, val2: m_heldSeconds); }
            var before = m_credit;

            m_credit = Math.Min(val1: (m_credit + (seconds * m_share)), val2: Math.Max(val1: m_credit, val2: cap));
            m_granted += (m_credit - before);
            m_uploadCredit = Math.Min(val1: (m_uploadCredit + (seconds * SdfGpuWork.UploadBytesPerSecond)), val2: Math.Max(val1: m_uploadCredit, val2: MostUploadCreditBytes));
            m_at = now;
            if (seconds > 0.0) { m_wanted = false; }
        }
    }
    /// <summary>Marks that a service found the budget empty with a job to take, so the next accrual banks its whole
    /// gap.</summary>
    public void Want() {
        lock (m_gate) {
            m_wanted = true;
            m_wants++;
        }
    }
    /// <summary>Gives a service a turn on the budget, at full weight, until it parts (<see cref="Part"/>).</summary>
    /// <param name="name">The service's name, which <see cref="Describe"/> prints the turn under.</param>
    /// <returns>The turn.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public SdfGpuWorkTurn Join(string name) {
        ArgumentNullException.ThrowIfNull(argument: name);

        var turn = new SdfGpuWorkTurn(name: name);

        lock (m_gate) { m_turns.Add(item: turn); }
        return turn;
    }
    /// <summary>Takes a service's turn off the budget: it holds no other turn back from then on.</summary>
    /// <param name="turn">The turn <see cref="Join"/> gave.</param>
    public void Part(SdfGpuWorkTurn turn) {
        lock (m_gate) {
            _ = m_turns.Remove(item: turn);
            if (ReferenceEquals(objA: m_holder, objB: turn)) { LetGo(); }
        }
    }
    /// <summary>Returns the weight a turn takes for a residency's share of the display's pixels: the share, held between
    /// <see cref="LeastWeight"/> and one; one for a share that is not a number.</summary>
    /// <param name="share">The share, in [0, 1].</param>
    /// <returns>The weight.</returns>
    public static double WeightOf(double share) => (double.IsNaN(d: share) ? 1.0 : Math.Clamp(max: 1.0, min: LeastWeight, value: share));
    /// <summary>Weighs a turn by its residency's share of the display's pixels (<see cref="WeightOf"/>), which the jobs it
    /// charges from now on move its virtual time over.</summary>
    /// <param name="turn">The turn.</param>
    /// <param name="share">The share, in [0, 1]; zero for a residency no view shows.</param>
    public void Weigh(SdfGpuWorkTurn turn, double share) {
        ArgumentNullException.ThrowIfNull(argument: turn);

        lock (m_gate) { turn.Weight = WeightOf(share: share); }
    }
    /// <summary>Returns whether a service may take a budgeted job now: its turn's virtual time is within one frame
    /// allowance of the least among the other turns that took work within <see cref="ActiveSeconds"/> and have not found
    /// nothing to take since, or no other turn has. A turn that has not taken work in that span is first brought up to
    /// one frame allowance behind that least, so the time it left unused is never banked.</summary>
    /// <param name="turn">The service's turn.</param>
    /// <returns><see langword="true"/> when the service may take a job.</returns>
    public bool Takes(SdfGpuWorkTurn turn) {
        ArgumentNullException.ThrowIfNull(argument: turn);

        var now = m_time.GetTimestamp();
        var active = ((long)(ActiveSeconds * m_time.TimestampFrequency));

        lock (m_gate) {
            var least = double.PositiveInfinity;

            foreach (var other in m_turns) {
                if (!ReferenceEquals(objA: other, objB: turn) && Active(candidate: other)) { least = Math.Min(val1: least, val2: other.Virtual); }
            }
            if (double.IsPositiveInfinity(d: least)) { return true; }

            var slack = (m_share * m_period);

            if (!Active(candidate: turn)) { turn.Virtual = Math.Max(val1: turn.Virtual, val2: (least - slack)); }
            if (turn.Virtual <= (least + slack)) { return true; }
            turn.Holds++;
            return false;
        }

        bool Active(SdfGpuWorkTurn candidate) => ((candidate.ActiveAt != 0L) && ((now - candidate.ActiveAt) <= active));
    }
    /// <summary>Marks that a service found nothing to take: its turn holds no other turn to its pace until it next takes
    /// work.</summary>
    /// <param name="turn">The service's turn.</param>
    public void Idle(SdfGpuWorkTurn turn) {
        ArgumentNullException.ThrowIfNull(argument: turn);

        lock (m_gate) {
            turn.ActiveAt = 0L;
            if (ReferenceEquals(objA: m_holder, objB: turn)) { LetGo(); }
        }
    }
    /// <summary>Charges a submitted job's estimated GPU time and enters it among the jobs queued ahead of the next frame
    /// (<see cref="AllowanceOf"/>, <see cref="OwedAllowance"/>) until its charge leaves (<see cref="Leave"/>). A job the
    /// frame owes enters the queue alone and debits nothing: it is held to the owed allowance, not the credit.</summary>
    /// <param name="seconds">The estimate, in seconds.</param>
    /// <param name="owed">Whether the job is work the frame owes, taken under <see cref="OwedAllowance"/>.</param>
    /// <param name="turn">The submitting service's turn (<see cref="Join"/>), whose virtual time the job moves by its
    /// estimate over the turn's weight, and which counts as having taken work now; <see langword="null"/> for none.</param>
    /// <returns>The charge: the seconds it debited (the estimate, less what the debt's floor dropped; zero for an owed
    /// job), which the job's settlement (<see cref="SettleGpu"/>) replaces, and the seconds it entered the queue with,
    /// which its leaving takes out.</returns>
    public SdfGpuWorkCharge ChargeGpu(double seconds, bool owed = false, SdfGpuWorkTurn? turn = null) {
        var queued = ((double.IsFinite(d: seconds) && (seconds > 0.0)) ? seconds : 0.0);
        var now = m_time.GetTimestamp();

        lock (m_gate) {
            m_queued += queued;
            m_queuedJobs++;

            var weight = 1.0;

            if (turn is not null) {
                weight = turn.Weight;
                turn.Virtual += (queued / weight);
                turn.Charged += queued;
                // Zero marks an idle turn (Idle), which a clock that reads zero must not.
                turn.ActiveAt = Math.Max(val1: now, val2: 1L);
            }
            if (owed) {
                m_owedJobs++;
                return new SdfGpuWorkCharge(Debited: 0.0, Queued: queued) { Owed = true, Turn = turn, Weight = weight };
            }
            // The holder's job is the one its hold banked for.
            if ((turn is not null) && ReferenceEquals(objA: m_holder, objB: turn)) { LetGo(); }

            return new SdfGpuWorkCharge(Debited: Charge(seconds: seconds), Queued: queued) { Turn = turn, Weight = weight };
        }
    }
    /// <summary>Takes a charged job out of the jobs queued ahead of the next frame: its job completed or was abandoned.
    /// Each charge leaves once; a charge no job made (<see langword="default"/>) leaves nothing.</summary>
    /// <param name="charge">The job's charge (<see cref="ChargeGpu"/>'s answer).</param>
    public void Leave(in SdfGpuWorkCharge charge) {
        if (!charge.Entered) { return; }
        lock (m_gate) {
            m_queuedJobs = Math.Max(val1: 0, val2: (m_queuedJobs - 1));
            m_queued = ((m_queuedJobs == 0) ? 0.0 : Math.Max(val1: 0.0, val2: (m_queued - charge.Queued)));
        }
    }
    /// <summary>Replaces what a charged job debited with its measured GPU time, held to what the job could have run
    /// (<see cref="MeasuredWithin"/>), and counts a measurement that was not one. It gives back at most what the charge
    /// debited. An owed job's time is counted apart and debits nothing.</summary>
    /// <param name="charge">The job's charge (<see cref="ChargeGpu"/>'s answer), whose debit the measurement
    /// replaces.</param>
    /// <param name="estimate">The seconds the job was estimated to take, which a measurement no job could have made
    /// falls back to.</param>
    /// <param name="measured">The seconds its timestamps read.</param>
    /// <param name="inFlight">The wall time, in seconds, between the job's submission and its answer.</param>
    /// <returns>The seconds the job is charged in the estimate's place.</returns>
    public double SettleGpu(in SdfGpuWorkCharge charge, double estimate, double measured, double inFlight) {
        var seconds = MeasuredWithin(clamped: out var clamped, estimate: estimate, inFlight: inFlight, measured: measured);

        lock (m_gate) {
            if (clamped) { m_clamped++; }
            if (charge.Turn is { } turn) {
                turn.Virtual += ((seconds - charge.Queued) / charge.Weight);
                turn.Charged += (seconds - charge.Queued);
            }
            if (charge.Owed) {
                m_owedSeconds += seconds;
            } else {
                _ = Charge(seconds: (seconds - charge.Debited));
            }
        }

        return seconds;
    }
    /// <summary>Takes a background job's span on the queue's timestamp clock, which frames are judged against
    /// (<see cref="FramesMissedBehind"/>). The budget keeps the latest <see cref="KeptSpans"/>.</summary>
    /// <param name="startSeconds">When the job began, in seconds on the queue's clock.</param>
    /// <param name="endSeconds">When it ended, on the same clock; a span that does not end after it begins is
    /// ignored.</param>
    public void Ran(double startSeconds, double endSeconds) {
        if (!(endSeconds > startSeconds) || !double.IsFinite(d: startSeconds) || !double.IsFinite(d: endSeconds)) { return; }
        lock (m_gate) {
            m_spans[m_spanNext] = (startSeconds, endSeconds);
            m_spanNext = ((m_spanNext + 1) % KeptSpans);
            m_spanCount = Math.Min(val1: KeptSpans, val2: (m_spanCount + 1));
        }
    }
    /// <summary>Charges the host payload bytes a taken job stages.</summary>
    /// <param name="bytes">The bytes.</param>
    public void ChargeUpload(long bytes) {
        lock (m_gate) {
            m_uploadCredit -= bytes;
            m_uploaded += bytes;
        }
    }
    /// <summary>Describes the budget: the live share against its cap, one frame's allowance, the period and the frame's own
    /// GPU time it read (or why it read none), the frames' mean interval, the owed allowance it set and the owed jobs
    /// charged with their measured GPU time, the steps that lowered and raised the share, the credit it holds, what it
    /// granted and what its services charged since it was made (their ratio is how much of the allowance the work used),
    /// how often one found it empty, the measurements no job could have made and the charges dropped at the debt's
    /// floor; then the work queued ahead of the next frame and the holds begun while the frame was over its period with the one standing (<see cref="Hold"/>), and the frames judged against the background jobs' spans:
    /// those behind background work, those it may have cost (<see cref="FramesMissedBehind"/>), and the background
    /// GPU time ahead of the frames behind it, in all and at most; then each service's turn: its weight, the GPU time its
    /// jobs were charged and the takes its turn held back.</summary>
    /// <returns>One space-led field.</returns>
    public string Describe() {
        lock (m_gate) {
            var turns = new System.Text.StringBuilder();

            foreach (var turn in m_turns) {
                _ = turns.Append(provider: CultureInfo.InvariantCulture, handler: $"{((turns.Length == 0) ? string.Empty : ",")}{turn.Name}:{turn.Weight:0.000}/{(turn.Charged * 1000.0):0}ms/{turn.Holds}");
            }
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $" work-turns=[{turns}] work-budget=[gpu-share:{m_share:0.000},share-cap:{SdfGpuWork.GpuShare:0.00},frame-allowance:{(m_share * m_period * 1000.0):0.00}ms,period:{(m_period * 1000.0):0.00}ms,frame-gpu:{(m_frameSeconds * 1000.0):0.00}ms,over-period:{(m_over ? "yes" : "no")},interval:{(m_interval * 1000.0):0.00}ms,owed-allowance:{(m_owedFrame * 1000.0):0.00}ms,owed-jobs:{m_owedJobs},owed-gpu:{(m_owedSeconds * 1000.0):0}ms,follows:{m_follows},falls:{m_falls},rises:{m_rises},gpu-credit:{(m_credit * 1000.0):0.00}ms,gpu-granted:{(m_granted * 1000.0):0}ms,gpu-charged:{(m_charged * 1000.0):0}ms,upload-rate:{SdfGpuWork.UploadBytesPerSecond:0}B/s,upload-credit:{m_uploadCredit:0}B,uploaded:{m_uploaded:0}B,wants:{m_wants},clamped:{m_clamped},floored:{m_floored},queued:{m_queuedJobs}/{(m_queued * 1000.0):0.00}ms,holds:{m_holds},holding:{(m_holder?.Name ?? "none")}/{(m_heldSeconds * 1000.0):0.00}ms] frames-behind=[judged:{m_framesJudged},behind:{m_framesBehind},missed:{m_framesMissed},behind-gpu:{(m_behindSeconds * 1000.0):0.0}ms,most-behind:{(m_mostBehindSeconds * 1000.0):0.00}ms]");
        }
    }

    // Judges one frame against the background jobs' spans, under the gate. The background work behind which it ran is
    // the run of spans ending at its first submission with no idle gap past QueueGapSeconds, and every span between its
    // first submission's start and its last's end; their union's length is the wait charged to it. The frame is behind
    // when that wait is positive, and may have been missed for it when the wait and its own GPU time exceed its period.
    private void Judge(PendingFrame frame) {
        Span<(double Start, double End)> spans = stackalloc (double Start, double End)[KeptSpans];
        var count = 0;
        var from = frame.Start;

        // The run ahead: each pass takes the run's start back past every span that reaches it.
        for (var extended = true; extended;) {
            extended = false;
            for (var index = 0; (index < m_spanCount); index++) {
                var (start, end) = m_spans[index];

                if ((start < from) && (end >= (from - QueueGapSeconds))) {
                    from = start;
                    extended = true;
                }
            }
        }
        for (var index = 0; (index < m_spanCount); index++) {
            var (start, end) = m_spans[index];
            var clippedStart = Math.Max(val1: start, val2: from);
            var clippedEnd = Math.Min(val1: end, val2: frame.End);

            if (clippedEnd > clippedStart) { spans[count++] = (clippedStart, clippedEnd); }
        }
        spans[..count].Sort(comparison: static (a, b) => a.Start.CompareTo(value: b.Start));

        var wait = 0.0;
        var reach = double.NegativeInfinity;

        foreach (var (start, end) in spans[..count]) {
            var begin = Math.Max(val1: start, val2: reach);

            if (end > begin) { wait += (end - begin); }
            reach = Math.Max(val1: reach, val2: end);
        }
        m_framesJudged++;
        if (!(wait > 0.0)) { return; }
        m_framesBehind++;
        m_behindSeconds += wait;
        m_mostBehindSeconds = Math.Max(val1: m_mostBehindSeconds, val2: wait);
        if ((wait + frame.Own) > frame.Period) { m_framesMissed++; }
    }

    // The one write that moves the GPU credit by a charge: under the gate. A charge that is not a number changes
    // nothing, and the debt stops at its floor. Answers the seconds the credit actually fell by (negative for a refund).
    private double Charge(double seconds) {
        if (double.IsNaN(d: seconds)) {
            m_floored++;

            return 0.0;
        }

        var before = m_credit;

        m_credit -= seconds;
        if (m_credit < -MostDebtSeconds) {
            m_credit = Math.Min(val1: before, val2: -MostDebtSeconds);
            m_floored++;
        }

        var debited = (before - m_credit);

        m_charged += debited;
        return debited;
    }

    // A frame's reading awaiting judgement: its span on the queue's clock, its own GPU time and period, all in seconds,
    // and the reading it was taken at.
    private readonly record struct PendingFrame(double Start, double End, double Own, double Period, long Reading);
}
/// <summary>One job's charge to a <see cref="SdfGpuWorkBudget"/> (<see cref="SdfGpuWorkBudget.ChargeGpu"/>): what it
/// debited from the GPU credit, which its settlement replaces, and the priced length it entered the queue ahead of the
/// next frame with, which its leaving (<see cref="SdfGpuWorkBudget.Leave"/>) takes out.</summary>
/// <param name="Debited">The seconds the charge debited.</param>
/// <param name="Queued">The seconds the job entered the queue with: its estimate, or zero for one that is not a positive
/// number.</param>
public readonly record struct SdfGpuWorkCharge(double Debited, double Queued) {
    // Only a charge the budget made has a job in the queue: the default charge (a slot with no charged job) has not.
    internal bool Entered { get; } = true;
    // Whether the job is work the frame owes, whose time the credit never carries (SdfGpuWorkBudget.ChargeGpu).
    internal bool Owed { get; init; }
    // The turn the job moved and the weight it moved it at, which its settlement moves it at again.
    internal SdfGpuWorkTurn? Turn { get; init; }
    internal double Weight { get; init; } = 1.0;
}
/// <summary>A service's turn on a <see cref="SdfGpuWorkBudget"/> (<see cref="SdfGpuWorkBudget.Join"/>): its weight, its
/// residency's share of the display's pixels, and the virtual time its charged jobs moved, by which the budget orders the
/// services' takes (<see cref="SdfGpuWorkBudget.Takes"/>). Its state is read and written only under the budget's
/// gate.</summary>
public sealed class SdfGpuWorkTurn {
    internal SdfGpuWorkTurn(string name) { Name = name; }

    /// <summary>Gets the name of the service that holds the turn.</summary>
    public string Name { get; }
    // The weight (SdfGpuWorkBudget.Weigh), the GPU seconds charged over the weight, the seconds charged, when the turn
    // last took work (a timestamp of the budget's clock, zero once it found nothing to take) and the takes it was held back from.
    internal double Weight { get; set; } = 1.0;
    internal double Virtual { get; set; }
    internal double Charged { get; set; }
    internal long ActiveAt { get; set; }
    internal long Holds { get; set; }
}
