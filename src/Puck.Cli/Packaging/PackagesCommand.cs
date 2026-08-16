using System.Text;
using System.Xml.Linq;

namespace Puck.Cli.Packaging;

// The `puck packages` verb: enumerates every csproj under src/ that opts into packing
// (<IsPackable>true</IsPackable>, see build/Packaging.targets) and reports the same declarations
// `dotnet pack` reads — <PackageId>, <Description>, <PackageTags>. --check compares a page's GENERATED
// package section against this list, and --write regenerates that section — the same drift-detection and
// regeneration shape `puck schema --check`/`puck schema` establishes for the checked-in JSON Schema files.
// docs/site/index.html carries the one checked-in GENERATED instance today; a hand-maintained list next to
// a generated one is the same second source docs/project-map.md's layering block exists to avoid.
// src/Web.Functions is excluded from the walk for the reason Architecture.props' PuckArchitectureGateEnabled
// predicate states beside its own matching exclusion.
// Exit 0 listed/wrote/matched, 1 check found drift, 2 usage error or missing repository root.
internal static class PackagesCommand {
    private const string GeneratedBegin = "<!-- GENERATED: puck packages -->";
    private const string GeneratedEnd = "<!-- /GENERATED -->";
    private const string HelpText =
        """
        puck packages — report the published ByteTerrace.Puck.* NuGet packages

        Usage: puck packages [options]

        Options:
          --check <path>   compare <path>'s GENERATED package section against the current
                            packable-project list; write nothing, exit 1 with a drift
                            report on disagreement
          --write <path>   regenerate the GENERATED package section in <path> from the
                            current packable-project list
          -h, --help       this text

        With neither option, lists every project under src/ declaring
        <IsPackable>true</IsPackable> — package id, description, and tags — sorted by
        package id.

        The GENERATED section is delimited by a comment pair:
          <!-- GENERATED: puck packages -->
          ...
          <!-- /GENERATED -->
        --write replaces everything between and including that pair; the rest of the file
        is untouched. docs/site/index.html carries the one checked-in instance today.

        Exit codes: 0 listed/wrote/matched, 1 check found drift, 2 usage error or missing
        repository root.
        """;

    private readonly record struct PackageEntry(string Description, string File, string Id, IReadOnlyList<string> Tags);

    private static int Check(string repositoryRoot, string relativePath, IReadOnlyList<PackageEntry> packages) {
        var fullPath = Path.Combine(
            path1: repositoryRoot,
            path2: relativePath.Replace(
                newChar: Path.DirectorySeparatorChar,
                oldChar: '/'
            )
        );

        if (!File.Exists(path: fullPath)) {
            Console.Error.WriteLine(value: $"packages: {relativePath} does not exist.");

            return 1;
        }

        // CRLF is checkout noise, never content: a CRLF working copy of an LF-authored page must compare
        // equal, the same normalization `puck schema --check` applies before comparing generated text.
        var text = File.ReadAllText(path: fullPath).Replace(
            newValue: "\n",
            oldValue: "\r\n"
        );

        if (!TryLocateSection(
            beginIndex: out var beginIndex,
            endIndex: out var endIndex,
            text: text
        )) {
            Console.Error.WriteLine(value: $"packages: {relativePath} carries no '{GeneratedBegin}' … '{GeneratedEnd}' section.");

            return 1;
        }

        var indent = LeadingWhitespace(
            index: beginIndex,
            text: text
        );
        var expected = RenderSection(
            indent: indent,
            packages: packages
        );
        var actual = text[beginIndex..endIndex];

        if (string.Equals(
            a: actual,
            b: expected,
            comparisonType: StringComparison.Ordinal
        )) {
            Console.Out.WriteLine(value: $"packages: {relativePath} matches the current {packages.Count} packable project(s).");

            return 0;
        }

        Console.Error.WriteLine(value: $"packages: {relativePath} does not match the current list — regenerate with 'puck packages --write {relativePath}'.");
        Console.Error.WriteLine(value: "  checked-in:");
        Console.Error.WriteLine(value: Indent(text: actual));
        Console.Error.WriteLine(value: "  generated:");
        Console.Error.WriteLine(value: Indent(text: expected));

        return 1;
    }
    private static IReadOnlyList<PackageEntry> Discover(string repositoryRoot) {
        var directory = Path.Combine(
            path1: repositoryRoot,
            path2: "src"
        );
        var packages = new List<PackageEntry>();

        if (!Directory.Exists(path: directory)) {
            return packages;
        }

        foreach (var file in Directory.EnumerateFiles(
            path: directory,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*.csproj"
        )) {
            if (
                file.Contains(value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains(value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}Web.Functions{Path.DirectorySeparatorChar}"
            )
            ) {
                continue;
            }

            var document = XDocument.Load(uri: file);

            if (!string.Equals(
                a: Element(
                    document: document,
                    name: "IsPackable"
                ),
                b: "true",
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                continue;
            }

            var tags = (Element(
                document: document,
                name: "PackageTags"
            ) ?? "")
                .Split(
                options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
                separator: ';'
            );

            packages.Add(item: new PackageEntry(
                Description: (Element(
                    document: document,
                    name: "Description"
                ) ?? ""),
                File: file,
                Id: (Element(
                    document: document,
                    name: "PackageId"
                ) ?? Path.GetFileNameWithoutExtension(path: file)),
                Tags: tags
            ));
        }

        packages.Sort(comparison: (a, b) => string.CompareOrdinal(
            strA: a.Id,
            strB: b.Id
        ));

        return packages;
    }
    private static string? Element(XDocument document, string name) =>
        document.Descendants().FirstOrDefault(predicate: e => (e.Name.LocalName == name))?.Value.Trim();
    private static string Indent(string text) =>
        string.Join(
            separator: '\n',
            values: text.Split(separator: '\n').Select(selector: l => $"    {l}")
        );
    private static string LeadingWhitespace(string text, int index) {
        var start = index;

        while (
            (start > 0) &&
            (text[(start - 1)] is ' ' or '\t')
        ) {
            --start;
        }

        return text[start..index];
    }
    private static string RenderSection(IReadOnlyList<PackageEntry> packages, string indent) {
        var builder = new StringBuilder();

        _ = builder.Append(value: GeneratedBegin).Append(value: '\n');
        _ = builder.Append(value: indent).Append(value: "<p class=\"libs\">\n");
        _ = builder.Append(value: indent).Append(value: "  Published libraries:\n");

        for (var index = 0; (index < packages.Count); index++) {
            var suffix = ((index == (packages.Count - 1))
                ? "."
                : ","
            );

            _ = builder.Append(value: indent).Append(value: $"  <code>{packages[index].Id}</code>{suffix}\n");
        }

        _ = builder.Append(value: indent).Append(value: "</p>\n");
        _ = builder.Append(value: indent).Append(value: GeneratedEnd);

        return builder.ToString();
    }
    private static bool TryLocateSection(string text, out int beginIndex, out int endIndex) {
        var begin = text.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: GeneratedBegin
        );

        if (begin < 0) {
            beginIndex = 0;
            endIndex = 0;

            return false;
        }

        var end = text.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: begin,
            value: GeneratedEnd
        );

