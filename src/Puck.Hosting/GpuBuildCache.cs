using System.Runtime.ExceptionServices;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Hosting;

/// <summary>
/// GPU objects built once per device and key and shared by every holder that asks for the same pair: the one
/// mechanism behind the pass-pipeline cache, whose entries are every pass pipeline, the SDF engine's kernels and the
/// region-copy pipeline among them. A holder takes
/// a <see cref="GpuBuildLease{TKey, T}"/>; the first lease on a pair starts its build on the thread pool
/// (<see cref="BackgroundBuild{T}"/>), every later lease on that pair joins it, and the built value is disposed when its
/// last lease is released. That release cancels a build still in flight and waits, outside the cache's lock, only for
/// the creation already in the driver.
/// <para>
/// The build counts what it creates into <see cref="Work"/>, a <see cref="GpuWorkLedger"/> named by the cache, which it
/// receives through <see cref="GpuBuildRequest{TKey}.Ledger"/>; no holder's ledger counts it. A device is matched by
/// reference and a key by its own equality, so a key carries what builds its value and equates only what determines
/// it; the key an entry keeps is the one its first lease brought (<see cref="GpuBuildLease{TKey, T}.Key"/>).
/// </para>
/// <para>
/// Acquiring and waiting are safe on any thread; the holders poll and release on the thread that renders. A device
/// recreated in place after a loss keeps its identity, so every holder releases its lease on device loss, which drops
/// the device's entries, and a value built before the loss is never handed to a lease on the recreated device.
/// </para>
/// </summary>
/// <typeparam name="TKey">What a value is built from, equal exactly when two values would be the same.</typeparam>
/// <typeparam name="T">The built value, which owns the objects it created.</typeparam>
public sealed class GpuBuildCache<TKey, T> where TKey : IEquatable<TKey> where T : class, IDisposable {
    private readonly Func<GpuBuildRequest<TKey>, CancellationToken, T> m_build;

    private readonly List<GpuBuildLease<TKey, T>.Entry> m_entries = [];
    private readonly Lock m_gate = new();

    private readonly GpuWorkLedger m_work;

    /// <summary>Initializes a new instance of the <see cref="GpuBuildCache{TKey, T}"/> class.</summary>
    /// <param name="workSourceName">The name a counters report heads <see cref="Work"/>'s section with, such as
    /// <c>gpu.pass-pipelines</c>.</param>
    /// <param name="build">Builds one entry's value on a pool thread from its device, key and the cache's ledger; it
    /// checks the token between creations, never inside one, and releases what it created when it throws.</param>
    /// <exception cref="ArgumentNullException"><paramref name="build"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="workSourceName"/> is not a work source name.</exception>
    public GpuBuildCache(string workSourceName, Func<GpuBuildRequest<TKey>, CancellationToken, T> build) {
        ArgumentNullException.ThrowIfNull(argument: build);

        m_build = build;
        m_work = new GpuWorkLedger(
            framesInFlight: 1,
            name: workSourceName
        );
    }

    /// <summary>Gets the number of entries a new lease can join, built or building: every entry with a lease.</summary>
    public int SharedEntries {
        get {
            lock (m_gate) {
                return m_entries.Count;
            }
        }
    }
    /// <summary>Gets what the cache's builds have created, over the cache's whole life.</summary>
    public IWorkCounterSource Work => m_work;

