namespace Puck.Platform;

/// <summary>The one-image counterpart of <see cref="LatestSlotPublication"/>: a producer that has exactly one shared
/// texture (a view export) and its readers coordinate through one atomic state instead of rotating slots. The producer
/// reserves the image before each write (<see cref="TryBeginWrite"/>), which fails while any reader holds it, so it keeps
/// the last completed image rather than overwrite one being read; <see cref="EndWrite"/> publishes a completed write with
/// the value it signals on the image's shared fence, which a reader waits for on its own device. A reader releases only
/// once its reads have finished. <see cref="Retire"/> refuses every later reservation and acquisition without waiting for
/// readers still holding the image. Slot zero is the one slot.</summary>
public sealed class SingleSlotPublication : ISharedSlotRing {
    // 0 = no completed image, 1 = readable with no readers, n + 1 = readable with n readers, -2 = producer writing.
    private const int Writing = -2;

    private ulong m_fenceValue;
    private bool m_hadCompletedBeforeWrite;
    private volatile bool m_retired;
    private int m_state;
    private long m_version;

    /// <summary>Gets whether a completed image is published and the publication is not retired.</summary>
    public bool HasCompletedFrame => (!m_retired && (Volatile.Read(location: ref m_state) >= 1));
    /// <inheritdoc/>
    /// <remarks>Zero while a completed image is published, else <c>-1</c>.</remarks>
    public int LatestSlot => (HasCompletedFrame
        ? 0
        : -1
    );
    /// <inheritdoc/>
    public long Version => Interlocked.Read(location: ref m_version);

    /// <summary>Ends a write <see cref="TryBeginWrite"/> reserved. A completed write is published with its fence value,
    /// stored before the image turns readable, so a reader that acquires it reads the value of the write it samples; a
    /// write that did not complete keeps the image and value published before it.</summary>
    /// <param name="completed">Whether the write completed.</param>
    /// <param name="fenceValue">The value the write signals on the image's shared fence, or zero when it finished before
    /// this call.</param>
    public void EndWrite(bool completed, ulong fenceValue) {
        if (completed) {
            Volatile.Write(
                location: ref m_fenceValue,
                value: fenceValue
            );
            _ = Interlocked.Increment(location: ref m_version);
        }

        Volatile.Write(
            location: ref m_state,
            value: ((completed || m_hadCompletedBeforeWrite)
            ? 1
            : 0)
        );
    }
    /// <inheritdoc/>
    public void Release(int slot) {
        if (slot != 0) {
            return;
        }

        while (true) {
            var state = Volatile.Read(location: ref m_state);

            if (state <= 1) {
                return;
            }
            if (Interlocked.CompareExchange(
                comparand: state,
                location1: ref m_state,
                value: (state - 1)
            ) == state) {
                return;
            }
        }
    }
    /// <summary>Refuses every later reservation and acquisition. Returns at once: a reader still holding the image
    /// releases it when its reads finish, and nothing writes it again.</summary>
    public void Retire() => m_retired = true;
    /// <inheritdoc/>
    public bool TryAcquireLatest(out int slot, out ulong fenceValue, out long version) {
        version = 0L;
        while (true) {
            var state = Volatile.Read(location: ref m_state);

            if (
                m_retired ||
                (state < 1) ||
                (state == int.MaxValue)
            ) {
                slot = -1;
                fenceValue = 0UL;

                return false;
            }
            if (Interlocked.CompareExchange(
                comparand: state,
                location1: ref m_state,
                value: (state + 1)
            ) != state) {
                continue;
            }

            // A retirement that raced the acquisition wins.
            if (m_retired) {
                Release(slot: 0);
                slot = -1;
                fenceValue = 0UL;

                return false;
            }

            slot = 0;
            fenceValue = Volatile.Read(location: ref m_fenceValue);
            version = Interlocked.Read(location: ref m_version);

            return true;
        }
    }
    /// <summary>Reserves the image for the producer's next write: fails while a reader holds it, while another write is
    /// reserved, and once retired.</summary>
    /// <returns>Whether the producer may write the image.</returns>
    public bool TryBeginWrite() {
        while (true) {
            var state = Volatile.Read(location: ref m_state);

            if (
                m_retired ||
                (state > 1) ||
                (state < 0)
            ) {
                return false;
            }
            if (Interlocked.CompareExchange(
                comparand: state,
                location1: ref m_state,
                value: Writing
            ) == state) {
                m_hadCompletedBeforeWrite = (state == 1);

                return true;
            }
        }
    }
}
