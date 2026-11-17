using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Security;

namespace Puck.DirectX;

/// <summary>
/// One Direct3D 12 device's memory budget reading (<see cref="GpuMemoryReading"/>), read from the adapter the device was
/// created on, held as <c>IDXGIAdapter3</c> for the device's life. Each segment group its <see cref="GpuMemorySegments"/>
/// names is read with <c>IDXGIAdapter3::QueryVideoMemoryInfo</c>, every field kept: the local group always, the
/// non-local group only on a discrete adapter, since a unified adapter's system pool and local group are one memory.
/// <see cref="Refresh"/> follows the backends' one refresh rule (<see cref="GpuMemoryReadingRefresh"/>): it reads when an
/// auto-reset event registered with <c>RegisterVideoMemoryBudgetChangeNotificationEvent</c> is set, tested without
/// waiting, since that event signals budget changes, and otherwise once a second, since usage moves with every allocation
/// and no event signals it. The serial moves only when a budget changes. <see cref="Dispose"/> unregisters the event
/// before the device goes; a recreated device gets a new instance. Not thread-safe: <see cref="Refresh"/> runs on the frame thread, and <see cref="Reading"/> is
/// safe to read from any thread.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXVideoMemory : IDisposable {
    private readonly GpuMemorySegments m_segments;

    private nint m_adapter;
    private uint m_cookie;
    private HANDLE m_event;

    private readonly GpuMemoryReadingRefresh m_refresh;

    private GpuMemoryReading m_reading = GpuMemoryReading.Absent;
    // The reservation last passed to the adapter, after clamping, and whether the adapter took it; null before the first.
    private ulong? m_reserved;
    private bool m_reservationRefused;
    private bool m_stale;

    /// <summary>Initializes a new instance of the <see cref="DirectXVideoMemory"/> class, taking ownership of an
    /// adapter reference, and registers its budget-change event. A registration the adapter refuses leaves the reading
    /// to <see cref="Refresh"/>'s first call alone.</summary>
    /// <param name="adapter">The native <c>IDXGIAdapter3</c> the device was created on; the instance owns the reference
    /// and releases it on <see cref="Dispose"/>.</param>
    /// <param name="segments">The device's segments.</param>
    /// <param name="serial">The serial of the last reading the device's predecessor published, so a recreated device's
    /// serial keeps moving forward.</param>
    /// <exception cref="ArgumentException"><paramref name="adapter"/> is zero.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is <see langword="null"/>.</exception>
    /// <exception cref="DirectXException">The event could not be created.</exception>
    public DirectXVideoMemory(nint adapter, GpuMemorySegments segments, ulong serial) {
        ArgumentNullException.ThrowIfNull(argument: segments);
        if (0 == adapter) {
            throw new ArgumentException(message: "The video memory reading needs the device's adapter.", paramName: nameof(adapter));
        }

        m_adapter = adapter;
        m_segments = segments;
        m_refresh = new GpuMemoryReadingRefresh(serial: serial);
        m_event = PInvoke.CreateEvent(
            bInitialState: true,
            bManualReset: false,
            lpEventAttributes: ((SECURITY_ATTRIBUTES*)null),
            lpName: default(PCWSTR)
        );

        if (m_event.IsNull) {
            DirectXConstants.Release(pointer: ref m_adapter);
            throw new DirectXException(operation: "CreateEventW", result: Marshal.GetHRForLastWin32Error());
        }

        uint cookie;

        // The event starts set, so the first refresh reads whether or not the registration sets it again.
        try {
            ((IDXGIAdapter3*)m_adapter)->RegisterVideoMemoryBudgetChangeNotificationEvent(hEvent: m_event, pdwCookie: &cookie);
            m_cookie = cookie;
        } catch (COMException) {
            m_cookie = 0U;
        }
    }

    /// <summary>Gets the latest reading; <see cref="GpuMemoryReading.Absent"/> before the first refresh, and a lost
    /// reading once a query fails or the instance is disposed.</summary>
    public GpuMemoryReading Reading => Volatile.Read(location: ref m_reading);

    /// <summary>Reads every segment group again when the budget-change event is set, tested without waiting, or a second
    /// has passed since the last read (<see cref="GpuMemoryReadingRefresh"/>); otherwise makes no driver call.</summary>
    /// <param name="stale">Whether something outside the adapter's own signal made the reading stale: the device
    /// refused an allocation since the last refresh.</param>
    /// <returns>Whether a new reading was published.</returns>
    public bool Refresh(bool stale = false) {
        if (0 == m_adapter) {
            return false;
        }

        // The wait resets the auto-reset event, so it is tested whether or not the interval has passed.
        var signalled = (!m_event.IsNull && (PInvoke.WaitForSingleObject(dwMilliseconds: 0U, hHandle: m_event) == WAIT_EVENT.WAIT_OBJECT_0));
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        if (!m_refresh.Due(signalled: (signalled || stale || m_stale), timestamp: now)) {
            return false;
        }

        m_stale = false;

        var readings = new GpuSegmentReading[m_segments.Segments.Count];

        for (var index = 0; (index < readings.Length); index++) {
            var segment = m_segments.Segments[index];
            DXGI_QUERY_VIDEO_MEMORY_INFO info;

            // A removed device's adapter refuses the query; its reading is lost until the device is recreated.
            try {
                ((IDXGIAdapter3*)m_adapter)->QueryVideoMemoryInfo(NodeIndex: 0U, MemorySegmentGroup: ((DXGI_MEMORY_SEGMENT_GROUP)segment.Index), pVideoMemoryInfo: &info);
            } catch (COMException) {
                Volatile.Write(location: ref m_reading, value: GpuMemoryReading.Lost(serial: m_refresh.Serial));

                return true;
            }

            readings[index] = new GpuSegmentReading(
                AvailableForReservation: info.AvailableForReservation,
                Budget: info.Budget,
                CurrentReservation: info.CurrentReservation,
                Segment: segment,
                Usage: info.CurrentUsage
            );
        }

        Volatile.Write(location: ref m_reading, value: m_refresh.Publish(estimated: false, last: m_reading, readings: readings, segments: m_segments, timestamp: now));

        return true;
    }
    /// <summary>Passes the bytes the process cannot run without to the adapter's local segment group
    /// (<c>IDXGIAdapter3::SetVideoMemoryReservation</c>), clamped to what the last reading says may be reserved there,
    /// when the clamped figure differs from the one last passed; the next refresh then reads the reservation again.
    /// Before the first reading nothing is passed, since nothing says what may be reserved.</summary>
    /// <param name="bytes">The bytes the process cannot run without.</param>
    /// <returns>The bytes the adapter last took, or <see langword="null"/> before the first reading, after disposal or
    /// when the adapter refused the call.</returns>
    public ulong? Reserve(ulong bytes) {
        if ((0 == m_adapter) || (Reading is not { State: GpuMemoryReadingState.Read } reading)) {
            return null;
        }

        var available = 0UL;

        foreach (var segment in reading.Segments) {
            if (segment.Segment.Index == GpuMemorySegments.DirectXLocal) { available = (segment.AvailableForReservation ?? 0UL); }
        }

        var clamped = Math.Min(val1: bytes, val2: available);

        if (m_reserved == clamped) {
            return (m_reservationRefused ? null : clamped);
        }

        m_reserved = clamped;
        m_stale = true;
        try {
            ((IDXGIAdapter3*)m_adapter)->SetVideoMemoryReservation(MemorySegmentGroup: DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, NodeIndex: 0U, Reservation: clamped);
            m_reservationRefused = false;
        } catch (COMException) {
            m_reservationRefused = true;
        }

        return (m_reservationRefused ? null : clamped);
    }
    /// <summary>Unregisters the budget-change event, closes it and releases the adapter. Safe to call more than once.</summary>
    public void Dispose() {
        if (0 == m_adapter) {
            return;
        }

        if (0U != m_cookie) {
            ((IDXGIAdapter3*)m_adapter)->UnregisterVideoMemoryBudgetChangeNotification(dwCookie: m_cookie);
            m_cookie = 0U;
        }
        if (!m_event.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_event);
            m_event = HANDLE.Null;
        }

        DirectXConstants.Release(pointer: ref m_adapter);
        Volatile.Write(location: ref m_reading, value: GpuMemoryReading.Lost(serial: m_refresh.Serial));
    }
}
