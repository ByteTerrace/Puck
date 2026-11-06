using System.CommandLine;
using System.Text;

namespace Puck.Cli.Packaging;

// The `puck packages` verb: enumerates every csproj under src/ that opts into packing (<IsPackable>true</IsPackable>,
// see build/Packaging.targets) by the <PackageId> `dotnet pack` reads, and writes the site page's GENERATED package
// section from that list, or checks it, through CliGeneratedFile.
public static class PackagesCommand {
    private const string GeneratedBegin = "<!-- GENERATED: puck packages -->";
    private const string GeneratedEnd = "<!-- /GENERATED -->";
    private const string Page = "docs/site/index.html";

    private readonly record struct PackageEntry(string Id);

    private static IReadOnlyList<PackageEntry> Discover(string repositoryRoot) => [
        .. PackableProjects.Discover(root: repositoryRoot)
            .Select(selector: static project => new PackageEntry(Id: (PackableProjects.Property(document: project.Document, name: "PackageId") ?? Path.GetFileNameWithoutExtension(path: project.File))))
            .OrderBy(keySelector: static package => package.Id, comparer: StringComparer.Ordinal),
    ];    private static string LeadingWhitespace(string text, int index) {
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
        _ = builder.Append(value: indent).Append(value: "  NuGet packages:\n");

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
    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        var fullPath = Path.Combine(path1: repositoryRoot, path2: Page);

        if (!File.Exists(path: fullPath)) {
            return CliExit.Refuse(verb: "packages", what: Page, why: "does not exist.");
        }

        var text = File.ReadAllText(path: fullPath).ReplaceLineEndings(replacementText: "\n");

        if (!TryLocateSection(beginIndex: out var beginIndex, endIndex: out var endIndex, text: text)) {
            return CliExit.Refuse(verb: "packages", what: Page, why: $"carries no '{GeneratedBegin}' … '{GeneratedEnd}' section.");
        }

        var packages = Discover(repositoryRoot: repositoryRoot);
        var generated = string.Concat(
            str0: text[..beginIndex],
            str1: RenderSection(indent: LeadingWhitespace(index: beginIndex, text: text), packages: packages),
            str2: text[endIndex..]
        );

        return (CliGeneratedFile.WriteOrCheck(
            check: check,
            detail: $" ({packages.Count} packable project(s))",
            relativePath: Page,
            repositoryRoot: repositoryRoot,
            source: "the packable projects",
            text: generated,
            verb: "packages"
        ) ? CliExit.Success : CliExit.Failed);
    }    private static bool TryLocateSection(string text, out int beginIndex, out int endIndex) {
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
    public static Command Create() => CliOptions.CheckVerb(
        checkDescription: $"Regenerate the GENERATED package section of {Page} in memory and compare it; write nothing, and exit 1 naming the first differing line.",
        description: $"Write the published ByteTerrace.Puck.* NuGet package list into {Page}, or check it.",
        detail: $"""
            Every project under src/ declaring <IsPackable>true</IsPackable> is listed by its package id, in ordinal
            order, between the comment pair {GeneratedBegin} and {GeneratedEnd} in {Page}; the rest of the page is
            untouched.

            Exit codes: 0 wrote or matched, 1 --check found drift, 2 refused (no repository root, no page or no section).
            """,
        name: "packages",
        run: Run
    );
}