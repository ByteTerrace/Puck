using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
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
    private static bool TryComposeViewPipelineUpsert(WorldDefinition current, WorldMutation.UpsertViewPipeline mutation, out WorldDefinition candidate, out string reason) {
        var views = current.Views;

        candidate = (current with {
            ViewsRaw = (views with {
                Pipelines = Upsert(
                    item: mutation.Pipeline,
                    keyOf: static pipeline => pipeline.Name,
                    list: views.Pipelines
                ),
            }),
        });
        reason = string.Empty;

        return true;
    }
    private static bool TryComposeViewPipelineRemove(WorldDefinition current, WorldMutation.RemoveViewPipeline mutation, out WorldDefinition candidate, out string reason) {
        var views = current.Views;

        if (!Remove(
            key: mutation.Name,
            keyOf: static pipeline => pipeline.Name,
            list: views.Pipelines,
            result: out var pipelines
        )) {
            candidate = current;
            reason = $"no views.pipelines row named '{mutation.Name}'";

            return false;
        }

        candidate = (current with { ViewsRaw = (views with { Pipelines = pipelines }) });
        reason = string.Empty;

        return true;
    }
}
