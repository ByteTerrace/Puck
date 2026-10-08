using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm.Views;
using Puck.Testing;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the infinity views of a world nest to the render graph's depth and fall back past it, a world
/// carries at most <see cref="WorldInfinityViewPlan.MaxViews"/> of them and is refused by name past that
/// (<see cref="WorldInfinityViewPlan"/>); the views a presentation publishes follow what a viewer's previous frame showed
/// of them, read by the viewer within the frame, and republish nothing while it turns inside a footprint step
/// (<see cref="WorldInfinityViews"/>); and the instances are named so no author's name equals one
/// (<see cref="WorldViewNames.Sky"/>).
/// </summary>
public sealed class WorldInfinityViewLawTests {
    private static readonly Vector3 Anchor = new(x: 5f, y: 2f, z: -8f);

    private static InfinityViewSpec Spec(string name, InfinityViewMask? mask = null, InfinityViewKind kind = InfinityViewKind.World, float scale = 1f) => new(
        Anchor: Anchor,
        Fallback: new Vector3(x: 0.2f, y: 0.1f, z: 0.05f),
        FarDistance: 400f,
        Kind: kind,
        Levers: InfinityViewLevers.None,
        Mask: mask,
        MinimumTier: QualityTier.Low,
        Name: name,
        Orientation: Quaternion.Identity,
        Refresh: 2,
        Scale: scale
    );
    private static CameraSnapshot Viewer(float yaw = 0f) => CameraSnapshot.LookAt(
        fieldOfViewRadians: 1.0f,
        position: Vector3.Zero,
        target: Vector3.Transform(value: -Vector3.UnitZ, rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: yaw)),
        viewportHeight: 720f,
        viewportWidth: 1280f
    );
    // A small cone straight ahead of a level viewer.
    private static InfinityViewMask Ahead(float halfAngle = 0.1f) => new(Axis: -Vector3.UnitZ, HalfAngle: halfAngle);
    private static IReadOnlyList<InfinityViewSpec> NoChildren(string parent, InfinityViewSpec spec) => [];

    private sealed class VisibleLayers(long root, long nested) : IGpuWorkReadback {
        public void AddTo(int slot, Span<long> counts, int rowCount) {
            var columns = GpuWork.SubmissionKinds.Length;
            var evaluations = GpuWork.SubmissionKinds.IndexOf(GpuWork.SkyEvaluations);
            // Outside + two passes + four details: sky/plain, sky/lobby, composite/plain, composite/lobby.
            counts[((4 * columns) + evaluations)] += root;
            counts[((6 * columns) + evaluations)] += nested;
        }
    }

    [Fact]
    public void OnlyTheViewersExecutedCompositeChangesItsNamedLayerDemand() {
        var views = new WorldInfinityViews();

        views.Apply(plan: WorldInfinityViewPlan.Resolve([Spec("lobby")], (_, _) => [Spec("lobby")], nestingDepth: 2));
        var ledger = new GpuWorkLedger(framesInFlight: 1, name: "gpu.infinity-demand");
        var gpu = GpuWorkCounting.Wrap(new FakeGpuDevice().Services, ledger);

        ledger.Configure(1, ["sdf.world$sky", "sdf.world$composite"]);
        ledger.ConfigureDetails(details: [new(Detail: "plain", Pass: 0), new(Detail: "lobby", Pass: 0), new(Detail: "plain", Pass: 1), new(Detail: "lobby", Pass: 1)]);
        var sample = new GpuWorkSample();

        void Submit(long shown, bool standing = false) {
            ledger.EnterPass(pass: 0);
            ledger.LeavePass();
            if (standing) { ledger.StandPass(pass: 1); } else { ledger.EnterPass(pass: 1); ledger.LeavePass(); }
            ledger.ReadOnCompletion(readback: new VisibleLayers(nested: shown, root: 999), slot: 0);
            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: []);
            Assert.True(condition: ledger.TryReadCompleted(sample: sample));
        }
        Submit(7);
        views.Report(parent: null, sample);
        Assert.True(condition: views.Demand.IsDemanded(view: "sky$lobby"));
        Assert.False(condition: views.Demand.IsDemanded(view: "sky$lobby$sky$lobby"));
        views.Report(parent: "sky$lobby", sample);
        Assert.True(condition: views.Demand.IsDemanded(view: "sky$lobby$sky$lobby"));
        Submit(0, standing: true);
        views.Report(parent: null, sample);
        Assert.True(condition: views.Demand.IsDemanded(view: "sky$lobby"));
        Submit(0);
        views.Report(parent: null, sample);
        Assert.False(condition: views.Demand.IsDemanded(view: "sky$lobby"));
        Assert.True(condition: views.Demand.IsDemanded(view: "sky$lobby$sky$lobby"));
    }
    [Fact]
    public void AnUnavailableObservationKeepsItsFitAndFallbackWithoutPublishingARead() {
        var views = new WorldInfinityViews();

        views.Apply(plan: WorldInfinityViewPlan.Resolve([Spec("lobby")], (_, _) => [Spec("moon")], nestingDepth: 2));
        views.Demand.Report(texels: 12, view: "sky$lobby");
        var published = new WorldViewSet();

        published.Begin();
        views.Update(Viewer(), 1280, 720, QualityTier.High, published, available: _ => false);
        _ = published.TryPublish(instances: out _);
        Assert.Empty(collection: published.Instances.Views);
        var fallback = Assert.Single(collection: views.BindingsOf(null, Viewer(), 1280, 720, QualityTier.High));

        Assert.Null(@object: fallback.Producer);
        Assert.True(condition: views.FrameOf(name: "sky$lobby").Visible);

        published.Begin();
        views.Update(Viewer(), 1280, 720, QualityTier.High, published, available: name => (name == "sky$lobby"));
        Assert.True(condition: published.TryPublish(instances: out var admitted));
        var lobby = Assert.Single(collection: admitted.Views);

        Assert.Equal("sky$lobby", lobby.Name);
        Assert.Empty(collection: lobby.Reads!);
        Assert.Equal("sky$lobby", Assert.Single(collection: views.BindingsOf(null, Viewer(), 1280, 720, QualityTier.High)).Producer);
        Assert.Null(@object: Assert.Single(collection: views.BindingsOf("sky$lobby", views.FrameOf(name: "sky$lobby").Camera, 1280, 720, QualityTier.High)).Producer);
    }
    [Fact]
    public void AnotherCameraOfAShownWorldKeepsTheOriginalNamesAndCapDecision() {
        var plan = WorldInfinityViewPlan.Resolve([Spec("lobby")], (_, spec) => spec.Name switch {
            "lobby" => [Spec("moon")],
            "moon" => [Spec("star")],
            _ => [],
        }, nestingDepth: 3, cap: 2);
        var camera = plan.Below(parent: "sky$lobby");
        var moon = Assert.Single(collection: camera.Views);

        Assert.Equal("sky$lobby$sky$moon", moon.Name);
        Assert.Null(@object: moon.Parent);
        var fallback = Assert.Single(collection: camera.Fallbacks);

        Assert.Equal(moon.Name, fallback.Parent);
        Assert.Equal("star", fallback.Spec.Name);
        Assert.Equal(Assert.Single(collection: plan.Fallbacks).Reason, fallback.Reason);
        Assert.Same(plan, plan.Below(parent: null));
    }
    [Fact]
    public void AnInstanceIsNamedUnderTheSkyAndNestedUnderTheViewWhoseWorldShowsIt() {
        Assert.Equal(expected: "sky$lobby", actual: WorldViewNames.Sky(layer: "lobby"));
        Assert.Equal(expected: "session$0$sky$moon", actual: WorldViewNames.NestedSky(layer: "moon", view: WorldViewNames.Session(screen: 0)));
        Assert.Equal(expected: "sky$lobby$sky$moon", actual: WorldViewNames.NestedSky(layer: "moon", view: WorldViewNames.Sky(layer: "lobby")));
        Assert.Throws<ArgumentException>(testCode: () => WorldViewNames.Sky(layer: "a$b"));
        Assert.Throws<ArgumentException>(testCode: () => WorldViewNames.Sky(layer: ""));
        Assert.True(condition: Puck.State.GeneratedName.IsGenerated(name: WorldViewNames.Sky(layer: "lobby")));
    }
    [Fact]
    public void ViewsNestToTheGraphsDepthAndThePastItDrawsItsFallback() {
        var lobby = Spec(name: "lobby");
        var moon = Spec(name: "moon");
        var star = Spec(name: "star");

        IReadOnlyList<InfinityViewSpec> Children(string parent, InfinityViewSpec spec) => spec.Name switch {
            "lobby" => [moon],
            "moon" => [star],
            _ => [],
        };

        var two = WorldInfinityViewPlan.Resolve(childrenOf: Children, nestingDepth: 2, roots: [lobby]);

        Assert.Equal(expected: ["sky$lobby", "sky$lobby$sky$moon"], actual: two.Views.Select(selector: static view => view.Name));
        Assert.Equal(expected: [1, 2], actual: two.Views.Select(selector: static view => view.Depth));
        Assert.Equal(expected: [null, "sky$lobby"], actual: two.Views.Select(selector: static view => view.Parent));

        var fallback = Assert.Single(collection: two.Fallbacks);

        Assert.Equal(expected: "sky$lobby$sky$moon", actual: fallback.Parent);
        Assert.Equal(expected: star.Fallback, actual: fallback.Spec.Fallback);
        Assert.Contains(actualString: fallback.Reason, expectedSubstring: "nesting depth");

        var none = WorldInfinityViewPlan.Resolve(childrenOf: Children, nestingDepth: 0, roots: [lobby]);

        Assert.Empty(collection: none.Views);
        Assert.Single(collection: none.Fallbacks);

        var three = WorldInfinityViewPlan.Resolve(childrenOf: Children, nestingDepth: RenderGraphInstanceSet.DefaultNestingDepth, roots: [lobby]);

        Assert.Equal(expected: 3, actual: three.Views.Count);
        Assert.Empty(collection: three.Fallbacks);
    }
    [Fact]
    public void TwoWorldsShowingEachOtherNestToTheDepthAndStop() {
        var a = Spec(name: "a");
        var b = Spec(name: "b");

        IReadOnlyList<InfinityViewSpec> Children(string parent, InfinityViewSpec spec) => ((spec.Name == "a") ? [b] : [a]);

        var plan = WorldInfinityViewPlan.Resolve(childrenOf: Children, nestingDepth: 3, roots: [a]);

        Assert.Equal(expected: ["sky$a", "sky$a$sky$b", "sky$a$sky$b$sky$a"], actual: plan.Views.Select(selector: static view => view.Name));
        Assert.Single(collection: plan.Fallbacks);
    }
    [Fact]
    public void ViewsPastTheCapDrawTheirFallbackInBreadthOrderAndTheCapAdmitsExactlyItsCount() {
        var roots = Enumerable.Range(count: 5, start: 0).Select(selector: static index => Spec(name: $"r{index}")).ToArray();
        var child = Spec(name: "child");

        // Three children under the first root would be instances 6 to 8 and the ninth; breadth order keeps every root first.
        var plan = WorldInfinityViewPlan.Resolve(
            cap: 6,
            childrenOf: (parent, spec) => ((spec.Name == "r0") ? [child, child with { Name = "c2" }, child with { Name = "c3" }] : []),
            nestingDepth: 3,
            roots: roots
        );

        Assert.Equal(expected: 6, actual: plan.Views.Count);
        Assert.Equal(expected: ["r0", "r1", "r2", "r3", "r4"], actual: plan.Views.Take(count: 5).Select(selector: static view => view.Spec.Name));
        Assert.Equal(expected: 1, actual: plan.Views.Count(predicate: static view => (view.Depth == 2)));
        Assert.Equal(expected: 2, actual: plan.Fallbacks.Count);
        Assert.All(collection: plan.Fallbacks, action: static fallback => Assert.Contains(actualString: fallback.Reason, expectedSubstring: "at most 6"));
        Assert.Equal(expected: ["infinity views: 6 of 6", .. plan.Fallbacks.Select(selector: static fallback => $"  fallback: {fallback.Reason}")], actual: plan.Describe(cap: 6));
    }
    [Fact]
    public void AWorldAuthoringMoreThanTheCapIsRefusedByNameAndOneAtTheCapIsAdmitted() {
        var atCap = Enumerable.Range(count: WorldInfinityViewPlan.MaxViews, start: 0).Select(selector: static index => Spec(name: $"v{index}")).ToArray();

        Assert.True(condition: WorldInfinityViewPlan.TryCheck(reason: out _, specs: atCap));

        Assert.False(condition: WorldInfinityViewPlan.TryCheck(reason: out var past, specs: [.. atCap, Spec(name: "one-more")]));
        Assert.Contains(actualString: past, expectedSubstring: $"{(WorldInfinityViewPlan.MaxViews + 1)} infinity views");
        Assert.Contains(actualString: past, expectedSubstring: $"at most {WorldInfinityViewPlan.MaxViews}");
        Assert.False(condition: WorldInfinityViewPlan.TryCheck(reason: out var twice, specs: [Spec(name: "v"), Spec(name: "v")]));
        Assert.Contains(actualString: twice, expectedSubstring: "two infinity views are named 'v'");
        Assert.False(condition: WorldInfinityViewPlan.TryCheck(reason: out var sound, specs: [Spec(name: "v") with { Scale = 0f }]));
        Assert.Contains(actualString: sound, expectedSubstring: "scale");
    }
    [Fact]
    public void AViewIsPublishedUnseenUntilAFrameShowedItThenSeenWhileItsRegionIsInFrustum() {
        var views = new WorldInfinityViews();
        var set = new WorldViewSet();

        views.Apply(plan: WorldInfinityViewPlan.Resolve(childrenOf: NoChildren, nestingDepth: 3, roots: [Spec(name: "lobby", mask: Ahead())]));

        WorldView Publish(CameraSnapshot viewer) {
            set.Begin();
            views.Update(tier: QualityTier.High, viewer: viewer, viewerHeight: 720u, viewerWidth: 1280u, views: set);

            _ = set.TryPublish(instances: out _);

            return set.Instances.Views.Single();
        }

        var first = Publish(viewer: Viewer());

        Assert.Equal(expected: WorldViewDemand.Sky, actual: first.Demand);
        Assert.Equal(expected: "sky$lobby", actual: first.Name);
        Assert.False(condition: first.FilmsWorld);
        Assert.Null(@object: first.Parent);
        Assert.Equal(expected: RenderGraphRefresh.Every(divisor: 2), actual: first.Refresh);

        views.Demand.Report(texels: 3000L, view: "sky$lobby");

        Assert.Equal(expected: WorldViewDemand.Sky | WorldViewDemand.SkySeen, actual: Publish(viewer: Viewer()).Demand);
        // The viewer turns away: the region leaves the frustum, so the graph stops showing it though it was demanded.
        Assert.Equal(expected: WorldViewDemand.Sky, actual: Publish(viewer: Viewer(yaw: 2.5f)).Demand);

        views.Demand.Report(texels: 0L, view: "sky$lobby");

        Assert.Equal(expected: WorldViewDemand.Sky, actual: Publish(viewer: Viewer()).Demand);
    }
    [Fact]
    public void TheViewerReadsARootSkyViewWithinTheFrameAndAParentReadsItsChildren() {
        var lobby = Spec(name: "lobby");
        var moon = Spec(name: "moon");
        var views = new WorldInfinityViews();
        var set = new WorldViewSet();

        views.Apply(plan: WorldInfinityViewPlan.Resolve(childrenOf: (parent, spec) => ((spec.Name == "lobby") ? [moon] : []), nestingDepth: 3, roots: [lobby]));
        set.Begin();
        views.Update(tier: QualityTier.High, viewer: Viewer(), viewerHeight: 720u, viewerWidth: 1280u, views: set);
        _ = set.TryPublish(instances: out var published);

        var camera = new WorldView(Demand: WorldViewDemand.Root, FilmsWorld: true, Height: 1.0, Name: WorldViewGraphs.WorldInstance, Refresh: RenderGraphRefresh.EveryFrame, Width: 1.0);
        var instances = WorldViewInstances.Of(views: [camera, .. published!.Views]).Instances(sources: []);
        var viewer = instances.Single(predicate: static instance => (instance.Name == WorldViewGraphs.WorldInstance));
        var parent = instances.Single(predicate: static instance => (instance.Name == "sky$lobby"));
        var child = instances.Single(predicate: static instance => (instance.Name == "sky$lobby$sky$moon"));

        // The viewer reads the root view this frame; the nested one it does not read at all, its parent does.
        Assert.Equal(expected: ["sky$lobby"], actual: viewer.Reads.Where(predicate: static read => !read.PreviousFrame).Select(selector: static read => read.Producer));
        Assert.Equal(expected: ["sky$lobby$sky$moon"], actual: parent.Reads.Select(selector: static read => read.Producer));
        Assert.Empty(collection: child.Reads);
        Assert.All(collection: parent.Reads, action: static read => Assert.False(condition: read.PreviousFrame));
    }
    [Fact]
    public void TwoConsumersKeepTheirOwnFittedCameraBindingsAndDemand() {
        var plan = WorldInfinityViewPlan.Resolve(childrenOf: NoChildren, nestingDepth: 3, roots: [Spec(name: "lobby")]);
        var first = new WorldInfinityViews();
        var second = new WorldInfinityViews(consumer: "camera");
        var set = new WorldViewSet();
        var left = Viewer();
        var right = Viewer(yaw: 0.4f);

        first.Apply(plan: plan);
        second.Apply(plan: plan);
        first.Demand.Report(texels: 12, view: "sky$lobby");
        set.Begin();
        first.Update(viewer: left, viewerWidth: 1280, viewerHeight: 720, tier: QualityTier.High, views: set);
        second.Update(viewer: right, viewerWidth: 640, viewerHeight: 360, tier: QualityTier.High, views: set);
        _ = set.TryPublish(instances: out var published);
        var a = Assert.Single(collection: first.BindingsOf(parent: null, tier: QualityTier.High, viewer: left, viewerHeight: 720, viewerWidth: 1280));
        var b = Assert.Single(collection: second.BindingsOf(parent: null, tier: QualityTier.High, viewer: right, viewerHeight: 360, viewerWidth: 640));

        Assert.Equal(expected: "lobby", actual: a.Layer);
        Assert.Equal(expected: a.Layer, actual: b.Layer);
        Assert.NotEqual(expected: a.Producer, actual: b.Producer);
        Assert.Equal(expected: left.Forward, actual: a.Parameters.Forward);
        Assert.Equal(expected: right.Forward, actual: b.Parameters.Forward);
        Assert.NotEqual(expected: a.Parameters.Forward, actual: b.Parameters.Forward);
        Assert.True(condition: published!.Views.Single(predicate: view => (view.Name == a.Producer)).Demand.HasFlag(flag: WorldViewDemand.SkySeen));
        Assert.False(condition: published.Views.Single(predicate: view => (view.Name == b.Producer)).Demand.HasFlag(flag: WorldViewDemand.SkySeen));

        var consumers = new[] { WorldViewGraphs.WorldInstance, "camera" }.Select(selector: static name =>
            new WorldView(Name: name, FilmsWorld: false, Demand: WorldViewDemand.Root, Width: 1, Height: 1, Refresh: RenderGraphRefresh.EveryFrame));
        var instances = WorldViewInstances.Of(views: [.. consumers, .. published.Views]).Instances(sources: []);

        Assert.Equal(expected: a.Producer, actual: Assert.Single(collection: instances.Single(predicate: static view => (view.Name == WorldViewGraphs.WorldInstance)).Reads).Producer);
        Assert.Equal(expected: b.Producer, actual: Assert.Single(collection: instances.Single(predicate: static view => (view.Name == "camera")).Reads).Producer);
    }
    [Fact]
    public void ANestedViewFitsTheCameraOfTheViewThatRendersItsWorldAndNeverTheViewers() {
        var views = new WorldInfinityViews();
        var set = new WorldViewSet();
        var lobby = Spec(name: "lobby", mask: Ahead(halfAngle: 0.4f));
        var moon = Spec(name: "moon", mask: Ahead(halfAngle: 0.1f)) with { Anchor = new Vector3(x: -90f, y: 0f, z: 33f) };

        views.Apply(plan: WorldInfinityViewPlan.Resolve(childrenOf: (parent, spec) => ((spec.Name == "lobby") ? [moon] : []), nestingDepth: 3, roots: [lobby]));
        set.Begin();
        views.Update(tier: QualityTier.High, viewer: Viewer(), viewerHeight: 720u, viewerWidth: 1280u, views: set);

        var outer = views.FrameOf(name: "sky$lobby");
        var inner = views.FrameOf(name: "sky$lobby$sky$moon");

        Assert.True(condition: outer.Visible);
        Assert.True(condition: inner.Visible);
        Assert.Equal(expected: Anchor, actual: outer.Camera.Position);
        Assert.Equal(expected: moon.Anchor, actual: inner.Camera.Position);
        // The inner view is fitted inside the outer instance's own image, so it is no wider than it.
        Assert.True(condition: (inner.Width <= outer.Width));
        Assert.True(condition: (inner.Height <= outer.Height));
        Assert.True(condition: (inner.Camera.TanHalfFieldOfView <= outer.Camera.TanHalfFieldOfView));
    }
    [Fact]
    public void AViewerTurningInsideAFootprintStepRepublishesNothingAndAPlanChangeForgetsARemovedView() {
        var views = new WorldInfinityViews();
        var set = new WorldViewSet();

        views.Apply(plan: WorldInfinityViewPlan.Resolve(childrenOf: NoChildren, nestingDepth: 3, roots: [Spec(name: "lobby", mask: Ahead(halfAngle: 0.3f))]));
        views.Demand.Report(texels: 10L, view: "sky$lobby");

        bool Frame(float yaw) {
            set.Begin();
            views.Update(tier: QualityTier.High, viewer: Viewer(yaw: yaw), viewerHeight: 720u, viewerWidth: 1280u, views: set);

            return set.TryPublish(instances: out _);
        }

        Assert.True(condition: Frame(yaw: 0f));
        Assert.False(condition: Frame(yaw: 0f), userMessage: "a steady frame");
        // 0.05 rad of yaw moves the region's rectangle by 5 percent of the field of view; the extent it covers stays in
        // its sixteenth.
        Assert.False(condition: Frame(yaw: 0.0005f), userMessage: "a turn inside the step");

        views.Apply(plan: WorldInfinityViewPlan.Empty);

        Assert.False(condition: views.Demand.IsDemanded(view: "sky$lobby"));

        set.Begin();
        views.Update(tier: QualityTier.High, viewer: Viewer(), viewerHeight: 720u, viewerWidth: 1280u, views: set);

        Assert.True(condition: set.TryPublish(instances: out var published));
        Assert.Empty(collection: published!.Views);
    }
    [InlineData(0.0, 0.0625)]
    [InlineData(0.0001, 0.0625)]
    [InlineData(0.0625, 0.0625)]
    [InlineData(0.07, 0.125)]
    [InlineData(0.99, 1.0)]
    [InlineData(3.0, 1.0)]
    [Theory]
    public void AFootprintRoundsUpToASixteenthAndNeverPastTheConsumer(double fraction, double expected) {
        // A zero fraction still covers a sixteenth: a footprint of zero would mean the consumer does not show the view.
        Assert.Equal(expected: expected, actual: WorldInfinityViews.Quantize(fraction: fraction));
    }
}
