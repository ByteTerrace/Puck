using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Assets.Documents;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

// The world.rates views lever (SdfUpdateRates.Views): a session whose glass covers few pixels of the display renders at a
// reduced rate through its footprint's refresh, and every frame its consumer renders while the lever is off.
public sealed partial class WorldViewPaneMappingLawTests {
    [Fact]
    public void APortalResumesOnTheFrameTheCameraTurnsBackEvenBeforeItsRatePeriod() {
        _ = ShowTwoWorlds();
        m_host.ViewScenes = new GlassScenes(Bakes: false, Glass: FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond")));
        m_host.RenderSettings = new WorldRenderSettings(defaults: new WorldRenderDefaults()) { UpdateRates = SdfUpdateRates.Views };
        m_rate = Paced60;
        Frame(pane: null);
        Assert.Equal(expected: 1, actual: RendersOf(instance: BootSession));

        for (var index = 0; index < m_views.Count; index++) {
            m_views[index] = m_views[index] with { Camera = Camera(x: 100f) };
        }
        Frame(pane: null);
        Assert.Equal(expected: 0, actual: RendersOf(instance: BootSession));

        for (var index = 0; index < m_views.Count; index++) {
            m_views[index] = m_views[index] with { Camera = Camera(x: 0f) };
        }
        Frame(pane: null);
        Assert.Equal(expected: 1, actual: RendersOf(instance: BootSession));
    }

    [Fact]
    public void AShortButWideGlassIsNotHeldAsAFourPixelPatch() {
        _ = ShowTwoWorlds();
        m_host.ViewScenes = new GlassScenes(Bakes: false, Glass: FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond")) with { HalfHeight = 0.01f });
        m_host.RenderSettings = new WorldRenderSettings(defaults: new WorldRenderDefaults()) { UpdateRates = SdfUpdateRates.Views };
        m_rate = Paced60;
        var renders = 0;
        for (var frame = 0; frame < 12; frame++) {
            Frame(pane: null);
            renders += RendersOf(instance: BootSession);
        }
        Assert.True(renders > 1);
        Assert.DoesNotContain(m_host.RenderSettings.RateReadings.Latest(SdfUpdateRates.Views), reading => reading.Hertz == SdfUpdateRateTiers.HeldHertz);
    }

    [Fact]
    public void AHeldPortalResumesWhenItsConsumerGrowsInTheSameFrame() {
        _ = ShowTwoWorlds();
        m_host.ViewScenes = new GlassScenes(Bakes: false, Glass: FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond")));
        m_host.RenderSettings = new WorldRenderSettings(defaults: new WorldRenderDefaults()) { UpdateRates = SdfUpdateRates.Views };
        m_rate = Paced60;
        for (var index = 0; index < m_views.Count; index++) {
            m_views[index] = m_views[index] with { Region = new NormalizedRect(X: 0f, Y: 0f, Width: 0.01f, Height: 0.01f) };
        }
        Frame(pane: null);
        Frame(pane: null);
        Assert.Equal(expected: 0, actual: RendersOf(instance: BootSession));
        m_views[0] = m_views[0] with { Region = Left };
        m_views[1] = m_views[1] with { Region = Right };
        Frame(pane: null);
        Assert.Equal(expected: 1, actual: RendersOf(instance: BootSession));
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void ASessionSmallOnTheDisplayRendersAtAReducedRateWhileTheViewsLeverIsOn(bool rated) {
        _ = ShowTwoWorlds();
        m_host.ViewScenes = new GlassScenes(Bakes: false, Glass: FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond")));

        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults()) {
            UpdateRates = (rated ? SdfUpdateRates.All : SdfUpdateRates.None),
        };

        m_host.RenderSettings = settings;
        m_rate = Paced60;

        var renders = 0;

        // The glass spans a third of a 64-pixel view, far below the 15 Hz tier: a quarter of 60 Hz once its camera is known.
        for (var frame = 0; (frame < 16); frame++) {
            Frame(pane: null);
            renders += RendersOf(instance: BootSession);
        }

        if (rated) {
            Assert.InRange(actual: renders, high: 6, low: 4);
            Assert.True(condition: (settings.RateCounts.Read(kind: SdfUpdateRateCounts.ViewsRated) > 0L));
            // The schedule shows the session rendering every fourth frame between holds: a 15 Hz tier's whole period.
            Assert.Equal(expected: (4d / 60d), actual: (settings.RateIntervals.Longest(hertz: 15, lever: SdfUpdateRates.Views) ?? 0d), tolerance: 1e-9d);
            // world.rates lists the session at its tier, and a tier view tints the glass that shows it.
            var reading = Assert.Single(collection: settings.RateReadings.Latest(lever: SdfUpdateRates.Views));

            Assert.Equal(expected: BootSession, actual: reading.Name);
            Assert.Equal(expected: 15, actual: reading.Hertz);
            Assert.InRange(actual: (m_host.DisplayShareOf(instance: BootSession) ?? 0d), high: 1d, low: 0.01d);
            Assert.Equal(expected: 0d, actual: m_host.DisplayShareOf(instance: "nothing"));

            var codes = new byte[4];

            m_host.WriteScreenRateCodes(codes: codes);
            Assert.Equal(expected: ((byte)SdfUpdateRateCode.Quarter), actual: ((byte)(codes[0] % ((byte)SdfUpdateRateCode.Updated))));
        } else {
            Assert.InRange(actual: renders, high: 16, low: 14);
            Assert.Equal(expected: 0L, actual: settings.RateCounts.Read(kind: SdfUpdateRateCounts.ViewsRated));
            Assert.Null(@object: settings.RateIntervals.Longest(hertz: 15, lever: SdfUpdateRates.Views));
            Assert.Empty(collection: settings.RateReadings.Latest(lever: SdfUpdateRates.Views));
            Assert.Null(@object: m_host.DisplayShareOf(instance: BootSession));

            // Nothing rated tints nothing, and a host showing no screens tints none.
            var codes = new byte[] { 9, 9, 9, 9 };

            m_host.WriteScreenRateCodes(codes: codes);
            Assert.All(collection: codes, action: static code => Assert.Equal(expected: ((byte)SdfUpdateRateCode.None), actual: code));
            m_host.Screens = null;
            codes[0] = 9;
            m_host.WriteScreenRateCodes(codes: codes);
            Assert.Equal(expected: ((byte)SdfUpdateRateCode.None), actual: codes[0]);
        }
    }
    // THE LAW: a session whose glass its consumer sees at no more than SdfUpdateRateTiers.ViewHeldPixels renders once,
    // then holds that image until it grows, while the views lever is on; a settled capture renders it every frame it is
    // pending. The glass stands 200 m beyond the camera, half a pixel of the 64-pixel view: its first frame, before the
    // consumer's camera is known, renders it (an instance that never rendered is never held), and every later frame holds
    // it, counted and listed by world.rates as held. The red leg is the lever off: it renders every frame.
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void ASessionTooSmallToSeeHoldsItsImageUntilASettledCaptureAsksForIt(bool rated) {
        _ = ShowTwoWorlds();
        m_host.ViewScenes = new GlassScenes(Bakes: false, Glass: (FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond")) with {
            Origin = new DocumentVector3(value: new Vector3(x: 0f, y: 1f, z: -200f)),
        }));

        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults()) {
            UpdateRates = (rated ? SdfUpdateRates.All : SdfUpdateRates.None),
        };

        m_host.RenderSettings = settings;
        m_rate = Paced60;

        var renders = 0;

        for (var frame = 0; (frame < 16); frame++) {
            Frame(pane: null);
            renders += RendersOf(instance: BootSession);
        }

        Assert.Equal(expected: (rated ? 1 : 16), actual: renders);
        Assert.Equal(expected: rated, actual: (settings.RateCounts.Read(kind: SdfUpdateRateCounts.ViewsHeld) > 0L));

        if (rated) {
            var reading = Assert.Single(collection: settings.RateReadings.Latest(lever: SdfUpdateRates.Views));

            Assert.Equal(expected: BootSession, actual: reading.Name);
            Assert.Equal(expected: SdfUpdateRateTiers.HeldHertz, actual: reading.Hertz);
            Assert.InRange(actual: reading.Size, high: SdfUpdateRateTiers.ViewHeldPixels, low: 0d);
        }

        m_instances.SettledCapturePath = "still.png";
        renders = 0;

        for (var frame = 0; (frame < 4); frame++) {
            Frame(pane: null);
            renders += RendersOf(instance: BootSession);
        }

        Assert.Equal(expected: 4, actual: renders);
    }
    // THE LAW: a root camera that films the world roots a session it films only while its own image shows the session's
    // glass, at the share of that image the glass spans. A probe camera facing away from a portal roots nothing beneath
    // it, so the portal renders only for a view that sees it; the red leg is the glass before the camera, rooted.
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AFilmingCameraRootsOnlyTheSessionsItsImageShows(bool facing) {
        var screens = ShowCamera(demand: WorldViewDemand.Root);

        screens.Reconcile(
            cameras: [FilmingCamera()],
            screens: [FacingScreen(index: 0, source: new WorldScreenSource.Probe(Id: "feed"))]
        );
        screens.ReconcileViews(views: WorldViewInstances.Of(views: [
            new WorldView(Demand: WorldViewDemand.Root, FilmsWorld: true, Height: 0.25, Name: ViewCamera, Refresh: RenderGraphRefresh.EveryFrame, Width: 0.25),
            new WorldView(Demand: WorldViewDemand.Screen, FilmsWorld: false, Height: 0.5, Name: "session", Refresh: RenderGraphRefresh.EveryFrame, Width: 0.5),
        ]));
        m_host.ViewScenes = new FilmedGlassScenes(
            Camera: Camera(x: 0f),
            Glass: (FacingScreen(index: 0, source: new WorldScreenSource.Session(Destination: "beyond")) with {
                Origin = new DocumentVector3(value: new Vector3(x: 0f, y: 1f, z: (facing ? 0f : 9f))),
            })
        );
        Prepare(pane: null);

        var rooted = m_host.Roots.Where(predicate: static root => (root.Instance == "session")).ToArray();

        Assert.Equal(expected: (facing ? 1 : 0), actual: rooted.Length);

        if (facing) {
            // The glass spans about a third of the camera's image, which the camera's quarter of the display scales.
            Assert.InRange(actual: rooted[0].Height, high: (0.25 * 0.5), low: (0.25 * 0.2));
        }
    }

    // A scene whose filming camera stands at a given pose and whose session "session" stands on a given glass of the
    // boot world.
    private sealed record FilmedGlassScenes(CameraSnapshot Camera, WorldScreen Glass) : IWorldViewScenes {
        public bool Bakes(string view) => false;
        public bool TryVisibilityCamera(string view, out CameraSnapshot camera) => TryCamera(view, out camera);
        public bool TryCamera(string view, out CameraSnapshot camera) {
            camera = Camera;

            return true;
        }
        public bool TrySurface(string view, SourceRay ray, out FixedVector3 point) {
            point = default;

            return false;
        }
        public bool TryPlacements(string view, out IReadOnlyList<SourceMapping> placements) {
            placements = [];

            return false;
        }
        public WorldPortalGlass PortalGlass(string consumer, string producer, out WorldScreen? glass) {
            glass = (string.Equals(a: producer, b: "session", comparisonType: StringComparison.Ordinal)
                ? Glass
                : null);

            return ((glass is null)
                ? WorldPortalGlass.Elsewhere
                : WorldPortalGlass.Found);
        }
    }
}
