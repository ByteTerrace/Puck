using System.Text.RegularExpressions;

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
// THE HONESTY CONTRACT, which is the whole reason this verb replaces a script:
//
//   A citation checker has two ways to be wrong, and they are not symmetric. It can miss a dead name
//   (quiet, and the reason the check exists). Or it can accuse a CORRECT citation because its own input
//   rotted — which is louder, wastes the author's time, and teaches everyone to distrust the check.
//
//   So this verb refuses to report when it can prove its own input is stale (exit 3), and it names the
//   INPUT as the suspect rather than the citation. It can prove staleness because the static sweep of verb
//   registrations is a LOWER BOUND on the live surface: a name spelled literally in a registration exists,
//   whatever the enumeration says. If the enumeration is missing one, the enumeration is behind the tree —
//   that is a fact about the file, not about any document quoting it.
//
// WHAT THIS VERB CANNOT SEE, stated so nobody reads a pass as more than it is. A retired verb whose name
// still sits in ANY string literal under src/ — a refusal message nobody updated, say — resolves here, and
// so does every document citing it. The stale string hides itself AND its citers. Retiring a verb therefore
// still owes a sweep of src/ runtime strings; this verb does not discharge that obligation and cannot.
//
// Exit 0 every citation resolved, 1 unresolved citations (each named), 2 usage error or no repository root,
// 3 the enumeration is provably stale and nothing was reported against it.
internal static class CitationsCommand {
    private const string HelpText =
        """
        puck citations — check cited verb tokens against vocabularies swept from the code

        Usage: puck citations [options]

        Options:
          --enumeration <path>  The console verb list, one name per line, as the runtime `help`
                                enumerates them (default: .runs/verbs-landed.txt). Re-record it by
                                booting and extracting, never by hand — see .runs/WAVE-STATE.md.
          -h, --help            This text.

        Scans .claude/skills/**/*.md for `backticked` tokens and src/**/*.cs for <c>…</c> tokens,
        keeps those shaped like a console verb, and resolves each against: the enumeration, verb
        names spelled literally in registrations, and every other verb-shaped string literal in
        src/ (a refusal door, a HUD binding token, a document field — names the code really knows).

        Exits 3 without reporting if a literally-registered verb is missing from the enumeration:
        that proves the enumeration is stale, and reporting citations against a stale list produces
        confident accusations of correct documentation.
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

        var enumerationPath = (scanner.Get(name: "enumeration") ?? Path.Combine(path1: root, path2: ".runs", path3: "verbs-landed.txt"));

        if (!File.Exists(path: enumerationPath)) {
            Console.Error.WriteLine(value: $"citations: no enumeration at '{CliPaths.ToDisplay(fullPath: enumerationPath)}' — nothing to resolve console verbs against.");

            return 3;
        }

        var enumerated = ReadEnumeration(path: enumerationPath);
        var sourceFiles = Directory.EnumerateFiles(path: Path.Combine(path1: root, path2: "src"), searchPattern: "*.cs", searchOption: SearchOption.AllDirectories)
            .OrderBy(keySelector: static path => path, comparer: StringComparer.Ordinal)
            .ToArray();
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

        // THE STALENESS GATE. A literally-registered verb the enumeration lacks proves the file is behind
        // the tree. Refuse rather than report — see this file's header.
        var missing = registered.Where(predicate: name => !enumerated.Contains(item: name))
            .OrderBy(keySelector: static name => name, comparer: StringComparer.Ordinal)
            .ToArray();

        if (missing.Length > 0) {
            Console.Error.WriteLine(value: $"citations: the enumeration is STALE — {missing.Length} verb(s) are registered in source but absent from {CliPaths.ToDisplay(fullPath: enumerationPath)}:");

            foreach (var name in missing) {
                Console.Error.WriteLine(value: $"    {name}");
            }

            Console.Error.WriteLine(value: "  Nothing was checked. Reporting citations against a stale list accuses correct documentation of quoting dead verbs.");
            Console.Error.WriteLine(value: "  Re-record it from a boot (see .runs/WAVE-STATE.md), then re-run — a hand edit fixes only the direction you noticed.");

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

    private readonly record struct Citation(string File, int Line, string Token);
}
