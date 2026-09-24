namespace Puck.Analyzers;

/// <summary>
/// The one list of environment variables Puck may read (<see cref="EnvironmentReadAnalyzer"/>, ENV001): each a value
/// the operating system, the .NET SDK or a CI host defines, never one Puck invents to switch itself. An entry names
/// the assemblies that may read it, so a CI-provided value stays inside the CI-facing verbs, and states why the read
/// cannot be a flag, a document, or a profile row instead.
/// </summary>
public static class EnvironmentReadAllowlist {
    private static readonly string[] Anywhere = [];
    private static readonly string[] Cli = ["Puck.Cli"];
    // The identity team's file-based bootstrap app compiles as its own program and carries no analyzer; its reads are
    // listed so the list stays the whole inventory.
    private static readonly string[] CliAndBootstrap = ["Puck.Cli", "bootstrap"];
    private static readonly string[] Platform = ["Puck.Platform"];

    /// <summary>Gets every allowlisted variable, keyed by its exact (case-sensitive) name.</summary>
    public static IReadOnlyDictionary<string, EnvironmentReadEntry> Entries { get; } = new Dictionary<string, EnvironmentReadEntry>(comparer: StringComparer.Ordinal) {
        ["CI"] = new(
            Assemblies: CliAndBootstrap,
            Reason: "Set by every CI host; the World artifact key keys a CI build apart, and the bootstrap app refuses to run under CI."
        ),
        ["GH_TOKEN"] = new(
            Assemblies: Cli,
            Reason: "The GitHub CLI's token variable, which the workflow sets from the run's own token; the pull-request formatter authenticates with it."
        ),
        ["GITHUB_ACTIONS"] = new(
            Assemblies: CliAndBootstrap,
            Reason: "Defined by GitHub Actions; the CI-facing verbs mask secrets and write step outputs only there."
        ),
        ["GITHUB_API_URL"] = new(
            Assemblies: Cli,
            Reason: "Defined by GitHub Actions; the REST endpoint the pull-request formatter calls."
        ),
        ["GITHUB_GRAPHQL_URL"] = new(
            Assemblies: Cli,
            Reason: "Defined by GitHub Actions; the GraphQL endpoint the pull-request formatter calls."
        ),
        ["GITHUB_OUTPUT"] = new(
            Assemblies: Cli,
            Reason: "Defined by GitHub Actions; the file a step writes its outputs to."
        ),
        ["GITHUB_REF"] = new(
            Assemblies: Cli,
            Reason: "Defined by GitHub Actions; the branch a deployment or a release is cut from."
        ),
        ["GITHUB_REPOSITORY"] = new(
            Assemblies: Cli,
            Reason: "Defined by GitHub Actions; the repository a release tag or a formatting commit is written to."
        ),
        ["GITHUB_RUN_ATTEMPT"] = new(
            Assemblies: Cli,
            Reason: "Defined by GitHub Actions; names a run's release record and deployment tags apart from a retry's."
        ),
        ["GITHUB_RUN_ID"] = new(
            Assemblies: Cli,
            Reason: "Defined by GitHub Actions; names a run's release record and deployment tags."
        ),
        ["GITHUB_SHA"] = new(
            Assemblies: Cli,
            Reason: "Defined by GitHub Actions; the commit a deployment or a package publication must match."
        ),
        ["GITHUB_STEP_SUMMARY"] = new(
            Assemblies: Cli,
            Reason: "Defined by GitHub Actions; the file a step writes its job summary to."
        ),
        ["NUGET_API_KEY"] = new(
            Assemblies: Cli,
            Reason: "The short-lived key the NuGet trusted-publishing login issues; the SDK reads it itself, and the publish verb only refuses to push without it."
        ),
        ["NUGET_PACKAGES"] = new(
            Assemblies: Cli,
            Reason: "The .NET SDK's package-cache override; the bench reference capture finds the cache where the SDK does."
        ),
        ["PATH"] = new(
            Assemblies: Anywhere,
            Reason: "The operating system's executable search path; tools such as dxc, git and docker are found where a shell finds them."
        ),
        ["TF_BUILD"] = new(
            Assemblies: ["bootstrap"],
            Reason: "Defined by Azure Pipelines; the bootstrap app refuses to run in a pipeline."
        ),
        ["WAYLAND_DISPLAY"] = new(
            Assemblies: Platform,
            Reason: "Defined by the Wayland session; the native display backend is chosen from the session the process runs in."
        ),
        ["XDG_SESSION_TYPE"] = new(
            Assemblies: Platform,
            Reason: "Defined by the freedesktop session; the native display backend is chosen from the session the process runs in."
        ),
    };

    /// <summary>Returns whether an assembly may read a variable.</summary>
    /// <param name="name">The exact variable name.</param>
    /// <param name="assembly">The reading assembly's name.</param>
    /// <returns><see langword="true"/> when the variable is allowlisted and its entry admits the assembly.</returns>
    public static bool Admits(string name, string assembly) {
        if (!Entries.TryGetValue(
            key: name,
            value: out var entry
        )) {
            return false;
        }

        if (entry.Assemblies.Count == 0) {
            return true;
        }

        foreach (var admitted in entry.Assemblies) {
            if (string.Equals(
                a: admitted,
                b: assembly,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }
        }

        return false;
    }
}
/// <summary>One allowlisted environment variable.</summary>
/// <param name="Assemblies">The assemblies that may read it; empty admits every assembly.</param>
/// <param name="Reason">Why the value comes from the environment rather than a flag, a document or a profile.</param>
public sealed record EnvironmentReadEntry(IReadOnlyList<string> Assemblies, string Reason);
