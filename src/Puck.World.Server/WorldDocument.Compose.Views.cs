using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // The views section with the one field a single-field views mutation sets, composed against the section as it stands
    // when the mutation applies; the post passes are the whole ordered list, none written as absent.
    private static WorldViewDefaults ComposeViewField(WorldMutation mutation, WorldViewDefaults views) => mutation switch {
        WorldMutation.SetViewSeatRig m => (views with { SeatRigRaw = m.SeatRig }),
        WorldMutation.SetViewSeatControl m => (views with { SeatControlRaw = m.SeatControl }),
        WorldMutation.SetViewPost m => (views with { Post = ((m.Post.Count == 0) ? null : m.Post) }),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(mutation)),
    };
    private static bool TryComposeViewLayoutUpsert(WorldDefinition current, WorldMutation.UpsertViewLayout mutation, out WorldDefinition candidate, out string reason) {
        var views = current.Views;

        candidate = (current with {
            ViewsRaw = (views with {
                Layouts = Upsert(
                    item: mutation.Layout,
                    keyOf: static layout => layout.Name,
                    list: views.Layouts
                ),
            }),
        });
        reason = string.Empty;

        return true;
    }
    private static bool TryComposeViewLayoutRemove(WorldDefinition current, WorldMutation.RemoveViewLayout mutation, out WorldDefinition candidate, out string reason) {
        var views = current.Views;

        if (!Remove(
            key: mutation.Name,
            keyOf: static layout => layout.Name,
            list: views.Layouts,
            result: out var layouts
        )) {
            candidate = current;
            reason = $"no view layout named '{mutation.Name}'";

            return false;
        }

        candidate = (current with { ViewsRaw = (views with { Layouts = layouts }) });
        reason = string.Empty;

        return true;
    }
    private static bool TryComposeViewGraphUpsert(WorldDefinition current, WorldMutation.UpsertViewGraph mutation, out WorldDefinition candidate, out string reason) {
        var views = current.Views;

        candidate = (current with {
            ViewsRaw = (views with {
                Graphs = Upsert(
                    item: mutation.Graph,
                    keyOf: static graph => graph.Name,
                    list: (views.Graphs ?? [])
                ),
            }),
        });
        reason = string.Empty;

        return true;
    }
    private static bool TryComposeViewGraphRemove(WorldDefinition current, WorldMutation.RemoveViewGraph mutation, out WorldDefinition candidate, out string reason) {
        var views = current.Views;

        if (!Remove(
            key: mutation.Name,
            keyOf: static graph => graph.Name,
            list: (views.Graphs ?? []),
            result: out var graphs
        )) {
            candidate = current;
            reason = $"no views.graphs row named '{mutation.Name}'";

            return false;
        }

        candidate = (current with { ViewsRaw = (views with { Graphs = graphs }) });
        reason = string.Empty;

        return true;
    }
}
