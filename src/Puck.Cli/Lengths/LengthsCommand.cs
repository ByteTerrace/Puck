using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Lengths;

/// <summary><c>puck lengths</c> — checks or regenerates <c>FileLengths.json</c>, the ledger <c>FileLengthAnalyzer</c>
/// (LEN001–LEN004) reads in every compilation. The analyzer sees one compilation at a time, so a ledger entry whose
/// file was deleted or moved never reaches it; this verb walks the tracked tree instead. <c>--check</c> (the default)
/// reports every entry that is stale (file gone, or at or under the ceiling), every recorded file that grew, and every
/// unrecorded file over the ceiling, and exits 1 on any. <c>--write</c> rewrites the ledger from the tree: it removes
/// stale entries and lowers shrunken ones, and refuses (exit 1, naming the file) to raise a recorded length or add a
/// new file — the ledger only shrinks. The line count is line breaks plus one, the count the analyzer reads.</summary>
internal static class LengthsCommand {
    private const string LedgerFileName = "FileLengths.json";
    private static readonly Regex GeneratedName = new(options: RegexOptions.Compiled | RegexOptions.IgnoreCase, pattern: @"\.(g|generated|designer|g\.i)\.cs$");
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, IndentSize = 4 };

    public static int Run(string[] args) {
        if ((Array.IndexOf(array: args, value: "-h") >= 0) || (Array.IndexOf(array: args, value: "--help") >= 0)) {
            return Usage();
        }

        var write = false;

        foreach (var argument in args) {
            switch (argument) {
                case "--check":
                    break;
                case "--write":
                    write = true;
                    break;
                default:
                    Console.Error.WriteLine(value: $"ERROR: unrecognized argument '{argument}'; the accepted forms are: lengths [--check] | lengths --write");

                    return 2;
            }
        }

        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var ledgerPath = Path.Combine(path1: repositoryRoot, path2: LedgerFileName);

        if (!TryReadLedger(path: ledgerPath, ceiling: out var ceiling, recorded: out var recorded, error: out var error)) {
            Console.Error.WriteLine(value: $"ERROR: {LedgerFileName} is unusable: {error}");

            return 2;
        }

        var actual = MeasureTree(repositoryRoot: repositoryRoot);
        var problems = new List<string>();
        var next = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var (key, recordedLength) in recorded) {
            if (!actual.TryGetValue(key: key, value: out var lines)) {
                problems.Add(item: $"stale: '{key}' is recorded at {recordedLength} but is not a tracked source file");
            } else if (lines <= ceiling) {
                problems.Add(item: $"stale: '{key}' is {lines} lines, at or under the {ceiling}-line ceiling, but is recorded at {recordedLength}");
            } else if (lines > recordedLength) {
                problems.Add(item: $"grew: '{key}' is {lines} lines, over the {recordedLength} recorded; a file over the ceiling may only shrink");
                next[key] = recordedLength;
            } else {
                next[key] = lines;
            }
        }

        foreach (var (key, lines) in actual) {
            if ((lines > ceiling) && !recorded.ContainsKey(key: key)) {
                problems.Add(item: $"new: '{key}' is {lines} lines, over the {ceiling}-line ceiling and not recorded; split it rather than recording it");
            }
        }

        if (!write) {
            foreach (var problem in problems) {
                Console.Error.WriteLine(value: $"lengths: {problem}");
            }

            Console.WriteLine(value: $"lengths: ceiling {ceiling}; {recorded.Count} recorded file(s); {problems.Count} problem(s).");

            return ((problems.Count == 0) ? 0 : 1);
        }

        var refusals = problems.Where(predicate: static problem => (problem.StartsWith(value: "grew:", comparisonType: StringComparison.Ordinal) || problem.StartsWith(value: "new:", comparisonType: StringComparison.Ordinal))).ToList();

        foreach (var refusal in refusals) {
            Console.Error.WriteLine(value: $"lengths: {refusal}");
        }

        if (refusals.Count != 0) {
            Console.Error.WriteLine(value: "lengths: --write refuses to raise a recorded length or record a new file; nothing written.");

            return 1;
        }

        WriteLedger(path: ledgerPath, ceiling: ceiling, recorded: next);

        var lowered = recorded.Count(predicate: pair => (next.TryGetValue(key: pair.Key, value: out var lines) && (lines < pair.Value)));
        var removed = (recorded.Count - next.Count);

        Console.WriteLine(value: $"lengths: wrote {LedgerFileName} — {next.Count} recorded file(s), {lowered} lowered, {removed} removed.");

        return 0;
    }

    private static bool TryReadLedger(string path, out int ceiling, out Dictionary<string, int> recorded, out string? error) {
        ceiling = 0;
        recorded = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        error = null;

        if (!File.Exists(path: path)) {
            error = $"'{path}' does not exist.";

            return false;
        }

        JsonNode? root;

        try {
            root = JsonNode.Parse(json: File.ReadAllText(path: path));
        } catch (JsonException exception) {
            error = $"malformed JSON ({exception.Message}).";

            return false;
        }

        if ((root is not JsonObject document) || (document["format"]?.GetValue<int>() != 1) || (document["ceiling"] is not JsonValue ceilingValue) || (document["recorded"] is not JsonObject recordedObject)) {
            error = "expected an object with 'format' 1, an integer 'ceiling', and a 'recorded' object.";

            return false;
        }

        ceiling = ceilingValue.GetValue<int>();

        foreach (var pair in recordedObject) {
            if ((pair.Value is not JsonValue value) || !value.TryGetValue<int>(value: out var lines) || (lines <= ceiling)) {
                error = $"recorded length for '{pair.Key}' must be an integer above the ceiling.";

                return false;
            }

            recorded[pair.Key] = lines;
        }

        return true;
    }

    // The trees the root build compiles; experimental/ is quarantined and never reaches the analyzer.
    private static Dictionary<string, int> MeasureTree(string repositoryRoot) {
        var result = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var listing = CliProcess.RunCaptured(fileName: "git", arguments: ["-C", repositoryRoot, "ls-files", "-z", "--", "src/*.cs", "tests/*.cs", "build/*.cs"], input: "", timeout: TimeSpan.FromMinutes(value: 1));

        if (listing.ExitCode != 0) {
            throw new InvalidOperationException(message: $"git ls-files failed: {listing.Stderr}");
        }

        foreach (var relative in listing.Stdout.Split(separator: '\0', options: StringSplitOptions.RemoveEmptyEntries)) {
            if (GeneratedName.IsMatch(input: relative)) {
                continue;
            }

            var fullPath = Path.Combine(path1: repositoryRoot, path2: relative);

            if (!File.Exists(path: fullPath)) {
                continue;
            }

            var bytes = File.ReadAllBytes(path: fullPath);

            if (IsAutoGenerated(bytes: bytes)) {
                continue;
            }

            result[relative] = (bytes.Count(predicate: static b => (b == (byte)'\n')) + 1);
        }

        return result;
    }

    // The same header heuristic the compiler uses to mark a tree generated.
    private static bool IsAutoGenerated(byte[] bytes) {
        var head = Encoding.UTF8.GetString(bytes: bytes, index: 0, count: Math.Min(val1: bytes.Length, val2: 512));

        return (head.Contains(value: "<auto-generated", comparisonType: StringComparison.OrdinalIgnoreCase) || head.Contains(value: "<autogenerated", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteLedger(string path, int ceiling, SortedDictionary<string, int> recorded) {
        var recordedObject = new JsonObject();

        foreach (var (key, lines) in recorded) {
            recordedObject[key] = lines;
        }

        var document = new JsonObject {
            ["format"] = 1,
            ["ceiling"] = ceiling,
            ["recorded"] = recordedObject,
        };

        File.WriteAllText(path: path, contents: (document.ToJsonString(options: WriteOptions).ReplaceLineEndings(replacementText: "\n") + "\n"), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static int Usage() {
        Console.Error.WriteLine(value:
            """
            puck lengths — the file-length ledger FileLengthAnalyzer reads

            Usage:
              puck lengths [--check]   report stale, grown, and unrecorded-over-ceiling files; exit 1 on any
              puck lengths --write     rewrite FileLengths.json from the tree: remove stale entries, lower shrunken
                                       ones; refuses to raise a recorded length or record a new file (exit 1)

            The line count is line breaks plus one — what the analyzer counts. Generated files (*.g.cs and
            auto-generated headers) are outside the rule, as they are for the analyzer.
            """
        );

        return 2;
    }
}
