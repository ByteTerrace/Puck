using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;


namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Measures each primitive's sustained per-frame capacity on both real machines. This is the evidence the abstract
/// work-unit weights are derived from; it is skipped by default because it boots hundreds of ROMs.
/// </summary>
/// <remarks>
/// Run it with <c>PUCK_FORGE_MEASURE=1</c> to regenerate the table, then fold the reported weights into
/// <c>CartridgeCost</c> and the reported capacities into <see cref="CartridgeCostProfile"/>. Weights come from
/// capacity ratios, never from counting an emitter's instructions.
/// <para>
/// Every probe compiles under <see cref="CartridgeCostProfile.Calibration"/>. A probe costed against a standard
/// reservation would be refused before a ROM existed, and the search would report the model's own opinion rather than
/// what the machine does. <c>TheHarnessCanStillReachTheMachine</c> runs unconditionally so that seam cannot close
/// again unnoticed.
/// </para>
/// </remarks>
public sealed class CartridgeCostMeasurement {
    private const int BootSlack = 4;
    private const int Frames = 40;
    // The schema bounds a repeat count at 255, so the outer loop cannot search past it. Reach is bought with a
    // larger inner count instead, which is why the advanced machine is swept at a coarser grain.
    private const int SearchCeiling = 255;

    internal static CartridgeMusicVoice Lead(Puck.Assets.Documents.AudioDocument part) =>
        new(
            Voice: Puck.Assets.Documents.AudioEffectDocument.VoicePulse2,
            Part: part
        );
    internal static Puck.Assets.Documents.AudioDocument Track() => new(
        Schema: Puck.Assets.Documents.AudioDocument.CurrentSchema,
        Name: "t",
        Tempo: 8,
        Patterns: [[new Puck.Assets.Documents.AudioRowDocument(
                    Duty: null,
                    Envelope: null,
                    Note: "C4"
                )]],
        Order: [0],
        Effects: null
    );

