using Puck.Abstractions.Gpu;

namespace Puck.Demo.Overworld;

/// <summary>The procedural face-feed's expressions — simple pixel art, drawn in code (no external art assets,
/// trademark-clean). Switched by <see cref="ProceduralFeed.SetExpression"/>; proximity/boot events choose WHEN to
/// switch — this type is presentation-only.</summary>
public enum FaceExpression {
    /// <summary>Two steady eyes, occasionally blinking, a flat mouth — the resting expression.</summary>
    Idle,
    /// <summary>Upward-curved eyes and a wide grinning mouth.</summary>
    Happy,
    /// <summary>One eye larger than the other (a cocked-head look) and a small round "o" mouth.</summary>
    Curious,
}

/// <summary>
/// A creation's procedural face feed: CPU-composed 160x144 (the native brick panel size) RGBA8 frames published
/// through <see cref="IGpuSurfaceUpload"/> — mirrors <c>BakePreviewService.Publish</c>'s upload pattern byte-for-byte
/// (map, blocking copy, one reusable image-view handle valid until the next upload). It is the DEFAULT face feed a
/// screen-faced creation shows (published to the host's named-feed registry under
/// <see cref="Creator.CompanionState.DefaultFaceFeed"/>): a small set of procedural expressions
/// (<see cref="FaceExpression"/>) drawn directly into the pixel buffer with simple filled shapes (circles, rounded
/// mouths) — no external art, nothing trademarked. Presentation-only: this type never touches simulation state, and
/// nothing here is gated by Post (the demo is greenfield).
/// <para>
/// <see cref="Tick"/> advances the idle blink timer and republishes only when the expression changed OR a blink
/// toggled the drawn frame — an unattended idle face still blinks periodically instead of staring, but a steady
/// expression does not re-upload every frame for nothing.
/// </para>
/// </summary>
public sealed class ProceduralFeed : IDisposable {
    /// <summary>The face feed's fixed width — the native DMG/CGB panel size, matching every other diegetic screen
    /// source in the overworld.</summary>
    public const int FaceWidth = 160;
    /// <summary>The face feed's fixed height.</summary>
    public const int FaceHeight = 144;

    private const int BytesPerPixel = 4;
    // Blink cadence: the idle face blinks roughly every 3 seconds, the blink itself lasting a few frames' worth of
    // wall time — frame-count-free (this is presentation, not simulation, so wall-clock timing is fine here; nothing
    // here feeds determinism/replay).
    private const float BlinkIntervalSeconds = 3.0f;
    private const float BlinkDurationSeconds = 0.12f;

    // A small, warm palette — a dark CRT-glass background, a soft phosphor-green face line, and a bright highlight,
    // deliberately generic (no third-party character's color scheme).
    private static readonly byte[] BackgroundColor = [0x08, 0x0c, 0x10, 0xff];
    private static readonly byte[] FaceColor = [0x6a, 0xf0, 0x9a, 0xff];
    private static readonly byte[] HighlightColor = [0xd8, 0xff, 0xe8, 0xff];
    private readonly byte[] m_pixels = new byte[((FaceWidth * FaceHeight) * BytesPerPixel)];
    private IGpuSurfaceUpload? m_upload;
    private FaceExpression m_expression = FaceExpression.Idle;
    private FaceExpression? m_lastPublishedExpression;
    private bool m_lastPublishedBlink;
    private float m_blinkClock;
    private bool m_blinking;

    /// <summary>The current image view handle (valid until the next <see cref="Tick"/> that republishes); 0 before
    /// the first successful publish.</summary>
    public nint CurrentImageViewHandle { get; private set; }

    /// <summary>Switches the displayed expression. Takes effect on the next <see cref="Tick"/> (a no-op if it is
    /// already the current expression and no blink is pending).</summary>
    /// <param name="expression">The expression to show.</param>
    public void SetExpression(FaceExpression expression) {
        m_expression = expression;
    }

    /// <summary>Advances the blink timer and republishes the face when the expression changed or a blink toggled,
    /// blocking only long enough to map/copy/unmap the small 160x144 buffer (matching
    /// <c>BakePreviewService.Publish</c>'s upload cost). Never throws — a resolution failure (no GPU device/services
    /// yet) simply skips this tick; the caller retries next frame.</summary>
    /// <param name="deltaSeconds">Seconds advanced since the previous tick.</param>
    /// <param name="device">The GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    public void Tick(float deltaSeconds, IGpuDeviceContext device, IGpuComputeServices gpu) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(gpu);

        AdvanceBlink(deltaSeconds: deltaSeconds);

        if ((m_expression == m_lastPublishedExpression) && (m_blinking == m_lastPublishedBlink)) {
            return;
        }

        DrawFace(expression: m_expression, blinking: (m_blinking && (m_expression == FaceExpression.Idle)));

