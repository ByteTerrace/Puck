using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Puck.Cli.Architecture;

namespace Puck.Cli.Affected;

/// <summary>
/// The canary coverage index: which World source files each canary executes. Recording builds a World that carries
/// its method recorder (<c>-p:PuckRecordMethods=true</c>), runs the full canary set on it, maps every method each
/// leg's World compiled to its source file through the build's portable PDBs, and writes
/// <see cref="AffectedCommand.CoveragePath"/>. Reading inverts it into each source file with the canaries that
/// executed it.
/// </summary>
internal static partial class AffectedCoverage {
    /// <summary>The coverage index's schema id.</summary>
    public const string Schema = "puck.canary.coverage.v1";

    [GeneratedRegex(pattern: @"^canary (?<id>\S+) .*transcripts (?<directory>.+)$")]
    private static partial Regex TranscriptLine();

    /// <summary>Reads the index, inverted: every World source it recorded, with the canary ids that executed it.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The inverted index; empty when none has been recorded.</returns>
    /// <exception cref="InvalidDataException">The index exists and is not a <see cref="Schema"/> document.</exception>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Read(string repositoryRoot) {
        var path = Path.Combine(
            path1: repositoryRoot,
            path2: AffectedCommand.CoveragePath
        );

        return (File.Exists(path: path)
            ? Parse(utf8: File.ReadAllBytes(path: path))
            : new Dictionary<string, IReadOnlySet<string>>(comparer: StringComparer.Ordinal));
    }
    /// <summary>Reads the index as a revision recorded it, inverted: the entries a file deleted since that revision still
    /// has there.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="revision">The revision.</param>
    /// <returns>The inverted index; empty when the revision holds none.</returns>
    /// <exception cref="InvalidDataException">The revision's index is not a <see cref="Schema"/> document.</exception>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ReadAt(string repositoryRoot, string revision) {
        var shown = CliGit.Run(repositoryRoot, "show", $"{revision}:{AffectedCommand.CoveragePath}");

        return ((shown.ExitCode == 0)
            ? Parse(utf8: Encoding.UTF8.GetBytes(s: shown.Stdout))
            : new Dictionary<string, IReadOnlySet<string>>(comparer: StringComparer.Ordinal));
    }

    // Inverts one index document into each source file with the canary ids that executed it.
    private static Dictionary<string, IReadOnlySet<string>> Parse(byte[] utf8) {
        var coverage = new Dictionary<string, IReadOnlySet<string>>(comparer: StringComparer.Ordinal);

        using var document = JsonDocument.Parse(utf8Json: utf8);
        var root = document.RootElement;

        if (
            !root.TryGetProperty(propertyName: "schema", value: out var schema) ||
            (schema.GetString() != Schema) ||
            !root.TryGetProperty(propertyName: "sources", value: out var sources) ||
            !root.TryGetProperty(propertyName: "runs", value: out var runs)
        ) {
            throw new InvalidDataException(message: $"{AffectedCommand.CoveragePath} is not a {Schema} document; record it again with `puck affected --record`.");
        }

        var files = sources.EnumerateArray().Select(selector: static source => source.GetString()!).ToArray();
        var sets = files.Select(selector: static _ => new HashSet<string>(comparer: StringComparer.Ordinal)).ToArray();

        foreach (var run in runs.EnumerateObject()) {
            foreach (var index in run.Value.EnumerateArray()) {
                _ = sets[index.GetInt32()].Add(item: run.Name);
            }
        }

        for (var index = 0; (index < files.Length); index++) {
            coverage[files[index]] = sets[index];
        }

        return coverage;
    }
    // Every C# source of the projects the World is built from, repository-relative: the files the index answers for.
    private static SortedSet<string> WorldSources(string repositoryRoot) {
        var model = ArchitectureModel.Load(repositoryRoot: repositoryRoot);
        var closure = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(collection: ["Puck.World"]);
        var sources = new SortedSet<string>(comparer: StringComparer.Ordinal);

        while (pending.TryPop(result: out var name)) {
            if (!closure.Add(item: name) || !model.Projects.TryGetValue(key: name, value: out var project)) {
                continue;
            }

            foreach (var reference in project.References) {
                pending.Push(item: reference);
            }

            var tracked = CliGit.Run(repositoryRoot, "ls-files", "--", Path.GetRelativePath(path: Path.GetDirectoryName(path: project.File)!, relativeTo: repositoryRoot).Replace(newChar: '/', oldChar: '\\'));

            foreach (var line in tracked.Stdout.Split(separator: '\n')) {
                var file = line.TrimEnd(trimChar: '\r');

                if (file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".cs")) {
                    _ = sources.Add(item: file);
                }
            }
        }

        return sources;
    }

    // The repository-relative source file each (assembly, token) method was compiled from, read off the build's
    // portable PDBs; a method with no sequence points (compiler-generated) or outside the repository maps to none.
    private sealed class SourceMap(string buildDirectory, string repositoryRoot) : IDisposable {
        private readonly Dictionary<string, (MetadataReaderProvider Provider, MetadataReader Reader)?> m_readers = new(comparer: StringComparer.Ordinal);
        private readonly string m_root = (Path.GetFullPath(path: repositoryRoot).Replace(newChar: '/', oldChar: '\\').TrimEnd(trimChar: '/') + "/");

        public void Dispose() {
            foreach (var entry in m_readers.Values) {
                entry?.Provider.Dispose();
            }
        }
        public string? FileOf(string assembly, int token) {
            if (!m_readers.TryGetValue(key: assembly, value: out var entry)) {
                var pdb = Path.Combine(path1: buildDirectory, path2: (assembly + ".pdb"));

                entry = null;

                if (File.Exists(path: pdb)) {
                    var provider = MetadataReaderProvider.FromPortablePdbStream(stream: File.OpenRead(path: pdb));

                    entry = (provider, provider.GetMetadataReader());
                }

                m_readers[assembly] = entry;
            }

            if (entry is not { } found) {
                return null;
            }

            var handle = MetadataTokens.MethodDefinitionHandle(rowNumber: token & 0x00FFFFFF);
            var information = found.Reader.GetMethodDebugInformation(handle: handle);
            var documentHandle = information.Document;

            if (documentHandle.IsNil) {
                foreach (var point in information.GetSequencePoints()) {
                    documentHandle = point.Document;

                    break;
                }
            }

            if (documentHandle.IsNil) {
                return null;
            }

            var path = found.Reader.GetString(handle: found.Reader.GetDocument(handle: documentHandle).Name).Replace(newChar: '/', oldChar: '\\');

            return (path.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: m_root)
                ? path[m_root.Length..]
                : null
            );
        }
    }

    /// <summary>Records the index: builds the recording World, runs the full canary set on it through this CLI, and
    /// writes <see cref="AffectedCommand.CoveragePath"/>.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="cli">This CLI's entry assembly, which runs the canaries as a child process.</param>
    /// <param name="scratch">A directory the recording World is built into.</param>
    /// <param name="error">The refusal, or empty.</param>
    /// <returns><see langword="true"/> when the index was written.</returns>
    public static bool TryRecord(string repositoryRoot, string cli, string scratch, out string error) {
        var build = Path.Combine(path1: scratch, path2: "world");
        var compile = CliProcess.RunCaptured(
            arguments: ["build", "src/Puck.World/Puck.World.csproj", "-c", "Release", "--nologo", "-v", "q", "-p:NuGetAudit=false", "-p:PuckRecordMethods=true", "--output", build],
            fileName: "dotnet",
            input: string.Empty,
            timeout: TimeSpan.FromMinutes(minutes: 30),
            workingDirectory: repositoryRoot
        );

        if (compile.ExitCode != 0) {
            error = $"the recording World did not build:{Environment.NewLine}{compile.Stdout}";

            return false;
        }

        Console.Error.WriteLine(value: "affected: running the full canary set on the recording World.");

        var run = CliProcess.RunCaptured(
            arguments: [cli, "canary", "--merge", "--world-artifact", Path.Combine(path1: build, path2: WorldArtifactBuild.ArtifactName)],
            fileName: "dotnet",
            input: string.Empty,
            timeout: TimeSpan.FromHours(hours: 3),
            workingDirectory: repositoryRoot
        );

        if (run.ExitCode != 0) {
            Console.Error.WriteLine(value: $"affected: the canary run exited {run.ExitCode}; its legs still record what they executed.");
        }

        var runs = new SortedDictionary<string, SortedSet<string>>(comparer: StringComparer.Ordinal);
        using var map = new SourceMap(buildDirectory: build, repositoryRoot: repositoryRoot);

        foreach (var line in run.Stdout.Split(separator: '\n')) {
            var match = TranscriptLine().Match(input: line.TrimEnd(trimChar: '\r'));

            if (!match.Success || !Directory.Exists(path: match.Groups["directory"].Value)) {
                continue;
            }

            var id = match.Groups["id"].Value;

            if (!runs.TryGetValue(key: id, value: out var files)) {
                files = new SortedSet<string>(comparer: StringComparer.Ordinal);
                runs[id] = files;
            }

            foreach (var record in Directory.EnumerateFiles(path: match.Groups["directory"].Value, searchOption: SearchOption.AllDirectories, searchPattern: "methods.*.txt")) {
                foreach (var entry in File.ReadLines(path: record)) {
                    var fields = entry.Split(separator: '\t');

                    if (
                        (fields.Length == 2) &&
                        int.TryParse(s: fields[1], style: System.Globalization.NumberStyles.HexNumber, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var token) &&
                        (map.FileOf(assembly: fields[0], token: token) is { } file)
                    ) {
                        _ = files.Add(item: file);
                    }
                }
            }
        }

        if (runs.Count == 0) {
            error = "the canary run named no leg transcripts, so nothing was recorded.";

            return false;
        }

        var sources = WorldSources(repositoryRoot: repositoryRoot);
        var positions = sources.Select(selector: static (source, index) => (source, index)).ToDictionary(elementSelector: static entry => entry.index, keySelector: static entry => entry.source, comparer: StringComparer.Ordinal);
        var index = Path.Combine(path1: repositoryRoot, path2: AffectedCommand.CoveragePath);

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: index)!);

        using (var stream = File.Create(path: index)) {
            using var writer = new Utf8JsonWriter(utf8Json: stream);

            writer.WriteStartObject();
            writer.WriteString(propertyName: "schema", value: Schema);
            writer.WriteStartArray(propertyName: "sources");

            foreach (var source in sources) {
                writer.WriteStringValue(value: source);
            }

            writer.WriteEndArray();
            writer.WriteStartObject(propertyName: "runs");

            foreach (var (id, files) in runs) {
                writer.WriteStartArray(propertyName: id);

                foreach (var file in files) {
                    if (positions.TryGetValue(key: file, value: out var position)) {
                        writer.WriteNumberValue(value: position);
                    }
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        File.AppendAllText(contents: "\n", path: index);
        Console.Out.WriteLine(value: $"affected: recorded {runs.Count} canary run(s) over {sources.Count} World source(s) into {AffectedCommand.CoveragePath}.");
        error = string.Empty;

        return true;
    }
}