        if (end < 0) {
            beginIndex = 0;
            endIndex = 0;

            return false;
        }

        beginIndex = begin;
        endIndex = (end + GeneratedEnd.Length);

        return true;
    }
    private static int Write(string repositoryRoot, string relativePath, IReadOnlyList<PackageEntry> packages) {
        var fullPath = Path.Combine(
            path1: repositoryRoot,
            path2: relativePath.Replace(
                newChar: Path.DirectorySeparatorChar,
                oldChar: '/'
            )
        );

        if (!File.Exists(path: fullPath)) {
            Console.Error.WriteLine(value: $"packages: {relativePath} does not exist.");

            return 1;
        }

        var original = File.ReadAllText(path: fullPath);
        var normalized = original.Replace(
            newValue: "\n",
            oldValue: "\r\n"
        );

        if (!TryLocateSection(
            beginIndex: out var beginIndex,
            endIndex: out var endIndex,
            text: normalized
        )) {
            Console.Error.WriteLine(value: $"packages: {relativePath} carries no '{GeneratedBegin}' … '{GeneratedEnd}' section.");

            return 1;
        }

        var indent = LeadingWhitespace(
            index: beginIndex,
            text: normalized
        );
        var replaced = string.Concat(
            str0: normalized[..beginIndex],
            str1: RenderSection(
                indent: indent,
                packages: packages
            ),
            str2: normalized[endIndex..]
        );
        var lineEnding = (original.Contains(value: "\r\n")
            ? "\r\n"
            : "\n"
        );

        File.WriteAllText(
            path: fullPath,
            contents: replaced.Replace(
                newValue: lineEnding,
                oldValue: "\n"
            ),
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        Console.Out.WriteLine(value: $"packages: wrote {packages.Count} packable project(s) to {relativePath}.");

        return 0;
    }

    public static int Run(string[] args) {
        var scanner = new ArgScanner().Flag(name: "h").Flag(name: "help").Value(name: "check").Value(name: "write");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"packages: {scanner.Error}");

            return 2;
        }

        if (
            scanner.Has(name: "h") ||
            scanner.Has(name: "help")
        ) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var packages = Discover(repositoryRoot: repositoryRoot);
        var checkPath = scanner.Get(name: "check");
        var writePath = scanner.Get(name: "write");

        if (checkPath is not null) {
            return Check(
                packages: packages,
                relativePath: checkPath,
                repositoryRoot: repositoryRoot
            );
        }

        if (writePath is not null) {
            return Write(
                packages: packages,
                relativePath: writePath,
                repositoryRoot: repositoryRoot
            );
        }

        foreach (var package in packages) {
            Console.Out.WriteLine(value: package.Id);
            Console.Out.WriteLine(value: $"  {package.Description}");
            Console.Out.WriteLine(value: $"  tags: {string.Join(
                separator: ", ",
                values: package.Tags
            )}");
        }

        Console.Out.WriteLine(value: $"packages: {packages.Count} packable project(s).");

        return 0;
    }
}
