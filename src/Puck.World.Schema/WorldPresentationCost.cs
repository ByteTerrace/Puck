using System.Globalization;

namespace Puck.World;

/// <summary>One frame-graph instance's price in the cost report: its extent ceiling, its rate and its passes.
/// <para>
/// Its extent follows its on-screen footprint each frame, so a document prices only its ceiling: one display. A render
/// costs its passes times its pixels, so the instance costs at most <see cref="PassDisplaysPerFrame"/> displays of
/// pass-pixels each presented frame.
/// </para>
/// </summary>
/// <param name="Name">The <c>views.graphs</c> row.</param>
/// <param name="Source">The graph document it renders.</param>
/// <param name="Divisor">The frames per render it declares, or <see langword="null"/> when it declares a rate in
/// hertz.</param>
/// <param name="Hertz">The renders a second it declares at most, or <see langword="null"/> when it declares a
/// divisor.</param>
/// <param name="Passes">The passes one render records, or <see langword="null"/> when its graph was not planned.</param>
/// <param name="Issue">Why <paramref name="Passes"/> is unknown, or <see langword="null"/>.</param>
public sealed record WorldGraphInstanceCost(string Name, string Source, int? Divisor, int? Hertz, int? Passes, string? Issue) {
    /// <summary>Gets the most pass-pixels a presented frame spends on the instance, in displays: its passes over its
    /// divisor, or <see langword="null"/> when its passes are unknown or its rate is in hertz, whose frames depend on
    /// the display's rate.</summary>
    public double? PassDisplaysPerFrame => (((Passes is { } passes) && (Divisor is { } divisor))
        ? (((double)passes) / divisor)
        : null
    );

    /// <summary>Describes the instance's rate: <c>every frame</c>, <c>1/N frames</c>, or <c>N Hz</c>.</summary>
    /// <returns>The description.</returns>
    public string DescribeRate() => ((Hertz is { } hertz)
        ? string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{hertz} Hz"
        )
        : (((Divisor ?? 1) == 1)
            ? "every frame"
            : string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"1/{Divisor} frames"
            ))
    );
}
/// <summary>One bound parameter's price in the cost report: the bytes a <c>views.graphs</c> row's
/// <c>parameter &lt;pass&gt;.&lt;member&gt;</c> owes its pass, computed from the document alone, so every host prices
/// a document alike.
/// <para>
/// A literal is written once, when its graph installs, and owes nothing afterwards. A state binding owes its bytes on
/// every tick that moves its row: a scalar field its <see cref="ScalarBytes"/>, an array one
/// <see cref="ArrayElementBytes"/> row of the World block per element its bound row presents
/// (<see cref="WorldBoundRow"/>). A scalar binding whose cell carries a value-over-time trait it reads moving (an
/// advance, or an eased dynamics follower read without <c>.$target</c>) presents a value interpolated at every frame's
/// fraction, so it also owes its bytes on each presented frame between ticks; a row read whole never interpolates.
/// </para>
/// </summary>
/// <param name="Pipeline">The <c>views.graphs</c> row that binds it.</param>
/// <param name="Pass">The pass it binds.</param>
/// <param name="Member">The pass's config field or array it binds.</param>
/// <param name="Token">The authored value: a <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c> token, or the literal's
/// number.</param>
/// <param name="Elements">The values it writes: 0 for a literal, 1 for a scalar binding, and the bound row's element
/// count for an array.</param>
/// <param name="BytesPerTick">The bytes it owes on a tick that moves its row.</param>
/// <param name="BytesPerFrame">The bytes it owes on each presented frame between ticks.</param>
public sealed record WorldBindingCost(string Pipeline, string Pass, string Member, string Token, int Elements, long BytesPerTick, long BytesPerFrame) {
    /// <summary>The bytes a scalar config field holds: one 32-bit float, int or uint.</summary>
    public const int ScalarBytes = 4;
    /// <summary>The bytes an array element holds in its pass's World block: one 16-byte constant-buffer row, the stride
    /// <c>ShaderPipelineParameterLayout.WriteArray</c> writes it at.</summary>
    public const int ArrayElementBytes = 16;

    /// <summary>Gets the binding's name, <c>&lt;pass&gt;.&lt;member&gt;</c>.</summary>
    public string Binding => $"{Pass}.{Member}";

    /// <summary>Prices every bound parameter a document's <c>views.graphs</c> rows declare, in row order, then pass and
    /// member in ordinal order.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>One line per bound parameter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldBindingCost> Measure(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var lines = new List<WorldBindingCost>();

        foreach (var graph in (definition.Views.Graphs ?? [])) {
            if (graph?.Parameters is not { } parameters) {
                continue;
            }

            foreach (var (pass, fields) in parameters.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
                foreach (var (member, value) in (fields ?? new Dictionary<string, BindableScalar>()).OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
                    lines.Add(item: Price(
                        definition: definition,
                        member: member,
                        pass: pass,
                        pipeline: graph.Name,
                        value: value
                    ));
                }
            }
        }

