using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Puck.Cli;

/// <summary>
/// The options that mean one thing on every verb they appear on, each built by exactly one factory so a spelling,
/// type, or default cannot drift between verbs.
/// </summary>
public static class CliOptions {
    /// <summary>The build configuration every verb that reads or makes a build defaults to.</summary>
    public const string DefaultConfiguration = "Release";
    /// <summary>The MSBuild switch on every dotnet build, test, pack and restore a verb launches. The fleet's machines are
    /// shared: a node kept alive for reuse outlives the verb and holds memory other agents and GPU work need.</summary>
    public const string NoNodeReuse = "-nodeReuse:false";

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
    /// <param name="detail">The help text printed below the usage: what the verb generates from and writes, or
    /// <see langword="null"/> for none.</param>
    /// <returns>The command.</returns>
    public static Command CheckVerb(string name, string description, string checkDescription, Func<bool, int> run, string? detail = null) {
        var check = Check(description: checkDescription);
        var command = new Command(
            description: description,
            name: name
        ) { check };

        command.SetAction(action: parseResult => run(arg: parseResult.GetValue(option: check)));

        if (detail is not null) {
            _ = command.Detail(detail: detail);
        }

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
    /// <summary>Creates <c>--jobs</c>, the most child processes or agent runs a verb runs at once, refusing a value
    /// below one or above <paramref name="maximum"/> at parse time.</summary>
    /// <param name="description">What one job is for this verb.</param>
    /// <param name="defaultValue">The value when the option is omitted; <see langword="null"/> is
    /// <see cref="DefaultJobs"/>.</param>
    /// <param name="maximum">The largest value the verb accepts, or <see langword="null"/> for no bound.</param>
    /// <returns>The option.</returns>
    public static Option<int> Jobs(string description, int? defaultValue = null, int? maximum = null) => Count(
        defaultValue: (defaultValue ?? DefaultJobs),
        description: description,
        maximum: maximum,
        minimum: 1,
        name: "--jobs"
    );
    /// <summary>Creates a whole-number option: a count, a size, a duration in the unit its name or description states.
    /// The value is parsed and bounded in one place at parse time, so a token that is not a whole number, or one outside
    /// the bounds, is a usage error naming the option, never an exception. A positive count passes a
    /// <paramref name="minimum"/> of one; a non-negative one passes zero.</summary>
    /// <typeparam name="T">The integer type.</typeparam>
    /// <param name="name">The option's name.</param>
    /// <param name="description">What the number counts, in which unit.</param>
    /// <param name="minimum">The smallest value accepted.</param>
    /// <param name="defaultValue">The value when the option is omitted, or <see langword="null"/> for none: the option is
    /// then <see langword="default"/> when omitted, unless the caller makes it required.</param>
    /// <param name="maximum">The largest value accepted, or <see langword="null"/> for no bound.</param>
    /// <returns>The option.</returns>
    public static Option<T> Count<T>(string name, string description, T minimum, T? defaultValue = null, T? maximum = null) where T : struct, IBinaryInteger<T> {
        var option = new Option<T>(name: name) {
            CustomParser = result => (ParseNumber(
                maximum: maximum,
                minimum: minimum,
                name: name,
                result: result,
                whole: true
            ) ?? defaultValue ?? default),
            Description = description,
        };

        if (defaultValue is { } fallback) {
            option.DefaultValueFactory = _ => fallback;
        }

        return option;
    }
    /// <summary>Creates a whole-number option as <see cref="Count{T}"/> does whose omission the verb tells apart from
    /// every value: its value is <see langword="null"/> when omitted.</summary>
    /// <typeparam name="T">The integer type.</typeparam>
    /// <param name="name">The option's name.</param>
    /// <param name="description">What the number counts, in which unit, and what omitting it means.</param>
    /// <param name="minimum">The smallest value accepted.</param>
    /// <param name="maximum">The largest value accepted, or <see langword="null"/> for no bound.</param>
    /// <returns>The option.</returns>
    public static Option<T?> OptionalCount<T>(string name, string description, T minimum, T? maximum = null) where T : struct, IBinaryInteger<T> => new(name: name) {
        CustomParser = result => ParseNumber(
            maximum: maximum,
            minimum: minimum,
            name: name,
            result: result,
            whole: true
        ),
        Description = description,
    };
    /// <summary>Creates a finite real-number option (a threshold, a price, a distance), parsed and bounded at parse time
    /// the way <see cref="Count{T}"/> parses a whole number; its value is <see langword="null"/> when omitted and no
    /// <paramref name="defaultValue"/> is given.</summary>
    /// <param name="name">The option's name.</param>
    /// <param name="description">What the number measures, in which unit.</param>
    /// <param name="minimum">The smallest value accepted.</param>
    /// <param name="defaultValue">The value when the option is omitted, or <see langword="null"/>.</param>
    /// <param name="maximum">The largest value accepted, or <see langword="null"/> for no bound.</param>
    /// <returns>The option.</returns>
    public static Option<double?> Amount(string name, string description, double minimum, double? defaultValue = null, double? maximum = null) {
        var option = new Option<double?>(name: name) {
            CustomParser = result => (ParseNumber(
                maximum: maximum,
                minimum: minimum,
                name: name,
                result: result,
                whole: false
            ) ?? defaultValue),
            Description = description,
        };

        if (defaultValue is { } fallback) {
            option.DefaultValueFactory = _ => fallback;
        }

        return option;
    }
    // The one number parser every count and amount shares: invariant culture, finite, inside its bounds. A refusal is
    // an error on the result, so the parser reports it as a usage error and nothing reads a value that failed.
    private static T? ParseNumber<T>(ArgumentResult result, string name, T minimum, T? maximum, bool whole) where T : struct, INumber<T> {
        if (result.Tokens.Count == 0) {
            return null;
        }

        var token = result.Tokens[^1].Value;

        if (
            T.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out var value,
                s: token,
                style: (whole ? NumberStyles.Integer : NumberStyles.Float)
            ) &&
            T.IsFinite(value: value) &&
            (value >= minimum) &&
            ((maximum is not { } upper) || (value <= upper))
        ) {
            return value;
        }

        var kind = (whole ? "a whole number" : "a finite number");
        var range = ((maximum is { } most)
            ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"from {minimum} to {most}")
            : string.Create(provider: CultureInfo.InvariantCulture, handler: $"of at least {minimum}"));

