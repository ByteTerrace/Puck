using Puck.Abstractions.Memory;

namespace Puck.Hosting.Jobs;

/// <summary>The disjoint categories of owned bytes. An alias never adds another charge.</summary>
public enum MemoryCategory { Live, Cached, Retirement, Metadata }
/// <summary>A consistent reading of owned storage and future admission promises, in bytes.</summary>
public readonly record struct MemoryReading(long Live, long Reserved, long Cached, long Retirement, long Metadata, long Budget, long Headroom) {
    /// <summary>Gets physical bytes owned by participating subsystems; reservations are not physical allocations.</summary>
    public long Owned => checked((((Live + Cached) + Retirement) + Metadata));
}
/// <summary>
/// The process's admission authority and byte ledger. Owners report storage, never buffer addresses or references.
/// Physical availability is an observation; a successful reservation promises only this account's unused budget.
/// </summary>
public sealed class MemoryAccount {
    /// <summary>Gets the process account shared by mesh libraries, residencies and jobs.</summary>
    public static MemoryAccount Shared { get; } = new();

    private readonly Lock m_gate = new();
    private readonly long[] m_bytes = new long[4];

    private long m_reserved;
    private long? m_budget;

    /// <summary>Requests that cache owners detach idle claims. Active consumer claims are unaffected.</summary>
    public event Action? TrimRequested;

    /// <summary>Asks participating caches to release their claims, outside the account lock.</summary>
    public void TrimCaches() => TrimRequested?.Invoke();

    /// <summary>Gets the machine-derived budget, leaving failure, cancellation and allocator headroom.</summary>
    public long DefaultBudget { get; } = Math.Max(val1: 0, val2: (((HostMemory.InstalledPhysicalBytes() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) * 3) / 4));
    /// <summary>Gets the unspendable failure and allocator reserve.</summary>
    public long Headroom { get; } = Math.Clamp(((HostMemory.InstalledPhysicalBytes() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) / 32), (16L << 20), (512L << 20));

    /// <summary>Gets the default cache allowance, governed by the same authority as job admission.</summary>
    public long CacheBudget => Math.Clamp((DefaultBudget / 12), (16L << 20), (512L << 20));

    /// <summary>Lowers the admission budget, or restores the machine-derived value. Existing owners keep their storage.</summary>
    public void SetBudget(long? bytes) {
        if (bytes is < 0) { throw new ArgumentOutOfRangeException(paramName: nameof(bytes)); }
        lock (m_gate) { m_budget = ((bytes is { } value) ? Math.Min(val1: value, val2: DefaultBudget) : null); }
        TrimCaches();
    }
    /// <summary>Reads all categories atomically.</summary>
    public MemoryReading Read() {
        lock (m_gate) { return new(m_bytes[0], m_reserved, m_bytes[1], m_bytes[2], m_bytes[3], (m_budget ?? DefaultBudget), Headroom); }
    }
    /// <summary>Creates an explicit owner. It must outlive every allocation that reports to it.</summary>
    public Owner CreateOwner(string name, MemoryCategory category = MemoryCategory.Live) => new(account: this, category: category, name: name);

    /// <summary>One lifecycle's allocation totals and simultaneous-peak reservation, with no buffer registry.</summary>
    public sealed class Owner : IAllocationObserver, IDisposable {
        private readonly MemoryAccount m_account;

        private MemoryCategory m_category;
        private long m_bytes;
        private long m_peak;
        private bool m_closed;

        /// <summary>Gets the owner for diagnostics.</summary>
        public string Name { get; }

        internal Owner(MemoryAccount account, string name, MemoryCategory category) { m_account = account; Name = name; m_category = category; }

        private long Promise => Math.Max(val1: 0, val2: (m_peak - m_bytes));

        /// <summary>Reserves the owner's simultaneous peak before it starts; false means defer without holding partial resources.</summary>
        public bool TryReserve(long peak) {
            ArgumentOutOfRangeException.ThrowIfNegative(peak);
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_closed, instance: this);
                var extra = (Math.Max(val1: 0, val2: (peak - m_bytes)) - Promise);
                var owned = m_account.m_bytes.Sum();
                var spare = Math.Max(val1: 0, val2: ((((m_account.m_budget ?? m_account.DefaultBudget) - m_account.Headroom) - owned) - m_account.m_reserved));

                if (HostMemory.AvailablePhysicalBytes() is { } available) { spare = Math.Min(val1: spare, val2: Math.Max(val1: 0, val2: ((available - m_account.Headroom) - m_account.m_reserved))); }
                if ((extra > 0) && (extra > spare)) { return false; }
                m_account.m_reserved += extra;
                m_peak = peak;
                return true;
            }
        }
        /// <summary>Releases the future promise after work ends; any still-owned storage remains accounted.</summary>
        public void ReleaseReservation() {
            lock (m_account.m_gate) { m_account.m_reserved -= Promise; m_peak = 0; }
        }
        /// <inheritdoc/>
        public void Change(long bytes) {
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: (m_closed && (bytes > 0)), instance: this);
                var next = checked((m_bytes + bytes));

                if (next < 0) { throw new InvalidOperationException(message: $"Memory owner {Name} released more bytes than it owns."); }
                m_account.m_reserved -= Promise;
                m_bytes = next;
                m_account.m_bytes[((int)m_category)] += bytes;
                m_account.m_reserved += Promise;
            }
        }
        /// <summary>Moves the existing charge between categories without charging aliases.</summary>
        public void Classify(MemoryCategory category) {
            lock (m_account.m_gate) {
                m_account.m_bytes[((int)m_category)] -= m_bytes;
                m_category = category;
                m_account.m_bytes[((int)m_category)] += m_bytes;
            }
        }
        /// <inheritdoc/>
        public void Dispose() {
            lock (m_account.m_gate) {
                ReleaseReservation();
                m_closed = true;
                // Charges are released by the storage's actual disposer, never inferred from an owner's shutdown.
            }
        }
    }
}