        return lines;
    }

    private static WorldBindingCost Price(WorldDefinition definition, string pipeline, string pass, string member, BindableScalar value) {
        if (value.State is not { } binding) {
            return new WorldBindingCost(
                BytesPerFrame: 0,
                BytesPerTick: 0,
                Elements: 0,
                Member: member,
                Pass: pass,
                Pipeline: pipeline,
                Token: (value.Binding ?? (value.Literal?.ToString(provider: CultureInfo.InvariantCulture) ?? string.Empty))
            );
        }

        // A keyless token naming a keyed row binds the whole row, which only an array reads.
        if (
            (binding.Key is null) &&
            WorldBoundRow.TryResolve(
            definition: definition,
            length: out var length,
            row: out _,
            rowName: binding.Row
        )
        ) {
            return new WorldBindingCost(
                BytesPerFrame: 0,
                BytesPerTick: (((long)length) * ArrayElementBytes),
                Elements: length,
                Member: member,
                Pass: pass,
                Pipeline: pipeline,
                Token: value.Binding!
            );
        }

        return new WorldBindingCost(
            BytesPerFrame: (Interpolates(
                binding: binding,
                definition: definition
            )
                ? ScalarBytes
                : 0),
            BytesPerTick: ScalarBytes,
            Elements: 1,
            Member: member,
            Pass: pass,
            Pipeline: pipeline,
            Token: value.Binding!
        );
    }
    // Whether the state mirror presents the bound cell interpolated between ticks: its effective trait advances, or it
    // eases and the binding reads the follower rather than the stored truth.
    private static bool Interpolates(StateBinding binding, WorldDefinition definition) {
        if (
            !definition.StateCatalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: binding.Row
        ) ||
            (handle.Ordinal >= definition.State.Count)
        ) {
            return false;
        }

        var row = definition.State[handle.Ordinal];
        var cell = (((binding.Key is { } key) && CellName.TryParse(
            candidate: key,
            name: out var cellKey,
            reason: out _
        ))
            ? StateRows.FindCell(
                cells: row.Cells,
                key: cellKey
            )
            : null);
        var behavior = EffectiveBehavior.Resolve(
            cell: cell,
            row: row
        );

        return (
            (behavior.Advance is { PerSecondNumerator: not 0L }) ||
            (!binding.Target && (behavior.Dynamics is not null))
        );
    }
}
/// <summary>The cost report's presentation dimension: every frame-graph instance a document declares with the
/// scheduler's price ceiling, priced in pass-pixels, and every bound parameter with its ceilings, priced in bytes per
/// tick and bytes per frame. It is separate from the simulation's cycle bound, computed from the document alone, and
/// never priced in time.</summary>
/// <param name="Instances">One line per <c>views.graphs</c> row, in row order.</param>
/// <param name="PassPixelsPerFrame">The authored price ceiling on the instances the display does not show directly, or
/// 0 for none.</param>
public sealed record WorldPresentationCost(IReadOnlyList<WorldGraphInstanceCost> Instances, long PassPixelsPerFrame) {
    /// <summary>The issue a line carries when the analysis read no graph source.</summary>
    public const string UnplannedIssue = "the document analysis plans no graph source";

    /// <summary>Gets the dimension of a document that declares no graph instance.</summary>
    public static WorldPresentationCost None { get; } = new(
        Instances: [],
        PassPixelsPerFrame: 0
    );
    /// <summary>Gets every bound parameter's price, in the order <see cref="WorldBindingCost.Measure"/> lists
    /// them.</summary>
    public IReadOnlyList<WorldBindingCost> Bindings { get; init; } = [];
    /// <summary>Gets the authored ceiling on <see cref="BytesPerTick"/>, or 0 for none.</summary>
    public long BytesPerTickCeiling { get; init; }
    /// <summary>Gets the authored ceiling on <see cref="BytesPerFrame"/>, or 0 for none.</summary>
    public long BytesPerFrameCeiling { get; init; }
    /// <summary>Gets the bytes every binding together owes on a tick that moves every bound row.</summary>
    public long BytesPerTick => Bindings.Sum(selector: static line => line.BytesPerTick);
    /// <summary>Gets the bytes every binding together owes on each presented frame between ticks.</summary>
    public long BytesPerFrame => Bindings.Sum(selector: static line => line.BytesPerFrame);

    /// <summary>Returns the refusal a document's bindings earn against its ceilings: the first binding, in
    /// <see cref="WorldBindingCost.Measure"/> order, whose bytes carry the running total past an authored ceiling,
    /// named with its graph, or <see langword="null"/> when every ceiling holds.</summary>
    /// <param name="bindings">The document's priced bindings.</param>
    /// <param name="budget">The document's ceilings, or <see langword="null"/> for none.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    public static string? CeilingRefusal(IReadOnlyList<WorldBindingCost> bindings, WorldViewGraphBudget? budget) {
        ArgumentNullException.ThrowIfNull(argument: bindings);

