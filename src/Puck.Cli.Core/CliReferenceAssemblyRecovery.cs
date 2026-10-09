using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Puck.Abstractions;

namespace Puck.Cli;

/// <summary>Repairs only corrupt reference outputs named by a failed build inside its own tree, then retries once.</summary>
public static partial class CliReferenceAssemblyRecovery {
    [GeneratedRegex(@"\berror CS0009: Metadata file '(?<path>[^']+)' could not be opened", RegexOptions.CultureInvariant)]
    private static partial Regex Diagnostic();

    /// <summary>Runs a build, retaining its first failure before removing any diagnosed reference directories.</summary>
    /// <param name="tree">The tree that owns the build's intermediate outputs.</param>
    /// <param name="log">The first failure's transcript, written before recovery.</param>
    /// <param name="timeout">The combined attempts' deadline.</param>
    /// <param name="build">Runs one build attempt within the remaining budget.</param>
    /// <returns>The first result, or the one retry's result with both transcripts retained.</returns>
    public static CliProcessResult Run(string tree, string log, TimeSpan timeout, Func<TimeSpan, CliProcessResult> build) {
        var elapsed = Stopwatch.StartNew();
        var first = build(timeout);

        if (first.TimedOut || (first.ExitCode == 0)) { return first; }
        var matches = Diagnostic().Matches(input: ((first.Stdout + "\n") + first.Stderr));

        if (matches.Count == 0) { return first; }

        try {
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: Path.GetFullPath(path: log))!);
            File.WriteAllText(log, ((first.Stdout + "\n--- stderr ---\n") + first.Stderr));
            var directories = new HashSet<string>(comparer: PuckPaths.Comparer);
            var root = Path.GetFullPath(path: tree);

            foreach (Match match in matches) {
                var named = match.Groups["path"].Value;

                if (!Path.IsPathFullyQualified(path: named)) { throw new IOException(message: "the diagnosed reference path is not absolute"); }
                var path = Path.GetFullPath(path: named);
                var relative = Path.GetRelativePath(path: path, relativeTo: root).Replace(newChar: '/', oldChar: Path.DirectorySeparatorChar);
                var parts = relative.Split('/');

                if (Path.IsPathRooted(path: relative) || parts.Contains(value: "..") ||
                    (parts.Length < 4) || !parts.Contains(value: "obj") ||
                    (parts[^2] is not ("ref" or "refint")) ||
                    !string.Equals(a: Path.GetExtension(path: path), b: ".dll", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    throw new IOException(message: $"{PuckPaths.Normalize(path: path)} is not an owned obj reference assembly");
                }
                CliTreeFiles.RequireUnlinkedPath(path: path);
                var directory = Path.GetDirectoryName(path: path)!;

                if (CliTreeFiles.LinkedPath(tree: directory) is { } link) { throw new IOException(message: $"{link} is a link in the reference directory"); }
                using (var stream = File.OpenRead(path: path)) {
                    try {
                        using var image = new PEReader(peStream: stream);

                        if (image.HasMetadata) {
                            _ = image.GetMetadataReader().GetModuleDefinition();
                            throw new IOException(message: $"{PuckPaths.Normalize(path: path)} has managed metadata; recovery cannot attribute its diagnostic to corrupt output");
                        }
                    } catch (BadImageFormatException) {
                        // A zero-filled or truncated intermediate is not a PE image.
                    }
                }
                directories.Add(item: directory);
            }
            if (elapsed.Elapsed >= timeout) { return first; }
            foreach (var directory in directories) { Directory.Delete(directory, recursive: true); }
            Console.Error.WriteLine(value: $"build: removed corrupt reference outputs; retrying once. First failure: {CliPaths.ToDisplay(fullPath: log)}");
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException)) {
            return first with { Stderr = (first.Stderr + $"\nreference recovery refused: {exception.Message}") };
        }
        var remaining = (timeout - elapsed.Elapsed);

        if (remaining <= TimeSpan.Zero) { return first; }
        var retry = build(remaining);

        return retry with {
            Stdout = ((first.Stdout + "\n--- reference recovery retry ---\n") + retry.Stdout),
            Stderr = ((first.Stderr + "\n--- reference recovery retry ---\n") + retry.Stderr),
        };
    }
}
