using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.SdfVm;

/// <summary>The inputs whose changes discard a view instance's accumulated history without reallocating it.</summary>
/// <param name="Binding">The resolved view's binding revision, including a follow in place.</param>
/// <param name="Cut">The camera's cut revision.</param>
/// <param name="Width">The output width.</param>
/// <param name="Height">The output height.</param>
/// <param name="Ceiling">The render-scale ceiling.</param>
/// <param name="Enabled">Whether temporal sampling is enabled: the view resolves temporally or a capture converges.</param>
/// <param name="Debug">The debug view mode.</param>
/// <param name="Temporal">Whether the view resolves temporally (<c>SdfWorldPackage.TemporalFragment</c>), whose
/// output converges over one period and then stands.</param>
/// <param name="Unread">The frames the instance's render graph has left it unread and absent from displayed outputs,
/// including held consumer outputs: a view shown again after it was parked renders in a new epoch.</param>
public readonly record struct SdfTemporalEpoch(long Binding, long Cut, uint Width, uint Height, float Ceiling, bool Enabled, int Debug, bool Temporal = false, long Unread = 0) {
    /// <summary>Gets the exact indirect allocation, geometry epoch and completed lighting publication. A changed
    /// lane rejects accumulated color even when its old value lies inside the new frame's color bounds.</summary>
    public SdfIndirectHistory Indirect { get; init; }
}
/// <summary>A rendered camera and its sample grid, retained for motion reconstruction.</summary>
/// <param name="Camera">The camera whose basis and off-axis lens projected the sample.</param>
/// <param name="Jitter">The ray offset in render pixels.</param>
/// <param name="Width">The render width.</param>
/// <param name="Height">The render height.</param>
public readonly record struct SdfReprojectionView(CameraSnapshot Camera, Vector2 Jitter, uint Width, uint Height);
/// <summary>A view instance's accumulation epoch and eight-sample Halton ray sequence. This state is presentation only.
/// <para>
/// History continues from one render of the instance to the next while its epoch holds and the residency's previous
/// transform tables hold the poses the instance last rendered (<see cref="SdfWorldTables.PoseRevision"/>): frames the
/// instance stood through, or that other views of the residency rendered without moving a pose, break nothing.
/// </para>
/// </summary>
public sealed class SdfTemporalHistory {
    private SdfTemporalEpoch m_epoch;
    private bool m_prepared;
    // The pose revisions the latest prepared render reads: its current tables' and its previous tables'. A prepared
    // render whose submission fails commits nothing, so history continues only from the current poses of the latest
    // render that completed (Rendered), whatever the tables uploaded for the failed one.
    private long m_poses;
    private long m_previousPoses;
    private long m_renderedPoses;
    // Whether a counted sample source drives the index, and that source's count at the epoch's first sample.
    private bool m_counted;
    private bool m_rebase = true;
    private int m_countBase;
    private SdfReprojectionView m_currentView;
    // The temporal inputs the latest completed render fed its passes, which its output stands for.
    private bool m_rendered;
    private Inputs m_standing;
    // The renders since the inputs the instance's view is rendered from last changed (Changed).
    private uint m_settled;

    /// <summary>Gets the camera and sample grid of this instance's preceding completed render.</summary>
    public SdfReprojectionView PreviousView { get; private set; }
    /// <summary>Gets whether the preceding render belongs to this epoch, independently of whether jitter is enabled.</summary>
    public bool HasPreviousView { get; private set; }

    /// <summary>The number of samples in one convergence period.</summary>
    public const uint Period = 8;

    /// <summary>Gets the epoch the latest render was prepared in.</summary>
    public SdfTemporalEpoch Epoch => m_epoch;
    /// <summary>Gets the number of preceding samples in the current epoch.</summary>
    public uint Frames { get; private set; }
    /// <summary>Gets the current offset in render pixels, positive Y down; the first sample is the pixel center.</summary>
    public Vector2 Jitter => JitterAt(epoch: m_epoch, frames: Frames);

