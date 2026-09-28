using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.SdfVm;

/// <summary>The inputs whose changes discard a view instance's accumulated history without reallocating it.</summary>
/// <param name="Binding">The resolved view's binding revision, including a follow in place.</param>
/// <param name="Cut">The camera's cut revision.</param>
/// <param name="Width">The output width.</param>
/// <param name="Height">The output height.</param>
/// <param name="Ceiling">The render-scale ceiling.</param>
/// <param name="Enabled">Whether temporal sampling is enabled.</param>
/// <param name="Debug">The debug view mode.</param>
public readonly record struct SdfTemporalEpoch(long Binding, long Cut, uint Width, uint Height, float Ceiling, bool Enabled, int Debug);
/// <summary>A rendered camera and its sample grid, retained for motion reconstruction.</summary>
/// <param name="Camera">The camera whose basis and off-axis lens projected the sample.</param>
/// <param name="Jitter">The ray offset in render pixels.</param>
/// <param name="Width">The render width.</param>
/// <param name="Height">The render height.</param>
public readonly record struct SdfReprojectionView(CameraSnapshot Camera, Vector2 Jitter, uint Width, uint Height);
/// <summary>A view instance's accumulation epoch and eight-sample Halton ray sequence. This state is presentation only.</summary>
public sealed class SdfTemporalHistory {
    private SdfTemporalEpoch m_epoch;
    private bool m_prepared;
    private long m_lastFrame = -1;
    private SdfReprojectionView m_currentView;

    /// <summary>Gets the camera and sample grid of this instance's preceding completed render.</summary>
    public SdfReprojectionView PreviousView { get; private set; }
    /// <summary>Gets whether the preceding render belongs to this epoch, independently of whether jitter is enabled.</summary>
    public bool HasPreviousView { get; private set; }

    /// <summary>The number of samples in one convergence period.</summary>
    public const uint Period = 8;

    /// <summary>Gets the number of preceding samples in the current epoch.</summary>
    public uint Frames { get; private set; }
    /// <summary>Gets the current offset in render pixels, positive Y down; the first sample is the pixel center.</summary>
    public Vector2 Jitter => ((m_epoch.Enabled && (m_epoch.Debug == 0)) ? Sample(index: Frames) : Vector2.Zero);

    /// <summary>Returns a centered Halton (2, 3) sample, with the first position replaced by the pixel center.</summary>
    /// <param name="index">The accumulated sample index, wrapped to the period.</param>
    /// <returns>The render-pixel ray offset.</returns>
    public static Vector2 Sample(uint index) {
        index %= Period;
        return ((index == 0) ? Vector2.Zero : new Vector2(x: (RadicalInverse(index: index, radix: 2) - 0.5f), y: (RadicalInverse(index: index, radix: 3) - 0.5f)));
    }
    /// <summary>Prepares a render, resetting when its epoch changes or its residency did not follow the preceding render.</summary>
    /// <param name="epoch">The current reset inputs.</param>
    /// <param name="camera">The camera being rendered.</param>
    /// <param name="frame">The residency's consumed-frame ordinal.</param>
    public void Prepare(SdfTemporalEpoch epoch, long frame, CameraSnapshot camera) {
        if (!m_prepared || (epoch != m_epoch) || (frame != (m_lastFrame + 1))) {
            Reset();
        }
        m_epoch = epoch;
        m_prepared = true;
        m_lastFrame = frame;
        m_currentView = new SdfReprojectionView(Camera: camera, Jitter: Jitter, Width: epoch.Width, Height: epoch.Height);
    }
    /// <summary>Discards history while retaining its storage.</summary>
    public void Reset() {
        Frames = 0;
        HasPreviousView = false;
    }
    /// <summary>Accounts for one rendered frame.</summary>
    public void Rendered() {
        PreviousView = m_currentView;
        HasPreviousView = true;
        if (m_epoch.Enabled && (m_epoch.Debug == 0) && (Frames < uint.MaxValue)) {
            Frames++;
        }
    }

    private static float RadicalInverse(uint index, uint radix) {
        var value = 0f;
        var fraction = (1f / radix);

        while (index != 0) {
            value += ((index % radix) * fraction);
            index /= radix;
            fraction /= radix;
        }
        return value;
    }
}
