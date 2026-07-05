using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.Platform;

namespace Puck.Demo.Camera;

/// <summary>
/// The host side of the camera peripheral: an <see cref="ICameraSensor"/> backed by the PC's physical webcam. It opens
/// the platform camera service's CPU-pixel tier lazily, and on each capture the emulated Pocket Camera triggers, it box-
/// downscales the newest B8G8R8A8 webcam frame to the M64282FP's <c>128</c>×<c>112</c> grayscale plane. The webcam frame
/// is the ONE non-deterministic input in the whole chain — latched here at the capture instant, exactly like a joypad
/// read — so nothing downstream (the sensor processing, the deposited tiles) is anything but pure integer arithmetic.
/// When no camera is present (an unsupported platform, no device, or a mid-stream unplug) it fills a flat mid-grey field
/// so the viewfinder degrades gracefully rather than failing.
/// <para>
/// A physical webcam is a SINGLE device: opening it once per camera cabinet gives each its own capture session, and two
/// sessions on one device starve each other (each gets only alternate frames) — the "alternating flicker" when more than
/// one cabinet runs the camera cart. So all camera sensors SHARE one reference-counted session, pulling the same
/// latest-frame-wins frame under a lock; the last sensor to dispose closes the device.
/// </para>
/// </summary>
internal sealed class WebcamCameraSensor : ICameraSensor, IDisposable {
    private const int RequestedWidth = 320;
    private const int RequestedHeight = 240;
    private const byte FallbackLevel = 0x80;

    // Rec. 601 luma weights (×256) for the B, G, R channels of a B8G8R8A8 pixel.
    private const int BlueWeight = 29;
    private const int GreenWeight = 150;
    private const int RedWeight = 77;

    // The ONE shared physical-webcam session and its guard, reference-counted across every camera cabinet.
    private static readonly object s_gate = new();
    private static ICameraCaptureService? s_service;
    private static ICameraCaptureSession? s_session;
    private static bool s_opened;
    private static bool s_unavailable;
    private static int s_refCount;

    private bool m_disposed;

    /// <summary>Creates a sensor over the shared webcam session (the device is opened lazily on the first capture).</summary>
    /// <param name="service">The platform camera-capture service (Media Foundation on Windows, else the null service).</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public WebcamCameraSensor(ICameraCaptureService service) {
        ArgumentNullException.ThrowIfNull(argument: service);

        lock (s_gate) {
            s_service ??= service;
            s_refCount++;
        }
    }

    /// <inheritdoc/>
    public void Read(Span<byte> destination) {
        var plane = destination[..SensorImage.PixelCount];

        lock (s_gate) {
            EnsureSession();

            if ((s_session is { IsEnded: false } session) && session.TryCapture(surface: out var surface) && surface.IsCpuPixels) {
                // Downscale under the lock: the shared session's single-threaded pull contract must hold even though
                // several cabinets' step threads reach here. It is a cheap 128×112 box-average, so the serialization is
                // negligible, and every cabinet sees the SAME latest frame — no starvation, no flicker.
                Downscale(surface: surface, destination: plane);

                return;
            }

            // A session that has ended (device unplugged) is dropped so the next capture re-opens the device.
            if (s_session is { IsEnded: true }) {
                s_session.Dispose();
                s_session = null;
                s_opened = false;
            }
        }

        plane.Fill(value: FallbackLevel);
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (s_gate) {
            if (m_disposed) {
                return;
            }

            m_disposed = true;

            // The last camera cabinet to go closes the device (and clears the sticky state so a future camera cabinet
            // re-opens cleanly).
            if (--s_refCount <= 0) {
                s_refCount = 0;
                s_session?.Dispose();
                s_session = null;
                s_opened = false;
                s_unavailable = false;
            }
        }
    }

    // Opens the shared session once (under s_gate). Sticky-unavailable so a device-less run doesn't hammer the service.
    private static void EnsureSession() {
        if (s_opened || s_unavailable) {
            return;
        }

        s_opened = true;

        if (s_service is not { IsSupported: true }) {
            s_unavailable = true;

            return;
        }

        if (s_service.TryOpenDefault(requestedWidth: RequestedWidth, requestedHeight: RequestedHeight, session: out var session)) {
            s_session = session;
        }
        else {
            s_unavailable = true;
        }
    }

    // Box-averages the webcam frame (B8G8R8A8, tightly packed) down to the 128×112 grayscale sensor plane. Each output
    // photosite is the mean luma of the source block it maps to.
    private static void Downscale(Puck.Abstractions.Presentation.Surface surface, Span<byte> destination) {
        var sourceWidth = (int)surface.Width;
        var sourceHeight = (int)surface.Height;
        var pixels = surface.Pixels.Span;

        if ((sourceWidth <= 0) || (sourceHeight <= 0) || (pixels.Length < (sourceWidth * sourceHeight * 4))) {
            destination.Fill(value: FallbackLevel);

            return;
        }

        for (var destinationY = 0; (destinationY < SensorImage.Height); ++destinationY) {
            var sourceY0 = ((destinationY * sourceHeight) / SensorImage.Height);
            var sourceY1 = (((destinationY + 1) * sourceHeight) / SensorImage.Height);

            if (sourceY1 <= sourceY0) {
                sourceY1 = (sourceY0 + 1);
            }

            for (var destinationX = 0; (destinationX < SensorImage.Width); ++destinationX) {
                var sourceX0 = ((destinationX * sourceWidth) / SensorImage.Width);
                var sourceX1 = (((destinationX + 1) * sourceWidth) / SensorImage.Width);

                if (sourceX1 <= sourceX0) {
                    sourceX1 = (sourceX0 + 1);
                }

                long sum = 0;
                var samples = 0;

                for (var sourceY = sourceY0; (sourceY < sourceY1); ++sourceY) {
                    var rowBase = (sourceY * sourceWidth * 4);

                    for (var sourceX = sourceX0; (sourceX < sourceX1); ++sourceX) {
                        var offset = (rowBase + (sourceX * 4));

                        sum += (((pixels[offset] * BlueWeight) + (pixels[offset + 1] * GreenWeight) + (pixels[offset + 2] * RedWeight)) >> 8);
                        ++samples;
                    }
                }

                destination[(destinationY * SensorImage.Width) + destinationX] = (byte)(sum / samples);
            }
        }
    }
}
