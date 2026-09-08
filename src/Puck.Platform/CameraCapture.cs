using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform;

/// <summary>One sensor's place in a camera open: the sensor and the capture envelope to negotiate near.</summary>
/// <param name="Sensor">Which physical sensor the stream reads.</param>
/// <param name="Width">The desired frame width; a coordinated driver profile keeps its own declared extent.</param>
/// <param name="Height">The desired frame height.</param>
/// <param name="RateHz">The desired capture rate; zero prefers the device's fastest mode at the chosen size.</param>
public readonly record struct CameraStreamRequest(CameraSensor Sensor, int Width, int Height, uint RateHz);
/// <summary>The native transport format a camera graph negotiated before any platform conversion into Puck's
/// presentation surface.</summary>
/// <param name="Subtype">The native media subtype (for example <c>YUY2</c>, <c>MJPG</c>, or <c>L8</c>).</param>
/// <param name="RateHz">The native transport cadence in frames per second, or zero when the driver did not report it.</param>
/// <param name="Mode">The named coordinated capture mode, or <see langword="null"/> for an ordinary single-stream
/// graph.</param>
public readonly record struct CameraCaptureFormat(string Subtype, double RateHz, string? Mode = null);
/// <summary>One physical camera as the platform enumerates it, independent of whether — or how — anything has it
/// open. <see cref="Id"/> is the platform's stable device identity: reconnecting the same physical camera reports the
/// same <see cref="Id"/>, so it survives a hot-unplug/replug and is safe to key a roster entry by.</summary>
/// <param name="Id">The platform's stable device identity (on Windows, the Media Foundation frame-source group id).</param>
/// <param name="Name">The driver-reported display name.</param>
/// <param name="Sensors">The physical sensors this device exposes.</param>
public readonly record struct CameraDeviceInfo(string Id, string Name, IReadOnlyList<CameraSensor> Sensors);
/// <summary>
/// Opens a physical camera as a backend-neutral graph of sensor streams. One open is one device graph: every
/// requested sensor streams, or the open refuses as a whole and the caller decides which sensor to drop. Two tiers,
/// each behind its own opener: CPU pixels (<see cref="TryOpenPixels"/> — frames read back to host memory) and shared
/// GPU textures (<see cref="TryOpenShared"/> — frames converted on the platform's device and copied into
/// consumer-provisioned shared targets, never visiting host memory). The device remains authoritative for the
/// negotiated extent and format; read both off each stream.
/// </summary>
public interface ICameraCaptureService {
    /// <summary>Gets a value indicating whether this platform can open camera devices at all.</summary>
    bool IsSupported { get; }

