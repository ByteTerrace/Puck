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

    // The host payload bytes the budget banks at most: a full slot ring of the largest jobs.
    private const double MostUploadCreditBytes = (SdfGpuWork.SlotCount * ((double)SdfGpuWork.MostUploadJobBytes));

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
    /// (their work is unbudgeted), so a drain's result does not depend on it.</summary>
    /// <param name="periodSeconds">The display's period, in seconds; positive.</param>
    /// <param name="frameSeconds">The frame's own GPU time read back since the previous reading, in seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="periodSeconds"/> is not positive.</exception>
    public void Observe(double periodSeconds, double frameSeconds) {
        if (!(periodSeconds > 0.0)) { throw new ArgumentOutOfRangeException(paramName: nameof(periodSeconds), actualValue: periodSeconds, message: "A display period is positive."); }

        lock (m_gate) {
            if (m_follows != "frame") {
                // A reading after none starts a window of its own.
                m_follows = "frame";
                m_windowFrames = 0;
                m_windowSeconds = 0.0;
            }
            m_period = periodSeconds;
            m_windowFrames++;
            m_windowSeconds += (double.IsFinite(d: frameSeconds) ? Math.Max(val1: 0.0, val2: frameSeconds) : 0.0);
            if (m_windowFrames < FollowFrames) { return; }
            m_frameSeconds = (m_windowSeconds / m_windowFrames);
            m_windowFrames = 0;
            m_windowSeconds = 0.0;

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
    /// <summary>Takes a frame with no reading: the share stands at <see cref="SdfGpuWork.GpuShare"/> of the display's
    /// period, or of <see cref="ReferencePeriodSeconds"/> when none is known, and the budget names why.</summary>
    /// <param name="periodSeconds">The display's period, in seconds; zero or less when none is known.</param>
    /// <param name="reason">Why there is no reading, one word for <see cref="Describe"/>.</param>
    public void Unread(double periodSeconds, string reason) {
        ArgumentNullException.ThrowIfNull(argument: reason);

        lock (m_gate) {
            m_follows = reason;
            m_period = ((periodSeconds > 0.0) ? periodSeconds : ReferencePeriodSeconds);
            m_share = SdfGpuWork.GpuShare;
            m_holding = false;
            m_windowFrames = 0;
            m_windowSeconds = 0.0;
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
    /// <summary>Charges a submitted job's estimated GPU time.</summary>
    /// <param name="seconds">The estimate, in seconds.</param>
    /// <returns>The seconds the charge debited: the estimate, less what the debt's floor dropped; what the job's
    /// settlement (<see cref="SettleGpu"/>) replaces.</returns>
    public double ChargeGpu(double seconds) {
        lock (m_gate) { return Charge(seconds: seconds); }
    }
    /// <summary>Replaces what a charged job debited with its measured GPU time, held to what the job could have run
    /// (<see cref="MeasuredWithin"/>), and counts a measurement that was not one. It gives back at most what the charge
    /// debited.</summary>
    /// <param name="debited">The seconds the job's charge debited (<see cref="ChargeGpu"/>'s answer).</param>
    /// <param name="estimate">The seconds the job was estimated to take, which a measurement no job could have made
    /// falls back to.</param>
    /// <param name="measured">The seconds its timestamps read.</param>
    /// <param name="inFlight">The wall time, in seconds, between the job's submission and its answer.</param>
    /// <returns>The seconds the job is charged in the estimate's place.</returns>
    public double SettleGpu(double debited, double estimate, double measured, double inFlight) {
        var seconds = MeasuredWithin(clamped: out var clamped, estimate: estimate, inFlight: inFlight, measured: measured);

        lock (m_gate) {
            if (clamped) { m_clamped++; }
            _ = Charge(seconds: (seconds - debited));
        }

        return seconds;
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
    /// GPU time it read (or why it read none), the steps that lowered and raised the share, the credit it holds, what it
    /// granted and what its services charged since it was made (their ratio is how much of the allowance the work used),
    /// how often one found it empty, the measurements no job could have made and the charges dropped at the debt's
    /// floor.</summary>
    /// <returns>One space-led field.</returns>
    public string Describe() {
        lock (m_gate) {
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $" work-budget=[gpu-share:{m_share:0.000},share-cap:{SdfGpuWork.GpuShare:0.00},frame-allowance:{(m_share * m_period * 1000.0):0.00}ms,period:{(m_period * 1000.0):0.00}ms,frame-gpu:{(m_frameSeconds * 1000.0):0.00}ms,follows:{m_follows},falls:{m_falls},rises:{m_rises},gpu-credit:{(m_credit * 1000.0):0.00}ms,gpu-granted:{(m_granted * 1000.0):0}ms,gpu-charged:{(m_charged * 1000.0):0}ms,upload-rate:{SdfGpuWork.UploadBytesPerSecond:0}B/s,upload-credit:{m_uploadCredit:0}B,uploaded:{m_uploaded:0}B,wants:{m_wants},clamped:{m_clamped},floored:{m_floored}]");
        }
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
}
