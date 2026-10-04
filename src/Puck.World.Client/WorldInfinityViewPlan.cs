using Puck.Hosting;
using Puck.SdfVm.Views;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>One infinity view the viewer's world renders: a planned <c>sdf.world</c> instance.</summary>
/// <param name="Name">The instance name: <c>sky$&lt;layer&gt;</c> for a view the viewer's own sky shows
/// (<see cref="WorldViewNames.Sky"/>), and the name of the view whose world's sky shows it joined with the layer for a
/// deeper one (<see cref="WorldViewNames.NestedSky"/>).</param>
/// <param name="Parent">The instance whose world's sky shows it, or <see langword="null"/> for the viewer's own.</param>
/// <param name="Depth">How many worlds down it is, 1 for the viewer's own sky.</param>
/// <param name="Spec">What it renders.</param>
public sealed record WorldInfinityView(string Name, string? Parent, int Depth, InfinityViewSpec Spec);
/// <summary>A view the plan does not render, whose layer draws its fallback colour instead.</summary>
/// <param name="Parent">The instance whose composite draws the fallback, or <see langword="null"/> for the viewer's own.</param>
/// <param name="Spec">The view; its <see cref="InfinityViewSpec.Fallback"/> is the colour.</param>
/// <param name="Reason">Why it is not rendered, by name.</param>
public sealed record WorldInfinityFallback(string? Parent, InfinityViewSpec Spec, string Reason);
/// <summary>
/// The infinity views a world renders and the ones it does not. A view shows another world, or far geometry, from
/// its anchor; the world it shows has a sky of its own, which may show views in turn, so the plan nests them to the
/// graph's depth (<see cref="RenderGraphInstanceSet.NestingDepth"/>) and a view past it draws its layer's
/// <see cref="InfinityViewSpec.Fallback"/> colour in place of an instance. A world carries at most
/// <see cref="MaxViews"/> infinity views at once, however deep: each is a residency whose tables take aperture bytes the
/// smallest supported GPU's host-visible heap cannot spare, so a view past the cap draws its fallback too, and a document
/// authoring more than the cap is refused (<see cref="TryCheck"/>).
/// </summary>
public sealed class WorldInfinityViewPlan {
    /// <summary>The most infinity views a world carries: instances planned at any depth, and views authored in one
    /// world's sky.</summary>
    public const int MaxViews = SdfSky.MaxInfinityViews;

    private WorldInfinityViewPlan(IReadOnlyList<WorldInfinityView> views, IReadOnlyList<WorldInfinityFallback> fallbacks) {
        Views = views;
        Fallbacks = fallbacks;
    }

    /// <summary>Gets the plan with no view.</summary>
    public static WorldInfinityViewPlan Empty { get; } = new(fallbacks: [], views: []);
    /// <summary>Gets the views that render, each after the view whose world's sky shows it.</summary>
    public IReadOnlyList<WorldInfinityView> Views { get; }
    /// <summary>Gets the views that draw their fallback.</summary>
    public IReadOnlyList<WorldInfinityFallback> Fallbacks { get; }

    /// <summary>Returns the existing plan below one shown world for another camera of that same world. It keeps the
    /// planned layer names and cap decisions, so the camera shares those residencies and cannot open another subtree.</summary>
    /// <param name="parent">The shown world's planned layer path, or null for this plan.</param>
    /// <returns>The descendants with the direct children's parent made local to the consuming camera.</returns>
    public WorldInfinityViewPlan Below(string? parent) {
        if (parent is null) { return this; }
        var kept = new HashSet<string>(StringComparer.Ordinal) { parent };
        var views = new List<WorldInfinityView>();
        foreach (var view in Views) {
            if ((view.Parent is null) || !kept.Contains(view.Parent)) { continue; }
            _ = kept.Add(view.Name);
            views.Add(view with { Parent = ((view.Parent == parent) ? null : view.Parent) });
        }
        var fallbacks = Fallbacks.Where(fallback => (fallback.Parent is not null) && kept.Contains(fallback.Parent))
            .Select(fallback => fallback with { Parent = ((fallback.Parent == parent) ? null : fallback.Parent) }).ToArray();
        return new WorldInfinityViewPlan(views, fallbacks);
    }

