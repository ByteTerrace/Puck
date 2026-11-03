using Puck.Abstractions.Memory;

namespace Puck.Hosting.Jobs;

/// <summary>The disjoint categories of owned bytes. An alias never adds another charge.</summary>
public enum MemoryCategory {
    /// <summary>Storage an active consumer holds.</summary>
    Live,
    /// <summary>Storage only a reclaimable cache claim holds.</summary>
    Cached,
    /// <summary>Storage of work that was retired and is waiting for its last borrower.</summary>
    Retirement,
    /// <summary>Bookkeeping storage beside the payloads it describes.</summary>
    Metadata,
}
/// <summary>A consistent reading of owned storage and future admission promises, in bytes.</summary>
public readonly record struct MemoryReading(long Live, long Reserved, long Cached, long Retirement, long Metadata, long Budget, long Headroom) {
    /// <summary>Gets physical bytes owned by participating subsystems; reservations are not physical allocations.</summary>
    public long Owned => checked((((Live + Cached) + Retirement) + Metadata));
}
/// <summary>
/// The process's admission authority and byte ledger. Owners report storage, never buffer addresses or references.
/// A reservation promises only this account's unused budget. The machine's free physical memory is an observation the
/// caller reads outside the account and passes in: it paces admission, and a caller that has nothing admitted admits
/// its next job regardless (<see cref="Owner.Reserve"/>), so a reading never holds work back forever.
/// </summary>
public sealed class MemoryAccount {
    /// <summary>Gets the process account shared by mesh libraries, residencies and jobs.</summary>
    public static MemoryAccount Shared { get; } = new();

    private readonly Lock m_gate = new();
    private readonly long[] m_bytes = new long[4];
    private readonly HashSet<Owner> m_owners = [];

    private long m_reserved;
    private long? m_budget;

    /// <summary>Requests that cache owners detach idle claims. Active consumer claims are unaffected.</summary>
    public event Action? TrimRequested;

    /// <summary>Asks participating caches to release their claims, outside the account lock. A cache whose claims belong
    /// to another thread defers the release to that thread (<see cref="DeferTrim"/>), so some storage can outlive the
    /// call; <see cref="DeferredTrims"/> counts those still waiting.</summary>
    public void TrimCaches() => TrimRequested?.Invoke();

    private int m_deferredTrims;

    /// <summary>Gets the trims a cache owner deferred to its own thread that have not run yet.</summary>
    public int DeferredTrims => Volatile.Read(location: ref m_deferredTrims);
    /// <summary>Records a trim request a cache owner will service on its own thread; it calls
    /// <see cref="CompleteDeferredTrim"/> once it has.</summary>
    public void DeferTrim() => _ = Interlocked.Increment(location: ref m_deferredTrims);
    /// <summary>Records that a deferred trim has run, or that its owner was disposed with it still waiting.</summary>
    public void CompleteDeferredTrim() => _ = Interlocked.Decrement(location: ref m_deferredTrims);

    /// <summary>Gets the machine-derived budget, leaving failure, cancellation and allocator headroom.</summary>
    public long DefaultBudget { get; } = Math.Max(val1: 0, val2: (((HostMemory.InstalledPhysicalBytes() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) * 3) / 4));
    /// <summary>Gets the unspendable failure and allocator reserve.</summary>
    public long Headroom { get; } = Math.Clamp(((HostMemory.InstalledPhysicalBytes() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) / 32), (16L << 20), (512L << 20));

    /// <summary>Gets the default cache allowance, governed by the same authority as job admission.</summary>
    public long CacheBudget => Math.Clamp((DefaultBudget / 12), (16L << 20), (512L << 20));

    /// <summary>Lowers the admission budget, or restores the machine-derived value. Existing owners keep their storage;
    /// a budget lowered below the one before asks the caches to release their claims (<see cref="TrimCaches"/>).</summary>
    /// <param name="bytes">The budget, in bytes, at most <see cref="DefaultBudget"/>; <see langword="null"/> restores the
    /// machine-derived budget.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public void SetBudget(long? bytes) {
        if (bytes is < 0) { throw new ArgumentOutOfRangeException(paramName: nameof(bytes)); }
        bool lowered;