    /// <summary>Returns a centered Halton (2, 3) sample, with the first position replaced by the pixel center.</summary>
    /// <param name="index">The accumulated sample index, wrapped to the period.</param>
    /// <returns>The render-pixel ray offset.</returns>
    public static Vector2 Sample(uint index) {
        index %= Period;
        return ((index == 0) ? Vector2.Zero : new Vector2(x: (RadicalInverse(index: index, radix: 2) - 0.5f), y: (RadicalInverse(index: index, radix: 3) - 0.5f)));
    }
    /// <summary>Returns whether a pass in an epoch reads the previous view and the previous transform tables: the
    /// <c>motion</c> debug view does.</summary>
    /// <param name="epoch">The epoch.</param>
    /// <returns><see langword="true"/> when the previous view and poses feed the pass's pixels.</returns>
    public static bool ReadsMotion(SdfTemporalEpoch epoch) => (epoch.Debug == DebugViewModes.Motion);
    /// <summary>Prepares a render, resetting when its epoch changes or the residency's previous transform tables do not
    /// hold the poses of this instance's preceding render. A sample grid other than the preceding render's restarts the
    /// settling period (<see cref="Changed"/>), whether a dip, a recovery or a replacement graph moved it.</summary>
    /// <param name="epoch">The current reset inputs.</param>
    /// <param name="camera">The camera being rendered.</param>
    /// <param name="previousPoses">The pose revision the residency's previous transform tables hold for this render
    /// (<see cref="SdfWorldTables.PreviousPoseRevision"/>).</param>
    /// <param name="currentPoses">The pose revision its current tables hold (<see cref="SdfWorldTables.PoseRevision"/>).</param>
    /// <param name="renderWidth">The current sample-grid width, or zero for the output width.</param>
    /// <param name="renderHeight">The current sample-grid height, or zero for the output height.</param>
    /// <param name="counted">The samples a converging capture has counted (<c>RenderGraphConvergence.Samples</c>),
    /// which then drive the index from the epoch's first render, or <see langword="null"/> when each completed render
    /// advances it.</param>
    public void Prepare(SdfTemporalEpoch epoch, CameraSnapshot camera, long previousPoses, long currentPoses, uint renderWidth = 0, uint renderHeight = 0, int? counted = null) {
        var width = ((renderWidth == 0) ? epoch.Width : renderWidth);
        var height = ((renderHeight == 0) ? epoch.Height : renderHeight);

        if ((m_currentView.Width != width) || (m_currentView.Height != height)) {
            Changed();
        }
        if (!Continues(epoch: epoch, previousPoses: previousPoses)) {
            Reset();
        }
        m_epoch = epoch;
        m_prepared = true;
        m_poses = currentPoses;
        m_previousPoses = previousPoses;
        m_counted = counted.HasValue;
        if (counted is { } samples) {
            if (m_rebase) {
                m_countBase = samples;
                m_rebase = false;
            }
            Frames = (Sampling(epoch: epoch) ? ((uint)(samples - m_countBase)) : 0U);
        }
        m_currentView = new SdfReprojectionView(Camera: camera, Jitter: Jitter, Width: width, Height: height);
    }
    /// <summary>Discards history while retaining its storage: the next render starts the epoch at the pixel center with
    /// no previous view.</summary>
    public void Reset() {
        Frames = 0;
        HasPreviousView = false;
        m_prepared = false;
        m_rebase = true;
    }
    /// <summary>Records that the view's rendered inputs changed this frame (a moved pose, camera or frame value): a
    /// temporally resolved view renders one more period of samples before it stands again.</summary>
    public void Changed() =>
        m_settled = 0;
    /// <summary>Accounts for one rendered frame.</summary>
    public void Rendered() {
        if (m_settled < uint.MaxValue) {
            m_settled++;
        }
        m_standing = new Inputs(
            HasPreviousView: HasPreviousView,
            Jitter: Jitter,
            PreviousPoses: m_previousPoses,
            PreviousView: PreviousView
        );
        m_rendered = true;
        m_renderedPoses = m_poses;
        PreviousView = m_currentView;
        HasPreviousView = true;
        if (!m_counted && Sampling(epoch: m_epoch) && (Frames < uint.MaxValue)) {
            Frames++;
        }
    }
    /// <summary>Returns whether a render taken now would feed the instance's passes the temporal inputs its latest
    /// completed render fed them, so that render's output may stand: the same jitter, and, where the pass reads motion
    /// (<see cref="ReadsMotion"/>), the same previous view and previous poses. A temporally resolved view instead stands
    /// once its history holds one <see cref="Period"/> of samples and it has rendered a period since its inputs last
    /// changed (<see cref="Changed"/>): its converged output stands, whatever jitter would come next.</summary>
    /// <param name="epoch">The epoch a render taken now would be prepared in.</param>
    /// <param name="previousPoses">The pose revision the residency's previous transform tables would hold for that
    /// render: its current tables' revision before the frame's upload.</param>
    /// <returns><see langword="true"/> when the latest render's temporal inputs are the ones a render now would use.</returns>
    public bool Stands(SdfTemporalEpoch epoch, long previousPoses) {
        if (!m_rendered) {
            return false;
        }

        var continues = Continues(epoch: epoch, previousPoses: previousPoses);

        if (epoch.Temporal && Sampling(epoch: epoch)) {
            return (continues && (Frames >= Period) && (m_settled >= Period));
        }
        if (JitterAt(epoch: epoch, frames: (continues ? Frames : 0U)) != m_standing.Jitter) {
            return false;
        }

        return (
            !ReadsMotion(epoch: epoch) ||
            (
                (m_standing.HasPreviousView == continues) &&
                (
                    !continues ||
                    (
                        (m_standing.PreviousView == PreviousView) &&
                        (m_standing.PreviousPoses == previousPoses)
                    )
                )
            )
        );
    }

    private bool Continues(SdfTemporalEpoch epoch, long previousPoses) => (
        m_prepared &&
        (epoch == m_epoch) &&
        (previousPoses == m_renderedPoses)
    );
    private static bool Sampling(SdfTemporalEpoch epoch) => (epoch.Enabled && (epoch.Debug == 0));
    private static Vector2 JitterAt(SdfTemporalEpoch epoch, uint frames) => (Sampling(epoch: epoch) ? Sample(index: frames) : Vector2.Zero);
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

    private readonly record struct Inputs(Vector2 Jitter, bool HasPreviousView, SdfReprojectionView PreviousView, long PreviousPoses);
}
