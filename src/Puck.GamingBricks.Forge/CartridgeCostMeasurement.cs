using Puck.Assets.Documents;
using Puck.Maths;
using Puck.State;

namespace Puck.GamingBricks.Forge;

/// <summary>What a measurement probe carries beside its loop, each charged against the same frame the loop runs in.</summary>
/// <param name="Sprites">Sprites drawn every frame.</param>
/// <param name="Maps">Background cells written every frame, each through the vertical-blank queue.</param>
/// <param name="BlitWidth">The width, in cells, of a screen blitted every frame; zero for none.</param>
/// <param name="BlitHeight">The height, in cells, of that screen.</param>
/// <param name="Save">Bytes saved and loaded back every frame; zero for none.</param>
/// <param name="Music">Whether a one-note track is played and stopped every frame.</param>
/// <param name="Raster">Raster rows scrolled every frame.</param>
public readonly record struct CartridgeCostLoad(int Sprites = 0, int Maps = 0, int BlitWidth = 0, int BlitHeight = 0, int Save = 0, bool Music = false, int Raster = 0);
/// <summary>One shape the measurement sweeps: a loop body repeated a searched number of times over a fixed load.</summary>
/// <param name="Name">The shape's name in the report.</param>
/// <param name="Target">The machine the shape is measured on.</param>
/// <param name="Body">The statements one inner iteration runs.</param>
/// <param name="Granularity">The inner loop's count; the search varies the outer count, so this is the search's grain.</param>
/// <param name="Load">What the probe carries beside its loop.</param>
public sealed record CartridgeCostShape(string Name, string Target, CartridgeStatement[] Body, int Granularity, CartridgeCostLoad Load);
/// <summary>The largest per-frame iteration count a machine was seen to sustain for one shape.</summary>
/// <param name="Shape">The shape measured.</param>
/// <param name="Iterations">Outer count times grain at the largest sustained outer count.</param>
/// <param name="Clipped">Whether the search stopped at its own ceiling, so <paramref name="Iterations"/> reports the
/// search rather than the machine.</param>
/// <param name="Cost">The model's cost of the largest sustained probe, or <see langword="default"/> when clipped.</param>
public readonly record struct CartridgeCostCapacity(CartridgeCostShape Shape, int Iterations, bool Clipped, CostBound Cost);
/// <summary>
/// Measures each primitive's sustained per-frame capacity on a real machine. This is the evidence the abstract work-unit
/// weights in <see cref="CartridgeCost"/> are derived from: cost per iteration is inversely proportional to the
/// iterations a machine sustains at full frame rate, so weights come from capacity ratios, never from counting an
/// emitter's instructions.
/// <para>
/// The package holds no machine, so the caller supplies one: a function that compiles a probe for its target, boots it,
/// runs the given frames with no input held, and returns the byte variable <see cref="Ticks"/>. <c>puck cartridge-cost</c>
/// supplies both real machines; fold the capacities it reports into <see cref="CartridgeCost"/>'s weights and
/// <see cref="CartridgeCostProfile"/>'s reservations.
/// </para>
/// <para>
/// A probe is costed under nothing: the model advises and never refuses, so a probe far past a target's reservation
/// still compiles and simply misses frames, which is exactly what the search needs to see.
/// </para>
/// </summary>
public static class CartridgeCostMeasurement {
    /// <summary>The frames a probe runs before its tick count is read.</summary>
    public const int Frames = 40;
    /// <summary>The frames a probe may lose to booting and still count as sustaining its work.</summary>
    public const int BootSlack = 4;
    /// <summary>The largest outer count the search tries; the schema bounds a <c>repeat</c> count at 255.</summary>
    public const int SearchCeiling = 255;
    /// <summary>The byte variable every probe increments once per frame it completes.</summary>
    public const string Ticks = "ticks";

    private const string Sink = "sink";

