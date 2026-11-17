using System.Diagnostics;
using System.Globalization;

namespace Puck.SdfVm;

/// <summary>
/// One presentation's budget of background GPU work, which every residency's service draws on
/// (<see cref="SdfGpuWork"/>) in place of a budget each: at most <see cref="SdfGpuWork.GpuShare"/> of the GPU's time, by
/// each job's measured GPU time, and at most <see cref="SdfGpuWork.UploadBytesPerSecond"/> of host payload staged, for
/// the residencies together.
/// <para>
/// Both are rates of wall time. Credit accrues by the time since the last accrual, whichever service made it, so the
/// work a second does not fall with the frame rate: a service that found the budget empty marks it wanted
/// (<see cref="Want"/>), and the next accrual then banks the whole gap, up to <see cref="MostBankSeconds"/> of it. An
/// accrual nobody waited on banks at most one frame's share (<see cref="SdfGpuWork.FrameShareSeconds"/>), so the frame
/// after an idle spell carries no more background work than any other. A service that is serviced while its host waits
/// asks when the budget next holds (<see cref="UntilHolds"/>) and comes back then.
/// </para>
/// <para>
/// Invariant: the GPU credit lies between a bank's worth and <see cref="MostDebtSeconds"/> of debt, whatever a job
/// reports. Two things hold it, both here: a settled charge is at most the wall time its job was in flight
/// (<see cref="MeasuredWithin"/>), since no job ran longer than that, and the credit is floored at the debt
/// (<see cref="ChargeGpu"/>, <see cref="SettleGpu"/> are the only writes that lower it). So one faulty measurement
/// (a timestamp pair read end before start wraps to nearly the counter's whole range) holds the work for at most
/// <see cref="MostDebtSeconds"/> over <see cref="SdfGpuWork.GpuShare"/> seconds, never for good.
/// </para>
/// <para>The services take from it first come, in the order their residencies are pumped. Thread-safe.</para>
/// </summary>
/// <param name="clock">Reads the time, in <see cref="Stopwatch"/> ticks; <see langword="null"/> reads
/// <see cref="Stopwatch.GetTimestamp"/>.</param>
public sealed class SdfGpuWorkBudget(Func<long>? clock = null) {
    /// <summary>The longest gap between accruals whose GPU time is banked whole when a service waited on the budget: a
    /// tenth of a second, the slowest pump the budget holds its rate across.</summary>
    public const double MostBankSeconds = 0.1;
    /// <summary>The most GPU time, in seconds, the budget owes: half a second, which <see cref="SdfGpuWork.GpuShare"/>
    /// repays in two seconds of wall time. Charges past it are dropped and counted.</summary>
    public const double MostDebtSeconds = 0.5;

    // The host payload bytes the budget banks at most: a full slot ring of the largest jobs.
    private const double MostUploadCreditBytes = (SdfGpuWork.SlotCount * ((double)SdfGpuWork.MostUploadJobBytes));

    private readonly Lock m_gate = new();
    private readonly Func<long> m_clock = (clock ?? Stopwatch.GetTimestamp);
    private double m_credit = SdfGpuWork.FrameShareSeconds;
    private double m_uploadCredit = MostUploadCreditBytes;

    private long m_at;
    private bool m_wanted;
    private double m_charged;
    private double m_uploaded;
    private long m_wants;
    private long m_clamped;
    private long m_floored;

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
                var gpu = ((m_credit > 0.0) ? 0.0 : (-m_credit / SdfGpuWork.GpuShare));
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
    /// <summary>Banks the GPU time and the upload bytes their rates allow since the last accrual by any service.</summary>
    public void Accrue() {
        var now = m_clock();

        lock (m_gate) {
            var seconds = ((m_at != 0) ? Math.Max(val1: 0.0, val2: ((now - m_at) / ((double)Stopwatch.Frequency))) : 0.0);
            // The cap bounds what an accrual adds, never what the budget already holds: several services accrue in one
            // frame, and all but the first find no time has passed.
            var cap = (m_wanted ? (MostBankSeconds * SdfGpuWork.GpuShare) : SdfGpuWork.FrameShareSeconds);

            m_credit = Math.Min(val1: (m_credit + (seconds * SdfGpuWork.GpuShare)), val2: Math.Max(val1: m_credit, val2: cap));
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
    public void ChargeGpu(double seconds) {
        lock (m_gate) { Charge(seconds: seconds); }
    }
    /// <summary>Replaces a charged job's estimate with its measured GPU time, held to what the job could have run
    /// (<see cref="MeasuredWithin"/>), and counts a measurement that was not one.</summary>
    /// <param name="estimate">The seconds the job was charged.</param>
    /// <param name="measured">The seconds its timestamps read.</param>
    /// <param name="inFlight">The wall time, in seconds, between the job's submission and its answer.</param>
    /// <returns>The seconds the job is charged in the estimate's place.</returns>
    public double SettleGpu(double estimate, double measured, double inFlight) {
        var seconds = MeasuredWithin(clamped: out var clamped, estimate: estimate, inFlight: inFlight, measured: measured);

        lock (m_gate) {
            if (clamped) { m_clamped++; }
            Charge(seconds: (seconds - estimate));
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
    /// <summary>Describes the budget: the credit it holds, what its services have charged since it was made, how often
    /// one found it empty, the measurements no job could have made and the charges dropped at the debt's floor.</summary>
    /// <returns>One space-led field.</returns>
    public string Describe() {
        lock (m_gate) {
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $" work-budget=[gpu-share:{SdfGpuWork.GpuShare:0.00},gpu-credit:{(m_credit * 1000.0):0.00}ms,gpu-charged:{(m_charged * 1000.0):0}ms,upload-rate:{SdfGpuWork.UploadBytesPerSecond:0}B/s,upload-credit:{m_uploadCredit:0}B,uploaded:{m_uploaded:0}B,wants:{m_wants},clamped:{m_clamped},floored:{m_floored}]");
        }
    }

    // The one write that lowers the GPU credit: under the gate. A charge that is not a number changes nothing, and the
    // debt stops at its floor.
    private void Charge(double seconds) {
        if (double.IsNaN(d: seconds)) {
            m_floored++;

            return;
        }
        m_credit -= seconds;
        m_charged += seconds;
        if (m_credit < -MostDebtSeconds) {
            m_credit = -MostDebtSeconds;
            m_floored++;
        }
    }
}
