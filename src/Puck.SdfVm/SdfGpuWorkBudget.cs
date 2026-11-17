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
/// after an idle spell carries no more background work than any other.
/// </para>
/// <para>The services take from it first come, in the order their residencies are pumped. Thread-safe.</para>
/// </summary>
/// <param name="clock">Reads the time, in <see cref="Stopwatch"/> ticks; <see langword="null"/> reads
/// <see cref="Stopwatch.GetTimestamp"/>.</param>
public sealed class SdfGpuWorkBudget(Func<long>? clock = null) {
    /// <summary>The longest gap between accruals whose GPU time is banked whole when a service waited on the budget: a
    /// tenth of a second, the slowest pump the budget holds its rate across.</summary>
    public const double MostBankSeconds = 0.1;

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

    /// <summary>Gets whether the budget holds credit of both kinds, so a service may take its next job.</summary>
    public bool Holds {
        get {
            lock (m_gate) { return ((m_credit > 0.0) && (m_uploadCredit > 0.0)); }
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
        lock (m_gate) {
            m_credit -= seconds;
            m_charged += seconds;
        }
    }
    /// <summary>Replaces a charged job's estimate with its measured GPU time.</summary>
    /// <param name="estimate">The seconds the job was charged.</param>
    /// <param name="measured">The seconds its timestamps measured.</param>
    public void SettleGpu(double estimate, double measured) {
        lock (m_gate) {
            m_credit += (estimate - measured);
            m_charged += (measured - estimate);
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
    /// <summary>Describes the budget: the credit it holds, what its services have charged since it was made, and how
    /// often one found it empty.</summary>
    /// <returns>One space-led field.</returns>
    public string Describe() {
        lock (m_gate) {
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $" work-budget=[gpu-share:{SdfGpuWork.GpuShare:0.00},gpu-credit:{(m_credit * 1000.0):0.00}ms,gpu-charged:{(m_charged * 1000.0):0}ms,upload-rate:{SdfGpuWork.UploadBytesPerSecond:0}B/s,upload-credit:{m_uploadCredit:0}B,uploaded:{m_uploaded:0}B,wants:{m_wants}]");
        }
    }
}
