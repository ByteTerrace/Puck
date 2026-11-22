namespace Puck.SdfVm;

// The world's one shadow solution (SdfWorldShadows): created once the pass pipelines it draws with are built, drawn by the
// upload ahead of every view's submission, and read by every view through CascadesOf and the layers its pass sets bind.
public sealed partial class SdfWorldTables {
    private SdfWorldShadows? m_shadows;
    // The frame the tables last packed, which the upload draws the shadows of.
    private SdfFrame? m_shadowFrame;

    /// <summary>Gets the world's shadows, or <see langword="null"/> while a pipeline they draw with still builds.</summary>
    internal SdfWorldShadows? Shadows {
        get {
            if ((m_shadows is null) && !m_disposed && (FrameWaiting() is null)) {
                m_shadows = new SdfWorldShadows(gpu: m_gpu, tables: this);
            }
            return m_shadows;
        }
    }
    /// <summary>Describes the world's shadow entries for <c>world.residency</c>.</summary>
    /// <returns>The description, empty before the shadows exist.</returns>
    public string DescribeShadows() => (m_shadows?.Describe() ?? string.Empty);

    // Starts a frame of the residency: the shadows decide the next upload's work afresh.
    internal void BeginShadowFrame() => m_shadows?.BeginFrame();
    // An image, framebuffer or buffer the shadows replaced, retired behind every submission that may read it.
    internal void RetireShadowObject(IDisposable replaced) => Retire(replaced: replaced);
    // Records the shadows' work into the upload's command buffer, as its own counted pass.
    private void RecordShadows(nint commandBuffer, int slot) {
        if ((m_shadowFrame is not { } frame) || (Shadows is not { } shadows)) {
            m_work.SkipPass(pass: ShadowsPass);
            return;
        }
        m_work.EnterPass(pass: ShadowsPass);
        shadows.Record(commandBuffer: commandBuffer, frame: frame, slot: slot);
        m_work.LeavePass();
    }
}
