using System.Numerics;
using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.World.Client;

/// <summary>
/// The cameras a world's seats hold, as a camera source reads them each frame: the device a seat holds can change, so
/// every read names the seat and the sensor rather than a device. Every member runs on the thread that produces frames.
/// </summary>
public interface IWorldSeatCameras {
    /// <summary>Acquires the latest image of a seat's sensor, held until the frame that samples it retires.</summary>
    /// <param name="seat">The 1-based seat.</param>
    /// <param name="sensor">The sensor.</param>
    /// <returns>The image's lease, or an empty lease while the sensor shows nothing.</returns>
    GpuImageLease Acquire(int seat, WorldCameraSensor sensor);
    /// <summary>Returns the extent a seat's sensor delivers: the extent its device negotiated once it streams, or the
    /// extent its feed requested before then.</summary>
    /// <param name="seat">The 1-based seat.</param>
    /// <param name="sensor">The sensor.</param>
    /// <returns>The extent, or <see langword="null"/> while the seat holds no camera with the sensor.</returns>
    (uint Width, uint Height)? Extent(int seat, WorldCameraSensor sensor);
    /// <summary>Returns why a seat's sensor shows nothing.</summary>
    /// <param name="seat">The 1-based seat.</param>
    /// <param name="sensor">The sensor.</param>
    /// <returns>The fault, or <see langword="null"/> while the sensor streams.</returns>
    string? Fault(int seat, WorldCameraSensor sensor);
    /// <summary>Returns the image-view handle of a seat's sensor's latest image, for a read that submits no GPU work.</summary>
    /// <param name="seat">The 1-based seat.</param>
    /// <param name="sensor">The sensor.</param>
    /// <returns>The handle, or zero while the sensor shows nothing.</returns>
    nint Handle(int seat, WorldCameraSensor sensor);
    /// <summary>Returns the light a seat's sensor's latest image casts on the room.</summary>
    /// <param name="seat">The 1-based seat.</param>
    /// <param name="sensor">The sensor.</param>
    /// <returns>The light, or zero while the sensor shows nothing.</returns>
    Vector3 Light(int seat, WorldCameraSensor sensor);
}

/// <summary>
/// One camera source's feed: a reference to the sensor of the camera a seat holds, which the seat's cameras serve
/// (<see cref="IWorldSeatCameras"/>). Its descriptor states the extent the sensor delivers — the one the device
/// negotiated once it streams, the one the source requested before — and follows it when the device, its profile or its
/// tier changes, so the render graph schedules the source and a screen maps it at the extent its image has. Publishing,
/// device loss and disposal belong to the seat's cameras, so they are nothing here. Every member runs on the thread that
/// produces frames.
/// </summary>
public sealed class WorldCameraSourceFeed : IWorldImportFeed {
    private readonly IWorldSeatCameras m_cameras;

    private ImageSourceDescriptor m_descriptor;

    /// <summary>Initializes a new instance of the <see cref="WorldCameraSourceFeed"/> class.</summary>
    /// <param name="cameras">The cameras the world's seats hold.</param>
    /// <param name="profile">The source's requested profile, or <see langword="null"/> for
    /// <see cref="WorldFeedProfile.Default"/>.</param>
    /// <param name="seat">The 1-based seat whose camera the source shows.</param>
    /// <param name="sensor">The sensor the source shows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cameras"/> is <see langword="null"/>.</exception>
    public WorldCameraSourceFeed(IWorldSeatCameras cameras, WorldFeedProfile? profile, int seat, WorldCameraSensor sensor) {
        ArgumentNullException.ThrowIfNull(argument: cameras);

        var requested = (profile ?? WorldFeedProfile.Default);

        m_cameras = cameras;
        m_descriptor = new ImageSourceDescriptor(
            Cadence: ImageSourceCadence.Rate(rateHz: requested.RefreshRateHz),
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.External,
            Format: ImagePixelFormat.B8G8R8A8Unorm,
            Height: checked((uint)requested.Height),
            Producer: WorldImageProducerSettings.CameraId,
            Transport: ImageSourceTransport.Imported,
            Width: checked((uint)requested.Width)
        );
        Requested = requested;
        Seat = seat;
        Sensor = sensor;
    }

    /// <inheritdoc/>
    /// <remarks>The extent is the one the seat's sensor delivers, or the requested one while the seat holds no camera
    /// with the sensor; the descriptor is a new record only when the extent changed.</remarks>
    public ImageSourceDescriptor Descriptor {
        get {
            var (width, height) = (m_cameras.Extent(
                seat: Seat,
                sensor: Sensor
            ) ?? (checked((uint)Requested.Width), checked((uint)Requested.Height)));

            if (
                (width != m_descriptor.Width) ||
                (height != m_descriptor.Height)
            ) {
                m_descriptor = (m_descriptor with {
                    Height = height,
                    Width = width,
                });
            }

            return m_descriptor;
        }
    }
    /// <inheritdoc/>
    public string? Fault => m_cameras.Fault(
        seat: Seat,
        sensor: Sensor
    );
    /// <inheritdoc/>
    public Vector3 Light => m_cameras.Light(
        seat: Seat,
        sensor: Sensor
    );
    /// <summary>Gets the profile the source requested.</summary>
    public WorldFeedProfile Requested { get; }
    /// <summary>Gets the 1-based seat whose camera the source shows.</summary>
    public int Seat { get; }
    /// <summary>Gets the sensor the source shows.</summary>
    public WorldCameraSensor Sensor { get; }

    /// <inheritdoc/>
    public GpuImageLease AcquireFrame() => m_cameras.Acquire(
        seat: Seat,
        sensor: Sensor
    );
    /// <inheritdoc/>
    /// <remarks>The seat's cameras own every resource, so disposal releases nothing.</remarks>
    public void Dispose() { }
    /// <inheritdoc/>
    public nint Handle() => m_cameras.Handle(
        seat: Seat,
        sensor: Sensor
    );
    /// <inheritdoc/>
    /// <remarks>The seat's cameras recover their own device objects.</remarks>
    public void NotifyDeviceLost() { }
    /// <inheritdoc/>
    /// <remarks>The seat's cameras publish every sensor once per frame, whatever reads it.</remarks>
    public void Publish(in FrameContext context) { }
}