    /// <summary>Gets a one-note lead on the second pulse voice: the track the music load plays and stops.</summary>
    public static CartridgeMusicVoice Lead { get; } = new(
        Voice: AudioEffectDocument.VoicePulse2,
        Part: new AudioDocument(
            Schema: AudioDocument.CurrentSchema,
            Name: "t",
            Tempo: 8,
            Patterns: [[new AudioRowDocument(
                Duty: null,
                Envelope: null,
                Note: "C4"
            )]],
            Order: [0],
            Effects: null
        )
    );
    /// <summary>Gets the step one unit is defined against: a <c>set</c> writing a literal to a variable, eleven units.</summary>
    public static CartridgeStatement Step { get; } = Set(
        operation: null,
        value: CartridgeExpressions.Of(constant: 1)
    );

    /// <summary>
    /// Searches for the largest outer count a machine sustains for <paramref name="shape"/>. Sustaining is monotone in
    /// the outer count — more work never fits where less did not — so the search bisects rather than sweeping.
    /// </summary>
    /// <param name="shape">The shape to measure.</param>
    /// <param name="run">The machine: compiles a probe for its target, boots it, runs the given frames with no input
    /// held, and returns the probe's <see cref="Ticks"/> byte.</param>
    /// <returns>The largest sustained count, flagged when it is the search's ceiling rather than the machine's.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> or <paramref name="run"/> is <see langword="null"/>.</exception>
    public static CartridgeCostCapacity LargestSustained(CartridgeCostShape shape, Func<CartridgeDocument, int, int> run) {
        ArgumentNullException.ThrowIfNull(argument: shape);
        ArgumentNullException.ThrowIfNull(argument: run);

        var low = 0;
        var high = SearchCeiling;

        while (low < high) {
            var probe = (((low + high) + 1) / 2);

            if (run(
                arg1: Probe(
                    outer: probe,
                    shape: shape
                ),
                arg2: Frames
            ) >= (Frames - BootSlack)) { low = probe; } else { high = (probe - 1); }
        }

        var iterations = (low * shape.Granularity);

        if (low == SearchCeiling) {
            return new CartridgeCostCapacity(
                Clipped: true,
                Cost: default,
                Iterations: iterations,
                Shape: shape
            );
        }

        return new CartridgeCostCapacity(
            Clipped: false,
            Cost: CartridgeCost.Frame(
                document: Probe(
                    outer: low,
                    shape: shape
                ),
                profile: CartridgeCostProfile.For(target: shape.Target)
            ),
            Iterations: iterations,
            Shape: shape
        );
    }
    /// <summary>Builds the probe document for one outer count of a shape.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="outer">The outer loop's count, 0 through <see cref="SearchCeiling"/>.</param>
    /// <returns>A document whose one rule runs the load, then the shape's body outer times grain, then counts the frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    public static CartridgeDocument Probe(CartridgeCostShape shape, int outer) {
        ArgumentNullException.ThrowIfNull(argument: shape);

        var load = shape.Load;