        foreach (var (ceiling, member, unit, bytesOf) in ((ReadOnlySpan<(long, string, string, Func<WorldBindingCost, long>)>)[
            ((budget?.BytesPerTick ?? 0), "bytesPerTick", "tick", static line => line.BytesPerTick),
            ((budget?.BytesPerFrame ?? 0), "bytesPerFrame", "frame", static line => line.BytesPerFrame),
        ])) {
            if (ceiling <= 0) {
                continue;
            }

            var total = 0L;

            foreach (var line in bindings) {
                total += bytesOf(arg: line);

                if (total > ceiling) {
                    return string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"views.graphBudget.{member} {ceiling}: graph '{line.Pipeline}' binding '{line.Binding}' ({line.Token}, {bytesOf(arg: line)} bytes a {unit}) brings the bound parameters to {total} bytes a {unit}."
                    );
                }
            }
        }

        return null;
    }
    /// <summary>Prices a document's graph instances.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="passes">Plans a row's graph, returning its pass count or why it has none; <see langword="null"/>
    /// prices every line with <see cref="UnplannedIssue"/>.</param>
    /// <returns>The dimension.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldPresentationCost Measure(WorldDefinition definition, Func<WorldViewGraph, (int? Passes, string? Issue)>? passes = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var graphs = (definition.Views.Graphs ?? []);

        if (
            (graphs.Count == 0) &&
            (definition.Views.GraphBudget is null)
        ) {
            return None;
        }

        var lines = new WorldGraphInstanceCost[graphs.Count];

        for (var index = 0; (index < graphs.Count); index++) {
            var graph = graphs[index];
            var planned = ((passes is null)
                ? (Passes: null, Issue: UnplannedIssue)
                : passes(arg: graph)
            );
            var refresh = WorldViewGraphs.RefreshOf(graph: graph);

            lines[index] = new WorldGraphInstanceCost(
                Divisor: ((refresh.Hertz == 0)
                    ? refresh.Divisor
                    : null),
                Hertz: ((refresh.Hertz == 0)
                    ? null
                    : refresh.Hertz),
                Issue: planned.Issue,
                Name: graph.Name,
                Passes: planned.Passes,
                Source: (graph.Source ?? graph.Package!)
            );
        }

        return new WorldPresentationCost(
            Instances: lines,
            PassPixelsPerFrame: (definition.Views.GraphBudget?.PassPixelsPerFrame ?? 0)
        ) {
            Bindings = WorldBindingCost.Measure(definition: definition),
            BytesPerFrameCeiling = (definition.Views.GraphBudget?.BytesPerFrame ?? 0),
            BytesPerTickCeiling = (definition.Views.GraphBudget?.BytesPerTick ?? 0),
        };
    }
    /// <summary>Describes the dimension as one <c>world.budget</c> clause: each instance with its extent ceiling, rate
    /// and passes, then the price ceiling, then each bound parameter with its bytes per tick and per frame, then their
    /// totals against their ceilings.</summary>
    /// <returns>The clause.</returns>
    public string Describe() {
        if (Instances.Count == 0) {
            return "graphs none";
        }

        var lines = Instances.Select(selector: static line => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{line.Name} ({line.Source}) extent<=display rate {line.DescribeRate()} passes {((line.Passes is { } passes) ? passes.ToString(provider: CultureInfo.InvariantCulture) : $"? ({line.Issue})")}{((line.PassDisplaysPerFrame is { } displays) ? $" <= {displays:0.###} display pass-pixels/frame" : string.Empty)}"
        ));
        var bindings = Bindings.Select(selector: static line => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{line.Pipeline} {line.Binding} ({line.Token}) {line.BytesPerTick} B/tick {line.BytesPerFrame} B/frame"
        ));

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"graphs {Instances.Count} instance(s): {string.Join(separator: "; ", values: lines)}; budget {((PassPixelsPerFrame == 0) ? "unbounded" : $"{PassPixelsPerFrame} pass-pixels/frame")}; bindings {Bindings.Count}{((Bindings.Count == 0) ? string.Empty : $": {string.Join(separator: "; ", values: bindings)}")}; {BytesPerTick} B/tick of {Ceiling(ceiling: BytesPerTickCeiling)}, {BytesPerFrame} B/frame of {Ceiling(ceiling: BytesPerFrameCeiling)}"
        );

        static string Ceiling(long ceiling) => ((ceiling == 0)
            ? "unbounded"
            : ceiling.ToString(provider: CultureInfo.InvariantCulture));
    }
}
public sealed partial record WorldCostReport {
    /// <summary>Gets the presentation dimension: every frame-graph instance with its extent ceiling, rate and passes.
    /// A document-only analysis leaves passes unplanned; a host that reads the graph sources prices them.</summary>
    public WorldPresentationCost Presentation { get; init; } = WorldPresentationCost.None;
}
