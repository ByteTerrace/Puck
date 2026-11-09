using System.CommandLine;

using Puck.Transpiler.Diagnostics;
using Puck.World;
using Puck.World.Authoring;
using Puck.World.Server;

namespace Puck.Cli.Creation;

/// <summary>
/// <c>puck creation</c> — the offline reads of a creation: <c>stats --world &lt;path&gt; [--prototype &lt;id&gt;]</c> reports a creation's shape budget and feature
/// usage, a PUCK122 warning line for each blend joining shapes that move independently
/// (<see cref="CreationMotion.MovingBlends"/>), field-scope clamps and segments, then constructs the world's deterministic contact field, and <c>shape --world
/// &lt;path&gt; --prototype &lt;id&gt; [--shape &lt;name&gt;...]</c> reads each shape's law (<see cref="CreationShapeLaws"/>),
/// the twin of the in-engine <c>creation.shape</c>. Exit codes: 0 succeeded, 1 loading, render emission or contact
/// compilation failed (stats), or loading failed (shape), 2 a usage error (unknown prototype/shape name, missing file).
/// </summary>
public static partial class CreationCommand {
    public static Command Create() => new(
        description: "Inspect creations: their field budget and each shape's law.",
        name: "creation"
    ) {
        StatsCommand(),
        ShapeCommand(),
    };

