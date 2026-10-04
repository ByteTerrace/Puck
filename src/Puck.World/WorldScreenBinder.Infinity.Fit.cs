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
            || !m_instanceHost.TryEndpoint(WorldInstanceHost.BootInstanceName, out var endpoint) || (endpoint is null)) { return; }
        BeginInfinityConsumers(presenter);
        var root = InfinityRootOf(endpoint.Mirror, WorldInstanceHost.BootInstanceName, 0, WorldViewGraphs.WorldInstance);
        for (var index = 0; index < Math.Min(m_hostViewCount, views.Count); index++) {
            if (presenter.TryRoutedView(index, out _, out _)) { continue; }
            FitInfinity(root, null, ((index == 0) ? WorldViewGraphs.WorldInstance : WorldViewNames.World(index + 1)), presenter, views, index, sky,
                ((uint)Math.Max(1, width * views[index].Region.Width)), ((uint)Math.Max(1, height * views[index].Region.Height)));
        }
        foreach (var (name, index) in m_cameraViewIndices) {
            if (((uint)index >= (uint)views.Count) || !m_cameraViews.TryGetValue(name, out var camera)) { continue; }
            FitInfinity(root, null, name, presenter, views, index, sky, camera.Row.RenderWidth, camera.Row.RenderHeight);
        }
        EndInfinityConsumers(presenter);
    }

    private void FitSessionSky(SessionFeed feed, List<SdfViewSnapshot> views, SdfSky sky, uint width, uint height) {
        BeginInfinityConsumers(feed);
        var root = InfinityRootOf(feed.Mirror, feed.InstanceName, feed.Depth, feed.RegistrationName);
        if (views.Count != 0) { FitInfinity(root, null, feed.RegistrationName, feed, views, 0, sky, width, height); }
        FitNestedCameraSkies(feed.Nested, root, null, feed, views, sky);
        EndInfinityConsumers(feed);
    }

    private void FitRoutedSky(WorldRoutedScene scene, List<SdfViewSnapshot> views, SdfSky sky, uint width, uint height) {
        BeginInfinityConsumers(scene);
        var root = InfinityRootOf(scene.Endpoint.Mirror, scene.Endpoint.Identity, 0, RoutedViewName(scene));
        if (Presenter is { } presenter) {
            for (var index = 0; index < m_hostViewCount; index++) {
                if (presenter.TryRoutedView(index, out var routed, out var destination)
                    && ReferenceEquals(routed, scene) && ((uint)destination < (uint)views.Count)) {
                    var view = views[destination];
                    FitInfinity(root, null, ((index == 0) ? WorldViewGraphs.WorldInstance : WorldViewNames.World(index + 1)), scene, views, destination, sky,
                        ((uint)Math.Max(1, width * view.Region.Width)), ((uint)Math.Max(1, height * view.Region.Height)));
                }
            }
        }
        foreach (var feed in m_feeds) {
            if ((feed.WindowRoute.Window is not { } window) || !ReferenceEquals(window.Scene, scene)
                || ((uint)window.Index >= (uint)views.Count)) { continue; }
            var nested = InfinityRootOf(feed.Mirror, feed.InstanceName, feed.Depth, feed.RegistrationName);
            FitInfinity(nested, null, feed.RegistrationName, scene, views, window.Index, sky,
                ((uint)(feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth)),
                ((uint)(feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight)));
            FitNestedCameraSkies(feed.Nested, nested, null, scene, views, sky);
        }
        FitNestedCameraSkies(m_routedScreens.GetValueOrDefault(scene), root, null, scene, views, sky);
        EndInfinityConsumers(scene);
    }

    private void FilmInfinity(InfinityEntry entry, List<SdfViewSnapshot> views, SdfSky sky, uint width, uint height) {
        entry.Tier = InfinityTier(sky);
        // The entry's consumer indices precede the camera screens. Their child fits share this plan's existing scenes.
        foreach (var consumer in entry.Root.Consumers.Values) {
            if (!entry.Indices.TryGetValue(consumer.Fit.Consumer, out var index) || ((uint)index >= (uint)views.Count)) { continue; }
            UpdateInfinityFit(entry.Root, consumer);
            var camera = views[index].Camera;
            views[index] = views[index] with { SkyViews = consumer.Fit.BindingsOf(entry.Planned.Name, camera,
                ((uint)Math.Max(1, width)), ((uint)Math.Max(1, height)), entry.Tier) };
        }
        BeginInfinityConsumers(entry);
        m_nestedFilms.Begin(entry);
        if (entry.Screens is { } level) {
            FilmLevel(level, entry.Mirror, entry, views);
            FitNestedCameraSkies(level, entry.Root, entry.Planned.Name, entry, views, sky);
        }
        m_nestedFilms.End(entry);
        EndInfinityConsumers(entry);
    }

    private void FitNestedCameraSkies(WorldNestedScreens<SessionFeed>? level, InfinityRoot root, string? parent,
        object target, List<SdfViewSnapshot> views, SdfSky sky) {
        if (level is null) { return; }
        foreach (var camera in level.Cameras) {
            if (!m_nestedFilms.TryGet(camera.Name, out var film) || !ReferenceEquals(film.Target, target)
                || ((uint)film.Index >= (uint)views.Count)) { continue; }
            FitInfinity(root, parent, camera.Name, target, views, film.Index, sky, camera.Camera.RenderWidth, camera.Camera.RenderHeight);
        }
    }

    private void FitInfinity(InfinityRoot root, string? parent, string name, object target, List<SdfViewSnapshot> views,
        int index, SdfSky sky, uint width, uint height) {
        if (!root.Consumers.TryGetValue(name, out var consumer) || (consumer.Parent != parent)) {
            root.Consumers[name] = consumer = new InfinityConsumer(name, parent, target, root.PlanOf(parent));
        }
        consumer.Target = target;
        consumer.Seen = true;
        consumer.Viewer = views[index];
        consumer.Width = width;
        consumer.Height = height;
        consumer.Tier = InfinityTier(sky);
        UpdateInfinityFit(root, consumer);
        views[index] = views[index] with { SkyViews = consumer.Fit.BindingsOf(null, views[index].Camera, width, height, consumer.Tier) };
    }

    private void UpdateInfinityFit(InfinityRoot root, InfinityConsumer consumer) {
        var fit = consumer.Fit;
        if ((Runtime?.NodeOf(fit.Consumer) is { } node) && node.TryReadCompleted(consumer.Sample)) { fit.Report(null, consumer.Sample); }
        foreach (var view in fit.Plan.Views) {
            if ((Runtime?.NodeOf(fit.ProducerOf(view.Name)) is { } child) && child.TryReadCompleted(consumer.Sample)) {
                fit.Report(view.Name, consumer.Sample);
            }
        }
        consumer.Views.Begin();
        fit.Update(consumer.Viewer.Camera, consumer.Width, consumer.Height, consumer.Tier, consumer.Views,
            available: root.Entries.ContainsKey,
            readsOf: name => root.Entries.GetValueOrDefault(name)?.Screens?.Reads,
            tierOf: name => (root.Entries.GetValueOrDefault(name)?.Tier ?? consumer.Tier));
        _ = consumer.Views.TryPublish(out _);
        foreach (var view in consumer.Views.Instances.Views) {
            var planned = fit.Plan.Views.First(layer => fit.ProducerOf(layer.Name) == view.Name);
            var entry = root.Entries[planned.Name];
            var frame = fit.FrameOf(planned.Name);
            if (!entry.Indices.TryGetValue(fit.Consumer, out var index)) {
                index = 0;
                while (entry.Indices.ContainsValue(index)) { index++; }
                entry.Indices.Add(fit.Consumer, index);
                if (index == entry.Views.Count) { entry.Views.Add(default); }
            }
            var extent = view.OutputExtent!.Value;
            entry.Views[index] = new SdfViewSnapshot((frame.Visible ? frame.Camera : consumer.Viewer.Camera), new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f)) {
                Quality = WorldInfinityViewScene.QualityOf(planned.Spec.Levers) with { IndirectMethod = consumer.Viewer.Quality.IndirectMethod },
                SkyViews = fit.BindingsOf(planned.Name, frame.Camera, ((uint)extent.Width), ((uint)extent.Height), entry.Tier),
            };
            m_infinityOutputs[view.Name] = new InfinityOutput(entry, index);
        }
    }

    private void BeginInfinityConsumers(object target) {
        foreach (var root in m_infinityRoots.Values) {
            foreach (var consumer in root.Consumers.Values) {
                if (ReferenceEquals(consumer.Target, target)) { consumer.Seen = false; }
            }
        }
    }

    private void EndInfinityConsumers(object target) {
        foreach (var root in m_infinityRoots.Values) {
            foreach (var (name, consumer) in root.Consumers.ToArray()) {
                if (ReferenceEquals(consumer.Target, target) && !consumer.Seen) { _ = root.Consumers.Remove(name); }
            }
            PruneInfinityIndices(root);
        }
        RebuildInfinityOutputs();
        ReconcileViews();
    }

    private void RebuildInfinityOutputs() {
        m_infinityOutputs.Clear();
        foreach (var root in m_infinityRoots.Values) {
            foreach (var consumer in root.Consumers.Values) {
                foreach (var view in consumer.Views.Instances.Views) {
                    var planned = consumer.Fit.Plan.Views.FirstOrDefault(layer => consumer.Fit.ProducerOf(layer.Name) == view.Name);
                    if ((planned is not null) && root.Entries.TryGetValue(planned.Name, out var entry) && entry.Indices.TryGetValue(consumer.Fit.Consumer, out var index)) {
                        m_infinityOutputs[view.Name] = new InfinityOutput(entry, index);
                    }
                }
            }
        }
    }

    private static void PruneInfinityIndices(InfinityRoot root) {
        foreach (var entry in root.Entries.Values) {
            foreach (var name in entry.Indices.Keys.ToArray()) {
                if (!root.Consumers.ContainsKey(name)) { _ = entry.Indices.Remove(name); }
            }
            while ((entry.Views.Count != 0) && !entry.Indices.ContainsValue(entry.Views.Count - 1)) { entry.Views.RemoveAt(entry.Views.Count - 1); }
        }
    }

    private void SetInfinityViews() {
        foreach (var root in m_infinityRoots.Values) {
            foreach (var consumer in root.Consumers.Values) {
                foreach (var view in consumer.Views.Instances.Views) {
                    if (m_infinityOutputs.ContainsKey(view.Name)) { m_views.Set(view); }
                }
            }
        }
    }

    private string? InfinityParent(InfinityEntry entry) {
        foreach (var (name, output) in m_infinityOutputs) {
            if (ReferenceEquals(output.Entry, entry)) { return name; }
        }
        return null;
    }

    private bool InfinityShown(SessionFeed feed) {
        while (feed.ParentFeed is { } parent) { feed = parent; }
        return (feed.ParentInfinity is not { } infinity) || (InfinityParent(infinity) is not null);
    }
}