    /// <summary>Enumerates every physical camera currently attached, in a stable order by <see cref="CameraDeviceInfo.Id"/>.
    /// A completed scan distinguishes itself from a failed one: an empty machine (or an unsupported platform) returns an
    /// empty list, while the platform's scan mechanism itself failing throws instead — a caller must never read an empty
    /// list as "every camera was unplugged". This call may block on platform discovery: call it on a worker thread.
    /// Implementations support enumeration concurrently with graph opens and existing streams.</summary>
    /// <returns>The attached cameras from a completed scan; empty when none are attached or the platform is unsupported.</returns>
    /// <exception cref="InvalidOperationException">The platform's device scan failed.</exception>
    IReadOnlyList<CameraDeviceInfo> EnumerateDevices();
    /// <summary>Tries to open the requested sensors of one physical camera as one CPU-pixel graph, negotiating each
    /// single-sensor stream near its envelope: the smallest native frame size covering the requested extent (else the
    /// largest available), at the lowest native rate covering the requested rate (else the highest available). A
    /// successful open has already published at least one usable host frame from every returned stream.</summary>
    /// <param name="deviceId">The physical camera to open, from <see cref="EnumerateDevices"/>.</param>
    /// <param name="streams">The sensors to open, in the order their streams are returned.</param>
    /// <param name="graph">When this returns <see langword="true"/>, the open graph; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if every requested sensor opened and published a usable host frame.</returns>
    bool TryOpenPixels(string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraPixelStream>? graph);
    /// <summary>Tries to open the requested sensors of one physical camera as one shared-texture graph on the
    /// consumer's render adapter. A successful open has validated a native GPU sample from every requested sensor. The
    /// returned streams remain idle until each receives its targets through <see cref="ICameraSharedStream.Start"/>; a
    /// coordinated graph publishes only after every stream has started.</summary>
    /// <param name="adapterLuid">The consumer render device's adapter LUID; the platform's device must share the adapter
    /// for the shared textures to be openable.</param>
    /// <param name="deviceId">The physical camera to open, from <see cref="EnumerateDevices"/>.</param>
    /// <param name="streams">The sensors to open, in the order their streams are returned.</param>
    /// <param name="graph">When this returns <see langword="true"/>, the live source graph; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if every requested sensor opened and delivered a native GPU sample.</returns>
    bool TryOpenShared(long adapterLuid, string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraSharedStream>? graph);
}
/// <summary>One open camera device: its sensor streams and its one physical control surface. Disposing the graph
/// stops every stream.</summary>
/// <typeparam name="TStream">The tier's stream shape.</typeparam>
public interface ICameraGraph<out TStream> : IDisposable where TStream : ICameraStream {
    /// <summary>Gets the device's live control surface. Controls live on the physical source, independent of which
    /// tier reads frames, and apply mid-stream.</summary>
    ICameraControlSurface Controls { get; }
    /// <summary>Gets the platform device identity this graph opened — the same <see cref="CameraDeviceInfo.Id"/> that
    /// was passed to <see cref="ICameraCaptureService.TryOpenPixels"/>/<see cref="ICameraCaptureService.TryOpenShared"/>.</summary>
    string DeviceId { get; }
    /// <summary>Gets a value indicating whether the graph has permanently stopped (device unplugged, end of stream, or
    /// a mid-stream error) — the consumer's signal to dispose it and reopen.</summary>
    bool IsEnded { get; }
    /// <summary>Gets a human-readable device name, for diagnostics.</summary>
    string Name { get; }
    /// <summary>Gets the sensor streams, in request order.</summary>
    IReadOnlyList<TStream> Streams { get; }
}
/// <summary>One sensor's stream inside a camera graph. Frames arrive on a platform-owned thread; the consumer polls
/// <see cref="FrameVersion"/> and never blocks.</summary>
public interface ICameraStream {
    /// <summary>Gets a monotonically increasing count of frames delivered; a consumer compares it against the value it
    /// last processed to skip unchanged frames.</summary>
    long FrameVersion { get; }
    /// <summary>Gets the negotiated frame height in pixels.</summary>
    int Height { get; }
    /// <summary>Gets the <see cref="System.Diagnostics.Stopwatch"/> timestamp of the most recent frame's arrival, stamped
    /// on the platform thread at publish.</summary>
    long LastFrameTimestamp { get; }
    /// <summary>Gets the native transport subtype/rate and any named coordinated capture mode.</summary>
    CameraCaptureFormat NativeFormat { get; }
    /// <summary>Gets the physical sensor this stream reads.</summary>
    CameraSensor Sensor { get; }
    /// <summary>Gets the negotiated frame width in pixels.</summary>
    int Width { get; }
}
/// <summary>A CPU-pixel stream: <see cref="IFrameCaptureSource.TryCapture"/> returns the newest frame as
/// <see cref="SurfaceFormat.B8G8R8A8Unorm"/> pixels (latest-frame-wins, stale frames dropped). A stream returned by a
/// successful <see cref="ICameraCaptureService.TryOpenPixels"/> already contains its first frame.</summary>
public interface ICameraPixelStream : ICameraStream, IFrameCaptureSource;
/// <summary>A shared-texture stream: the platform converts each frame on its own device and copies it into one of the
/// consumer-provisioned shared targets (sized <see cref="ICameraStream.Width"/> × <see cref="ICameraStream.Height"/>
/// in <see cref="TargetFormat"/>), completing the copy before publishing the slot. Consumers acquire the newest
/// completed slot and release it only after asynchronous GPU sampling retires; the producer drops a frame when every
/// non-current target remains acquired.</summary>
public interface ICameraSharedStream : ICameraStream, ISharedSlotRing {
    /// <summary>Gets the pixel format the consumer must provision the shared targets in.</summary>
    SurfaceFormat TargetFormat { get; }

    /// <summary>Begins streaming into the given shared targets; frames are published across slots that have no active
    /// consumer acquisition.</summary>
    /// <param name="sharedTargetHandles">The consumer-provisioned shared textures (opaque NT handles on Windows).</param>
    /// <exception cref="ArgumentException">Fewer than two targets were provided.</exception>
    /// <exception cref="InvalidOperationException">The stream already started.</exception>
    void Start(IReadOnlyList<nint> sharedTargetHandles);
}
/// <summary>A ring of consumer-owned slots with one producer: a consumer acquires the latest completed slot, samples
/// it across an asynchronous submission, and releases it; the producer never writes a slot a consumer holds.</summary>
public interface ISharedSlotRing {
    /// <summary>Gets the most recently published slot, or <c>-1</c> before the first publication.</summary>
    int LatestSlot { get; }

    /// <summary>Acquires the latest completed slot; pair a <see langword="true"/> result with <see cref="Release"/>.</summary>
    /// <param name="slot">When this returns <see langword="true"/>, the slot to sample.</param>
    /// <returns>Whether a slot has been published.</returns>
    bool TryAcquireLatest(out int slot);
    /// <summary>Releases a slot acquired by <see cref="TryAcquireLatest"/> once the work sampling it has retired.</summary>
    /// <param name="slot">The acquired slot.</param>
    void Release(int slot);
}
