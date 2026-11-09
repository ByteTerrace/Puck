using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Puck.Abstractions;
using Puck.Cli.Motion;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Sweep;

/// <summary>A route segment a tour stop plays: the controller program of one authored <c>test</c> block, its
/// <c>when</c> lines as console lines pinned to tick offsets from the first, and its <c>expect</c> lines as the arrival
/// the windowed run is judged by. The test block is the one source of the program: <c>puck test --reproduce</c> plays it
/// headless and proves it arrives and reproduces, and the tour plays the same lines in the windowed World.</summary>
/// <param name="Reference">The plan's spelling, <c>&lt;source&gt;#&lt;test name&gt;</c>.</param>
/// <param name="Steps">The program's lines, each at its offset from the first line's tick.</param>
/// <param name="Duration">The ticks from the first line to the test's export tick: the program plus its settle.</param>
/// <param name="Gates">The test's expectations as written, which the arrival is judged by.</param>
public sealed partial record SweepRoute(string Reference, IReadOnlyList<SweepStep> Steps, int Duration, IReadOnlyList<string> Gates) {
    /// <summary>Compiles a route's source and reads its test block.</summary>
    /// <param name="reference">The plan's spelling, <c>&lt;source&gt;#&lt;test name&gt;</c>, the source relative to the
    /// repository root.</param>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="route">The route.</param>
    /// <param name="error">Why the reference names no playable route.</param>
    /// <returns><see langword="true"/> when the route was read.</returns>
    public static bool TryLoad(string reference, string repositoryRoot, out SweepRoute? route, out string error) {
        route = null;

        var hash = reference.IndexOf(value: '#', comparisonType: StringComparison.Ordinal);

        if ((hash <= 0) || (hash == (reference.Length - 1))) {
            error = $"the route '{reference}' is not <source>#<test name>";

            return false;
        }

        var source = Path.GetFullPath(path: reference[..hash], basePath: repositoryRoot);
        var name = reference[(hash + 1)..];

        if (!File.Exists(path: source)) {
            error = $"the route's source {reference[..hash]} does not exist";

            return false;
        }

        _ = new WorldCompileCache(directory: WorldCompileCache.Shared.Directory).TryCompile(compiled: out var compiled, failure: out var failure, includeTests: true, path: source);

        if (failure is not null) {
            error = $"the route's source {reference[..hash]} does not compile: {string.Join(separator: "; ", values: failure.Diagnostics.Select(selector: static diagnostic => diagnostic.Message).Take(count: 3))}";

            return false;
        }

        var test = compiled!.Tests.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Test, b: name, comparisonType: StringComparison.Ordinal));

        if (test is null) {
            error = $"the route's source {reference[..hash]} has no test \"{name}\" (it has {string.Join(separator: ", ", values: compiled.Tests.Select(selector: static candidate => $"\"{candidate.Test}\""))})";

            return false;
        }
        if (test.Siblings.Count > 0) {
            error = $"the route \"{name}\" arms sibling worlds, and a tour stop plays one world";

            return false;
        }

        var document = (JsonNode.Parse(utf8Json: test.Json) as JsonObject);
        var rows = (document?["schedule"]?["rows"] as JsonArray);
        var settle = (document?["schedule"]?["settleTicks"]?.GetValue<long>() ?? 0L);

        if ((rows is null) || (rows.Count == 0)) {
            error = $"the route \"{name}\" has no when lines";

            return false;
        }

        var steps = new List<SweepStep>(capacity: rows.Count);
        var first = rows[0]!["tick"]!.GetValue<long>();
        var last = first;

        foreach (var row in rows.OfType<JsonObject>()) {
            // A tour line runs as the console, which drives seat 1's lane; a program for another seat would play as seat 1.
            if (!string.Equals(a: row["principal"]?.GetValue<string>(), b: "seat1", comparisonType: StringComparison.Ordinal) || (row["world"] is not null) || (row["expect"] is not null)) {
                error = $"the route \"{name}\" has the line '{row["command"]}', which is not an admitted seat 1 line of the booted world; a tour plays seat 1's program alone";

                return false;
            }

            var tick = row["tick"]!.GetValue<long>();

            steps.Add(item: new SweepStep(At: checked((int)(tick - first)), Run: row["command"]!.GetValue<string>()));
            last = tick;
        }

        var gates = (document?["state"]?["world"] as JsonArray)?.OfType<JsonObject>()
            .Select(selector: static row => row["verdict"]?["gate"]?.GetValue<string>())
            .OfType<string>()
            .ToArray() ?? [];

        route = new SweepRoute(Duration: checked((int)((last - first) + settle)), Gates: gates, Reference: reference, Steps: steps);
        error = string.Empty;

        return true;
    }

    /// <summary>Judges the route's arrival from the windowed body's <c>body.where 0</c> answer at the route's end. The
    /// gates read here are the test's body position and grounded claims; any other claim is judged by the headless run
    /// alone and returned as unjudged.</summary>
    /// <param name="where">The <c>body.where 0</c> answer.</param>
    /// <param name="unjudged">The gates this answer cannot judge.</param>
    /// <returns>Each gate the body fails, with the value it read; empty when the body arrived.</returns>
    public IReadOnlyList<string> Arrival(string where, out IReadOnlyList<string> unjudged) {
        var failures = new List<string>();
        var skipped = new List<string>();
        var posed = MotionRig.TryPoseOf(answer: where, position: out var position, yawDegrees: out _);
        // The body's reading as the plan's rule metrics, so a gate is judged by the sweep's one rule evaluator.
        var metrics = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["x"] = position.X.ToString(provider: CultureInfo.InvariantCulture),
            ["y"] = position.Y.ToString(provider: CultureInfo.InvariantCulture),
            ["z"] = position.Z.ToString(provider: CultureInfo.InvariantCulture),
        };
        var grounded = MotionRig.FactsOf(answer: where).Split(separator: '|').Contains(value: "grounded");

        foreach (var gate in Gates) {
            if (posed && (AxisGatePattern().Match(input: gate) is { Success: true } axis) && SweepRule.TryParse(rule: out var rule, text: $"{axis.Groups["axis"].Value} {axis.Groups["op"].Value} {axis.Groups["value"].Value}")) {
                if (rule.Holds(metrics: metrics) != true) {
                    failures.Add(item: $"{gate} (read {metrics[rule.Metric]})");
                }
            } else if (GroundedGatePattern().IsMatch(input: gate)) {
                if (!grounded) {
                    failures.Add(item: $"{gate} (not grounded)");
                }
            } else {
                skipped.Add(item: gate);
            }
        }

        unjudged = skipped;

        return failures;
    }

    [GeneratedRegex(pattern: @"^bodyPosition\(body, 0, (?<axis>[xyz])\) (?<op>>=|<=|==|!=|>|<) (?<value>-?\d+(?:\.\d+)?)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex AxisGatePattern();
    [GeneratedRegex(pattern: @"^fact\(body, 0, Grounded\) == 1$", options: RegexOptions.CultureInvariant)]
    private static partial Regex GroundedGatePattern();
}
/// <summary>What the live World says about one exhibit a route passes: its prototype, the text its creation carries (a
/// gallery plinth's label and plaque) and the spawn point named after it.</summary>
/// <param name="Exhibit">The placement's id.</param>
/// <param name="Prototype">The prototype the placement stands, or <see langword="null"/> when the World names no such placement.</param>
/// <param name="Text">The creation's text runs, in order.</param>
/// <param name="Spawn">The planar position of the spawn point named after the exhibit, or <see langword="null"/> when there is none.</param>
public sealed record SweepExhibit(string Exhibit, string? Prototype, IReadOnlyList<string> Text, (double X, double Z)? Spawn) {
    /// <summary>Reads an exhibit through the World's own document read-backs (<c>world.row</c>).</summary>
    /// <param name="world">The World.</param>
    /// <param name="exhibit">The placement's id.</param>
    /// <param name="spawns">The <c>world.row spawnPoints</c> answer, read once per stop.</param>
    /// <returns>The exhibit, with what the World could not answer left empty.</returns>
    public static SweepExhibit Read(SweepWorld world, string exhibit, string spawns) {
        var placement = world.Run(script: $"world.row placements {exhibit}");
        string? prototype = null;

        if (placement.Ok && (placement.Output.IndexOf(value: " = ", comparisonType: StringComparison.Ordinal) is var equals and >= 0)) {
            try {
                prototype = JsonNode.Parse(json: placement.Output[(equals + 3)..].TrimEnd().TrimEnd(']'))?["prototypeId"]?.GetValue<string>();
            } catch (JsonException) {
                prototype = null;
            }
        }

        var text = new List<string>();

        if (prototype is not null) {
            var runs = world.Run(script: $"world.row creations {prototype} document.textRuns");

            foreach (var line in (runs.Ok ? runs.Output.Split(separator: '\n') : [])) {
                if (line.IndexOf(value: ": - {", comparisonType: StringComparison.Ordinal) is var start and >= 0) {
                    try {
                        if (JsonNode.Parse(json: line[(start + 4)..].TrimEnd().TrimEnd(']'))?["text"]?.GetValue<string>() is { } run) {
                            text.Add(item: run);
                        }
                    } catch (JsonException) {
                        // A line that is not one run's JSON names no text.
                    }
                }
            }
        }

        (double X, double Z)? spawn = null;
        var marker = $"\"{exhibit}\" {{";

        foreach (var line in spawns.Split(separator: '\n')) {
            if (line.IndexOf(value: marker, comparisonType: StringComparison.Ordinal) is var at and >= 0) {
                try {
                    if (JsonNode.Parse(json: line[(at + marker.Length - 1)..].TrimEnd().TrimEnd(']'))?["position"] is JsonArray position) {
                        spawn = (position[0]!.GetValue<double>(), position[2]!.GetValue<double>());
                    }
                } catch (JsonException) {
                    spawn = null;
                }
            }
        }

        return new SweepExhibit(Exhibit: exhibit, Prototype: prototype, Spawn: spawn, Text: text);
    }
}
/// <summary>The mesh audit's verdict on the prototypes a route's captures name (<c>puck mesh audit --cpu</c>): each
/// prototype's surface groups, their score and their topology defects, read from the audit's own report.</summary>
public static partial class SweepMeshAudit {
    /// <summary>Audits prototypes in one run of this CLI's <c>puck mesh audit --cpu</c>, which bakes each group offline
    /// through the bake's own lattices and measures the mesh against its field.</summary>
    /// <param name="world">The world document or package.</param>
    /// <param name="prototypes">The prototypes.</param>
    /// <param name="repositoryRoot">The working directory.</param>
    /// <param name="budget">The sweep's budget, which bounds the run.</param>
    /// <param name="transcript">The file the audit's whole output is written to.</param>
    /// <returns>Each prototype's findings, worst defect first; a prototype the audit printed nothing for maps to a
    /// reason it was not measured.</returns>
    public static IReadOnlyDictionary<string, SweepAuditFinding> Run(string world, IReadOnlyCollection<string> prototypes, string repositoryRoot, SuiteBudget budget, string transcript) {
        var findings = new Dictionary<string, SweepAuditFinding>(comparer: StringComparer.Ordinal);

        if (prototypes.Count == 0) {
            return findings;
        }

        string[] arguments = [SweepCommand.CliEntry, "mesh", "audit", "--world", PuckPaths.Normalize(path: world), "--cpu", "--top", "1", "--worst", "0", "--prototype", .. prototypes];
        string output;

        try {
            var run = CliProcess.RunCaptured(
                arguments: arguments,
                cancellationToken: budget.Token,
                fileName: "dotnet",
                input: string.Empty,
                started: static process => ChildProcessJob.Assign(process: process),
                timeout: Timeout.InfiniteTimeSpan,
                workingDirectory: repositoryRoot
            );

            output = (run.Stdout + run.Stderr);
        } catch (Exception exception) when ((exception is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)) {
            output = $"puck mesh audit did not run: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
        }

        File.WriteAllText(contents: output, path: transcript);

        string? current = null;
        var scores = new Dictionary<string, double>(comparer: StringComparer.Ordinal);
        var defects = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);

        foreach (var line in output.Split(separator: '\n')) {
            if (GroupPattern().Match(input: line) is { Success: true } group) {
                current = group.Groups["prototype"].Value;
                scores[current] = Math.Max(val1: scores.GetValueOrDefault(key: current), val2: double.Parse(provider: CultureInfo.InvariantCulture, s: group.Groups["score"].Value));
                _ = defects.TryAdd(key: current, value: []);

                continue;
            }
            if ((current is not null) && TopologyPattern().Match(input: line) is { Success: true } topology) {
                foreach (var name in new[] { "folds", "flipped", "open", "nonmanifold", "uncovered" }) {
                    var count = int.Parse(provider: CultureInfo.InvariantCulture, s: topology.Groups[name].Value);

                    if (count > 0) {
                        defects[current].Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{count} {Spelling(name: name)}"));
                    }
                }
                current = null;
            }
        }

        foreach (var prototype in prototypes) {
            findings[prototype] = (scores.TryGetValue(key: prototype, value: out var score)
                ? new SweepAuditFinding(Defects: defects[prototype], Measured: true, Score: score)
                : new SweepAuditFinding(Defects: [], Measured: false, Score: null));
        }

        return findings;
    }

    private static string Spelling(string name) => name switch {
        "open" => "open edges",
        "nonmanifold" => "non-manifold edges",
        "uncovered" => "uncovered crossings",
        _ => name,
    };

    [GeneratedRegex(pattern: @"^group \d+ instance \S+ .+? \((?<prototype>[^)]+)\): \d+ triangles, .*score (?<score>\d+(?:\.\d+)?)", options: RegexOptions.CultureInvariant)]
    private static partial Regex GroupPattern();
    [GeneratedRegex(pattern: @"topology: (?<folds>\d+) folds, (?<flipped>\d+) flipped, \d+ slivers, (?<open>\d+) open edges, (?<nonmanifold>\d+) non-manifold edges; surface: \d+ crossings, (?<uncovered>\d+) uncovered", options: RegexOptions.CultureInvariant)]
    private static partial Regex TopologyPattern();
}
/// <summary>What a tour needs to play route stops beyond its World: where routes and the world resolve, and the
/// sweep's budget for the mesh audit it runs on what the captures frame.</summary>
/// <param name="Budget">The sweep's budget.</param>
/// <param name="RepositoryRoot">The repository root, which a route's source is relative to.</param>
/// <param name="World">The world document or package the World booted, which the mesh audit reads.</param>
public sealed record SweepRouteContext(SuiteBudget Budget, string RepositoryRoot, string World);
/// <summary>One prototype's mesh audit.</summary>
/// <param name="Measured">Whether the audit reported a surface group for it.</param>
/// <param name="Score">The worst group's score, or <see langword="null"/> when unmeasured.</param>
/// <param name="Defects">The topology defects its groups carry: folds, flipped triangles, open and non-manifold edges,
/// uncovered crossings; empty when it has none.</param>
public sealed record SweepAuditFinding(bool Measured, double? Score, IReadOnlyList<string> Defects);
