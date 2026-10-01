using Puck.Abstractions.Presentation;

namespace Puck.World.Client;

/// <summary>The allocation envelope of each layout occupant. Selection and easing belong to
/// <see cref="WorldViewComposer"/>; these extents reserve every rect that composition can place without resizing a node.</summary>
public static class WorldViewOutputRegions {
    /// <summary>Returns the largest width and height a view ordinal occupies across the authored layouts and, when
    /// there is no catch-all layout, the built-in seat ladder. The origin is zero: this sizes an image, not a placement.</summary>
    /// <param name="views">The world's view defaults.</param>
    /// <param name="view">The zero-based view ordinal.</param>
    /// <returns>The view's output envelope.</returns>
    public static NormalizedRect View(WorldViewDefaults views, int view) {
        ArgumentNullException.ThrowIfNull(argument: views);
        ArgumentOutOfRangeException.ThrowIfNegative(value: view);
        var width = 0f;
        var height = 0f;
        var catchall = false;

        for (var layoutIndex = 0; (layoutIndex < views.Layouts.Count); layoutIndex++) {
            var layout = views.Layouts[layoutIndex];

            catchall |= (layout.SeatCount == 0);
            var ordinal = 0;

            for (var slotIndex = 0; (slotIndex < layout.Slots.Count); slotIndex++) {
                var slot = layout.Slots[slotIndex];

                if (slot.Instance is not null) { continue; }
                if (ordinal++ == view) {
                    width = MathF.Max(x: width, y: slot.Width);
                    height = MathF.Max(x: height, y: slot.Height);
                }
            }
            // An instance-only composition still films the spectator beneath its panes.
            if ((ordinal == 0) && (view == 0)) { width = 1f; height = 1f; }
        }
        if (!catchall) {
            for (var count = 1; (count <= PlayerRoster.MaxSlots); count++) {
                if (view >= count) { continue; }
                var region = WorldFramePresenter.LayoutRegion(count: count, index: view);

                width = MathF.Max(x: width, y: region.Width);
                height = MathF.Max(x: height, y: region.Height);
            }
        }
        return new NormalizedRect(Height: height, Width: width, X: 0f, Y: 0f);
    }
    /// <summary>Returns the largest width and height of every slot placing one pane, or the supplied region when no
    /// authored slot names it.</summary>
    /// <param name="views">The world's view defaults.</param>
    /// <param name="instance">The pane's instance name.</param>
    /// <param name="region">The current placement, used when no layout declares the pane.</param>
    /// <returns>The pane's output envelope.</returns>
    public static NormalizedRect Pane(WorldViewDefaults views, string instance, NormalizedRect region) {
        ArgumentNullException.ThrowIfNull(argument: views);
        var width = 0f;
        var height = 0f;

        for (var layoutIndex = 0; (layoutIndex < views.Layouts.Count); layoutIndex++) {
            var layout = views.Layouts[layoutIndex];

            for (var slotIndex = 0; (slotIndex < layout.Slots.Count); slotIndex++) {
                var slot = layout.Slots[slotIndex];

                if (!string.Equals(a: slot.Instance, b: instance, comparisonType: StringComparison.Ordinal)) { continue; }
                width = MathF.Max(x: width, y: slot.Width);
                height = MathF.Max(x: height, y: slot.Height);
            }
        }
        return (((width > 0f) && (height > 0f))
            ? new NormalizedRect(Height: height, Width: width, X: 0f, Y: 0f)
            : region);
    }
}
