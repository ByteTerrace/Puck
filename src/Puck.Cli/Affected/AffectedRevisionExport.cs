using System.Formats.Tar;
using System.Globalization;

namespace Puck.Cli.Affected;

/// <summary>
/// The one export of a revision's files to disk, for the readers that read files rather than git: the document
/// composer (<see cref="AffectedRevisionDocuments"/>) and the base side of the compiled-world comparison
/// (<see cref="AffectedCompiledWorlds"/>). It exports only the paths its caller names that the revision holds, never the
/// whole repository: <c>git archive -o</c> writes them to a tar file in a run directory of its own, which is then
/// extracted beside it, and every git run has its standard input closed and a bounded wait. A git run that does not
/// finish within the bound, or exits nonzero, refuses the export by name (<see cref="AffectedRevisionExportRefusedException"/>).
/// <para>The archive is never read from git's standard output: a tar reader stops at the end-of-archive marker, before
/// the padding git still writes to fill its last record, so git blocks on a full pipe while its caller waits for it to
/// exit.</para>
/// </summary>
internal sealed class AffectedRevisionExport : IDisposable {
    /// <summary>How long one git run of an export may take before the export is refused.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromMinutes(minutes: 2);
    /// <summary>The trees a canary's world documents, the sources they lower from, and every file their compiles read
    /// are authored in: what a revision's document composer needs exported.</summary>
    public static IReadOnlyList<string> DocumentTrees { get; } = [
        "src/Puck.World/Assets",
        "tests/Puck.World.Canaries",
        "tests/Puck.World.Verdicts",
        "worlds",
    ];

    private readonly RunDirectory m_directory;

    private AffectedRevisionExport(RunDirectory directory, string root) {
        m_directory = directory;
        Root = root;
    }

    /// <summary>Gets the exported tree's root, which holds each exported path at its repository-relative place.</summary>
    public string Root { get; }

    private static string Git(string repository, string revision, TimeSpan bound, params string[] arguments) {
        Puck.Hosting.ChildProcessResult run;

        try {
            run = CliGit.RunAsync(
                arguments: arguments,
                input: string.Empty,
                repository: repository,
                timeout: bound
            ).GetAwaiter().GetResult();
        } catch (TimeoutException) {
            throw new AffectedRevisionExportRefusedException(message: $"the export of {revision} is refused: git {arguments[0]} did not finish within {bound.TotalSeconds.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)} s.");
        }

        if (run.ExitCode != 0) {
            throw new AffectedRevisionExportRefusedException(message: $"the export of {revision} is refused: git {arguments[0]} exited {run.ExitCode}: {run.Stderr.Trim()}");
        }

        return run.Stdout;
    }

    /// <summary>Exports the named paths a revision holds.</summary>
    /// <param name="repository">The repository root.</param>
    /// <param name="revision">The revision whose files are exported.</param>
    /// <param name="paths">The repository-relative files and directories to export; one the revision does not hold is
    /// left out.</param>
    /// <param name="bound">How long each git run may take; <see cref="Bound"/> when <see langword="null"/>.</param>
    /// <returns>The export, deleted when disposed.</returns>
    /// <exception cref="AffectedRevisionExportRefusedException">A git run did not finish within the bound, or exited
    /// nonzero.</exception>
    public static AffectedRevisionExport Create(string repository, string revision, IReadOnlyList<string> paths, TimeSpan? bound = null) {
        var limit = (bound ?? Bound);
        var directory = RunDirectory.Create(prefix: "puck-affected-export-", keepOnFailure: false);

        try {
            var root = Path.Combine(path1: directory.Path, path2: "tree");
            string[] held = [.. Git(repository, revision, limit, ["ls-tree", "-z", "--name-only", revision, "--", .. paths])
                .Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0')];

            _ = Directory.CreateDirectory(path: root);

            if (held.Length > 0) {
                var archive = Path.Combine(path1: directory.Path, path2: "export.tar");

                _ = Git(repository, revision, limit, ["archive", "--format=tar", "-o", archive, revision, "--", .. held]);
                TarFile.ExtractToDirectory(destinationDirectoryName: root, overwriteFiles: false, sourceFileName: archive);
                File.Delete(path: archive);
            }

            return new AffectedRevisionExport(directory: directory, root: root);
        } catch {
            directory.Dispose();

            throw;
        }
    }
    /// <summary>Deletes the export.</summary>
    public void Dispose() => m_directory.Dispose();
}
/// <summary>A revision's export could not be made: a git run it needed did not finish within its bound, or failed. The
/// message names the revision, the git command and why.</summary>
/// <param name="message">The refusal.</param>
internal sealed class AffectedRevisionExportRefusedException(string message) : Exception(message: message);
