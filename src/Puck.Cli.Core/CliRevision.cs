namespace Puck.Cli;

/// <summary>
/// The revision a <c>puck</c> build was made at, and whether the checkout it runs in has moved past it. A CLI installed
/// as a global tool runs whatever revision it was packed from, so a checkout whose code has moved on can be driven by
/// an older adapter or verb without any sign of it; <c>puck --version</c> and <c>puck mcp --profile operator</c> say so on
/// standard error. The check reads only the working directory's checkout and never requires one: outside a checkout,
/// with no git, or with no recorded revision, it says nothing.
/// </summary>
public static class CliRevision {
    // The longest the HEAD lookup may hold a verb's start.
    private static readonly TimeSpan HeadDeadline = TimeSpan.FromSeconds(seconds: 5);

    /// <summary>The section of the CLI reference that says how to reinstall the checkout's CLI.</summary>
    public const string ReinstallReference = "docs/reference/cli.md#installing-the-checkouts-cli-on-path";

    /// <summary>Gets the commit this CLI was built at, from the runtime configuration's <c>Puck.SourceRevision</c>
    /// (stamped by Directory.Build.targets, the key <c>WorldSchema.SourceRevision</c> reads in a World), or
    /// <see langword="null"/> when the build recorded none.</summary>
    public static string? Built { get; } = ((AppContext.GetData(name: "Puck.SourceRevision") is string { Length: > 0 } revision) ? revision : null);

    /// <summary>Indicates whether a refusal's reason is a strict reader refusing a member it does not know: System.Text.Json's
    /// unmapped member, or a strict CLI reader's unknown member. A CLI built before the document's schema gained that member
    /// refuses it this way, so the reason alone cannot tell revision skew from a bad document.</summary>
    /// <param name="reason">The refusal's reason.</param>
    /// <returns><see langword="true"/> when the reason names an unknown member.</returns>
    public static bool NamesUnknownMember(string reason) => (
        reason.Contains(comparisonType: StringComparison.Ordinal, value: "could not be mapped to any .NET member") ||
        reason.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "unknown member")
    );
    /// <summary>Writes the stale-revision warning to standard error after a refusal that names an unknown member, when this
    /// CLI was built at another revision than the working directory's checkout, so revision skew names itself.</summary>
    /// <param name="reason">The refusal's reason.</param>
    public static void ExplainUnknownMember(string reason) {
        if (NamesUnknownMember(reason: reason) && (StaleWarningAsync(built: Built).GetAwaiter().GetResult() is { } warning)) {
            Console.Error.WriteLine(value: warning);
        }
    }

    /// <summary>Spells a version with the revision it was built at as semantic-version build metadata.</summary>
    /// <param name="version">The package version.</param>
    /// <param name="revision">The commit, or <see langword="null"/> when the build recorded none.</param>
    /// <returns><c>version+revision</c>, or the version alone.</returns>
    public static string Describe(string version, string? revision) => (string.IsNullOrEmpty(value: revision)
        ? version
        : $"{version}+{revision}");
    /// <summary>Compares the revision a build was made at with a checkout's HEAD.</summary>
    /// <param name="built">The build's commit, or <see langword="null"/> when it recorded none.</param>
    /// <param name="checkout">The checkout's root directory.</param>
    /// <param name="head">The checkout's HEAD commit, or <see langword="null"/> when it could not be read.</param>
    /// <returns>The warning to print, or <see langword="null"/> when the two agree or either is unknown.</returns>
    public static string? Compare(string? built, string checkout, string? head) =>
        ((string.IsNullOrEmpty(value: built) || string.IsNullOrEmpty(value: head) || string.Equals(
            a: built,
            b: head,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ))
            ? null
            : $"puck: this CLI was built at {built}, but the checkout at {checkout} is at {head}. A CLI from another revision can behave differently from the checkout's code; reinstall it from the checkout ({ReinstallReference}).");
    /// <summary>Reads the checkout holding the working directory and returns the warning <see cref="Compare"/> gives for
    /// it.</summary>
    /// <param name="built">The build's commit, or <see langword="null"/> when it recorded none.</param>
    /// <param name="workingDirectory">The directory whose checkout is read; <see langword="null"/> is the process's working
    /// directory.</param>
    /// <param name="cancellationToken">Cancels the HEAD lookup.</param>
    /// <returns>The warning, or <see langword="null"/>.</returns>
    public static async Task<string?> StaleWarningAsync(string? built, string? workingDirectory = null, CancellationToken cancellationToken = default) {
        if (string.IsNullOrEmpty(value: built)) {
            return null;
        }

        var checkout = RepositoryPaths.Ascend(
            probe: static directory => (File.Exists(path: Path.Combine(
                path1: directory.FullName,
                path2: "Puck.slnx"
            ))
                ? directory.FullName
                : null),
            start: (workingDirectory ?? Environment.CurrentDirectory)
        );

        if (checkout is null) {
            return null;
        }

        try {
            var run = await CliGit.RunAsync(
                arguments: ["rev-parse", "--verify", "--quiet", "HEAD"],
                cancellationToken: cancellationToken,
                repository: checkout,
                timeout: HeadDeadline
            ).ConfigureAwait(continueOnCapturedContext: false);

            return ((run.ExitCode == 0)
                ? Compare(
                    built: built,
                    checkout: checkout,
                    head: run.Stdout.Trim()
                )
                : null);
        } catch (Exception error) when ((error is TimeoutException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)) {
            // No git, or one that does not answer: the check is advice, and its absence is not a failure.
            return null;
        }
    }
}