        return CartridgeDocuments.Create(
            target: shape.Target,
            title: "COST"
        ) with {
            Variables = [
                new CartridgeVariable(
                    Name: "o",
                    Initial: 0
                ),
                new CartridgeVariable(
                    Name: "i",
                    Initial: 0
                ),
                new CartridgeVariable(
                    Name: "a",
                    Initial: 3
                ),
                new CartridgeVariable(
                    Name: Sink,
                    Initial: 0
                ),
                new CartridgeVariable(
                    Name: Ticks,
                    Initial: 0
                ),
            ],
            Arrays = [
                new CartridgeArray(
                    Initial: new int[shape.Granularity],
                    Name: "cells"
                ),
                .. ((load.Save > 0)
                    ? new[] { new CartridgeArray(
                        Initial: new int[load.Save],
                        Name: "kept"
                    ) }
                    : []),
            ],
            Save = ((load.Save > 0)
                ? new CartridgeSave(
                    Arrays: ["kept"],
                    Variables: [],
                    Version: 1
                )
                : null),
            Sounds = (load.Music
                ? [new CartridgeSound(
                    Name: "theme",
                    Music: [Lead]
                )]
                : []),
            Sprites = [.. Enumerable.Range(
                count: load.Sprites,
                start: 0
            ).Select(selector: static index => new CartridgeSprite(
                Name: $"s{index}",
                Tile: CartridgeExpressions.Of(constant: 0),
                X: CartridgeExpressions.Of(state: Sink),
                Y: CartridgeExpressions.Of(constant: 40),
                Visible: CartridgeExpressions.Of(constant: 1)
            ))],
            Raster = [.. Enumerable.Range(
                count: load.Raster,
                start: 0
            ).Select(selector: static index => new CartridgeRasterRow(
                Line: ((index * 16) + 8),
                ScrollX: CartridgeExpressions.Of(state: Sink),
                ScrollY: CartridgeExpressions.Of(constant: 0)
            ))],
            Screens = ((load.BlitWidth > 0)
                ? [new CartridgeScreen(
                    Name: "panel",
                    Width: load.BlitWidth,
                    Tiles: new int[(load.BlitWidth * load.BlitHeight)]
                )]
                : []),
            Rules = [new CartridgeRule(
                Name: "work",
                Body: [
                    .. Enumerable.Range(
                        count: load.Maps,
                        start: 0
                    ).Select(selector: static index => new CartridgeStatement(
                        Kind: "map",
                        Row: CartridgeExpressions.Of(constant: index),
                        Column: CartridgeExpressions.Of(constant: 0),
                        Tile: CartridgeExpressions.Of(constant: 0)
                    )),
                    .. ((load.BlitWidth > 0)
                        ? new[] { new CartridgeStatement(
                            Kind: "blit",
                            Screen: "panel",
                            Row: CartridgeExpressions.Of(constant: 0),
                            Column: CartridgeExpressions.Of(constant: 0)
                        ) }
                        : []),
                    .. ((load.Save > 0)
                        ? new[] { new CartridgeStatement(Kind: "save"), new CartridgeStatement(Kind: "load") }
                        : []),
                    .. (load.Music
                        ? new[] { new CartridgeStatement(
                            Kind: "play",
                            Sound: "theme"
                        ), new CartridgeStatement(Kind: "stop") }
                        : []),
                    new CartridgeStatement(
                        Kind: "repeat",
                        Count: outer,
                        Index: "o",
                        Body: [
                            new CartridgeStatement(
                                Kind: "repeat",
                                Count: shape.Granularity,
                                Index: "i",
                                Body: shape.Body
                            ),
                        ]
                    ),
                    new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: Ticks),
                        Operation: ExpressionOp.Add,
                        Value: CartridgeExpressions.Of(constant: 1)
                    ),
                ]
            )],
        };
    }
    /// <summary>
    /// Returns every shape the measurement sweeps on a target, in report order. Shapes are chosen so the solve is determined: <c>step</c> and <c>step-twice</c> separate per-iteration loop
    /// overhead from one step's cost, and every other shape adds exactly one primitive on top of a known baseline. A
    /// second grain separates per-loop setup from per-iteration overhead. Sprites, map writes and blits are charged
    /// against the same frame, so each weight comes from the capacity a known count of them costs; map writes cannot be
    /// measured inside the loop because the queue they feed is bounded per frame. The advanced machine sustains several
    /// times what the Color one does and the outer count cannot pass 255, so it is swept at a coarser grain.
    /// </summary>
    /// <param name="target">The target, <c>cgb</c> or <c>agb</c>.</param>
    /// <returns>The shapes.</returns>
    public static IReadOnlyList<CartridgeCostShape> Shapes(string target) {
        var grain = ((target == "agb")
            ? 20
            : 10
        );
        var constant = CartridgeExpressions.Of(constant: 1);
        var variable = CartridgeExpressions.Of(state: "a");
        var element = CartridgeExpressions.Of(
            state: "cells",
            key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "i"))
        );
        var addConstant = Set(
            operation: ExpressionOp.Add,
            value: constant
        );
        var shapes = new List<CartridgeCostShape>();

        void Add(string name, CartridgeStatement[] body, int granularity = 0, CartridgeCostLoad load = default) => shapes.Add(item: new CartridgeCostShape(
            Body: body,
            Granularity: ((granularity == 0)
                ? grain
                : granularity),
            Load: load,
            Name: name,
            Target: target
        ));

        Add(
            body: [Step],
            name: "step"
        );
        Add(
            body: [Step, Step],
            name: "step-twice"
        );
        Add(
            body: [addConstant],
            name: "add-constant"
        );
        Add(
            body: [Set(
                operation: ExpressionOp.Add,
                value: variable
            )],
            name: "read-variable"
        );
        Add(
            body: [Set(
                operation: ExpressionOp.Add,
                value: element
            )],
            name: "read-array"
        );
        Add(
            body: [new CartridgeStatement(
                Kind: "set",
                Target: new CartridgeTarget(
                    State: "cells",
                    Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "i"))
                ),
                Operation: null,
                Value: constant
            )],
            name: "write-array"
        );
        Add(
            body: [new CartridgeStatement(
                Kind: "if",
                When: CartridgeExpressions.Gate(
                    comparison: ExpressionOp.GreaterOrEqual,
                    left: variable,
                    right: constant
                ),
                Then: [addConstant]
            )],
            name: "compare-taken"
        );
        Add(
            body: [new CartridgeStatement(
                Kind: "if",
                When: CartridgeExpressions.Gate(
                    comparison: ExpressionOp.Less,
                    left: variable,
                    right: constant
                ),
                Then: [addConstant]
            )],
            name: "compare-skipped"
        );
        Add(
            body: [new CartridgeStatement(
                Kind: "if",
                When: CartridgeExpressions.Pressing(
                    button: "a",
                    mode: "held"
                ),
                Then: [addConstant]
            )],
            name: "key-condition"
        );
        foreach (var (name, operation, operand) in new (string, ExpressionOp, int)[] {
            ("multiply", ExpressionOp.Multiply, 3),
            ("divide", ExpressionOp.Divide, 3),
            ("remainder", ExpressionOp.Remainder, 3),
            ("shift", ExpressionOp.ShiftLeft, 1),
        }) {
            Add(
                body: [Set(
                    operation: operation,
                    value: CartridgeExpressions.Of(constant: operand)
                )],
                name: name
            );
        }
        Add(
            body: [Step],
            granularity: (grain * 2),
            name: "step-grain-double"
        );
        Add(
            body: [Step],
            load: new CartridgeCostLoad(Sprites: 40),
            name: "step-sprites40"
        );
        Add(
            body: [Step],
            load: new CartridgeCostLoad(Maps: 24),
            name: "step-maps24"
        );
        if (target == "cgb") {
            Add(
                body: [Step],
                load: new CartridgeCostLoad(Music: true),
                name: "play-stop"
            );
            foreach (var payload in new[] { 8, 32, 64 }) {
                Add(
                    body: [Step],
                    load: new CartridgeCostLoad(Save: payload),
                    name: $"save{payload}"
                );
            }
        }
        foreach (var rows in new[] { 1, 4, 8 }) {
            Add(
                body: [Step],
                load: new CartridgeCostLoad(Raster: rows),
                name: $"raster{rows}"
            );
        }
        Add(
            body: [Step],
            load: new CartridgeCostLoad(
                BlitHeight: 6,
                BlitWidth: 20
            ),
            name: "blit20x6"
        );

        return shapes;
    }

    private static CartridgeStatement Set(ExpressionOp? operation, ExpressionProgram value) =>
        new(
            Kind: "set",
            Target: new CartridgeTarget(State: Sink),
            Operation: operation,
            Value: value
        );
}