    /// <summary>Checks the infinity views one world's sky authors: every record sound, names distinct, no more than the
    /// cap.</summary>
    /// <param name="specs">The views one world authors.</param>
    /// <param name="reason">The first fault by name, or empty.</param>
    /// <param name="cap">The most views, <see cref="MaxViews"/> unless a test says otherwise.</param>
    /// <returns><see langword="true"/> when the views are admissible.</returns>
    public static bool TryCheck(IReadOnlyList<InfinityViewSpec> specs, out string reason, int cap = MaxViews) {
        ArgumentNullException.ThrowIfNull(argument: specs);

        if (specs.Count > cap) {
            reason = $"the world authors {specs.Count} infinity views; a world carries at most {cap}, one residency each.";

            return false;
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var spec in specs) {
            if (!spec.TryValidate(reason: out reason)) {
                return false;
            }
            if (!names.Add(item: spec.Name)) {
                reason = $"two infinity views are named '{spec.Name}'.";

                return false;
            }
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Plans the views of a world.</summary>
    /// <param name="roots">The views the viewer's own sky shows.</param>
    /// <param name="childrenOf">Answers the views the sky of the world a planned view shows itself shows, given the
    /// instance name and the view; nothing for far geometry, whose residency holds no sky.</param>
    /// <param name="nestingDepth">How many worlds down views render (<see cref="RenderGraphInstanceSet.NestingDepth"/>);
    /// a view deeper draws its fallback.</param>
    /// <param name="cap">The most instances, <see cref="MaxViews"/> unless a test says otherwise.</param>
    /// <returns>The plan: views in breadth order, so a view that fits the cap is never displaced by a deeper one.</returns>
    public static WorldInfinityViewPlan Resolve(IReadOnlyList<InfinityViewSpec> roots, Func<string, InfinityViewSpec, IReadOnlyList<InfinityViewSpec>> childrenOf, int nestingDepth, int cap = MaxViews) {
        ArgumentNullException.ThrowIfNull(argument: roots);
        ArgumentNullException.ThrowIfNull(argument: childrenOf);

        var views = new List<WorldInfinityView>();
        var fallbacks = new List<WorldInfinityFallback>();
        var pending = new Queue<(string? Parent, int Depth, InfinityViewSpec Spec)>(collection: roots.Select(selector: static spec => (((string?)null), 1, spec)));

        while (pending.TryDequeue(result: out var next)) {
            if (next.Depth > nestingDepth) {
                fallbacks.Add(item: new WorldInfinityFallback(
                    Parent: next.Parent,
                    Reason: $"'{next.Spec.Name}' lies {next.Depth} worlds down; views render to the nesting depth, {nestingDepth}.",
                    Spec: next.Spec
                ));

                continue;
            }
            if (views.Count >= cap) {
                fallbacks.Add(item: new WorldInfinityFallback(
                    Parent: next.Parent,
                    Reason: $"'{next.Spec.Name}' would be infinity view {(views.Count + 1)}; a world carries at most {cap}.",
                    Spec: next.Spec
                ));

                continue;
            }

            var name = ((next.Parent is null)
                ? WorldViewNames.Sky(layer: next.Spec.Name)
                : WorldViewNames.NestedSky(layer: next.Spec.Name, view: next.Parent));

            views.Add(item: new WorldInfinityView(
                Depth: next.Depth,
                Name: name,
                Parent: next.Parent,
                Spec: next.Spec
            ));

            if (next.Spec.Kind == InfinityViewKind.World) {
                foreach (var child in childrenOf(arg1: name, arg2: next.Spec)) {
                    pending.Enqueue(item: (name, (next.Depth + 1), child));
                }
            }
        }

        return new WorldInfinityViewPlan(
            fallbacks: fallbacks,
            views: views
        );
    }
    /// <summary>Describes the plan as <c>world.budget</c> reports it: the live count against the cap, and each view that
    /// draws its fallback with the reason.</summary>
    /// <param name="cap">The cap.</param>
    /// <returns>One line, then one per fallback.</returns>
    public IReadOnlyList<string> Describe(int cap = MaxViews) => [
        $"infinity views: {Views.Count} of {cap}",
        .. Fallbacks.Select(selector: static fallback => $"  fallback: {fallback.Reason}"),
    ];
}