    /// <summary>Takes a lease on <paramref name="key"/>'s value on <paramref name="device"/>, joining the entry another
    /// holder already leases or starting its build on the thread pool. Safe on any thread.</summary>
    /// <param name="device">The device the value is created on, through its services.</param>
    /// <param name="key">What the value is built from.</param>
    /// <returns>The lease, which the caller releases once nothing it recorded with the value is in flight.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="key"/> is
    /// <see langword="null"/>.</exception>
    public GpuBuildLease<TKey, T> Acquire(IGpuDeviceContext device, TKey key) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: key);

        lock (m_gate) {
            foreach (var entry in m_entries) {
                if (
                    ReferenceEquals(
                        objA: entry.Device,
                        objB: device
                    ) &&
                    entry.Key.Equals(other: key)
                ) {
                    entry.Holders++;

                    return new GpuBuildLease<TKey, T>(
                        cache: this,
                        entry: entry
                    );
                }
            }

            var created = new GpuBuildLease<TKey, T>.Entry(
                device: device,
                key: key
            );

            m_entries.Add(item: created);
            StartBuild(entry: created);

            return new GpuBuildLease<TKey, T>(
                cache: this,
                entry: created
            );
        }
    }

    internal T? Poll(GpuBuildLease<TKey, T>.Entry entry) {
        lock (m_gate) {
            return TakeLocked(entry: entry);
        }
    }
    internal void Release(GpuBuildLease<TKey, T>.Entry entry) {
        if (Detach(entry: entry) is { } build) {
            Retire(
                build: build,
                entry: entry
            );
        }
    }
    // Gives up a holder's share of an entry; the last one cancels the entry's build and takes the entry out of the list,
    // returning the canceled build to wait for. The cancel lands inside the gate, before the entry leaves the list, so a
    // reader that no longer finds the entry knows its build has been told to stop.
    internal CanceledBuild<T>? Detach(GpuBuildLease<TKey, T>.Entry entry) {
        lock (m_gate) {
            if (--entry.Holders > 0) {
                return null;
            }

            entry.IsReleased = true;
            _ = m_entries.Remove(item: entry);

            return entry.Build.Detach();
        }
    }
    // With its last lease released and the entry out of the list, nothing else reaches the entry, so the wait for the
    // creation still in the driver holds no lock another holder's poll or acquire needs.
    internal static void Retire(CanceledBuild<T> build, GpuBuildLease<TKey, T>.Entry entry) {
        build.Wait(discard: static value => value.Dispose());
        entry.Value?.Dispose();
        entry.Value = null;
    }
    internal T Wait(GpuBuildLease<TKey, T>.Entry entry, CancellationToken cancellationToken) {
        while (true) {
            Task completion;

            lock (m_gate) {
                if (TakeLocked(entry: entry) is { } ready) {
                    return ready;
                }

                completion = entry.Build.Completion!;
            }

            // A failed build is taken, and rethrown, under the gate on the next pass.
            try {
                completion.Wait(cancellationToken: cancellationToken);
            } catch (AggregateException) {
            }
        }
    }

    private void StartBuild(GpuBuildLease<TKey, T>.Entry entry) {
        var request = new GpuBuildRequest<TKey>(
            Device: entry.Device,
            Key: entry.Key,
            Ledger: m_work
        );
        var build = m_build;

        entry.Build.Start(build: token => build(
            arg1: request,
            arg2: token
        ));
    }
    // The ready value, or null while its build runs; starts a build when none is pending. A failed build leaves nothing
    // pending, so the next poll by any holder starts a fresh one.
    private T? TakeLocked(GpuBuildLease<TKey, T>.Entry entry) {
        // A wait racing its own lease's last release finds the entry released and never starts a build nobody owns.
        ObjectDisposedException.ThrowIf(
            condition: entry.IsReleased,
            type: typeof(GpuBuildLease<TKey, T>)
        );

        if (entry.Value is { } ready) {
            return ready;
        }

        if (!entry.Build.IsPending) {
            StartBuild(entry: entry);
        }

        if (!entry.Build.TryTake(
            error: out var error,
            result: out var built
        )) {
            return null;
        }

        if (error is not null) {
            ExceptionDispatchInfo.Throw(source: error);
        }

        entry.Value = built;

        return built;
    }
}
/// <summary>What a <see cref="GpuBuildCache{TKey, T}"/> build is handed: the entry's device and key, and the cache's
/// ledger, which counts every object the build creates.</summary>
/// <typeparam name="TKey">What the value is built from.</typeparam>
/// <param name="Device">The device the value is created on.</param>
/// <param name="Key">The key the entry was created with.</param>
/// <param name="Ledger">The cache's ledger, which the build wraps the device's services with
/// (<see cref="GpuWorkCounting.Wrap(GpuDeviceServices, GpuWorkLedger)"/>).</param>
public readonly record struct GpuBuildRequest<TKey>(IGpuDeviceContext Device, TKey Key, GpuWorkLedger Ledger);
/// <summary>
/// One holder's share of a <see cref="GpuBuildCache{TKey, T}"/> entry. The holder polls it from the frame thread, or
/// waits for it from its own background build, records with the value, and releases it on device loss and disposal once
/// nothing it recorded with the value is in flight.
/// </summary>
/// <typeparam name="TKey">What the value is built from.</typeparam>
/// <typeparam name="T">The built value.</typeparam>
public sealed class GpuBuildLease<TKey, T> where TKey : IEquatable<TKey> where T : class, IDisposable {
    private GpuBuildCache<TKey, T>? m_cache;