    private static CartridgeCompilation Compile(string target, CartridgeDocument document) =>
        ((target == "agb")
            ? new AgbCartridgeCompiler().Compile(document: document)
            : new HgbCartridgeCompiler().Compile(document: document)
        );
    private static CartridgeDocument Document(string target, CartridgeStatement[] body, int outer, int granularity, int sprites, int maps, int blitWidth, int blitHeight, int save, bool music, int raster) {
        return CartridgeDocuments.Create(
            target: target,
            title: "COST"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "o",
                Initial: 0
            ), new CartridgeVariable(
                Name: "i",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "a",
                Initial: 3
            ), new CartridgeVariable(
                Name: "sink",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "ticks",
                Initial: 0
            ),
            ],
            Arrays = [
                new CartridgeArray(
                Initial: new int[granularity],
                Name: "cells"
            ),
                .. ((save > 0)
            ? new[] { new CartridgeArray(
                    Initial: new int[save],
                    Name: "kept"
                ) }
            : []),
            ],
            Save = ((save > 0)
            ? new CartridgeSave(
                Arrays: ["kept"],
                Variables: [],
                Version: 1
            )
            : null),
            Sounds = (music
            ? [new CartridgeSound(
                    Name: "theme",
                    Music: [Lead(part: Track())]
                )]
            : []),
            Sprites = [.. Enumerable.Range(
                count: sprites,
                start: 0
            ).Select(selector: index => new CartridgeSprite(
                Name: $"s{index}",
                Tile: CartridgeExpressions.Of(constant: 0),
                X: CartridgeExpressions.Of(state: "sink"),
                Y: CartridgeExpressions.Of(constant: 40),
                Visible: CartridgeExpressions.Of(constant: 1)
            ))],
            Raster = [.. Enumerable.Range(
                count: raster,
                start: 0
            ).Select(selector: index => new CartridgeRasterRow(
                Line: ((index * 16) + 8),
                ScrollX: CartridgeExpressions.Of(state: "sink"),
                ScrollY: CartridgeExpressions.Of(constant: 0)
            ))],
            Screens = ((blitWidth > 0)
            ? [new CartridgeScreen(
                    Name: "panel",
                    Width: blitWidth,
                    Tiles: new int[(blitWidth * blitHeight)]
                )]
            : []),
            Rules = [new CartridgeRule(
                Name: "work",
                Body: [
                .. Enumerable.Range(
                        count: maps,
                        start: 0
                    ).Select(selector: index => new CartridgeStatement(
                        Kind: "map",
                        Row: CartridgeExpressions.Of(constant: index),
                        Column: CartridgeExpressions.Of(constant: 0),
                        Tile: CartridgeExpressions.Of(constant: 0)
                    )),
                .. ((blitWidth > 0)
            ? new[] { new CartridgeStatement(
                            Kind: "blit",
                            Screen: "panel",
                            Row: CartridgeExpressions.Of(constant: 0),
                            Column: CartridgeExpressions.Of(constant: 0)
                        ) }
            : []),
                .. ((save > 0)
            ? new[] { new CartridgeStatement(Kind: "save"), new CartridgeStatement(Kind: "load") }
            : []),
                .. (music
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
                                Count: granularity,
                                Index: "i",
                                Body: body
                            ),
                ]
                    ),
                Set(
                        target: "ticks",
                        operation: ExpressionOp.Add,
                        value: CartridgeExpressions.Of(constant: 1)
                    ),
            ]
            )],
        };
    }
    // A search that stops at its own ceiling reports the ceiling, not the machine, so the result says which it is. The
    // modelled cost of the largest sustained document rides along: the smallest of those across every shape is what a
    // machine's reservation may be, since anything larger admits a document the machine was seen to miss frames on.
    private static string LargestSustained(string target, CartridgeStatement[] body, int granularity, int sprites, int maps = 0, int blitWidth = 0, int blitHeight = 0, int save = 0, bool music = false, int raster = 0) {
        var low = 0;
        var high = SearchCeiling;

        while (low < high) {
            var probe = (((low + high) + 1) / 2);

            if (Sustains(
                blitHeight: blitHeight,
                blitWidth: blitWidth,
                body: body,
                granularity: granularity,
                maps: maps,
                music: music,
                outer: probe,
                raster: raster,
                save: save,
                sprites: sprites,
                target: target
            )) { low = probe; } else { high = (probe - 1); }
        }

        if (low == SearchCeiling) {
            return $"{(low * granularity)} CLIPPED";
        }

        var largest = Document(
            blitHeight: blitHeight,
            blitWidth: blitWidth,
            body: body,
            granularity: granularity,
            maps: maps,
            music: music,
            outer: low,
            raster: raster,
            save: save,
            sprites: sprites,
            target: target
        );
        var cost = CartridgeCost.Frame(
            document: largest,
            profile: CartridgeCostProfile.For(target: target)
        );

        return $"{(low * granularity),6}  units {(cost.IsKnown
            ? cost.Cycles.ToString()
            : "unmodeled")}";
    }
    private static CartridgeStatement Set(string target, ExpressionOp? operation, ValueExpression value) =>
        new(
            Kind: "set",
            Target: new CartridgeTarget(State: target),
            Operation: operation,
            Value: value
        );
    // Shapes are chosen so the solve is determined: "step" and "step-twice" separate per-iteration loop overhead from
    // one step's cost, and every other shape adds exactly one primitive on top of a known baseline.
    private static (string, CartridgeStatement[])[] Shapes() {
        var constant = CartridgeExpressions.Of(constant: 1);
        var variable = CartridgeExpressions.Of(state: "a");
        var element = CartridgeExpressions.Of(
            state: "cells",
            key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "i"))
        );
        var step = Set(
            operation: null,
            target: "sink",
            value: constant
        );
        var addConstant = Set(
            operation: ExpressionOp.Add,
            target: "sink",
            value: constant
        );

        return [
            ("step", [step]),
            ("step-twice", [step, step]),
            ("add-constant", [addConstant]),
            ("read-variable", [Set(
                    operation: ExpressionOp.Add,
                    target: "sink",
                    value: variable
                )]),
            ("read-array", [Set(
                    operation: ExpressionOp.Add,
                    target: "sink",
                    value: element
                )]),
            ("write-array", [new CartridgeStatement(
                    Kind: "set",
                    Target: new CartridgeTarget(
                        State: "cells",
                        Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "i"))
                    ),
                    Operation: null,
                    Value: constant
                )]),
            ("compare-taken", [new CartridgeStatement(
                    Kind: "if",
                    When: CartridgeExpressions.Gate(
                        comparison: ActionStateComparison.GreaterOrEqual,
                        left: variable,
                        right: constant
                    ),
                    Then: [addConstant]
                )]),
            ("compare-skipped", [new CartridgeStatement(
                    Kind: "if",
                    When: CartridgeExpressions.Gate(
                        comparison: ActionStateComparison.Less,
                        left: variable,
                        right: constant
                    ),
                    Then: [addConstant]
                )]),
            ("key-condition", [new CartridgeStatement(
                    Kind: "if",
                    When: CartridgeExpressions.Pressing(
                        button: "a",
                        mode: "held"
                    ),
                    Then: [addConstant]
                )]),
            ("multiply", [Set(
                    target: "sink",
                    operation: ExpressionOp.Multiply,
                    value: CartridgeExpressions.Of(constant: 3)
                )]),
            ("divide", [Set(
                    target: "sink",
                    operation: ExpressionOp.Divide,
                    value: CartridgeExpressions.Of(constant: 3)
                )]),
            ("modulo", [Set(
                    target: "sink",
                    operation: ExpressionOp.Modulo,
                    value: CartridgeExpressions.Of(constant: 3)
                )]),
            ("shift", [Set(
                    target: "sink",
                    operation: ExpressionOp.ShiftLeft,
                    value: CartridgeExpressions.Of(constant: 1)
                )]),
        ];
    }
    private static bool Sustains(string target, CartridgeStatement[] body, int outer, int granularity, int sprites, int maps, int blitWidth, int blitHeight, int save, bool music, int raster) {
        var document = Document(
            blitHeight: blitHeight,
            blitWidth: blitWidth,
            body: body,
            granularity: granularity,
            maps: maps,
            music: music,
            outer: outer,
            raster: raster,
            save: save,
            sprites: sprites,
            target: target
        );
        var result = Compile(
            document: document,
            target: target
        );

        if (target == "agb") {
            using var machine = new AgbVerifyMachineDriver(
                rom: result.Rom,
                label: "cost"
            );

            machine.RunFrames(
                frames: Frames,
                keys: AgbKeys.None
            );
            return (machine.ReadByte(address: result.Variables["ticks"]) >= (Frames - BootSlack));
        }

        using var probe = new VerifyMachineDriver(
            rom: result.Rom,
            label: "cost"
        );

        probe.RunFrames(
            buttons: JoypadButtons.None,
            frames: Frames
        );
        return (probe.Read(address: ((ushort)result.Variables["ticks"])) >= (Frames - BootSlack));
    }

    [Fact]
    public void ReportSustainedCapacity() {
        if (Environment.GetEnvironmentVariable(variable: "PUCK_FORGE_MEASURE") is not "1") {
            return;
        }

        var log = new System.Text.StringBuilder();

        foreach (var target in new[] { "cgb", "agb" }) {
            // The advanced machine sustains several times what the Color one does, and the outer count cannot pass
            // 255, so its sweep uses a coarser grain to stay inside the search's reach.
            var grain = ((target == "agb")
                ? 20
                : 10
            );

            log.AppendLine(value: $"{target} grain {grain}");
            foreach (var (name, body) in Shapes()) {
                log.AppendLine(value: $"{target} {name,-18} {LargestSustained(
                    target: target,
                    body: body,
                    granularity: grain,
                    sprites: 0
                )}");
            }

            // A second granularity separates per-loop setup from per-iteration overhead. Sprites, map writes and blits
            // are charged against the same frame, so each weight comes from the capacity a known count of them costs;
            // map writes cannot be measured inside the loop because the queue they feed is bounded per frame.
            var step = Set(
                target: "sink",
                operation: null,
                value: CartridgeExpressions.Of(constant: 1)
            );

            log.AppendLine(value: $"{target} {"step-grain-double",-18} {LargestSustained(
                target: target,
                body: [step],
                granularity: (grain * 2),
                sprites: 0
            )}");
            log.AppendLine(value: $"{target} {"step-sprites40",-18} {LargestSustained(
                target: target,
                body: [step],
                granularity: grain,
                sprites: 40
            )}");
            log.AppendLine(value: $"{target} {"step-maps24",-18} {LargestSustained(
                target: target,
                body: [step],
                granularity: grain,
                sprites: 0,
                maps: 24
            )}");
            if (target == "cgb") {
                log.AppendLine(value: $"{target} {"play-stop",-18} {LargestSustained(
                    target: target,
                    body: [step],
                    granularity: grain,
                    sprites: 0,
                    music: true
                )}");
                foreach (var payload in new[] { 8, 32, 64 }) {
                    log.AppendLine(value: $"{target} save{payload,-14} {LargestSustained(
                        target: target,
                        body: [step],
                        granularity: grain,
                        sprites: 0,
                        save: payload
                    )}");
                }
            }
            foreach (var rows in new[] { 1, 4, 8 }) {
                log.AppendLine(value: $"{target} raster{rows,-12} {LargestSustained(
                    target: target,
                    body: [step],
                    granularity: grain,
                    sprites: 0,
                    raster: rows
                )}");
            }

            foreach (var (w, h) in new[] { (20, 6) }) {
                log.AppendLine(value: $"{target} blit{w}x{h,-13} {LargestSustained(
                    target: target,
                    body: [step],
                    granularity: grain,
                    sprites: 0,
                    blitWidth: w,
                    blitHeight: h
                )}");
            }
        }

        File.WriteAllText(
            path: Path.Combine(
                path1: Path.GetTempPath(),
                path2: "puck-forge-cost.txt"
            ),
            contents: log.ToString()
        );
    }
    /// <summary>Proves a probe past the standard reservation still reaches a machine, which is what the search needs.</summary>
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void TheHarnessCanStillReachTheMachine(string target) {
        var body = new[] { Set(
            target: "sink",
            operation: null,
            value: CartridgeExpressions.Of(constant: 1)
        ) };

        // Far past what the target's reservation grants, which the search needs to be able to reach: nothing refuses a
        // document for being slow, so a probe compiles and simply misses frames.
        var beyond = Document(
            blitHeight: 0,
            blitWidth: 0,
            body: body,
            granularity: 255,
            maps: 0,
            music: false,
            outer: 255,
            raster: 0,
            save: 0,
            sprites: 0,
            target: target
        );

        Assert.Empty(collection: CartridgeDocuments.Validate(document: beyond));

        var (frame, reservation) = CartridgeDocuments.Estimate(document: beyond);
        Assert.True(condition: (frame.Cycles > reservation));
        Assert.NotEmpty(collection: Compile(
            document: beyond,
            target: target
        ).Rom);
    }
}