        result.AddError(errorMessage: $"{name} takes {kind} {range}, not '{token}'.");

        return null;
    }
    /// <summary>Creates a text option whose value names something (a holder, a lane, a symbol, a channel): a blank value,
    /// or one spanning several lines, is a usage error at parse time.</summary>
    /// <param name="name">The option's name.</param>
    /// <param name="description">What the value names.</param>
    /// <param name="required">Whether the verb refuses to run without it.</param>
    /// <returns>The option; its value is <see langword="null"/> when omitted.</returns>
    public static Option<string> Name(string name, string description, bool required = false) => new(name: name) {
        CustomParser = result => ParseName(
            name: name,
            result: result
        ),
        Description = description,
        Required = required,
    };
    /// <summary>Creates a positional argument whose value names something, refused at parse time as
    /// <see cref="Name"/> refuses a blank or multi-line value.</summary>
    /// <param name="name">The argument's name.</param>
    /// <param name="description">What the value names.</param>
    /// <returns>The argument.</returns>
    public static Argument<string> NameArgument(string name, string description) => new(name: name) {
        CustomParser = result => ParseName(
            name: $"<{name}>",
            result: result
        ),
        Description = description,
    };
    private static string? ParseName(ArgumentResult result, string name) {
        if (result.Tokens.Count == 0) {
            return null;
        }

        var token = result.Tokens[^1].Value;

        if (
            string.IsNullOrWhiteSpace(value: token) ||
            token.Contains(value: '\n') ||
            token.Contains(value: '\r')
        ) {
            result.AddError(errorMessage: $"{name} takes a single non-blank line.");

            return null;
        }

        return token;
    }
    /// <summary>Refuses, at parse time, any invocation of <paramref name="command"/> that passes more than one of
    /// <paramref name="options"/>. It reads only whether each option was typed, never a value, so it can never read a
    /// value that failed to parse.</summary>
    /// <param name="command">The command the options belong to.</param>
    /// <param name="options">The options that contradict one another.</param>
    public static void Exclusive(Command command, params Option[] options) => command.Validators.Add(item: result => {
        var typed = options.Where(predicate: option => (result.GetResult(option: option) is { Implicit: false })).Select(selector: static option => option.Name).ToList();

        if (typed.Count > 1) {
            result.AddError(errorMessage: $"{string.Join(separator: " and ", values: typed)} cannot be combined.");
        }
    });
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
    /// <summary>Creates the argument of a verb that runs another program: the executable and its arguments, which must
    /// follow a literal <c>--</c>. <see cref="CliRoot.Parse"/> refuses a token that lands in it before the
    /// <c>--</c>, so a mistyped option of the verb is a usage error at once instead of the program to run.</summary>
    /// <param name="description">What the program is run under.</param>
    /// <returns>The argument, named <c>command</c>, taking one token or more.</returns>
    public static CliForwardedArgument ForwardedCommand(string description) => new(name: "command") {
        AfterSeparator = true,
        Arity = ArgumentArity.OneOrMore,
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
/// <summary>An argument whose tokens belong to another tool's grammar; <see cref="CliOptions.Forwarded"/> and
/// <see cref="CliOptions.ForwardedCommand"/> create it.</summary>
/// <param name="name">The argument's name.</param>
public sealed class CliForwardedArgument(string name) : Argument<string[]>(name: name) {
    /// <summary>Gets whether every token of the argument must follow a literal <c>--</c>.</summary>
    public bool AfterSeparator { get; init; }
}
