using System.Numerics;

namespace Puck.SdfVm.Views;

/// <summary>
/// A screen source that samples SOMEONE ELSE'S already-produced image handle — the shape of a hosted guest's raw
/// framebuffer (a booted cabinet's emulated machine), but ZERO emulator references: this type knows nothing beyond
/// "call this delegate, get a handle." A CPU-drawn presentation feed (a procedural face, a console-mirror CRT) fits
/// the same shape (see <see cref="ViewStack"/>'s remarks on <see cref="IViewContent.IsBudgeted"/>) and may register
/// as one too, exactly as legitimately as a genuine hosted guest.
/// <para>
/// <see cref="IViewContent.IsBudgeted"/> is <see langword="false"/>: <see cref="Resolve"/> is a cheap delegate call,
/// never a render pass — the producer is assumed to already be kept current by whatever owns it (a guest's own frame
/// pump, a CPU feed's own <c>Tick</c>), so gating it behind <see cref="ViewStack"/>'s round-robin budget would only
/// ever serve a stale handle on the frames it is skipped.
/// </para>
/// </summary>
public sealed class GuestSurfaceView : IViewContent {
    private readonly Func<nint> m_source;

    /// <summary>Wraps a handle producer as view content.</summary>
    /// <param name="source">Produces this view's current image-view handle (0 = no signal), called fresh every
    /// resolve — never cached inside this type.</param>
    /// <param name="roomGlow">This content's fixed room-light contribution (see <see cref="RoomGlow"/>); default zero.</param>
    public GuestSurfaceView(Func<nint> source, Vector3 roomGlow = default) {
        ArgumentNullException.ThrowIfNull(source);

        m_source = source;
        RoomGlow = roomGlow;
    }

    /// <inheritdoc/>
    public Vector3 RoomGlow { get; set; }

    /// <inheritdoc/>
    public bool IsBudgeted => false;

    /// <inheritdoc/>
    public nint Resolve(in ViewRenderContext context) => m_source();
}
