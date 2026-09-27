using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>
/// The scene of a world seats are presented in elsewhere (<see cref="WorldContinuum.PresentedElsewhere"/>): the routed
/// authority's own static scene, stamp pool and population, drawn from its endpoint's delivered definition and state
/// mirror by a <see cref="WorldSessionSceneEmitter"/> over <see cref="WorldAuthorityEndpoint.Mirror"/>, the same
/// emitter a session screen draws a destination with. The frame differs from a session screen's in what frames it and
/// how it is lit: every seat presented in this world has a view, framed by the camera <see cref="WorldFramePresenter"/>
/// resolved for that seat this frame, rendered with the presentation's own quality levers and under the destination's
/// own sky and lighting (its <c>render.cycle</c> or static lanes). Seats presented in the same world share one
/// scene, so they share one program with many cameras, as the boot world's seats do.
/// </summary>
public sealed class WorldRoutedScene : ISdfFrameDresser {
    private readonly WorldRenderCycleTrack m_cycle = new();

    private readonly WorldSessionSceneEmitter m_emitter;
    private readonly Func<SdfFrame?> m_hostFrame;

    private readonly List<SdfViewSnapshot> m_views = [];
    // The views the last dressed frame rendered, kept for a frame whose seats all left in the frame between the
    // presenter's latch and this dress: a frame always carries a view.
    private readonly List<SdfViewSnapshot> m_dressedViews = [];

    /// <summary>Initializes the scene of one routed endpoint.</summary>
    /// <param name="endpoint">The routed authority the scene draws.</param>
    /// <param name="hostFrame">The frame the boot presentation dressed this frame, whose quality levers, clock and sky
    /// clock the scene's frame takes; <see langword="null"/> before the first.</param>
    /// <param name="bodyColor">The color each avatar is painted with by body index: a local seat keeps the color the
    /// boot presentation paints it with.</param>
    public WorldRoutedScene(WorldAuthorityEndpoint endpoint, Func<SdfFrame?> hostFrame, Func<int, Vector3> bodyColor) {
        ArgumentNullException.ThrowIfNull(argument: endpoint);
        ArgumentNullException.ThrowIfNull(argument: hostFrame);
        ArgumentNullException.ThrowIfNull(argument: bodyColor);

        Endpoint = endpoint;
        m_hostFrame = hostFrame;
        m_emitter = new WorldSessionSceneEmitter(
            bodyColor: bodyColor,
            castsAvatarShadows: true,
            effectiveCameraName: null,
            mirror: endpoint.Mirror
        );
        FrameSource = new SdfCompositionFrameSource(
            dresser: this,
            emitters: [m_emitter]
        );
    }

    /// <summary>Gets the routed authority the scene draws.</summary>
    public WorldAuthorityEndpoint Endpoint { get; }
    /// <summary>Gets the frame source a residency renders the scene through.</summary>
    public SdfCompositionFrameSource FrameSource { get; }
    /// <summary>Gets how many seat views the presenter latched into the scene this frame.</summary>
    public int ViewCount => m_views.Count;

    /// <summary>Clears the views the presenter latched, before it latches this frame's.</summary>
    public void BeginViews() => m_views.Clear();
    /// <summary>Latches a seat's view of this world for the frame.</summary>
    /// <param name="view">The seat's view, framed in this world's own coordinates.</param>
    /// <returns>The view's index in the scene's frames.</returns>
    public int AddView(in SdfViewSnapshot view) {
        m_views.Add(item: view);

        return (m_views.Count - 1);
    }
    /// <inheritdoc/>
    /// <remarks>The session emitter dresses first, so its stamp pool advances and its dressed definition is retained
    /// exactly as a session screen's; the frame then takes the seats' views, the host frame's levers and clock, and this
    /// world's own environment.</remarks>
    public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        var frame = m_emitter.Dress(
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

        if (m_views.Count != 0) {
            m_dressedViews.Clear();
            m_dressedViews.AddRange(collection: m_views);
        } else if (m_dressedViews.Count == 0) {
            m_dressedViews.AddRange(collection: frame.Views);
        }

        var mirror = Endpoint.Mirror;
        var environment = m_cycle.Resolve(
            definition: mirror.Definition,
            mirror: Endpoint.FollowState(),
            revision: mirror.DefinitionRevision
        );

        if (m_hostFrame() is not { } host) {
            return frame with {
                Environment = environment,
                Views = m_dressedViews,
            };
        }

        return frame with {
            DisableAmbientOcclusion = host.DisableAmbientOcclusion,
            DisableFarBound = host.DisableFarBound,
            DisableSoftShadows = host.DisableSoftShadows,
            EnableCadenceGate = host.EnableCadenceGate,
            Environment = environment,
            SampleIndex = host.SampleIndex,
            ShadowDistanceScale = host.ShadowDistanceScale,
            Time = host.Time,
            UseCameraTileShadowMask = host.UseCameraTileShadowMask,
            UseFastAmbientOcclusion = host.UseFastAmbientOcclusion,
            UseFastSoftShadowMarch = host.UseFastSoftShadowMarch,
            Views = m_dressedViews,
        };
    }
}
