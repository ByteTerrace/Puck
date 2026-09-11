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
/// <c>CartridgeCost</c>. Weights come from capacity ratios, never from counting an emitter's instructions.
/// </remarks>
public sealed class CartridgeCostMeasurement {
    private const int BootSlack = 4;
    private const int Frames = 40;

    [Fact]
    public void ReportSustainedCapacity() {
        if (Environment.GetEnvironmentVariable(variable: "PUCK_FORGE_MEASURE") is not "1") {
            return;
        }

        var log = new System.Text.StringBuilder();
        foreach (var target in new[] { "cgb", "agb" }) {
            foreach (var (name, body) in Shapes()) {
                log.AppendLine(value: $"{target} {name,-18} {LargestSustained(target: target, body: body, granularity: 10, sprites: 0)}");
            }

            // A second granularity separates per-loop setup from per-iteration overhead. Sprites, map writes and blits
            // are charged against the same frame, so each weight comes from the capacity a known count of them costs;
            // map writes cannot be measured inside the loop because the queue they feed is bounded per frame.
            var step = Set(target: "sink", operation: "set", value: new CartridgeValue(Constant: 1));
            log.AppendLine(value: $"{target} {"step-grain20",-18} {LargestSustained(target: target, body: [step], granularity: 20, sprites: 0)}");
            log.AppendLine(value: $"{target} {"step-sprites40",-18} {LargestSustained(target: target, body: [step], granularity: 10, sprites: 40)}");
            log.AppendLine(value: $"{target} {"step-maps24",-18} {LargestSustained(target: target, body: [step], granularity: 10, sprites: 0, maps: 24)}");
            if (target == "cgb") {
                log.AppendLine(value: $"{target} {"play-stop",-18} {LargestSustained(target: target, body: [step], granularity: 10, sprites: 0, music: true)}");
                foreach (var payload in new[] { 8, 32, 64 }) {
                    log.AppendLine(value: $"{target} save{payload,-14} {LargestSustained(target: target, body: [step], granularity: 10, sprites: 0, save: payload)}");
                }
            }
            foreach (var rows in new[] { 1, 4, 8 }) {
                log.AppendLine(value: $"{target} raster{rows,-12} {LargestSustained(target: target, body: [step], granularity: 10, sprites: 0, raster: rows)}");
            }

            foreach (var (w, h) in new[] { (20, 6) }) {
                log.AppendLine(value: $"{target} blit{w}x{h,-13} {LargestSustained(target: target, body: [step], granularity: 10, sprites: 0, blitWidth: w, blitHeight: h)}");
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
        var step = Set(target: "sink", operation: "set", value: constant);
        var addConstant = Set(target: "sink", operation: "add", value: constant);
        return [
            ("step", [step]),
            ("step-twice", [step, step]),
            ("add-constant", [addConstant]),
            ("read-variable", [Set(target: "sink", operation: "add", value: variable)]),
            ("read-array", [Set(target: "sink", operation: "add", value: element)]),
            ("write-array", [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "cells", Index: new CartridgeValue(Variable: "i")), Operation: "set", Value: constant)]),
            ("compare-taken", [new CartridgeStatement(Kind: "if", When: [new CartridgeCondition(Kind: "compare", Left: variable, Comparison: "ge", Right: constant)], Then: [addConstant])]),
            ("compare-skipped", [new CartridgeStatement(Kind: "if", When: [new CartridgeCondition(Kind: "compare", Left: variable, Comparison: "lt", Right: constant)], Then: [addConstant])]),
            ("key-condition", [new CartridgeStatement(Kind: "if", When: [new CartridgeCondition(Kind: "key", Key: "a", Mode: "held")], Then: [addConstant])]),
            ("multiply", [Set(target: "sink", operation: "mul", value: new CartridgeValue(Constant: 3))]),
            ("divide", [Set(target: "sink", operation: "div", value: new CartridgeValue(Constant: 3))]),
            ("modulo", [Set(target: "sink", operation: "mod", value: new CartridgeValue(Constant: 3))]),
            ("shift", [Set(target: "sink", operation: "shl", value: new CartridgeValue(Constant: 1))]),
        ];
    }

    private static int LargestSustained(string target, CartridgeStatement[] body, int granularity, int sprites, int maps = 0, int blitWidth = 0, int blitHeight = 0, int save = 0, bool music = false, int raster = 0) {
        var low = 0;
        var high = 255;
        while (low < high) {
            var probe = (low + high + 1) / 2;
            if (Sustains(target: target, body: body, outer: probe, granularity: granularity, sprites: sprites, maps: maps, blitWidth: blitWidth, blitHeight: blitHeight, save: save, music: music, raster: raster)) { low = probe; } else { high = probe - 1; }
        }

        return low * granularity;
    }

    private static bool Sustains(string target, CartridgeStatement[] body, int outer, int granularity, int sprites, int maps, int blitWidth, int blitHeight, int save, bool music, int raster) {
        var document = CartridgeDocuments.Create(target: target, title: "COST") with {
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
            Sounds = music ? [new CartridgeSound(Name: "theme", Music: Track())] : [],
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
                Set(target: "ticks", operation: "add", value: new CartridgeValue(Constant: 1)),
            ])],
        };
        ICartridgeCompiler compiler = target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        var result = compiler.Compile(document: document);
        if (target == "agb") {
            using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "cost");
            machine.RunFrames(keys: AgbKeys.None, frames: Frames);
            return machine.ReadByte(address: result.Variables["ticks"]) >= Frames - BootSlack;
        }

        using var probe = new VerifyMachineDriver(rom: result.Rom, label: "cost");
        probe.RunFrames(buttons: JoypadButtons.None, frames: Frames);
        return probe.Read(address: (ushort)result.Variables["ticks"]) >= Frames - BootSlack;
    }

    internal static Puck.Assets.Documents.AudioDocument Track() => new(
        Schema: Puck.Assets.Documents.AudioDocument.CurrentSchema, Name: "t", Tempo: 8,
        Patterns: [[new Puck.Assets.Documents.AudioRowDocument(Note: "C4", Duty: null, Envelope: null)]], Order: [0], Effects: null);

    private static CartridgeStatement Set(string target, string operation, CartridgeValue value) =>
        new(Kind: "set", Target: new CartridgeTarget(Variable: target), Operation: operation, Value: value);
}
