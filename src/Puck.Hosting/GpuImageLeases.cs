using Puck.Abstractions.Gpu;

namespace Puck.Hosting;

/// <summary>
/// The images a set of render nodes own on one device, each disposed only once its owner has dropped it and every reader
/// that leased it has retired its lease. An owner creates its images through <see cref="Wrap(IGpuImageFactory)"/>'s factory, whose images
/// dispose through this table; a reader asks for a lease on any image by its handle (<see cref="TryLease"/>), whoever
/// owns it, and holds the lease in a <see cref="LeaseRetireList"/> that retires it once the submission that read the
/// image has finished. An image a reader leased stays alive after its owner drops it (an owner released, retired or
/// replacing its graph) until the last lease on it retires, and is disposed then.
/// <para>
/// Leasing and retiring reuse slots and callbacks once the table has grown to the peak simultaneous image and lease
/// counts. A slot whose generation is exhausted is never reused. Every member may be called from any thread.
/// </para>
/// </summary>
public sealed class GpuImageLeases {
    private readonly Dictionary<nint, int> m_byHandle = [];
    private readonly Lock m_gate = new();
    private Entry[] m_entries = [];
    private int m_free = -1;
    private LeaseSlot[] m_leases = [];
    private int m_freeLease = -1;

    private int m_leasesUsed;
    private int m_used;

    // Objects queued while the table disposes in the background, the one worker draining them while it runs, and the
    // first failure a disposal met, which DrainDisposals rethrows.
    private readonly bool m_disposesInBackground;
    private readonly Lock m_disposalGate = new();
    private readonly Queue<IDisposable> m_disposals = new();
    private Task m_disposer = Task.CompletedTask;
    private bool m_disposing;
    private Exception? m_disposalFailure;

    /// <summary>Initializes a new instance of the <see cref="GpuImageLeases"/> class.</summary>
    /// <param name="disposesInBackground">Whether an image freed once its owner dropped it and its last lease retired is
    /// disposed on a worker thread rather than on the thread that freed it, so destroying a graph's targets costs the
    /// frame nothing: every submission that read the image has finished by then, and creating and destroying device
    /// objects is free-threaded on both backends. <see cref="DrainDisposals"/> waits for them.</param>
    public GpuImageLeases(bool disposesInBackground = false) => m_disposesInBackground = disposesInBackground;

    /// <summary>Gets whether the table disposes what it frees, and what <see cref="DisposeLater"/> is handed, on a worker
    /// thread.</summary>
    public bool DisposesInBackground => m_disposesInBackground;

    /// <summary>Disposes a device object no submission can still read, an owner's buffer or region that held no image of
    /// this table, on the table's worker when it disposes in the background and at once otherwise, so an owner releasing a
    /// replaced graph frees all of it off the frame thread. <see cref="DrainDisposals"/> waits for it.</summary>
    /// <param name="disposable">The object, or <see langword="null"/> for none.</param>
    public void DisposeLater(IDisposable? disposable) {
        if (disposable is null) {
            return;
        }
        if (!m_disposesInBackground) {
            disposable.Dispose();

            return;
        }

        lock (m_disposalGate) {
            m_disposals.Enqueue(item: disposable);

            if (!m_disposing) {
                m_disposing = true;
                m_disposer = Task.Run(action: DisposeQueued);
            }
        }
    }
    /// <summary>Waits until every object the table freed or was handed has been disposed, then rethrows the first failure
    /// a background disposal met, if any. A table that disposes on the calling thread has nothing to wait for.</summary>
    public void DrainDisposals() {
        while (true) {
            Task disposer;

            lock (m_disposalGate) {
                if (!m_disposing) {
                    var failure = m_disposalFailure;

                    m_disposalFailure = null;

                    if (failure is not null) {
                        throw new InvalidOperationException(message: "Disposing a freed device object failed.", innerException: failure);
                    }

                    return;
                }

                disposer = m_disposer;
            }

            disposer.Wait();
        }
    }
    // Disposes a freed image, or queues it for the worker.
    private void DisposeFreed(IGpuImage? image) => DisposeLater(disposable: image);
    private void DisposeQueued() {
        while (true) {
            IDisposable? disposable;

            lock (m_disposalGate) {
                if (!m_disposals.TryDequeue(result: out disposable)) {
                    m_disposing = false;

                    return;
                }
            }

            try {
                disposable.Dispose();
            } catch (Exception error) {
                lock (m_disposalGate) {
                    m_disposalFailure ??= error;
                }
            }
        }
    }

