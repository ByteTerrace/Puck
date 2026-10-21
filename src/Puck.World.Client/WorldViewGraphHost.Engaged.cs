using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost {
    // The crop each engaged pane shows this frame, in its source's pixels, by pass; a pane absent here shows its whole
    // source.
    private readonly Dictionary<string, SourcePixelRect> m_paneCrops = new(comparer: StringComparer.Ordinal);
    // The engaged panes placed this frame, and those the panes last published show.
    private readonly HashSet<string> m_engagedPlaced = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_engagedShown = new(comparer: StringComparer.Ordinal);

    /// <summary>Returns the panes the synthesized root places: the <c>views.graphs</c> instances a layout slot names
    /// (<see cref="WorldRootGraph.PanesOf"/>), then the source instance of every engageable screen standing in the world,
    /// in screen order, each the emulator view a seat engaged on that screen shows (<see cref="PlaceEngaged"/>). The
    /// emulator views come last, so they draw over every view and pane.</summary>
    /// <param name="views">The document's <c>views</c> section.</param>
    /// <param name="screens">The world's screens, or <see langword="null"/> for none.</param>
    /// <returns>The pane names.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> PanesOf(WorldViewDefaults views, WorldScreenMappingSet? screens) {
        var panes = WorldRootGraph.PanesOf(views: views);

        if (screens is null) {
            return panes;
        }

        List<string>? placed = null;

        foreach (var screen in screens.Screens) {
            if (
                screen.Route.Engageable &&
                (screens.InstanceOf(screen: screen.Index) is { } instance) &&
                !panes.Contains(value: instance) &&
                !(placed?.Contains(item: instance) ?? false)
            ) {
                (placed ??= [.. panes]).Add(item: instance);
            }
        }

        return (((IReadOnlyList<string>?)placed) ?? panes);
    }
    /// <summary>Places a screen's emulator view this frame: the synthesized root shows the screen's source instance over a
    /// seat's view, cropped, scaled to its display aspect and fitted whole and centred inside it, with the fill colour
    /// around it (<see cref="WorldEmulatorView.TryFit"/>), every source pixel a crisp cell when the screen samples its
    /// source <see cref="GpuSamplerFilter.Nearest"/>. A screen the root places no emulator view for, a source that has
    /// not rendered, and an instance already placed this frame are ignored.</summary>
    /// <param name="screen">The engaged screen's index.</param>
    /// <param name="frame">The seat's view, in normalized display coordinates.</param>
    /// <param name="displayWidth">The display's width, in pixels.</param>
    /// <param name="displayHeight">The display's height, in pixels.</param>
    /// <returns><see langword="true"/> when the root shows the emulator view this frame.</returns>
    public bool PlaceEngaged(int screen, NormalizedRect frame, uint displayWidth, uint displayHeight) {
        if (
            (Screens is not { } screens) ||
            (screens.InstanceOf(screen: screen) is not { } instance) ||
            (Row(screens: screens, screen: screen) is not { } row) ||
            (m_synthesized is not { Plan: not null } synthesized) ||
            !synthesized.Panes.Contains(value: instance) ||
            m_placements.ContainsKey(key: instance) ||
            !TryExtent(height: out var sourceHeight, instance: instance, width: out var sourceWidth) ||
            !WorldEmulatorView.TryFit(
                crop: out var crop,
                display: row.Route.Display,
                displayHeight: ((int)Math.Clamp(max: int.MaxValue, min: 1u, value: displayHeight)),
                displayWidth: ((int)Math.Clamp(max: int.MaxValue, min: 1u, value: displayWidth)),
                drawn: out var drawn,
                frame: frame,
                sourceHeight: sourceHeight,
                sourceWidth: sourceWidth
            )
        ) {
            return false;
        }

        m_placements.Add(
            key: instance,
            value: new RenderGraphPlacement(
                Crop: new System.Numerics.Vector4(
                    w: (((float)crop.Height) / sourceHeight),
                    x: (((float)crop.X) / sourceWidth),
                    y: (((float)crop.Y) / sourceHeight),
                    z: (((float)crop.Width) / sourceWidth)
                ),
                Fill: WorldEmulatorView.FillOf(display: row.Route.Display),
                Surround: new System.Numerics.Vector4(
                    w: frame.Height,
                    x: frame.X,
                    y: frame.Y,
                    z: frame.Width
                ),
                Height: drawn.Height,
                Left: drawn.X,
                Pixelated: (row.Filter == GpuSamplerFilter.Nearest),
                Sharpness: 0f,
                Shown: true,
                Top: drawn.Y,
                Width: drawn.Width
            )
        );
        m_paneCrops[instance] = crop;
        _ = m_engagedPlaced.Add(item: instance);
        m_footprints.Add(item: new RenderGraphFootprint(
            Consumer: WorldViewGraphs.MainInstance,
            Height: drawn.Height,
            Producer: instance,
            Width: drawn.Width
        ));

        return true;
    }
    /// <summary>Returns whether the panes last published show an instance as an engaged screen's emulator view
    /// (<see cref="PlaceEngaged"/>).</summary>
    /// <param name="instance">The source instance's name.</param>
    /// <returns><see langword="true"/> when a published pane shows it as an emulator view.</returns>
    public bool ShowsEngaged(string instance) => m_engagedShown.Contains(item: instance);

    private static WorldScreen? Row(WorldScreenMappingSet screens, int screen) {
        foreach (var row in screens.Screens) {
            if (row.Index == screen) {
                return row;
            }
        }

        return null;
    }
    // Clears the engaged panes a frame placed, before the next frame places its own.
    private void ResetEngaged() {
        m_paneCrops.Clear();
        m_engagedPlaced.Clear();
    }
    // Hands the engaged panes this frame placed to the published set, when the panes are published.
    private void PublishEngaged() {
        m_engagedShown.Clear();

        foreach (var instance in m_engagedPlaced) {
            if (TryGetPane(instance: instance, mapping: out _)) {
                _ = m_engagedShown.Add(item: instance);
            }
        }
    }
    // The extent of what an instance's image shows, in the pixels its pane's mapping counts: the source's own where it
    // differs from the rendered extent (IWorldViewScenes.TrySourceExtent), else the extent the latest schedule rendered it
    // at.
    private bool TryExtent(string instance, out int width, out int height) {
        if (
            (ViewScenes is { } scenes) &&
            scenes.TrySourceExtent(
                height: out height,
                instance: instance,
                width: out width
            )
        ) {
            return true;
        }

        width = 0;
        height = 0;

        if (m_runtime is not { Latest: { } latest } runtime) {
            return false;
        }

        var index = runtime.Instances.IndexOf(name: instance);

        if (
            (((uint)index) >= ((uint)latest.Instances.Count)) ||
            (latest.Instances[index] is not { Width: > 0, Height: > 0 } row) ||
            !string.Equals(
                a: row.Instance,
                b: instance,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            return false;
        }

        width = row.Width;
        height = row.Height;

        return true;
    }
}
