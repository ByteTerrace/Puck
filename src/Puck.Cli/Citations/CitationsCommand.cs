using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Puck.Cli.Source;

namespace Puck.Cli.Citations;

// The `puck citations` verb: every verb-shaped token a document CITES, checked against vocabularies swept
// from the code itself.
//
// Two corpora, because a dead name can hide in either: markdown backticks under .claude/skills (agent
// guidance, loaded as authoritative) and <c>…</c> tokens in src/ XML docs.
//
// There is NO suppression list, deliberately. A token passes by resolving in a live vocabulary, never by
// being named in a file someone has to remember to update — an exclusion that outlives its reason is
// indistinguishable from a defect it is hiding. Every vocabulary below is derived from source, so it cannot
// drift from what the code does.
//
// The console-verb vocabulary itself is BOOTED, not read from a file: with no --enumeration override, this
// verb builds and runs the real Puck.World twice — headless and windowed — piping `help` over stdin each
// time and unioning the two vocabularies. Some command modules register only under one boot shape (a
// presentation-only verb never registers headless), so either boot alone under-reports the surface.
//
// THE HONESTY CONTRACT, which is the whole reason this verb replaces a script:
//
//   A citation checker has two ways to be wrong, and they are not symmetric. It can miss a dead name
//   (quiet, and the reason the check exists). Or it can accuse a CORRECT citation because its own input
//   rotted — which is louder, wastes the author's time, and teaches everyone to distrust the check.
//
//   So this verb refuses to report when it can prove its own input is incomplete (exit 3), and it names
//   the INPUT as the suspect rather than the citation. It can prove incompleteness because the static
//   sweep of verb registrations is a LOWER BOUND on the live surface: a name spelled literally in a
//   registration exists, whatever the enumeration says. If the enumeration is missing one, the enumeration
//   under-reports the tree — that is a fact about the boot (or the supplied file), not about any document
//   quoting it.
//
// WHAT THIS VERB CANNOT SEE, stated so nobody reads a pass as more than it is. A retired verb whose name
// still sits in ANY string literal under src/ — a refusal message nobody updated, say — resolves here, and
// so does every document citing it. The stale string hides itself AND its citers. Retiring a verb therefore
// still owes a sweep of src/ runtime strings; this verb does not discharge that obligation and cannot.
//
// Exit 0 every citation resolved, 1 unresolved citations (each named), 2 usage error, no repository root,
// or the enumeration boot/build refused, 3 the enumeration is provably incomplete and nothing was reported
// against it.
internal static class CitationsCommand {
    private const string HelpText =
        """
        puck citations — check cited verb tokens against vocabularies swept from the code

        Usage: puck citations [options]

        Options:
          --enumeration <path>  The console verb list, one name per line, as the runtime `help`
                                enumerates them. Absent, this verb builds Puck.World (Release) and
                                boots it headless and windowed, piping `help` over stdin to each and
                                unioning the two vocabularies — there is no default file.
          -h, --help            This text.

        Scans .claude/skills/**/*.md for `backticked` tokens and src/**/*.cs for <c>…</c> tokens,
        keeps those shaped like a console verb, and resolves each against: the enumeration, verb
        names spelled literally in registrations, every other verb-shaped string literal in src/ (a
        refusal door, a HUD binding token — names the code really knows), and every world-document
        field path the generated section schemas declare (`storage.userId`).

        Exits 3 without reporting if a literally-registered verb is missing from the enumeration:
        that proves the enumeration under-reports the live surface, and reporting citations against
        an incomplete vocabulary produces confident accusations of correct documentation.
        """;

    // The families the console actually uses. A dotted token outside them is some other kind of name and
    // is not this verb's business.
    private static readonly Regex Family = new(
        options: RegexOptions.Compiled,
        pattern: @"^(world|player|screen|editor|identity|chat|replay|storage|capture|audio|view|speaker|channel|wire)\.");
    // A verb-shaped token: lowercase head, at least one dotted segment. Trailing argument text inside the
    // same span is ignored — `world.row.set views.seatRig` cites `world.row.set`.
    private static readonly Regex MarkdownToken = new(
        options: RegexOptions.Compiled,
        pattern: @"`([a-z][A-Za-z0-9]*(?:\.[a-z][A-Za-z0-9-]*)+)[^`]*`");
    private static readonly Regex XmlDocToken = new(
        options: RegexOptions.Compiled,
        pattern: @"<c>([a-z][A-Za-z0-9]*(?:\.[a-z][A-Za-z0-9-]*)+)[^<]*</c>");
    // A verb registration's own name argument — the lower bound the staleness gate rests on.
    //
    // ANCHORED TO LINE START, which is the whole discriminator: a command registration spells `name:` on its
    // own line inside a multi-line CommandDefinition call, while `name:` also appears mid-line as an ordinary
    // named argument to helpers that have nothing to do with verbs — `RequireGain(value: …, name:
    // "audio.masterGain", …)` in the document validator, for one. An unanchored pattern sweeps those in too:
    // a sweep confident enough to refuse a run must be narrow enough to be right, or it becomes the accusing
    // instrument it exists to replace.
    private static readonly Regex Registration = new(
        options: RegexOptions.Compiled,
        pattern: @"^\s*name:\s*""([a-z][A-Za-z0-9]*(?:\.[a-z][A-Za-z0-9-]*)+)""");
    // Any verb-shaped string literal in source: a refusal door, a HUD binding token, a session lever, a
    // document member path. Not a console verb, but a name the code genuinely carries — a document citing
    // one is describing a real mechanism, not a dead verb.
    private static readonly Regex SourceLiteral = new(
        options: RegexOptions.Compiled,
        pattern: @"""([a-z][A-Za-z0-9]*(?:\.[a-z][A-Za-z0-9-]*)+)""");