    private readonly Entry m_entry;

    internal GpuBuildLease(GpuBuildCache<TKey, T> cache, Entry entry) {
        m_cache = cache;
        m_entry = entry;
    }

    /// <summary>Gets the ready value, or <see langword="null"/> before its build has completed and after the lease is
    /// released.</summary>
    public T? Current => ((m_cache is null)
        ? null
        : m_entry.Value
    );
    /// <summary>Gets whether the lease has been released.</summary>
    public bool IsReleased => (m_cache is null);
    /// <summary>Gets the key the entry was created with: the first lease's, which every later lease on the entry
    /// equals.</summary>
    public TKey Key => m_entry.Key;

    /// <summary>Returns the ready value, or <see langword="null"/> while its build runs. A build that failed rethrows its
    /// exception here, on the frame thread, so a device loss reaches the host's recovery; the next poll starts a fresh
    /// build. Allocates nothing while the build runs or once the value is ready.</summary>
    /// <returns>The ready value, or <see langword="null"/> while it builds.</returns>
    /// <exception cref="ObjectDisposedException">The lease has been released.</exception>
    public T? Poll() {
        var cache = m_cache;

        ObjectDisposedException.ThrowIf(
            condition: (cache is null),
            instance: this
        );

        return cache!.Poll(entry: m_entry);
    }
    /// <summary>Gives up the lease. The last lease on an entry waits out a build still in flight, discarding its
    /// result, and disposes the value; call it once nothing recorded with the value is in flight and before the device
    /// goes away. Releasing twice does nothing.</summary>
    public void Release() {
        if (Interlocked.Exchange(
            location1: ref m_cache,
            value: null
        ) is { } cache) {
            cache.Release(entry: m_entry);
        }
    }
    /// <summary>Gives up several leases at once: every build a last lease leaves unheld is canceled before any is waited
    /// for, so the release waits only for the creations already in the driver and no waiting build starts one while an
    /// earlier lease's is waited out. Each lease is released as <see cref="Release()"/> releases it; a released or
    /// <see langword="null"/> lease is skipped.</summary>
    /// <param name="leases">The leases.</param>
    /// <exception cref="ArgumentNullException"><paramref name="leases"/> is <see langword="null"/>.</exception>
    public static void Release(IReadOnlyList<GpuBuildLease<TKey, T>?> leases) {
        ArgumentNullException.ThrowIfNull(argument: leases);

        var canceled = new (CanceledBuild<T> Build, Entry Entry)?[leases.Count];

        for (var index = 0; (index < leases.Count); index++) {
            if (
                (leases[index] is { } lease) &&
                (Interlocked.Exchange(
                    location1: ref lease.m_cache,
                    value: null
                ) is { } cache) &&
                (cache.Detach(entry: lease.m_entry) is { } build)
            ) {
                canceled[index] = (build, lease.m_entry);
            }
        }

        foreach (var retiring in canceled) {
            if (retiring is { } retired) {
                GpuBuildCache<TKey, T>.Retire(
                    build: retired.Build,
                    entry: retired.Entry
                );
            }
        }
    }
    /// <summary>Blocks until the value is ready and returns it, for a holder's own background build: the wait may run
    /// the entry's build inline when it has not started. A build that failed rethrows its exception here; the next
    /// wait or poll starts a fresh one.</summary>
    /// <param name="cancellationToken">The token that ends the wait; the entry's build keeps running for its other
    /// holders.</param>
    /// <returns>The ready value.</returns>
    /// <exception cref="ObjectDisposedException">The lease has been released.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public T Wait(CancellationToken cancellationToken) {
        var cache = m_cache;

        ObjectDisposedException.ThrowIf(
            condition: (cache is null),
            instance: this
        );

        return cache!.Wait(
            cancellationToken: cancellationToken,
            entry: m_entry
        );
    }

    // One entry and its holders. Every mutable member is read and written under the cache's gate.
    internal sealed class Entry(IGpuDeviceContext device, TKey key) {
        public BackgroundBuild<T> Build { get; } = new();
        public IGpuDeviceContext Device { get; } = device;
        public int Holders { get; set; } = 1;

        // Set when the last lease releases, before the build detaches; the entry never builds again.
        public bool IsReleased { get; set; }

        public TKey Key { get; } = key;

        public T? Value { get; set; }
    }
}