    private static Command StatsCommand() {
        var worldOption = new Option<string>(name: "--world") { Description = "The world document to inspect.", Required = true };
        var prototypeOption = new Option<string?>(name: "--prototype") { Description = "Limit to one prototype id (default: every prototype carrying a creation document)." };
        var command = new Command(
            description: "Reports a creation's expanded field work and encoded bytes, counts by primitive/blend, which shapes use domain/onion/twist/bend/rounding, palette slot usage, and global/shared field clamps and segments for static and pooled rest geometry. Constructs the whole world's deterministic contact field, including solid placements, even when a prototype filter is supplied.",
            name: "stats"
        ) { worldOption, prototypeOption };

        command.SetAction(action: parseResult => RunStats(
            prototypeId: parseResult.GetValue(option: prototypeOption),
            worldPath: parseResult.GetValue(option: worldOption)!
        ));

        return command;
    }
    private static Command ShapeCommand() {
        var worldOption = new Option<string>(name: "--world") { Description = "The world document to inspect.", Required = true };
        var prototypeOption = new Option<string>(name: "--prototype") { Description = "The prototype id whose creation to read.", Required = true };
        var shapeOption = new Option<string[]>(name: "--shape") { AllowMultipleArgumentsPerToken = true, Description = "Limit to the named shapes (default: every shape)." };
        var command = new Command(
            description: "Reads back each shape's law, as the in-engine creation.shape does: what the renderer lowers it to, whether that field is exact, a bound (its gain and step scale) or uncertified, whether the fixed-point evaluator interprets it, and what deterministic contact makes of it on a solid placement.",
            name: "shape"
        ) { worldOption, prototypeOption, shapeOption };

        command.SetAction(action: parseResult => RunShape(
            prototypeId: parseResult.GetValue(option: prototypeOption)!,
            shapeNames: (parseResult.GetValue(option: shapeOption) ?? []),
            worldPath: parseResult.GetValue(option: worldOption)!
        ));

        return command;
    }
    private static int RunShape(string worldPath, string prototypeId, string[] shapeNames) {
        if (!CliWorldDocuments.TryLoad(definition: out var definition, fullPath: out var fullPath, path: worldPath, verb: "creation shape")) {
            return CliExit.Refused;
        }

        var creations = definition.Creations;

        if (WorldDefinitionRows.FindCreation(creations: creations, id: prototypeId) is not { } creation) {
            Console.Error.WriteLine(value: $"creation shape: '{fullPath}' names no prototype '{prototypeId}' — prototypes: {string.Join(separator: ", ", values: creations.Select(selector: static candidate => candidate.Id.Value))}");

            return 2;
        }

        // Normalized and in the engine frame, as the World's creation.shape reads it.
        var document = creation.EngineDocument;
        var shapes = (document.Shapes ?? []);
        var selected = new List<int>();

        foreach (var name in shapeNames) {
            var found = shapes.ToList().FindIndex(match: shape => string.Equals(a: shape.Name?.Value, b: name, comparisonType: StringComparison.Ordinal));

            if (found < 0) {
                Console.Error.WriteLine(value: $"creation shape: '{prototypeId}' has no shape named '{name}' — shapes: {string.Join(separator: ", ", values: shapes.Select(selector: static (shape, at) => (shape.Name?.Value ?? $"#{at}")))}");

                return 2;
            }
            selected.Add(item: found);
        }

        var laws = ((selected.Count == 0)
            ? CreationShapeLaws.Of(document: document)
            : [.. selected.Select(selector: index => CreationShapeLaws.Of(document: document, index: index))]);

        Console.Out.WriteLine(value: CreationShapeLaws.Describe(laws: laws, prototype: prototypeId, shapeCount: shapes.Count, verb: "creation shape"));

        return 0;
    }
    private static int RunStats(string worldPath, string? prototypeId) {
        if (!CliWorldDocuments.TryLoad(definition: out var loaded, fullPath: out var fullPath, path: worldPath, verb: "creation stats")) {
            return CliExit.Refused;
        }

        var definition = loaded;

        var prototypes = definition.Creations
            .Where(predicate: p => (p.Document is not null))
            .Where(predicate: p => ((prototypeId is null) || string.Equals(
            a: p.Id,
            b: prototypeId,
            comparisonType: StringComparison.Ordinal
        )))
            .ToList();

        if (prototypes.Count == 0) {
            Console.Error.WriteLine(value: ((prototypeId is null)
                ? $"creation stats: '{fullPath}' declares no prototype carrying a creation document."
                : $"creation stats: '{fullPath}' names no prototype '{prototypeId}' with a creation document."));

            return 2;
        }

        foreach (var prototype in prototypes) {
            ReportPrototype(
                document: prototype.Document!,
                id: prototype.Id
            );
            try {
                ReportStepClamps(
                    definition: definition,
                    prototype: prototype
                );
            } catch (Exception exception) when ((exception is InvalidOperationException or ArgumentException)) {
                return CliExit.Refuse(verb: "creation stats", what: $"'{prototype.Id}'", why: $"step-clamp inspection failed: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }
        }

        try {
            if (!WorldSolidField.TryBuild(
                definition,
                out _,
                out var contactReason
            )) {
                return CliExit.Refuse(verb: "creation stats", what: "the world", why: $"contact inspection failed: {contactReason}");
            }
        } catch (Exception exception) when ((exception is InvalidOperationException or ArgumentException)) {
            return CliExit.Refuse(verb: "creation stats", what: "the world", why: $"contact inspection failed: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        Console.Out.WriteLine(value: "  contact: accepted — the world's deterministic field compiled, including solid placements and screens; render-only facets remain omitted.");
        return 0;
    }
    private static void ReportPrototype(string id, CreationDocument document) {
        var shapes = (document.Shapes ?? []);
        var stampShapes = document.StampShapeCount();

        Console.Out.WriteLine(value: $"[{id}] shapes: {shapes.Count}, expanded field shapes: {stampShapes}; bake work and encoded bytes are reported below, with no shape-count ceiling.");

        WriteHistogram(
            label: "primitive",
            values: shapes.Select(selector: static s => s.Type.ToString())
        );
        WriteHistogram(
            label: "blend",
            values: shapes.Select(selector: static s => (s.Blend?.ToString() ?? "Union"))
        );
        Console.Out.WriteLine(value: $"  domain: {shapes.Count(predicate: static s => (s.Domain is { Count: > 0 }))}, onion: {shapes.Count(predicate: static s => (s.Onion is > 0))}, twist: {shapes.Count(predicate: static s => (s.Twist is not (null or 0f)))}, bend: {shapes.Count(predicate: static s => (s.Bend is not (null or 0f)))}, rounding: {shapes.Count(predicate: static s => (s.Rounding is > 0))}, dilate: {shapes.Count(predicate: static s => (s.Dilate is > 0))}, lift: {shapes.Count(predicate: static s => (s.Lift is not null))}, chamfer: {shapes.Count(predicate: static s => (s.Chamfer is > 0))}, panel: {shapes.Count(predicate: static s => (s.Panel is not null))}");

        foreach (var shape in shapes) {
            List<string> facets = [];

            if (shape.Flare is not null) { facets.Add(item: "flare"); }
            if (shape.Shear is not null) { facets.Add(item: "shear"); }
            if (shape.Bumps is { Count: > 0 }) { facets.Add(item: "bumps"); }
            if (shape.Warp is not null) { facets.Add(item: "warp"); }
            if (shape.Displace is not null) { facets.Add(item: "displace"); }
            if (shape.Elongate is not null) { facets.Add(item: "elongate"); }
            if (shape.Erode is not null) { facets.Add(item: "erode"); }
            if (shape.Cells is not null) { facets.Add(item: "cells"); }
            if (facets.Count > 0) {
                Console.Out.WriteLine(value: $"  shape {shape.Id} ({(shape.Name?.Value ?? shape.Type.ToString())}): {string.Join(
                    separator: ", ",
                    values: facets
                )}");
            }
        }

        foreach (var blend in CreationMotion.MovingBlends(document: document)) {
            var label = (CreationMotion.Creases(blend: blend, document: document) ? $"warning {PuckDiagnosticCodes.MovingBlend}" : "skin");

            Console.Out.WriteLine(value: $"  {label}: '{id}' {CreationMotion.Describe(blend: blend, document: document)}");
        }
        foreach (var shape in CreationMotion.UngroupedBlends(document: document)) {
            Console.Out.WriteLine(value: $"  warning {PuckDiagnosticCodes.UngroupedBlend}: '{id}' {CreationMotion.DescribeUngrouped(document: document, shape: shape)}");
        }

        if (document.Palette is { Count: > 0 } palette) {
            var usage = new int[palette.Count];

            foreach (var shape in shapes) {
                if (
                    (shape.Material is { } slot) &&
                    (slot >= 0) &&
                    (slot < usage.Length)
                ) {
                    usage[slot]++;
                }
            }

            Console.Out.WriteLine(value: $"  palette: {string.Join(
                separator: ", ",
                values: usage.Select(selector: (count, index) => $"{index}={count}")
            )}");
        }
    }
    private static void WriteHistogram(string label, IEnumerable<string> values) {
        var counts = values
            .GroupBy(keySelector: static v => v)
            .OrderByDescending(keySelector: static g => g.Count())
            .Select(selector: g => $"{g.Key}={g.Count()}");

        Console.Out.WriteLine(value: $"  {label}: {string.Join(
            separator: ", ",
            values: counts
        )}");
    }
}