        lock (m_gate) {
            var before = (m_budget ?? DefaultBudget);

            m_budget = ((bytes is { } value) ? Math.Min(val1: value, val2: DefaultBudget) : null);
            lowered = ((m_budget ?? DefaultBudget) < before);
        }
        if (lowered) { TrimCaches(); }
    }
    /// <summary>Reads all categories atomically.</summary>
    /// <returns>The reading.</returns>
    public MemoryReading Read() {
        lock (m_gate) { return new(m_bytes[0], m_reserved, m_bytes[1], m_bytes[2], m_bytes[3], (m_budget ?? DefaultBudget), Headroom); }
    }
    /// <summary>Reads the account and every owner holding charged bytes or a reservation under the same lock.
    /// Empty idle owners are absent; disposed owners remain until their storage releases its last byte.</summary>
    /// <returns>A detached snapshot whose rows reconcile to its account categories and reservations.</returns>
    public MemoryOwnerSnapshot ReadOwners() {
        lock (m_gate) {
            var owners = new MemoryOwnerReading[m_owners.Count];
            var index = 0;

            foreach (var owner in m_owners) { owners[index++] = owner.Read(); }
            return new(Account: Read(), Owners: owners);
        }
    }
    /// <summary>Creates an explicit owner. It must outlive every allocation that reports to it.</summary>
    /// <param name="name">The owner's name, for diagnostics.</param>
    /// <param name="category">The category its charges count in until it is reclassified.</param>
    /// <returns>The owner.</returns>
    public Owner CreateOwner(string name, MemoryCategory category = MemoryCategory.Live) => new(account: this, category: category, name: name);

    /// <summary>One lifecycle's allocation totals and simultaneous-peak reservation, with no buffer registry.</summary>
    public sealed class Owner : IAllocationObserver, IDisposable {
        private readonly MemoryAccount m_account;

        private MemoryCategory m_category;
        private long m_bytes;
        private long m_reservationPeak;
        private long m_ownedPeak;
        private bool m_closed;
        private bool m_tracked;

        /// <summary>Gets the owner for diagnostics.</summary>
        public string Name { get; }
        /// <summary>Gets the bytes the owner's storage holds now.</summary>
        public long Bytes {
            get {
                lock (m_account.m_gate) { return m_bytes; }
            }
        }

        internal Owner(MemoryAccount account, string name, MemoryCategory category) { m_account = account; Name = name; m_category = category; }

        private long Promise => Math.Max(val1: 0, val2: (m_reservationPeak - m_bytes));

        // Called only by the existing owner mutation doors, under the account lock. Empty owners need no account
        // reference; a disposed-but-charged owner stays visible until the storage's actual release reaches Change.
        private void Track() {
            var charged = ((m_bytes != 0) || (Promise != 0));

            if (charged == m_tracked) { return; }
            if (charged) { _ = m_account.m_owners.Add(item: this); }
            else { _ = m_account.m_owners.Remove(item: this); }
            m_tracked = charged;
        }

        internal MemoryOwnerReading Read() {
            lock (m_account.m_gate) {
                return new(Name, m_category, m_bytes, Promise, m_ownedPeak, m_reservationPeak, m_closed);
            }
        }

        /// <summary>Reserves the owner's simultaneous peak before its work starts, or lowers a reservation already held;
        /// false means defer without holding partial resources.</summary>
        /// <param name="peak">The most bytes the owner's storage holds at once while the work runs.</param>
        /// <param name="available">The machine's free physical memory, in bytes, as the caller read it outside the
        /// account (<see cref="HostMemory.AvailablePhysicalBytes"/>), or <see langword="null"/> to pace by the budget
        /// alone.</param>
        /// <returns><see langword="true"/> when the reservation is held.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="peak"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
        public bool TryReserve(long peak, long? available = null) {
            ArgumentOutOfRangeException.ThrowIfNegative(peak);
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_closed, instance: this);
                var extra = (Math.Max(val1: 0, val2: (peak - m_bytes)) - Promise);
                var spare = Math.Max(val1: 0, val2: ((((m_account.m_budget ?? m_account.DefaultBudget) - m_account.Headroom) - m_account.m_bytes.Sum()) - m_account.m_reserved));

                if (available is { } free) { spare = Math.Min(val1: spare, val2: Math.Max(val1: 0, val2: ((free - m_account.Headroom) - m_account.m_reserved))); }
                if ((extra > 0) && (extra > spare)) { return false; }
                m_account.m_reserved += extra;
                m_reservationPeak = peak;
                Track();
                return true;
            }
        }
        /// <summary>Reserves the owner's simultaneous peak whatever the budget holds, for the one job a caller must
        /// admit so that its work progresses when nothing else of it is admitted.</summary>
        /// <param name="peak">The most bytes the owner's storage holds at once while the work runs.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="peak"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
        public void Reserve(long peak) {
            ArgumentOutOfRangeException.ThrowIfNegative(peak);
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_closed, instance: this);
                m_account.m_reserved += (Math.Max(val1: 0, val2: (peak - m_bytes)) - Promise);
                m_reservationPeak = peak;
                Track();
            }
        }
        /// <summary>Releases the future promise after work ends; any still-owned storage remains accounted.</summary>
        public void ReleaseReservation() {
            lock (m_account.m_gate) { m_account.m_reserved -= Promise; m_reservationPeak = 0; Track(); }
        }
        /// <inheritdoc/>
        public void Change(long bytes) {
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: (m_closed && (bytes > 0)), instance: this);
                var next = checked((m_bytes + bytes));

                if (next < 0) { throw new InvalidOperationException(message: $"Memory owner {Name} released more bytes than it owns."); }
                m_account.m_reserved -= Promise;
                m_bytes = next;
                m_ownedPeak = Math.Max(val1: m_ownedPeak, val2: next);
                m_account.m_bytes[((int)m_category)] += bytes;
                m_account.m_reserved += Promise;
                Track();
            }
        }
        /// <summary>Moves the existing charge between categories without charging aliases.</summary>
        /// <param name="category">The category the owner's charges count in from now on.</param>
        public void Classify(MemoryCategory category) {
            lock (m_account.m_gate) {
                if (category == m_category) { return; }
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
