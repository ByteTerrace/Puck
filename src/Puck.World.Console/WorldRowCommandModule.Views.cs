using Puck.World.Protocol;

namespace Puck.World;

public sealed partial class WorldRowCommandModule {
    // Adds the views section's rows to the rest of the section table.
    private static Dictionary<string, RowSection> WithViewSections(Dictionary<string, RowSection> sections) {
        foreach (var (path, section) in ViewSections()) {
            sections.Add(
                key: path,
                value: section
            );
        }

        return sections;
    }
    // The views section's rows: its keyed layouts and graphs, and its keyless seat rig, seat control and post passes.
    private static Dictionary<string, RowSection> ViewSections() => new(comparer: StringComparer.Ordinal) {
        ["views.layouts"] = new RowSection(
        RowType: typeof(WorldViewLayout),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldViewLayout,
            toMutation: static (principal, layout) => new WorldMutation.UpsertViewLayout(
                Layout: layout,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveViewLayout(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldViewLayout,
            keyOf: static row => row.Name,
            select: static definition => definition.Views.Layouts
        )
    ),
        ["views.graphs"] = new RowSection(
        RowType: typeof(WorldViewGraph),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldViewGraph,
            toMutation: static (principal, graph) => new WorldMutation.UpsertViewGraph(
                Graph: graph,
                Principal: principal
            )
        ),
        Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveViewGraph(
            Name: name,
            Principal: principal
        )),
        Read: ReadRowByKey(
            info: WorldJsonContext.Default.WorldViewGraph,
            keyOf: static row => row.Name,
            select: static definition => (definition.Views.Graphs ?? [])
        )
    ),
        ["views.seatRig"] = new RowSection(
        RowType: typeof(WorldCameraProgram),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldCameraProgram,
            toMutation: static (principal, rig) => new WorldMutation.SetViewSeatRig(
                Principal: principal,
                SeatRig: rig
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldCameraProgram,
            select: static definition => definition.Views.SeatRig
        )
    ),
        ["views.seatControl"] = new RowSection(
        RowType: typeof(WorldSeatViewControl),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldSeatViewControl,
            toMutation: static (principal, control) => new WorldMutation.SetViewSeatControl(
                Principal: principal,
                SeatControl: control
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldSeatViewControl,
            select: static definition => definition.Views.SeatControl
        )
    ),
        // The post passes are one ordered list, set whole, so a pass is added, reordered or removed by writing the list.
        ["views.post"] = new RowSection(
        RowType: typeof(IReadOnlyList<WorldViewPostPass>),
        Upsert: Upsert(
            info: WorldJsonContext.Default.WorldViewPostPassList,
            toMutation: static (principal, post) => new WorldMutation.SetViewPost(
                Post: post,
                Principal: principal
            )
        ),
        Remove: null,
        Read: ReadRow(
            info: WorldJsonContext.Default.WorldViewPostPassList,
            select: static definition => (definition.Views.Post ?? [])
        )
    ),
    };
}