    public static int Run(string[] args) {
        var scanner = new ArgScanner().Flag(name: "h").Flag(name: "help").Value(name: "enumeration");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"citations: {scanner.Error}");
            Console.Error.WriteLine(value: HelpText);

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.WriteLine(value: HelpText);

            return 0;
        }

        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) {
            return 2;
        }

        var enumerationPath = scanner.Get(name: "enumeration");
        HashSet<string> enumerated;
        string enumerationSource;

        if (enumerationPath is not null) {
            if (!File.Exists(path: enumerationPath)) {
                Console.Error.WriteLine(value: $"citations: no enumeration at '{CliPaths.ToDisplay(fullPath: enumerationPath)}' — nothing to resolve console verbs against.");

                return 2;
            }

            enumerated = ReadEnumeration(path: enumerationPath);
            enumerationSource = CliPaths.ToDisplay(fullPath: enumerationPath);
        } else {
            if (!TryEnumerateLive(enumerated: out enumerated, error: out var liveError, root: root)) {
                Console.Error.WriteLine(value: $"citations: {liveError}");

                return 2;
            }

            enumerationSource = "the live `help` enumeration (booted headless + windowed)";
        }

        var sourceFiles = FileWalk.Enumerate(verb: "citations", roots: [Path.Combine(path1: root, path2: "src")], include: [], exclude: [], extension: ".cs");

        if (sourceFiles is null) {
            return 2;
        }
        // Command modules live in the COMPOSITION ROOT and nowhere else — src/Puck.World/ proper, never its
        // .Data or .Server siblings.
        //
        // The narrowing is a real guard, not tidiness. Line-anchoring alone is FORMATTING-DEPENDENT: the
        // validator's `RequireGain(value: …, name: "audio.masterGain", …)` is family-shaped and escapes the
        // sweep only because it sits mid-line, and this repository's formatter wraps calls argument-per-line
        // as its dominant style. One catch-up pass over Puck.World.Schema would put that `name:` at line start
        // and exit-3 a perfectly current enumeration. A gate whose correctness depends on how a neighbouring
        // file happens to be wrapped is not a gate. Excluding the projects that cannot contain a registration
        // closes the class outright rather than betting on layout.
        var commandRoot = $"{Path.DirectorySeparatorChar}Puck.World{Path.DirectorySeparatorChar}";
        var commandFiles = sourceFiles.Where(predicate: path => path.Contains(comparisonType: StringComparison.Ordinal, value: commandRoot)).ToArray();
        var registered = SweepLiterals(files: commandFiles, pattern: Registration);
        var literals = SweepLiterals(files: sourceFiles, pattern: SourceLiteral);

        literals.UnionWith(other: SweepDocumentFields(root: root));

        // THE COMPLETENESS GATE. A literally-registered verb the enumeration lacks proves the enumeration is
        // behind the tree. Refuse rather than report — see this file's header.
        var missing = registered.Where(predicate: name => !enumerated.Contains(item: name))
            .OrderBy(keySelector: static name => name, comparer: StringComparer.Ordinal)
            .ToArray();

        if (missing.Length > 0) {
            Console.Error.WriteLine(value: $"citations: the enumeration is INCOMPLETE — {missing.Length} verb(s) are registered in source but absent from {enumerationSource}:");

            foreach (var name in missing) {
                Console.Error.WriteLine(value: $"    {name}");
            }

            Console.Error.WriteLine(value: "  Nothing was checked. Reporting citations against an incomplete enumeration accuses correct documentation of quoting dead verbs.");
            Console.Error.WriteLine(value: ((enumerationPath is null)
                ? "  The headless+windowed boot union did not surface every registered verb — investigate the registration path for the named verb(s); there is no file to re-record."
                : "  Re-record the --enumeration file from a boot (or drop --enumeration to enumerate live), then re-run — a hand edit fixes only the direction you noticed."));

            return 3;
        }

        var unresolved = Scan(enumerated: enumerated, literals: literals, root: root, sourceFiles: sourceFiles);

        Console.WriteLine(value: $"citations: {enumerated.Count} enumerated verb(s), {registered.Count} registered in source, {literals.Count} verb-shaped literal(s); {unresolved.Count} unresolved citation(s).");

        if (unresolved.Count == 0) {
            return 0;
        }

        foreach (var citation in unresolved) {
            Console.Error.WriteLine(value: $"  {citation.File}:{citation.Line}: {citation.Token}");
        }

        Console.Error.WriteLine(value: "citations: each token above is cited as a verb but names nothing the code carries — not an enumerated verb, not a registration, not any string literal in src/.");
        Console.Error.WriteLine(value: "  If one names a real mechanism this verb cannot see, the fix is to sweep that vocabulary here, never to suppress the token.");

        return 1;
    }

    // Every citation whose token resolves in no vocabulary, in file then line order.
    private static List<Citation> Scan(string root, IReadOnlyList<string> sourceFiles, HashSet<string> enumerated, HashSet<string> literals) {
        var unresolved = new List<Citation>();
        var skills = Path.Combine(path1: root, path2: ".claude", path3: "skills");

        if (Directory.Exists(path: skills)) {
            var markdown = Directory.EnumerateFiles(path: skills, searchOption: SearchOption.AllDirectories, searchPattern: "*.md")
                .OrderBy(keySelector: static path => path, comparer: StringComparer.Ordinal);

            foreach (var file in markdown) {
                Collect(enumerated: enumerated, file: file, literals: literals, pattern: MarkdownToken, root: root, unresolved: unresolved);
            }
        }

        foreach (var file in sourceFiles) {
            Collect(enumerated: enumerated, file: file, literals: literals, pattern: XmlDocToken, root: root, unresolved: unresolved);
        }

        return unresolved;
    }
    // One file's citations. A token repeated in a file reports once — the reader fixes the name, not each
    // occurrence.
    private static void Collect(string root, string file, Regex pattern, HashSet<string> enumerated, HashSet<string> literals, List<Citation> unresolved) {
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var number = 0;

        foreach (var line in File.ReadLines(path: file)) {
            number++;

            foreach (Match match in pattern.Matches(input: line)) {
                var token = match.Groups[1].Value;

                if (!Family.IsMatch(input: token) || enumerated.Contains(item: token) || literals.Contains(item: token)) {
                    continue;
                }

                if (seen.Add(item: token)) {
                    unresolved.Add(item: new Citation(File: CliPaths.ToDisplay(fullPath: file, relativeTo: root), Line: number, Token: token));
                }
            }
        }
    }

    // A `help` listing line, built by CommandRegistry.BuildHelpText as `{name} - {description}` — the name is
    // group 1.
    private static readonly Regex HelpLine = new(
        options: RegexOptions.Compiled,
        pattern: @"^([a-z][A-Za-z0-9._-]*) - ");

    // Builds Puck.World once (Release) and boots it headless then windowed, piping `help` over stdin to each
    // and unioning the reported verb names. Reuses CliProcess — the same build-then-run shape Canary and
    // Parity already boot the real executable through — rather than a second process launcher.
    private static bool TryEnumerateLive(string root, out HashSet<string> enumerated, out string error) {
        enumerated = new HashSet<string>(comparer: StringComparer.Ordinal);
        error = string.Empty;

        var worldProject = Path.Combine(path1: root, path2: "src", path3: "Puck.World", path4: "Puck.World.csproj");
        var artifact = Path.Combine(paths: [root, "src", "Puck.World", "bin", "Release", "net10.0", "Puck.World.dll"]);

        Console.WriteLine(value: "citations: building Puck.World once (Release) to boot its live console vocabulary.");

        CliProcessResult build;

        try {
            build = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: ["build", worldProject, "-c", "Release", "--nologo", "--no-restore", "-p:NuGetAudit=false"],
                input: string.Empty,
                timeout: TimeSpan.FromSeconds(value: 300)
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            error = $"could not start the Puck.World build: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (build.TimedOut || (build.ExitCode != 0)) {
            error = (build.TimedOut ? "the Puck.World build timed out." : $"the Puck.World build exited {build.ExitCode}.");

            return false;
        }
        if (!File.Exists(path: artifact)) {
            error = $"the Puck.World build exited 0 but did not produce the exact artifact {artifact}.";

            return false;
        }

        var runDirectory = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-citations-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path: runDirectory);

        foreach (var headless in ((bool[])[true, false])) {
            if (!TryBootLeg(artifact: artifact, enumerated: enumerated, error: out error, headless: headless, runDirectory: runDirectory)) {
                return false;
            }
        }

        if (enumerated.Count == 0) {
            error = $"both boots exited 0 but produced no `name - description` help line on stdout; transcripts are at {runDirectory}.";

            return false;
        }

        return true;
    }
    // One boot leg: `--headless true` or `--headless false`, piping `help` then the runner-owned `wire.errors`
    // terminal observation over stdin, and folding every reported verb name into the shared set.
    private static bool TryBootLeg(string artifact, bool headless, string runDirectory, HashSet<string> enumerated, out string error) {
        error = string.Empty;

        var shape = (headless ? "headless" : "windowed");
        var stateDirectory = Path.Combine(path1: runDirectory, path2: $"state-{shape}");
        var input = $"help{Environment.NewLine}wire.errors{Environment.NewLine}";

        Console.WriteLine(value: $"citations: booting Puck.World {shape} to read its `help` vocabulary.");

        CliProcessResult process;

        try {
            process = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: [
                    artifact,
                    "--exit-after-seconds", "20",
                    "--state-dir", stateDirectory,
                    "--headless", (headless ? "true" : "false"),
                ],
                input: input,
                timeout: TimeSpan.FromSeconds(value: 60)
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            error = $"could not start the {shape} Puck.World boot: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        File.WriteAllText(path: Path.Combine(path1: runDirectory, path2: $"{shape}-stdout.log"), contents: process.Stdout, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(path: Path.Combine(path1: runDirectory, path2: $"{shape}-stderr.log"), contents: process.Stderr, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (process.TimedOut || (process.ExitCode != 0)) {
            error = $"the {shape} Puck.World boot {(process.TimedOut ? "timed out" : $"exited {process.ExitCode}")}; transcripts are at {runDirectory}.";

            return false;
        }

        var found = 0;

        foreach (var line in process.Stdout.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n')) {
            var match = HelpLine.Match(input: line);

            if (match.Success) {
                _ = enumerated.Add(item: match.Groups[1].Value);
                found++;
            }
        }

        if (found == 0) {
            error = $"the {shape} Puck.World boot exited 0 but its stdout carried no `name - description` help line; transcripts are at {runDirectory}.";

            return false;
        }

        return true;
    }
    private static HashSet<string> ReadEnumeration(string path) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path: path)) {
            var name = line.Trim();

            if (name.Length > 0) {
                _ = names.Add(item: name);
            }
        }

        return names;
    }
    private static HashSet<string> SweepLiterals(IReadOnlyList<string> files, Regex pattern) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var file in files) {
            foreach (var line in File.ReadLines(path: file)) {
                foreach (Match match in pattern.Matches(input: line)) {
                    _ = names.Add(item: match.Groups[1].Value);
                }
            }
        }

        return names;
    }
    // Every world-document field path the generated section schemas declare, spelled `section.member[.member…]`
    // — `storage.userId`, `audio.masterGain`. A document field is family-shaped, cited in XML docs and skills
    // exactly like a verb, and is a name the document really carries; the generated schema (puck schema) is the
    // one vocabulary that cannot drift from the shape.
    private static HashSet<string> SweepDocumentFields(string root) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var directory = Path.Combine(paths: [root, "src", "Puck.World", "Assets", "worlds", "schema"]);

        if (!Directory.Exists(path: directory)) {
            return names;
        }

        foreach (var file in Directory.EnumerateFiles(path: directory, searchPattern: "*.schema.json").OrderBy(keySelector: static path => path, comparer: StringComparer.Ordinal)) {
            var section = Path.GetFileName(path: file);

            section = section[..(section.Length - ".schema.json".Length)];

            using var document = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: file));

            AddProperties(element: document.RootElement, names: names, prefix: section);
        }

        return names;
    }
    private static void AddProperties(JsonElement element, string prefix, HashSet<string> names) {
        if (element.ValueKind != JsonValueKind.Object) {
            return;
        }

        if (element.TryGetProperty(propertyName: "properties", value: out var properties) && (properties.ValueKind == JsonValueKind.Object)) {
            foreach (var property in properties.EnumerateObject()) {
                var path = $"{prefix}.{property.Name}";

                _ = names.Add(item: path);
                AddProperties(element: property.Value, names: names, prefix: path);
            }
        }

        if (element.TryGetProperty(propertyName: "items", value: out var items)) {
            AddProperties(element: items, names: names, prefix: prefix);
        }
    }

    private readonly record struct Citation(string File, int Line, string Token);
}
