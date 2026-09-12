using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

using Puck.State;

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

    /// <summary>Proves a probe past the standard reservation still reaches a machine, which is what the search needs.</summary>
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void TheHarnessCanStillReachTheMachine(string target) {
        var body = new[] { Set(target: "sink", operation: null, value: new CartridgeValue(Constant: 1)) };

        // Far past what the target's reservation grants, which the search needs to be able to reach: nothing refuses a
        // document for being slow, so a probe compiles and simply misses frames.
        var beyond = Document(target: target, body: body, outer: 255, granularity: 255, sprites: 0, maps: 0, blitWidth: 0, blitHeight: 0, save: 0, music: false, raster: 0);
        Assert.Empty(collection: CartridgeDocuments.Validate(document: beyond));

        var (frame, reservation) = CartridgeDocuments.Estimate(document: beyond);
        Assert.True(condition: frame.Cycles > reservation);
        Assert.NotEmpty(collection: Compile(target: target, document: beyond).Rom);
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
            var grain = target == "agb" ? 20 : 10;
            log.AppendLine(value: $"{target} grain {grain}");
            foreach (var (name, body) in Shapes()) {
                log.AppendLine(value: $"{target} {name,-18} {LargestSustained(target: target, body: body, granularity: grain, sprites: 0)}");
            }

            // A second granularity separates per-loop setup from per-iteration overhead. Sprites, map writes and blits
            // are charged against the same frame, so each weight comes from the capacity a known count of them costs;
            // map writes cannot be measured inside the loop because the queue they feed is bounded per frame.
            var step = Set(target: "sink", operation: null, value: new CartridgeValue(Constant: 1));
            log.AppendLine(value: $"{target} {"step-grain-double",-18} {LargestSustained(target: target, body: [step], granularity: grain * 2, sprites: 0)}");
            log.AppendLine(value: $"{target} {"step-sprites40",-18} {LargestSustained(target: target, body: [step], granularity: grain, sprites: 40)}");
            log.AppendLine(value: $"{target} {"step-maps24",-18} {LargestSustained(target: target, body: [step], granularity: grain, sprites: 0, maps: 24)}");
            if (target == "cgb") {
                log.AppendLine(value: $"{target} {"play-stop",-18} {LargestSustained(target: target, body: [step], granularity: grain, sprites: 0, music: true)}");
                foreach (var payload in new[] { 8, 32, 64 }) {
                    log.AppendLine(value: $"{target} save{payload,-14} {LargestSustained(target: target, body: [step], granularity: grain, sprites: 0, save: payload)}");
                }
            }
            foreach (var rows in new[] { 1, 4, 8 }) {
                log.AppendLine(value: $"{target} raster{rows,-12} {LargestSustained(target: target, body: [step], granularity: grain, sprites: 0, raster: rows)}");
            }

            foreach (var (w, h) in new[] { (20, 6) }) {
                log.AppendLine(value: $"{target} blit{w}x{h,-13} {LargestSustained(target: target, body: [step], granularity: grain, sprites: 0, blitWidth: w, blitHeight: h)}");
            }
        }

        File.WriteAllText(path: Path.Combine(path1: Path.GetTempPath(), path2: "puck-forge-cost.txt"), contents: log.ToString());
    }

    // Shapes are chosen so the solve is determined: "step" and "step-twice" separate per-iteration loop overhead from
    // one step's cost, and every other shape adds exactly one primitive on top of a known baseline.
    private static (string, CartridgeStatement[])[] Shapes() {
        var constant = new CartridgeValue(Constant: 1);
        var variable = new CartridgeValue(Variable: "a");
        var element = new CartridgeValue(Array: "cells", Index: new CartridgeValue(Variable: "i"));
        var step = Set(target: "sink", operation: null, value: constant);
        var addConstant = Set(target: "sink", operation: ExpressionOp.Add, value: constant);
        return [
            ("step", [step]),
            ("step-twice", [step, step]),
            ("add-constant", [addConstant]),
            ("read-variable", [Set(target: "sink", operation: ExpressionOp.Add, value: variable)]),
            ("read-array", [Set(target: "sink", operation: ExpressionOp.Add, value: element)]),
            ("write-array", [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "cells", Index: new CartridgeValue(Variable: "i")), Operation: null, Value: constant)]),
            ("compare-taken", [new CartridgeStatement(Kind: "if", When: [new CartridgeCondition(Kind: "compare", Left: variable, Comparison: ActionStateComparison.GreaterOrEqual, Right: constant)], Then: [addConstant])]),
            ("compare-skipped", [new CartridgeStatement(Kind: "if", When: [new CartridgeCondition(Kind: "compare", Left: variable, Comparison: ActionStateComparison.Less, Right: constant)], Then: [addConstant])]),
            ("key-condition", [new CartridgeStatement(Kind: "if", When: [new CartridgeCondition(Kind: "key", Key: "a", Mode: "held")], Then: [addConstant])]),
            ("multiply", [Set(target: "sink", operation: ExpressionOp.Multiply, value: new CartridgeValue(Constant: 3))]),
            ("divide", [Set(target: "sink", operation: ExpressionOp.Divide, value: new CartridgeValue(Constant: 3))]),
            ("modulo", [Set(target: "sink", operation: ExpressionOp.Modulo, value: new CartridgeValue(Constant: 3))]),
            ("shift", [Set(target: "sink", operation: ExpressionOp.ShiftLeft, value: new CartridgeValue(Constant: 1))]),
        ];
    }

    // A search that stops at its own ceiling reports the ceiling, not the machine, so the result says which it is. The
    // modelled cost of the largest sustained document rides along: the smallest of those across every shape is what a
    // machine's reservation may be, since anything larger admits a document the machine was seen to miss frames on.
    private static string LargestSustained(string target, CartridgeStatement[] body, int granularity, int sprites, int maps = 0, int blitWidth = 0, int blitHeight = 0, int save = 0, bool music = false, int raster = 0) {
        var low = 0;
        var high = SearchCeiling;
        while (low < high) {
            var probe = (low + high + 1) / 2;
            if (Sustains(target: target, body: body, outer: probe, granularity: granularity, sprites: sprites, maps: maps, blitWidth: blitWidth, blitHeight: blitHeight, save: save, music: music, raster: raster)) { low = probe; } else { high = probe - 1; }
        }

        if (low == SearchCeiling) {
            return $"{low * granularity} CLIPPED";
        }

        var largest = Document(target: target, body: body, outer: low, granularity: granularity, sprites: sprites, maps: maps, blitWidth: blitWidth, blitHeight: blitHeight, save: save, music: music, raster: raster);
        var cost = CartridgeCost.Frame(document: largest, profile: CartridgeCostProfile.For(target: target));

        return $"{low * granularity,6}  units {(cost.IsKnown ? cost.Cycles.ToString() : "unmodeled")}";
    }

    private static bool Sustains(string target, CartridgeStatement[] body, int outer, int granularity, int sprites, int maps, int blitWidth, int blitHeight, int save, bool music, int raster) {
        var document = Document(target: target, body: body, outer: outer, granularity: granularity, sprites: sprites, maps: maps, blitWidth: blitWidth, blitHeight: blitHeight, save: save, music: music, raster: raster);
        var result = Compile(target: target, document: document);
        if (target == "agb") {
            using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "cost");
            machine.RunFrames(keys: AgbKeys.None, frames: Frames);
            return machine.ReadByte(address: result.Variables["ticks"]) >= Frames - BootSlack;
        }

        using var probe = new VerifyMachineDriver(rom: result.Rom, label: "cost");
        probe.RunFrames(buttons: JoypadButtons.None, frames: Frames);
        return probe.Read(address: (ushort)result.Variables["ticks"]) >= Frames - BootSlack;
    }

    private static CartridgeCompilation Compile(string target, CartridgeDocument document) =>
        target == "agb"
            ? new AgbCartridgeCompiler().Compile(document: document)
            : new HgbCartridgeCompiler().Compile(document: document);

    private static CartridgeDocument Document(string target, CartridgeStatement[] body, int outer, int granularity, int sprites, int maps, int blitWidth, int blitHeight, int save, bool music, int raster) {
        return CartridgeDocuments.Create(target: target, title: "COST") with {
            Variables = [
                new CartridgeVariable(Name: "o", Initial: 0), new CartridgeVariable(Name: "i", Initial: 0),
                new CartridgeVariable(Name: "a", Initial: 3), new CartridgeVariable(Name: "sink", Initial: 0),
                new CartridgeVariable(Name: "ticks", Initial: 0),
            ],
            Arrays = [
                new CartridgeArray(Name: "cells", Initial: new int[granularity]),
                .. save > 0 ? new[] { new CartridgeArray(Name: "kept", Initial: new int[save]) } : [],
            ],
            Save = save > 0 ? new CartridgeSave(Version: 1, Variables: [], Arrays: ["kept"]) : null,
            Sounds = music ? [new CartridgeSound(Name: "theme", Music: [Lead(part: Track())])] : [],
            Sprites = [.. Enumerable.Range(start: 0, count: sprites).Select(selector: index => new CartridgeSprite(
                Name: $"s{index}", Tile: new CartridgeValue(Constant: 0), X: new CartridgeValue(Variable: "sink"),
                Y: new CartridgeValue(Constant: 40), Visible: new CartridgeValue(Constant: 1)))],
            Raster = [.. Enumerable.Range(start: 0, count: raster).Select(selector: index => new CartridgeRasterRow(
                Line: (index * 16) + 8, ScrollX: new CartridgeValue(Variable: "sink"), ScrollY: new CartridgeValue(Constant: 0)))],
            Screens = blitWidth > 0 ? [new CartridgeScreen(Name: "panel", Width: blitWidth, Tiles: new int[blitWidth * blitHeight])] : [],
            Rules = [new CartridgeRule(Name: "work", When: [], Body: [
                .. Enumerable.Range(start: 0, count: maps).Select(selector: index => new CartridgeStatement(
                    Kind: "map", Row: new CartridgeValue(Constant: index), Column: new CartridgeValue(Constant: 0), Tile: new CartridgeValue(Constant: 0))),
                .. blitWidth > 0 ? new[] { new CartridgeStatement(Kind: "blit", Screen: "panel", Row: new CartridgeValue(Constant: 0), Column: new CartridgeValue(Constant: 0)) } : [],
                .. save > 0 ? new[] { new CartridgeStatement(Kind: "save"), new CartridgeStatement(Kind: "load") } : [],
                .. music ? new[] { new CartridgeStatement(Kind: "play", Sound: "theme"), new CartridgeStatement(Kind: "stop") } : [],
                new CartridgeStatement(Kind: "repeat", Count: outer, Index: "o", Body: [
                    new CartridgeStatement(Kind: "repeat", Count: granularity, Index: "i", Body: body),
                ]),
                Set(target: "ticks", operation: ExpressionOp.Add, value: new CartridgeValue(Constant: 1)),
            ])],
        };
    }

    internal static CartridgeMusicVoice Lead(Puck.Assets.Documents.AudioDocument part) =>
        new(Voice: Puck.Assets.Documents.AudioEffectDocument.VoicePulse2, Part: part);

    internal static Puck.Assets.Documents.AudioDocument Track() => new(
        Schema: Puck.Assets.Documents.AudioDocument.CurrentSchema, Name: "t", Tempo: 8,
        Patterns: [[new Puck.Assets.Documents.AudioRowDocument(Note: "C4", Duty: null, Envelope: null)]], Order: [0], Effects: null);

    private static CartridgeStatement Set(string target, ExpressionOp? operation, CartridgeValue value) =>
        new(Kind: "set", Target: new CartridgeTarget(Variable: target), Operation: operation, Value: value);
}
