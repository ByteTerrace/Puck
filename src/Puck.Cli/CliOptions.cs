using System.CommandLine;
using System.Text.Json;

namespace Puck.Cli;

/// <summary>
/// The options that mean one thing on every verb they appear on, each built by exactly one factory so a spelling,
/// type, or default cannot drift between verbs.
/// </summary>
internal static class CliOptions {
    /// <summary>The build configuration every verb that reads or makes a build defaults to.</summary>
    public const string DefaultConfiguration = "Release";

    /// <summary>Gets the default for <see cref="Jobs"/>: half the logical processors, at least one and at most
    /// eight.</summary>
    public static int DefaultJobs => Math.Clamp(
        max: 8,
        min: 1,
        value: (Environment.ProcessorCount / 2)
    );

    /// <summary>Creates <c>--check</c>, the one dry mode: write nothing and exit 1 on any drift.</summary>
    /// <param name="description">The verb's own statement of what it compares.</param>
    /// <returns>The option.</returns>
    public static Option<bool> Check(string description) => new(name: "--check") { Description = description };
    /// <summary>Creates a verb that takes <see cref="Check"/> and nothing else: it writes its output by default, and
    /// with <c>--check</c> compares instead.</summary>
    /// <param name="name">The verb's name.</param>
    /// <param name="description">The verb's one-line description.</param>
    /// <param name="checkDescription">The verb's statement of what <c>--check</c> compares.</param>
    /// <param name="run">The action, given whether <c>--check</c> was passed; it returns the exit code.</param>
    /// <returns>The command.</returns>
    public static Command CheckVerb(string name, string description, string checkDescription, Func<bool, int> run) {
        var check = Check(description: checkDescription);
        var command = new Command(
            description: description,
            name: name
        ) { check };

        command.SetAction(action: parseResult => run(arg: parseResult.GetValue(option: check)));

        return command;
    }
    /// <summary>Creates <c>--configuration</c>, the build configuration a verb reads or builds, defaulting to
    /// <see cref="DefaultConfiguration"/>.</summary>
    /// <param name="description">What the verb does with the configuration.</param>
    /// <returns>The option.</returns>
    public static Option<string> Configuration(string description) => new(name: "--configuration") {
        DefaultValueFactory = static _ => DefaultConfiguration,
        Description = description,
    };
    /// <summary>Creates <c>--file-list</c>, the path of a JSON file holding an array of paths, both the file and its
    /// entries resolving against the working directory. <see cref="ReadFileList"/> reads it.</summary>
    /// <param name="description">What the verb does with the listed files.</param>
    /// <returns>The option.</returns>
    public static Option<string> FileList(string description) => new(name: "--file-list") { Description = description };
    /// <summary>Creates <c>--jobs</c>, the most child processes a verb runs at once, defaulting to
    /// <see cref="DefaultJobs"/> and refusing a value below one.</summary>
    /// <param name="description">What one job is for this verb.</param>
    /// <returns>The option.</returns>
    public static Option<int> Jobs(string description) {
        var option = new Option<int>(name: "--jobs") {
            DefaultValueFactory = static _ => DefaultJobs,
            Description = description,
        };

        option.Validators.Add(item: static result => {
            if (result.GetValueOrDefault<int>() < 1) {
                result.AddError(errorMessage: "--jobs must be at least 1.");
            }
        });

        return option;
    }
    /// <summary>Creates the argument of a verb that forwards its tokens to another tool's grammar: it takes every token
    /// after the verb, option-shaped ones included, and the parser refuses none of them as a misspelled option.</summary>
    /// <param name="name">The argument's name.</param>
    /// <param name="description">Which tool receives the tokens.</param>
    /// <returns>The argument.</returns>
    public static CliForwardedArgument Forwarded(string name, string description) => new(name: name) {
        Arity = ArgumentArity.ZeroOrMore,
        DefaultValueFactory = static _ => [],
        Description = description,
    };
    /// <summary>Creates <c>--json</c>, which switches a verb's results to one JSON object per line on standard
    /// output.</summary>
    /// <param name="description">What each record carries, or <see langword="null"/> for the shared wording.</param>
    /// <returns>The option.</returns>
    public static Option<bool> Json(string? description = null) => new(name: "--json") { Description = (description ?? "Write one JSON object per line instead of text.") };
    /// <summary>Creates <c>--output</c>, the one path a verb writes to, resolving against the working
    /// directory.</summary>
    /// <param name="description">What is written there.</param>
    /// <param name="required">Whether the verb refuses to run without it.</param>
    /// <returns>The option.</returns>
    public static Option<string> Output(string description, bool required = false) => new(name: "--output") {
        Description = description,
        Required = required,
    };
    /// <summary>Reads a <see cref="FileList"/> manifest: a JSON array of relative paths, resolved against
    /// <paramref name="baseDirectory"/>, sorted ordinally and without duplicates.</summary>
    /// <param name="manifest">The manifest's path, resolving against the working directory.</param>
    /// <param name="baseDirectory">The directory each entry resolves against; the working directory for a caller's
    /// own list.</param>
    /// <returns>The full path of every entry.</returns>
    /// <exception cref="ArgumentException">An entry is null, rooted, or climbs out of
    /// <paramref name="baseDirectory"/> through <c>..</c>, <c>.</c>, or an empty segment.</exception>
    /// <exception cref="IOException">The manifest cannot be read.</exception>
    /// <exception cref="JsonException">The manifest is not a JSON array of strings.</exception>
    public static string[] ReadFileList(string manifest, string baseDirectory) {
        using var document = JsonDocument.Parse(json: File.ReadAllText(path: manifest));

        if (document.RootElement.ValueKind != JsonValueKind.Array) {
            throw new JsonException(message: $"{CliPaths.ToDisplay(fullPath: Path.GetFullPath(path: manifest))} is not a JSON array of paths.");
        }

        var paths = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var entry in document.RootElement.EnumerateArray()) {
            var relative = ((entry.ValueKind == JsonValueKind.String)
                ? entry.GetString()!
                : throw new ArgumentException(message: $"A listed path must be a string, not {entry.ValueKind}."));

            if (
                Path.IsPathRooted(path: relative) ||
                relative.Split(
                '/',
                '\\'
            ).Any(predicate: static segment => (segment is ".." or "." or ""))
            ) {
                throw new ArgumentException(message: $"Expected a contained relative path: {relative}");
            }

            paths.Add(item: Path.GetFullPath(
                basePath: baseDirectory,
                path: relative
            ));
        }

        return [.. paths];
    }
}
/// <summary>An argument whose tokens belong to another tool's grammar; <see cref="CliOptions.Forwarded"/> creates
/// it.</summary>
/// <param name="name">The argument's name.</param>
internal sealed class CliForwardedArgument(string name) : Argument<string[]>(name: name);
