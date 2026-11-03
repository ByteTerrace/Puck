using System.Text.RegularExpressions;

namespace Puck.Cli.Evals;

/// <summary>Reads the two files that define a <c>claude plugin eval</c> case: <c>prompt.md</c> (YAML frontmatter, then
/// the prompt) and <c>case.yaml</c>. It reads only the few keys Puck's tooling needs; the runner's own loader stays the
/// judge of the files' validity.</summary>
public static partial class CaseFiles {
    /// <summary>The prompt file's name.</summary>
    public const string Prompt = "prompt.md";
    /// <summary>The case file's name.</summary>
    public const string Case = "case.yaml";

    [GeneratedRegex(pattern: @"^tags:\s*\[(?<items>[^\]]*)\]\s*$")]
    private static partial Regex InlineTags();
    [GeneratedRegex(pattern: @"^tags:\s*$")]
    private static partial Regex BlockTags();
    [GeneratedRegex(pattern: @"^\s+-\s*(?<item>.+?)\s*$")]
    private static partial Regex BlockItem();
    [GeneratedRegex(pattern: @"^\s+history_file:\s*[""']?(?<path>[^""'#]+?)[""']?\s*(#.*)?$")]
    private static partial Regex HistoryKey();

    /// <summary>Indicates whether <paramref name="directory"/> is a case: it holds a <c>prompt.md</c> or a
    /// <c>case.yaml</c>.</summary>
    /// <param name="directory">The directory to test.</param>
    /// <returns><see langword="true"/> for a case directory.</returns>
    public static bool IsCase(string directory) =>
        (File.Exists(path: Path.Combine(path1: directory, path2: Prompt)) || File.Exists(path: Path.Combine(path1: directory, path2: Case)));
    /// <summary>Lists the case directories under an eval directory, in ordinal order: each directory that is a case,
    /// found below directories that are not, skipping <c>results</c> and <c>mocks</c>.</summary>
    /// <param name="evals">The eval directory.</param>
    /// <returns>The case directories' full paths.</returns>
    public static IReadOnlyList<string> Enumerate(string evals) {
        var found = new List<string>();

        void Walk(string directory) {
            foreach (var child in Directory.EnumerateDirectories(path: directory).Order(comparer: StringComparer.Ordinal)) {
                var name = Path.GetFileName(path: child);

                if ((name is "results" or "mocks") || name.StartsWith(value: '.')) { continue; }
                if (IsCase(directory: child)) { found.Add(item: child); } else { Walk(directory: child); }
            }
        }

        if (Directory.Exists(path: evals)) { Walk(directory: evals); }
        return found;
    }
    /// <summary>Returns the frontmatter lines of a <c>prompt.md</c> and the index of the line that closes it.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <returns>The index of the closing <c>---</c>, or -1 when the file opens no frontmatter.</returns>
    public static int FrontmatterEnd(IReadOnlyList<string> lines) {
        if ((lines.Count == 0) || (lines[0].Trim() != "---")) { return -1; }
        for (var index = 1; (index < lines.Count); index++) {
            if (lines[index].Trim() == "---") { return index; }
        }
        return -1;
    }
    /// <summary>Reads a case's tags from its <c>prompt.md</c> frontmatter, which overrides <c>case.yaml</c>, then from
    /// <c>case.yaml</c>; both the inline (<c>tags: [a, b]</c>) and block list forms are read.</summary>
    /// <param name="caseDirectory">The case directory.</param>
    /// <returns>The tags, empty when neither file names any or the directory no longer exists.</returns>
    public static IReadOnlyList<string> Tags(string caseDirectory) {
        var prompt = Path.Combine(path1: caseDirectory, path2: Prompt);

        if (File.Exists(path: prompt)) {
            var lines = File.ReadAllLines(path: prompt);
            var end = FrontmatterEnd(lines: lines);

            if ((end > 0) && ReadTags(lines: lines[1..end]) is { } frontmatter) { return frontmatter; }
        }
        var file = Path.Combine(path1: caseDirectory, path2: Case);

        return ((File.Exists(path: file) && ReadTags(lines: File.ReadAllLines(path: file)) is { } yaml) ? yaml : []);
    }
    private static IReadOnlyList<string>? ReadTags(IReadOnlyList<string> lines) {
        for (var index = 0; (index < lines.Count); index++) {
            if (InlineTags().Match(input: lines[index]) is { Success: true } inline) {
                return [.. inline.Groups["items"].Value.Split(separator: ',').Select(selector: static item => item.Trim().Trim('"', '\'')).Where(predicate: static item => (item.Length != 0))];
            }
            if (BlockTags().IsMatch(input: lines[index])) {
                var items = new List<string>();

                for (var item = (index + 1); ((item < lines.Count) && BlockItem().Match(input: lines[item]) is { Success: true } match); item++) {
                    items.Add(item: match.Groups["item"].Value.Trim('"', '\''));
                }
                return items;
            }
        }
        return null;
    }
    /// <summary>Reads whether a case's <c>prompt.md</c> frontmatter sets <paramref name="key"/>.</summary>
    /// <param name="caseDirectory">The case directory.</param>
    /// <param name="key">The top-level frontmatter key.</param>
    /// <returns><see langword="true"/> when the frontmatter has a line opening with the key.</returns>
    public static bool PromptSets(string caseDirectory, string key) {
        var prompt = Path.Combine(path1: caseDirectory, path2: Prompt);

        if (!File.Exists(path: prompt)) { return false; }
        var lines = File.ReadAllLines(path: prompt);
        var end = FrontmatterEnd(lines: lines);

        return ((end > 0) && lines[1..end].Any(predicate: line => line.StartsWith(value: $"{key}:", comparisonType: StringComparison.Ordinal)));
    }
    /// <summary>Reads the transcript a case resumes: the <c>history_file</c> its <c>case.yaml</c> names under
    /// <c>context</c>, resolved against the case directory.</summary>
    /// <param name="caseDirectory">The case directory.</param>
    /// <returns>The transcript's full path, or <see langword="null"/> when the case resumes none.</returns>
    public static string? HistoryFile(string caseDirectory) {
        var file = Path.Combine(path1: caseDirectory, path2: Case);

        if (!File.Exists(path: file)) { return null; }
        var inContext = false;

        foreach (var line in File.ReadAllLines(path: file)) {
            if (line.Trim().Length == 0) { continue; }
            if (!char.IsWhiteSpace(c: line[0])) { inContext = (line.TrimEnd() == "context:"); continue; }
            if (inContext && (HistoryKey().Match(input: line) is { Success: true } match)) {
                return Path.GetFullPath(path: Path.Combine(path1: caseDirectory, path2: match.Groups["path"].Value));
            }
        }
        return null;
    }
}