    /// <summary>Gets the number of images an owner has dropped that a lease still keeps alive.</summary>
    public int Deferred {
        get {
            lock (m_gate) {
                var count = 0;

                for (var index = 0; (index < m_used); index++) {
                    if (m_entries[index].Dropped && (m_entries[index].Leases > 0)) {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    /// <summary>Returns a lease on an image an owner created through <see cref="Wrap(IGpuImageFactory)"/> and has not disposed, or
    /// <see langword="false"/> for any other handle: an image no owner here created, such as a stand-in or another
    /// producer's, or one already gone.</summary>
    /// <param name="imageHandle">The image's native handle.</param>
    /// <param name="lease">The lease, carrying the image's view handle, when this returns <see langword="true"/>. Its
    /// holder retires it once no submission it made can still read the image.</param>
    /// <returns><see langword="true"/> when the image is leased.</returns>
    public bool TryLease(nint imageHandle, out GpuImageLease lease) {
        lock (m_gate) {
            if (
                (imageHandle == 0) ||
                !m_byHandle.TryGetValue(
                    key: imageHandle,
                    value: out var index
                )
            ) {
                lease = default;

                return false;
            }

            ref var entry = ref m_entries[index];
            int slot;

            if (m_freeLease >= 0) {
                slot = m_freeLease;
                m_freeLease = m_leases[slot].NextFree;
            } else {
                if (m_leasesUsed == m_leases.Length) {
                    Array.Resize(
                        array: ref m_leases,
                        newSize: Math.Max(
                            val1: 16,
                            val2: (m_leasesUsed * 2)
                        )
                    );
                }

                slot = m_leasesUsed++;
                m_leases[slot] = new LeaseSlot(images: this, slot: slot);
            }

            ref var held = ref m_leases[slot];

            held.Entry = index;
            held.Live = true;
            held.NextFree = -1;
            entry.Leases++;
            lease = new GpuImageLease(
                ImageViewHandle: entry.Image!.ImageViewHandle,
                Release: held.Release,
                ReleaseToken: held.Generation
            );

            return true;
        }
    }
    /// <summary>Returns an image factory whose images dispose through this table: an image disposed while leased is
    /// disposed once its last lease retires.</summary>
    /// <param name="factory">The factory that creates the images.</param>
    /// <returns>The leasing factory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public IGpuImageFactory Wrap(IGpuImageFactory factory) {
        ArgumentNullException.ThrowIfNull(argument: factory);

        return new LeasingImageFactory(
            images: this,
            inner: factory
        );
    }
    /// <summary>Returns a device's services whose image factory creates this table's images (<see cref="Wrap(IGpuImageFactory)"/>)
    /// and whose every other service is the device's own.</summary>
    /// <param name="services">The device's services.</param>
    /// <returns>The services.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public GpuDeviceServices Wrap(GpuDeviceServices services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        return new GpuDeviceServices {
            Bindings = services.Bindings,
            BufferFactory = services.BufferFactory,
            CommandPoolFactory = services.CommandPoolFactory,
            Faults = services.Faults,
            ImageFactory = Wrap(factory: services.ImageFactory),
            Naming = services.Naming,
            PipelineFactory = services.PipelineFactory,
            QueueSubmitter = services.QueueSubmitter,
            Recorder = services.Recorder,
            RenderPassFactory = services.RenderPassFactory,
            ShaderModuleFactory = services.ShaderModuleFactory,
            SurfaceTransferFactory = services.SurfaceTransferFactory,
            TimestampFactory = services.TimestampFactory,
        };
    }

    private LeasedImage Own(IGpuImage image) {
        lock (m_gate) {
            int index;

            if (m_free >= 0) {
                index = m_free;
                m_free = m_entries[index].NextFree;
            } else {
                if (m_used == m_entries.Length) {
                    Array.Resize(
                        array: ref m_entries,
                        newSize: Math.Max(
                            val1: 8,
                            val2: (m_used * 2)
                        )
                    );
                }

                index = m_used++;
            }

            ref var entry = ref m_entries[index];

            entry.Dropped = false;
            entry.Image = image;
            entry.Leases = 0;
            entry.NextFree = -1;
            // A handle names one live image; a device that hands out the same value twice (a fake) leaves the second
            // unleasable rather than confusing the two.
            entry.Leasable = m_byHandle.TryAdd(
                key: image.ImageHandle,
                value: index
            );

            return new LeasedImage(
                images: this,
                index: index,
                inner: image
            );
        }
    }
    // The owner disposes an image: at once when nothing leases it, otherwise once its last lease retires.
    private void Drop(int index) {
        IGpuImage? disposed;

        lock (m_gate) {
            ref var entry = ref m_entries[index];

            entry.Dropped = true;
            disposed = ((entry.Leases == 0)
                ? Free(index: index)
                : null);
        }

        DisposeFreed(image: disposed);
    }
    // Retires one lease: refuses one already retired, then drops its count on the image and disposes an image its owner
    // dropped once nothing leases it.
    private void Release(int slot, int generation) {
        IGpuImage? disposed = null;

        lock (m_gate) {
            if (
                (slot >= m_leasesUsed) ||
                !m_leases[slot].Live ||
                (m_leases[slot].Generation != generation)
            ) {
                throw new InvalidOperationException(message: $"An image lease (slot {slot}, generation {generation}) was retired twice: each lease retires once, after the last submission that read its image.");
            }

            ref var held = ref m_leases[slot];
            var index = held.Entry;

            held.Live = false;
            // A slot's callback identifies it, leaving the whole token for its generation. An exhausted slot is never
            // reused: no old lease can become valid again, even after the generation's entire range has been spent.
            if (held.Generation < int.MaxValue) {
                held.Generation++;
                held.NextFree = m_freeLease;
                m_freeLease = slot;
            }

            ref var entry = ref m_entries[index];

            entry.Leases--;

            if (entry.Dropped && (entry.Leases == 0)) {
                disposed = Free(index: index);
            }
        }

        DisposeFreed(image: disposed);
    }
    // Forgets an entry under the gate and returns its image for the caller to dispose outside it.
    private IGpuImage Free(int index) {
        ref var entry = ref m_entries[index];
        var image = entry.Image!;

        if (entry.Leasable) {
            _ = m_byHandle.Remove(key: image.ImageHandle);
        }

        entry.Image = null;
        entry.NextFree = m_free;
        m_free = index;

        return image;
    }

    // One lease: the image entry it counts against, and whether it is still to retire.
    private sealed class LeaseSlot(GpuImageLeases images, int slot) {
        public readonly Action<int> Release = generation => images.Release(generation: generation, slot: slot);

        public int Entry;
        public int Generation;
        public bool Live;
        public int NextFree;
    }
    private struct Entry {
        public bool Dropped;
        public bool Leasable;
        public IGpuImage? Image;
        public int Leases;
        public int NextFree;
    }
    // An owner's image: everything but disposal forwards to the backend's image, and disposal goes through the table.
    private sealed class LeasedImage(GpuImageLeases images, IGpuImage inner, int index) : IGpuImage {
        private int m_disposed;

        public GpuPixelFormat Format => inner.Format;
        public uint Height => inner.Height;
        public nint ImageHandle => inner.ImageHandle;
        public nint ImageViewHandle => inner.ImageViewHandle;
        public GpuImageUsage Usage => inner.Usage;
        public uint Width => inner.Width;

        public void Dispose() {
            if (Interlocked.Exchange(location1: ref m_disposed, value: 1) == 0) {
                images.Drop(index: index);
            }
        }
    }
    private sealed class LeasingImageFactory(GpuImageLeases images, IGpuImageFactory inner) : IGpuImageFactory {
        public IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name) => images.Own(image: inner.Create(
            format: format,
            height: height,
            name: in name,
            usage: usage,
            width: width
        ));
        public IGpuImage CreateDepth(in GpuDepthAttachment attachment, uint width, uint height, in GpuObjectName name) => images.Own(image: inner.CreateDepth(
            attachment: in attachment,
            height: height,
            name: in name,
            width: width
        ));
    }
}
