using System.Diagnostics;
using System.Globalization;

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
/// queue shares this budget, and each job is admitted (<see cref="Allowance"/>) only when its priced length and the
/// priced length of every charged job still in flight fit <see cref="FrameAllowanceSeconds"/>, so neither several slots
/// of allowance-sized jobs nor credit banked over a long gap (up to <see cref="MostBankSeconds"/> of the share) queue more
/// than an allowance ahead of a frame. The one exception keeps work progressing: a job with nothing queued ahead of it is
/// admitted whatever its price. The queued length moves only through a charge (<see cref="ChargeGpu"/>, which enters the
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
/// (landing, lighting) keeps its share beside them (<see cref="ChargeGpu"/>).
/// </para>
/// <para>The services take from it first come, in the order their residencies are pumped. Thread-safe.</para>
/// </summary>
/// <param name="clock">Reads the time, in <see cref="Stopwatch"/> ticks; <see langword="null"/> reads
/// <see cref="Stopwatch.GetTimestamp"/>.</param>
public sealed class SdfGpuWorkBudget(Func<long>? clock = null) {
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

    // The host payload bytes the budget banks at most: a full slot ring of the largest jobs.
    private const double MostUploadCreditBytes = (SdfGpuWork.SlotCount * ((double)SdfGpuWork.MostUploadJobBytes));
    // The frames awaiting judgement at most; an older one is judged early rather than dropped.
    private const int MostPendingFrames = 8;

    private readonly Lock m_gate = new();
    private readonly Func<long> m_clock = (clock ?? Stopwatch.GetTimestamp);
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
    /// <summary>Gets what the next budgeted job may take before the frame's deadline: the frame allowance less the priced
    /// length of the charged jobs in flight, and whether any is (<see cref="SdfGpuWorkAllowance"/>). Safe to read from any
    /// thread.</summary>
    public SdfGpuWorkAllowance Allowance {
        get {
            lock (m_gate) {
                var allowance = (m_share * m_period);

                return ((m_queuedJobs == 0) ? new(Queued: false, Seconds: allowance) : new(Queued: true, Seconds: Math.Max(val1: 0.0, val2: (allowance - m_queued))));
            }
        }
    }
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

        var now = m_clock();

        lock (m_gate) {
            if ((m_readingAt != 0) && (now > m_readingAt)) {
                m_windowWall += ((now - m_readingAt) / ((double)Stopwatch.Frequency));
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
        var now = m_clock();

        lock (m_gate) {
            var seconds = ((m_at != 0) ? Math.Max(val1: 0.0, val2: ((now - m_at) / ((double)Stopwatch.Frequency))) : 0.0);
            // The cap bounds what an accrual adds, never what the budget already holds: several services accrue in one
            // frame, and all but the first find no time has passed.
            var cap = (m_wanted ? (MostBankSeconds * m_share) : (m_share * m_period));
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
    /// <summary>Charges a submitted job's estimated GPU time and enters it among the jobs queued ahead of the next frame
    /// (<see cref="Allowance"/>, <see cref="OwedAllowance"/>) until its charge leaves (<see cref="Leave"/>). A job the
    /// frame owes enters the queue alone and debits nothing: it is held to the owed allowance, not the credit.</summary>
    /// <param name="seconds">The estimate, in seconds.</param>
    /// <param name="owed">Whether the job is work the frame owes, taken under <see cref="OwedAllowance"/>.</param>
    /// <returns>The charge: the seconds it debited (the estimate, less what the debt's floor dropped; zero for an owed
    /// job), which the job's settlement (<see cref="SettleGpu"/>) replaces, and the seconds it entered the queue with,
    /// which its leaving takes out.</returns>
    public SdfGpuWorkCharge ChargeGpu(double seconds, bool owed = false) {
        var queued = ((double.IsFinite(d: seconds) && (seconds > 0.0)) ? seconds : 0.0);

        lock (m_gate) {
            m_queued += queued;
            m_queuedJobs++;
            if (owed) {
                m_owedJobs++;
                return new SdfGpuWorkCharge(Debited: 0.0, Queued: queued) { Owed = true };
            }

            return new SdfGpuWorkCharge(Debited: Charge(seconds: seconds), Queued: queued);
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
    /// floor; then the work queued ahead of the next frame, and the frames judged against the background jobs' spans:
    /// those behind background work, those it may have cost (<see cref="FramesMissedBehind"/>), and the background
    /// GPU time ahead of the frames behind it, in all and at most.</summary>
    /// <returns>One space-led field.</returns>
    public string Describe() {
        lock (m_gate) {
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $" work-budget=[gpu-share:{m_share:0.000},share-cap:{SdfGpuWork.GpuShare:0.00},frame-allowance:{(m_share * m_period * 1000.0):0.00}ms,period:{(m_period * 1000.0):0.00}ms,frame-gpu:{(m_frameSeconds * 1000.0):0.00}ms,interval:{(m_interval * 1000.0):0.00}ms,owed-allowance:{(m_owedFrame * 1000.0):0.00}ms,owed-jobs:{m_owedJobs},owed-gpu:{(m_owedSeconds * 1000.0):0}ms,follows:{m_follows},falls:{m_falls},rises:{m_rises},gpu-credit:{(m_credit * 1000.0):0.00}ms,gpu-granted:{(m_granted * 1000.0):0}ms,gpu-charged:{(m_charged * 1000.0):0}ms,upload-rate:{SdfGpuWork.UploadBytesPerSecond:0}B/s,upload-credit:{m_uploadCredit:0}B,uploaded:{m_uploaded:0}B,wants:{m_wants},clamped:{m_clamped},floored:{m_floored},queued:{m_queuedJobs}/{(m_queued * 1000.0):0.00}ms] frames-behind=[judged:{m_framesJudged},behind:{m_framesBehind},missed:{m_framesMissed},behind-gpu:{(m_behindSeconds * 1000.0):0.0}ms,most-behind:{(m_mostBehindSeconds * 1000.0):0.00}ms]");
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
}
