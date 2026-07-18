using Puck.Demo.Forge;
using Puck.Demo.Ui;
using Puck.SdfVm.Debug;

namespace Puck.Demo.Overworld;

/// <summary>
/// The per-frame publisher feeding the overlay panels' hub/tracker/gallery stores from state that already exists
/// elsewhere — composed by <see cref="OverlayComposition.WrapPanels"/> and ticked once per produced frame, BECAUSE the
/// owners cannot publish themselves:
/// <list type="bullet">
/// <item>The HUB state lives on <see cref="OverworldFrameSource"/> as bare primitives (<c>HubActive</c>/
/// <c>HubSelection</c>) — that source sits at its exact CA1506 coupling ceiling and may name no store type, so this
/// feed reads the primitives it already exposes. The mode labels come from the ceiling-safe
/// <see cref="ForgeCommands"/> forwarders (never <c>AuthoringModeRegistry</c> directly on a ceiling type).</item>
/// <item>The TRACKER has zero render coupling by design (the console is its v1 display) — this feed polls the same
/// lazily-built singleton the console verbs drive (<see cref="ForgeCommands.TrackerModeInstance"/>).</item>
/// <item>The GALLERY prints stdout plaques today — this feed publishes the same exhibit strings through the
/// already-composed <c>OverworldFrameSource.SdfDebug.Gallery</c> facade.</item>
/// </list>
/// The toast store is deliberately NOT here: it is event-driven, published at the verb-result seam in
/// <c>DemoHost</c>. Presentation only — nothing here touches simulation state.
/// </summary>
internal sealed class OverlayPanelsFeed {
    private readonly OverworldFrameSource m_frameSource;
    private readonly GalleryPanelStore m_gallery;
    private readonly HubPanelStore m_hub;
    // The mode-label table is static registry data — snapshot it once (a new mode is a code change, not live state).
    private readonly string[] m_hubLabels;
    private readonly IServiceProvider m_services;
    private readonly TrackerPanelStore m_tracker;

    /// <summary>Initializes a new instance of the <see cref="OverlayPanelsFeed"/> class.</summary>
    /// <param name="frameSource">The live overworld frame source (hub primitives + the SDF-debug facade).</param>
    /// <param name="services">The application services (resolves the tracker's lazily-built singleton).</param>
    /// <param name="hub">The hub store to publish.</param>
    /// <param name="tracker">The tracker store to publish.</param>
    /// <param name="gallery">The gallery store to publish.</param>
    public OverlayPanelsFeed(OverworldFrameSource frameSource, IServiceProvider services, HubPanelStore hub, TrackerPanelStore tracker, GalleryPanelStore gallery) {
        ArgumentNullException.ThrowIfNull(frameSource);
        ArgumentNullException.ThrowIfNull(gallery);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tracker);

        m_frameSource = frameSource;
        m_gallery = gallery;
        m_hub = hub;
        m_services = services;
        m_tracker = tracker;
        m_hubLabels = new string[ForgeCommands.AuthoringModeCount];

        for (var index = 0; (index < m_hubLabels.Length); index++) {
            m_hubLabels[index] = ForgeCommands.AuthoringModeLabel(index: index);
        }
    }

    /// <summary>Publishes all three polled surfaces — call once per produced frame, before the node snapshots.</summary>
    public void Tick() {
        m_hub.Publish(active: m_frameSource.HubActive, labels: m_hubLabels, selection: m_frameSource.HubSelection);

        var trackerScene = ForgeCommands.TrackerModeInstance(services: m_services).Scene;

        m_tracker.Publish(
            active: trackerScene.Active,
            name: (trackerScene.Document.Name ?? ""),
            pattern: trackerScene.PatternIndex,
            patternCount: (trackerScene.Document.Patterns?.Count ?? 0),
            playing: trackerScene.Playing,
            row: trackerScene.RowIndex,
            rowCount: trackerScene.RowCount,
            tempo: (trackerScene.Document.Tempo ?? AudioDocument.DefaultTempo)
        );

        var debug = m_frameSource.SdfDebug;
        var galleryActive = (debug.Active && debug.Gallery.Active);

        m_gallery.Publish(
            active: galleryActive,
            meta: (galleryActive ? $"exhibit {debug.Gallery.Index}/{(SdfGalleryScene.Count - 1)} - {debug.Gallery.CurrentName}" : ""),
            title: debug.Gallery.CurrentTitle
        );
    }
}
