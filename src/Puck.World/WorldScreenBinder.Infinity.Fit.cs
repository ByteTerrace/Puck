using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    private static QualityTier InfinityTier(SdfSky sky) => sky.Quality switch {
        SdfSkyTier.High => QualityTier.High,
        SdfSkyTier.Medium => QualityTier.Medium,
        _ => QualityTier.Low,
    };
    private void FitBootSky(List<SdfViewSnapshot> views, SdfSky sky, uint width, uint height) {
        if ((Presenter is not { } presenter)
            || !m_instanceHost.TryEndpoint(endpoint: out var endpoint, name: WorldInstanceHost.BootInstanceName) || (endpoint is null)) { return; }
        BeginInfinityConsumers(target: presenter);
        var root = InfinityRootOf(endpoint.Mirror, WorldInstanceHost.BootInstanceName, 0, WorldViewGraphs.WorldInstance);

        for (var index = 0; (index < Math.Min(val1: m_hostViewCount, val2: views.Count)); index++) {
            if (presenter.TryRoutedView(index: out _, scene: out _, view: index)) { continue; }
            FitInfinity(root, null, ((index == 0) ? WorldViewGraphs.WorldInstance : WorldViewNames.World(view: (index + 1))), presenter, views, index, sky,
                ((uint)Math.Max(val1: 1, val2: (width * views[index].Region.Width))), ((uint)Math.Max(val1: 1, val2: (height * views[index].Region.Height))));
        }
        foreach (var (name, index) in m_cameraViewIndices) {
            if ((((uint)index) >= ((uint)views.Count)) || !m_cameraViews.TryGetValue(key: name, value: out var camera)) { continue; }
            FitInfinity(root, null, name, presenter, views, index, sky, camera.Row.RenderWidth, camera.Row.RenderHeight);
        }
        EndInfinityConsumers(target: presenter);
    }
    private void FitSessionSky(SessionFeed feed, List<SdfViewSnapshot> views, SdfSky sky, uint width, uint height) {
        BeginInfinityConsumers(target: feed);
        var root = InfinityRootOf(feed.Mirror, feed.InstanceName, feed.Depth, feed.RegistrationName);

        if (views.Count != 0) { FitInfinity(root, null, feed.RegistrationName, feed, views, 0, sky, width, height); }
        FitNestedCameraSkies(feed.Nested, root, null, feed, views, sky);
        EndInfinityConsumers(target: feed);
    }
    private void FitRoutedSky(WorldRoutedScene scene, List<SdfViewSnapshot> views, SdfSky sky, uint width, uint height) {
        BeginInfinityConsumers(target: scene);
        var root = InfinityRootOf(scene.Endpoint.Mirror, scene.Endpoint.Identity, 0, RoutedViewName(scene: scene));

        if (Presenter is { } presenter) {
            for (var index = 0; (index < m_hostViewCount); index++) {
                if (presenter.TryRoutedView(index: out var destination, scene: out var routed, view: index)
                    && ReferenceEquals(objA: routed, objB: scene) && (((uint)destination) < ((uint)views.Count))) {
                    var view = views[destination];

                    FitInfinity(root, null, ((index == 0) ? WorldViewGraphs.WorldInstance : WorldViewNames.World(view: (index + 1))), scene, views, destination, sky,
                        ((uint)Math.Max(val1: 1, val2: (width * view.Region.Width))), ((uint)Math.Max(val1: 1, val2: (height * view.Region.Height))));
                }
            }
        }
        foreach (var feed in m_feeds) {
            if ((feed.WindowRoute.Window is not { } window) || !ReferenceEquals(objA: window.Scene, objB: scene)
                || (((uint)window.Index) >= ((uint)views.Count))) { continue; }
            var nested = InfinityRootOf(feed.Mirror, feed.InstanceName, feed.Depth, feed.RegistrationName);

            FitInfinity(nested, null, feed.RegistrationName, scene, views, window.Index, sky,
                ((uint)(feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth)),
                ((uint)(feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight)));
            FitNestedCameraSkies(feed.Nested, nested, null, scene, views, sky);
        }
        FitNestedCameraSkies(m_routedScreens.GetValueOrDefault(key: scene), root, null, scene, views, sky);
        EndInfinityConsumers(target: scene);
    }
    private void FilmInfinity(InfinityEntry entry, List<SdfViewSnapshot> views, SdfSky sky, uint width, uint height) {
        entry.Tier = InfinityTier(sky: sky);
        // The entry's consumer indices precede the camera screens. Their child fits share this plan's existing scenes.
        foreach (var consumer in entry.Root.Consumers.Values) {
            if (!entry.Indices.TryGetValue(key: consumer.Fit.Consumer, value: out var index) || (((uint)index) >= ((uint)views.Count))) { continue; }
            UpdateInfinityFit(entry.Root, consumer);
            var camera = views[index].Camera;

            views[index] = views[index] with {
                SkyViews = consumer.Fit.BindingsOf(entry.Planned.Name, camera,
                ((uint)Math.Max(val1: 1, val2: width)), ((uint)Math.Max(val1: 1, val2: height)), entry.Tier),
            };
        }
        BeginInfinityConsumers(target: entry);
        m_nestedFilms.Begin(target: entry);
        if (entry.Screens is { } level) {
            FilmLevel(level: level, mirror: entry.Mirror, target: entry, views: views);
            FitNestedCameraSkies(level, entry.Root, entry.Planned.Name, entry, views, sky);
        }
        m_nestedFilms.End(target: entry);
        EndInfinityConsumers(target: entry);
    }
    private void FitNestedCameraSkies(WorldNestedScreens<SessionFeed>? level, InfinityRoot root, string? parent,
        object target, List<SdfViewSnapshot> views, SdfSky sky) {
        if (level is null) { return; }
        foreach (var camera in level.Cameras) {
            if (!m_nestedFilms.TryGet(camera.Name, out var film) || !ReferenceEquals(objA: film.Target, objB: target)
                || (((uint)film.Index) >= ((uint)views.Count))) { continue; }
            FitInfinity(root, parent, camera.Name, target, views, film.Index, sky, camera.Camera.RenderWidth, camera.Camera.RenderHeight);
        }
    }
    private void FitInfinity(InfinityRoot root, string? parent, string name, object target, List<SdfViewSnapshot> views,
        int index, SdfSky sky, uint width, uint height) {
        if (!root.Consumers.TryGetValue(key: name, value: out var consumer) || (consumer.Parent != parent)) {
            root.Consumers[name] = consumer = new InfinityConsumer(name, parent, target, root.PlanOf(parent: parent));
        }
        consumer.Target = target;
        consumer.Seen = true;
        consumer.Viewer = views[index];
        consumer.Width = width;
        consumer.Height = height;
        consumer.Tier = InfinityTier(sky: sky);
        UpdateInfinityFit(consumer: consumer, root: root);
        views[index] = views[index] with { SkyViews = consumer.Fit.BindingsOf(null, views[index].Camera, width, height, consumer.Tier) };
    }
    private void UpdateInfinityFit(InfinityRoot root, InfinityConsumer consumer) {
        var fit = consumer.Fit;

        if ((Runtime?.NodeOf(instance: fit.Consumer) is { } node) && node.TryReadCompleted(sample: consumer.Sample)) { fit.Report(parent: null, sample: consumer.Sample); }
        foreach (var view in fit.Plan.Views) {
            if ((Runtime?.NodeOf(instance: fit.ProducerOf(name: view.Name)) is { } child) && child.TryReadCompleted(sample: consumer.Sample)) {
                fit.Report(parent: view.Name, sample: consumer.Sample);
            }
        }
        consumer.Views.Begin();
        fit.Update(consumer.Viewer.Camera, consumer.Width, consumer.Height, consumer.Tier, consumer.Views,
            available: root.Entries.ContainsKey,
            readsOf: name => root.Entries.GetValueOrDefault(key: name)?.Screens?.Reads,
            tierOf: name => (root.Entries.GetValueOrDefault(key: name)?.Tier ?? consumer.Tier));
        _ = consumer.Views.TryPublish(instances: out _);
        foreach (var view in consumer.Views.Instances.Views) {
            var planned = fit.Plan.Views.First(predicate: layer => (fit.ProducerOf(name: layer.Name) == view.Name));
            var entry = root.Entries[planned.Name];
            var frame = fit.FrameOf(name: planned.Name);

            if (!entry.Indices.TryGetValue(key: fit.Consumer, value: out var index)) {
                index = 0;
                while (entry.Indices.ContainsValue(value: index)) { index++; }
                entry.Indices.Add(key: fit.Consumer, value: index);
                if (index == entry.Views.Count) { entry.Views.Add(item: default); }
            }
            var extent = view.OutputExtent!.Value;

            entry.Views[index] = new SdfViewSnapshot(Camera: (frame.Visible ? frame.Camera : consumer.Viewer.Camera), Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)) {
                Quality = WorldInfinityViewScene.QualityOf(levers: planned.Spec.Levers) with { IndirectMethod = consumer.Viewer.Quality.IndirectMethod },
                SkyViews = fit.BindingsOf(planned.Name, frame.Camera, ((uint)extent.Width), ((uint)extent.Height), entry.Tier),
            };
            m_infinityOutputs[view.Name] = new InfinityOutput(Entry: entry, Index: index);
        }
    }
    private void BeginInfinityConsumers(object target) {
        foreach (var root in m_infinityRoots.Values) {
            foreach (var consumer in root.Consumers.Values) {
                if (ReferenceEquals(objA: consumer.Target, objB: target)) { consumer.Seen = false; }
            }
        }
    }
    private void EndInfinityConsumers(object target) {
        foreach (var root in m_infinityRoots.Values) {
            foreach (var (name, consumer) in root.Consumers.ToArray()) {
                if (ReferenceEquals(objA: consumer.Target, objB: target) && !consumer.Seen) { _ = root.Consumers.Remove(key: name); }
            }
            PruneInfinityIndices(root: root);
        }
        RebuildInfinityOutputs();
        ReconcileViews();
    }
    private void RebuildInfinityOutputs() {
        m_infinityOutputs.Clear();
        foreach (var root in m_infinityRoots.Values) {
            foreach (var consumer in root.Consumers.Values) {
                foreach (var view in consumer.Views.Instances.Views) {
                    var planned = consumer.Fit.Plan.Views.FirstOrDefault(predicate: layer => (consumer.Fit.ProducerOf(name: layer.Name) == view.Name));

                    if ((planned is not null) && root.Entries.TryGetValue(key: planned.Name, value: out var entry) && entry.Indices.TryGetValue(key: consumer.Fit.Consumer, value: out var index)) {
                        m_infinityOutputs[view.Name] = new InfinityOutput(Entry: entry, Index: index);
                    }
                }
            }
        }
    }
    private static void PruneInfinityIndices(InfinityRoot root) {
        foreach (var entry in root.Entries.Values) {
            foreach (var name in entry.Indices.Keys.ToArray()) {
                if (!root.Consumers.ContainsKey(key: name)) { _ = entry.Indices.Remove(key: name); }
            }
            while ((entry.Views.Count != 0) && !entry.Indices.ContainsValue(value: (entry.Views.Count - 1))) { entry.Views.RemoveAt(index: (entry.Views.Count - 1)); }
        }
    }
    private void SetInfinityViews() {
        foreach (var root in m_infinityRoots.Values) {
            foreach (var consumer in root.Consumers.Values) {
                foreach (var view in consumer.Views.Instances.Views) {
                    if (m_infinityOutputs.ContainsKey(key: view.Name)) { m_views.Set(view: view); }
                }
            }
        }
    }
    private string? InfinityParent(InfinityEntry entry) {
        foreach (var (name, output) in m_infinityOutputs) {
            if (ReferenceEquals(objA: output.Entry, objB: entry)) { return name; }
        }
        return null;
    }
    private bool InfinityShown(SessionFeed feed) {
        while (feed.ParentFeed is { } parent) { feed = parent; }
        return ((feed.ParentInfinity is not { } infinity) || (InfinityParent(entry: infinity) is not null));
    }
}
