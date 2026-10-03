using System.Diagnostics;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>
/// A session view's frame source, on its own clock: the destination is independently scheduled, so the view hands its
/// composition the interval between its own frames rather than the host's frame delta, and no interpolation fraction.
/// Wall-clock and presentation-only: the away-seat framing it paces is not reproducible run to run. Before each capture
/// it has the world capture its own frame (the <c>captureHostFirst</c> action), so a window session fits against the
/// seat camera of the frame it renders in (<see cref="WorldWindowFrustumFit.FitFrom(Func{System.Numerics.Vector3?}, Func{WorldDefinition?}, Func{WorldDefinition}, Func{WorldScreen?})"/>), whatever order the frame's
/// residencies prepare in.
/// </summary>
/// <param name="inner">The session's composition, whose dresser is its <see cref="WorldSessionSceneEmitter"/>.</param>
/// <param name="captureHostFirst">Captures the world's frame for the frame being prepared when it has not been captured
/// yet; a no-op otherwise.</param>
/// <param name="resolution">The session's authored pixel extent, including the caller's resolved default, or null to
/// use the requested capture extent. It fixes the camera aspect from the first capture, independently of a residency's
/// largest previously requested extent.</param>
/// <param name="resolveResolution">Dresses the session view's saved quality and live pin, or null for unchanged views.</param>
public sealed class WorldSessionFrameSource(SdfCompositionFrameSource inner, Action captureHostFirst, WorldScreenResolution? resolution = null,
    Func<SdfViewSnapshot, uint, uint, SdfViewSnapshot>? resolveResolution = null) : ISdfFrameSource {
    private readonly List<SdfViewSnapshot> m_views = [];

    private bool m_hasProduced;
    private long m_lastProduceTimestamp;

    /// <inheritdoc/>
    public SdfGlyphAtlas? GlyphAtlas => ((ISdfFrameSource)inner).GlyphAtlas;
    /// <inheritdoc/>
    public IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => ((ISdfFrameSource)inner).ScreenDecals;
    /// <inheritdoc/>
    public IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? ScreenSurfaceTransforms => ((ISdfFrameSource)inner).ScreenSurfaceTransforms;

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        captureHostFirst();

        var timestamp = Stopwatch.GetTimestamp();
        var ownDelta = (m_hasProduced
            ? ((float)Stopwatch.GetElapsedTime(
                endingTimestamp: timestamp,
                startingTimestamp: m_lastProduceTimestamp
            ).TotalSeconds)
            : 0f);

        m_lastProduceTimestamp = timestamp;
        m_hasProduced = true;

        var frame = inner.CaptureFrame(
            deltaSeconds: ownDelta,
            height: ((uint)(resolution?.Height ?? ((int)height))),
            interpolationAlpha: 0f,
            width: ((uint)(resolution?.Width ?? ((int)width)))
        );

        if (resolveResolution is null) {
            return frame;
        }
        m_views.Clear();
        for (var index = 0; (index < frame.Views.Count); index++) {
            m_views.Add(item: resolveResolution(frame.Views[index], ((uint)(resolution?.Width ?? ((int)width))), ((uint)(resolution?.Height ?? ((int)height)))));
        }
        return frame with { Views = m_views };
    }
    /// <inheritdoc/>
    /// <remarks>The time a device loss takes to recover must not land as one giant smoothing delta on the next
    /// frame.</remarks>
    public void NotifyDeviceLost() {
        ((ISdfFrameSource)inner).NotifyDeviceLost();
        m_hasProduced = false;
    }
}
