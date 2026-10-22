using Puck.Abstractions.Counting;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.World.Client;

// The world.rates levers the host's footprints carry (SdfUpdateRates.Views and Screens): a footprint showing a nested
// view or a screen's source asks for it less often as the pixels it covers on the display shrink, so the scheduler
// renders the view or uploads the source at 30 or 15 Hz (RenderGraphFootprint.Refresh). The pixels are the footprint's
// share of its consumer times the consumer's share of the display, which the previous frame's footprints measure.
public sealed partial class WorldViewGraphHost {
    // Each instance's height as a share of the display's, measured from the previous frame's footprints.
    private readonly Dictionary<string, double> m_displayShares = new(comparer: StringComparer.Ordinal);
    // The display's presented frames a second this frame, or zero when unknown.
    private int m_displayHertz;
    // The most frequent refresh this frame's views asked for each source they show on screens.
    private readonly Dictionary<string, RenderGraphRefresh> m_sourceRefresh = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets or sets the live render settings whose <c>world.rates</c> levers rate the nested views and screens
    /// the host's footprints demand (<see cref="WorldRenderSettings.UpdateRates"/>), or <see langword="null"/> to demand
    /// each of them every frame.</summary>
    public WorldRenderSettings? RenderSettings { get; set; }

    // Measures each instance's share of the display's height from the footprints the previous frame demanded: a root's
    // share is its own, and a producer's is the largest over its consumers of the consumer's share times the footprint's.
    private void MeasureDisplayShares() {
        m_displayShares.Clear();
        m_sourceRefresh.Clear();

        if ((RenderSettings is not { } settings) || ((settings.UpdateRates & (SdfUpdateRates.Views | SdfUpdateRates.Screens)) == 0)) {
            return;
        }

        m_displayShares[WorldViewGraphs.MainInstance] = 1d;
        foreach (var root in m_roots) {
            m_displayShares[root.Instance] = Math.Max(val1: root.Height, val2: m_displayShares.GetValueOrDefault(key: root.Instance));
        }

        // The footprints form a graph without cycles, so each pass settles at least one more level of nesting.
        for (var pass = 0; (pass <= m_footprints.Count); pass++) {
            var changed = false;

            foreach (var footprint in m_footprints) {
                if (!m_displayShares.TryGetValue(key: footprint.Consumer, value: out var consumer)) {
                    continue;
                }

                var share = (consumer * footprint.Height);

                if (share > m_displayShares.GetValueOrDefault(key: footprint.Producer)) {
                    m_displayShares[footprint.Producer] = share;
                    changed = true;
                }
            }

            if (!changed) {
                break;
            }
        }
    }
    // The refresh a footprint showing a nested view asks for: every frame unless the views lever is on and the view covers
    // few enough pixels of the display (SdfUpdateRateTiers.ViewHertz).
    private RenderGraphRefresh ViewRefresh(string consumer, double coverage) {
        if (
            (RenderSettings is not { } settings) ||
            ((settings.UpdateRates & SdfUpdateRates.Views) == 0) ||
            !m_displayShares.TryGetValue(key: consumer, value: out var share)
        ) {
            return RenderGraphRefresh.EveryFrame;
        }

        return Rated(counts: settings.RateCounts, hertz: SdfUpdateRateTiers.ViewHertz(pixels: ((share * coverage) * m_displayHeight)), kind: SdfUpdateRateCounts.ViewsRated);
    }
    // The refresh a rate asks for at the display's rate, counting a footprint it holds: every frame when the display's
    // rate is unknown or the rate rounds to it (SdfUpdateRateTiers.Divisor).
    private RenderGraphRefresh Rated(int hertz, WorkCounterSet counts, WorkKind kind) {
        var divisor = SdfUpdateRateTiers.Divisor(displayHertz: m_displayHertz, hertz: hertz);

        if (divisor <= 1) {
            return RenderGraphRefresh.EveryFrame;
        }

        counts.Count(kind: kind);

        return RenderGraphRefresh.Every(divisor: divisor);
    }
    // The refresh a footprint showing a source on the boot world's screens asks for: every frame unless the screens lever
    // is on and every screen showing it stands at or below its image's own size on the display, out of the consumer's
    // sight counting as no size at all (SdfUpdateRateTiers.ScreenHertz). A consumer rendering another world (a session
    // through a window) shows it no larger than its own image; a consumer whose camera is not known yet and a source no
    // screen of the boot world shows ask every frame. A source's image size is the crop its boot-world screen shows.
    private RenderGraphRefresh ScreenRefresh(in RenderGraphFootprint footprint) {
        // A sky environment's reads take their views' refresh once every view's is known (RateEnvironmentReads).
        if (m_environmentResidencies.ContainsKey(key: footprint.Consumer)) {
            return RenderGraphRefresh.EveryFrame;
        }

        var refresh = ScreenSight(footprint: in footprint);

        // A view the display reached no share of last frame (a seat's view no one occupies) demands nothing to rate by.
        if (!m_displayShares.ContainsKey(key: footprint.Consumer)) {
            return refresh;
        }

        m_sourceRefresh[footprint.Producer] = ((m_sourceRefresh.TryGetValue(key: footprint.Producer, value: out var other) && (other.Divisor < refresh.Divisor))
            ? other
            : refresh);

        return refresh;
    }
    // A sky environment reads every screen its view shows, for the light they cast: each of its reads asks for its source
    // as often as the most frequent view footprint of that source this frame.
    private void RateEnvironmentReads() {
        if (m_sourceRefresh.Count == 0) {
            return;
        }

        for (var index = 0; (index < m_footprints.Count); index++) {
            var footprint = m_footprints[index];

            if (
                m_environmentResidencies.ContainsKey(key: footprint.Consumer) &&
                m_sourceRefresh.TryGetValue(key: footprint.Producer, value: out var refresh)
            ) {
                m_footprints[index] = (footprint with { Refresh = refresh });
            }
        }
    }
    // The refresh a view's footprint of a source asks for by the screens it shows the source on.
    private RenderGraphRefresh ScreenSight(in RenderGraphFootprint footprint) {
        if (
            (RenderSettings is not { } settings) ||
            ((settings.UpdateRates & SdfUpdateRates.Screens) == 0) ||
            (Screens is not { } screens) ||
            (ViewScenes is not { } scenes) ||
            !m_displayShares.TryGetValue(key: footprint.Consumer, value: out var consumerShare)
        ) {
            return RenderGraphRefresh.EveryFrame;
        }

        // A consumer rendering another world, a session through a window, shows the source no larger than its own image.
        var elsewhere = scenes.TryPlacements(placements: out _, view: footprint.Consumer);
        var known = (m_cameras.TryGetValue(key: footprint.Consumer, value: out var camera) || scenes.TryCamera(camera: out camera, view: footprint.Consumer));

        if (!elsewhere && !known) {
            return RenderGraphRefresh.EveryFrame;
        }

        var shown = false;
        var largest = 0d;

        foreach (var screen in screens.Screens) {
            if (
                !string.Equals(a: screens.InstanceOf(screen: screen.Index), b: footprint.Producer, comparisonType: StringComparison.Ordinal) ||
                !screens.TryGet(mapping: out var mapping, screen: screen.Index)
            ) {
                continue;
            }

            shown = true;

            if (!elsewhere && !WorldPortalVisibility.Sees(camera: in camera, glass: screen)) {
                continue;
            }

            var coverage = (elsewhere ? 1d : WorldPortalVisibility.Coverage(camera: in camera, glass: screen).Height);

            largest = Math.Max(val1: largest, val2: (((coverage * consumerShare) * m_displayHeight) / Math.Max(val1: 1, val2: mapping.Crop.Height)));
        }

        return Rated(counts: settings.RateCounts, hertz: (shown ? SdfUpdateRateTiers.ScreenHertz(share: largest) : 60), kind: SdfUpdateRateCounts.ScreensRated);
    }
}