        m_upload ??= gpu.SurfaceTransferFactory.CreateUpload(deviceContext: device);
        // The returned handle is only valid until the NEXT Upload on this object — re-stored on every publish,
        // exactly the contract IGpuSurfaceUpload documents (mirrors BakePreviewService.Publish).
        CurrentImageViewHandle = m_upload.Upload(
            deviceContext: device,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: FaceHeight,
            pixels: m_pixels,
            width: FaceWidth
        );
        m_lastPublishedExpression = m_expression;
        m_lastPublishedBlink = m_blinking;
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_upload?.Dispose();
        m_upload = null;
    }

    private void AdvanceBlink(float deltaSeconds) {
        m_blinkClock += deltaSeconds;

        if (m_blinking) {
            if (m_blinkClock >= BlinkDurationSeconds) {
                m_blinking = false;
                m_blinkClock = 0f;
            }

            return;
        }

        if (m_blinkClock >= BlinkIntervalSeconds) {
            m_blinking = true;
            m_blinkClock = 0f;
        }
    }

    // Draws directly into m_pixels: a dark background, two eyes (shape/position keyed by expression and blink), and a
    // mouth (keyed by expression) — plain filled circles/ellipses/rects, no external art.
    private void DrawFace(FaceExpression expression, bool blinking) {
        Fill(color: BackgroundColor);

        const int leftEyeX = ((FaceWidth * 3) / 8);
        const int rightEyeX = ((FaceWidth * 5) / 8);
        const int eyeY = ((FaceHeight * 2) / 5);

        switch (expression) {
            case FaceExpression.Happy: {
                    DrawUpwardArcEye(centerX: leftEyeX, centerY: eyeY);
                    DrawUpwardArcEye(centerX: rightEyeX, centerY: eyeY);
                    DrawGrinMouth();

                    break;
                }
            case FaceExpression.Curious: {
                    DrawRoundEye(centerX: leftEyeX, centerY: eyeY, radius: 10, blinking: false);
                    DrawRoundEye(centerX: rightEyeX, centerY: (eyeY - 4), radius: 14, blinking: false);
                    DrawRoundMouth();

                    break;
                }
            default: {
                    DrawRoundEye(centerX: leftEyeX, centerY: eyeY, radius: 12, blinking: blinking);
                    DrawRoundEye(centerX: rightEyeX, centerY: eyeY, radius: 12, blinking: blinking);
                    DrawFlatMouth();

                    break;
                }
        }
    }
    private void Fill(byte[] color) {
        for (var index = 0; (index < m_pixels.Length); index += BytesPerPixel) {
            m_pixels[(index + 0)] = color[0];
            m_pixels[(index + 1)] = color[1];
            m_pixels[(index + 2)] = color[2];
            m_pixels[(index + 3)] = color[3];
        }
    }
    // A round eye: a filled disc, or (while blinking) a thin horizontal line at its vertical center.
    private void DrawRoundEye(int centerX, int centerY, int radius, bool blinking) {
        if (blinking) {
            DrawFilledRect(left: (centerX - radius), top: (centerY - 1), right: (centerX + radius), bottom: (centerY + 1), color: FaceColor);

            return;
        }

        DrawFilledCircle(centerX: centerX, centerY: centerY, radius: radius, color: FaceColor);
        DrawFilledCircle(centerX: (centerX + (radius / 3)), centerY: (centerY - (radius / 3)), radius: (radius / 4), color: HighlightColor);
    }
    // A happy eye: an upward-curving arc (the top half of a ring), read as a closed, smiling eye.
    private void DrawUpwardArcEye(int centerX, int centerY) {
        const int radius = 12;
        const int thickness = 4;

        for (var x = -radius; (x <= radius); x++) {
            var normalized = (x / (float)radius);
            var archHeight = (int)(MathF.Sqrt(x: MathF.Max(x: 0f, y: (1f - (normalized * normalized)))) * radius);

            DrawFilledRect(left: (centerX + x), top: (centerY - archHeight), right: ((centerX + x) + 1), bottom: ((centerY - archHeight) + thickness), color: FaceColor);
        }
    }
    private void DrawFlatMouth() {
        const int halfWidth = 20;
        const int y = ((FaceHeight * 7) / 10);

        DrawFilledRect(left: ((FaceWidth / 2) - halfWidth), top: y, right: ((FaceWidth / 2) + halfWidth), bottom: (y + 3), color: FaceColor);
    }
    private void DrawGrinMouth() {
        const int halfWidth = 26;
        const int y = ((FaceHeight * 7) / 10);
        const int thickness = 5;

        for (var x = -halfWidth; (x <= halfWidth); x++) {
            var normalized = (x / (float)halfWidth);
            var dip = (int)(MathF.Sqrt(x: MathF.Max(x: 0f, y: (1f - (normalized * normalized)))) * 10f);

            DrawFilledRect(left: ((FaceWidth / 2) + x), top: (y + dip), right: (((FaceWidth / 2) + x) + 1), bottom: ((y + dip) + thickness), color: FaceColor);
        }
    }
    private void DrawRoundMouth() {
        DrawFilledCircle(centerX: (FaceWidth / 2), centerY: ((FaceHeight * 7) / 10), radius: 9, color: FaceColor);
        DrawFilledCircle(centerX: (FaceWidth / 2), centerY: ((FaceHeight * 7) / 10), radius: 5, color: BackgroundColor);
    }
    private void DrawFilledCircle(int centerX, int centerY, int radius, byte[] color) {
        var radiusSquared = (radius * radius);

        for (var y = -radius; (y <= radius); y++) {
            for (var x = -radius; (x <= radius); x++) {
                if (((x * x) + (y * y)) <= radiusSquared) {
                    SetPixel(x: (centerX + x), y: (centerY + y), color: color);
                }
            }
        }
    }
    private void DrawFilledRect(int left, int top, int right, int bottom, byte[] color) {
        for (var y = top; (y < bottom); y++) {
            for (var x = left; (x < right); x++) {
                SetPixel(x: x, y: y, color: color);
            }
        }
    }
    private void SetPixel(int x, int y, byte[] color) {
        if (
            (x < 0) ||
            (y < 0) ||
            (x >= FaceWidth) ||
            (y >= FaceHeight)
        ) {
            return;
        }

        var offset = (((y * FaceWidth) + x) * BytesPerPixel);

        m_pixels[(offset + 0)] = color[0];
        m_pixels[(offset + 1)] = color[1];
        m_pixels[(offset + 2)] = color[2];
        m_pixels[(offset + 3)] = color[3];
    }
}
