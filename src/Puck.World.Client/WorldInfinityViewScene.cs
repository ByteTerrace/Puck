using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The scene one infinity view renders: the destination world a <see cref="InfinityViewKind.World"/> view shows, drawn by
/// a <see cref="WorldSessionSceneEmitter"/> over its endpoint's mirror as a session screen draws it, or the named
/// prototypes of the viewer's own world (<see cref="InfinityViewKind.Far"/>), drawn by an emitter that holds only those. The
/// frame the emitter dresses is rewritten to the layer's consuming cameras: each takes the fit the presentation
/// supplied this frame, at the layer's anchor, turned with that viewer, at a quality that leaves
/// soft shadows and ambient occlusion off unless the view's levers turn them on, retains each consuming camera's
/// indirect comparison method, and its far distance is the view's own.
/// A frame the view is not visible in keeps the emitter's own view, which the graph does not render.
/// </summary>
public sealed class WorldInfinityViewScene : ISdfFrameDresser {
    private readonly ISdfFrameDresser m_inner;
    private readonly Func<IReadOnlyList<SdfViewSnapshot>> m_views;
    private readonly InfinityViewSpec m_spec;
    private readonly List<SdfViewSnapshot> m_fittedViews = [];

    /// <summary>Initializes the scene of one view.</summary>
    /// <param name="inner">The emitter that dresses the world or the prototypes.</param>
    /// <param name="views">Answers the consuming cameras the presentation fitted this frame. They share one scene
    /// and its residency; an empty list keeps the emitter's undemanded frame.</param>
    /// <param name="spec">The view.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldInfinityViewScene(ISdfFrameDresser inner, Func<IReadOnlyList<SdfViewSnapshot>> views, InfinityViewSpec spec) {
        ArgumentNullException.ThrowIfNull(argument: views);
        ArgumentNullException.ThrowIfNull(argument: inner);
        ArgumentNullException.ThrowIfNull(argument: spec);

        m_views = views;
        m_inner = inner;
        m_spec = spec;
    }

    /// <summary>Returns the quality a view renders at: the reduced panel quality of a session screen, with the soft shadows
    /// and the ambient occlusion its levers turn on restored.</summary>
    /// <param name="levers">The view's levers.</param>
    /// <returns>The quality.</returns>
    public static SdfViewQuality QualityOf(InfinityViewLevers levers) => new() {
        DisableAmbientOcclusion = !levers.HasFlag(flag: InfinityViewLevers.AmbientOcclusion),
        DisableFarBound = true,
        DisableSoftShadows = !levers.HasFlag(flag: InfinityViewLevers.Shadows),
    };

    /// <inheritdoc/>
    public SdfGlyphAtlas? GlyphAtlas => m_inner.GlyphAtlas;
    /// <inheritdoc/>
    public IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => m_inner.ScreenDecals;

    /// <summary>Gets or sets what films the shown world's camera screens and fits their sky after its consuming
    /// cameras have replaced the emitter's default view. The resolved sky and frame dimensions belong to this world.</summary>
    public Action<List<SdfViewSnapshot>, SdfSky, uint, uint>? FitSkyViews { get; set; }

    /// <inheritdoc/>
    public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        var dressed = m_inner.Dress(
            deltaSeconds: deltaSeconds,
            height: height,
            interpolationAlpha: interpolationAlpha,
            meshDraws: meshDraws,
            meshDrawsRevision: meshDrawsRevision,
            moved: moved,
            program: program,
            transforms: transforms,
            width: width
        );
        var views = m_views();

        if (views.Count == 0) {
            return dressed;
        }

        m_fittedViews.Clear();
        foreach (var view in views) {
            m_fittedViews.Add(item: view with { Quality = QualityOf(levers: m_spec.Levers) with { IndirectMethod = view.Quality.IndirectMethod } });
        }
        FitSkyViews?.Invoke(m_fittedViews, dressed.Sky, width, height);

        return dressed with {
            FarDistance = m_spec.FarDistance,
            Views = m_fittedViews,
        };
    }
}
