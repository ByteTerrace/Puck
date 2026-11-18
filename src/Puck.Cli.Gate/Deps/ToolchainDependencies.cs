using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Deps;

/// <summary>The toolchain half of <c>puck deps update</c>: the .NET SDK <c>global.json</c> pins, the MSBuild SDK packages
/// beside it, and the .NET tools in <c>.config/dotnet-tools.json</c>. The SDK moves to the newest stable SDK of the major
/// it already pins (any feature band: the pin is exact and rolls forward nowhere), read from Microsoft's release index; a
/// stable SDK of a newer major is held, since the target framework moves by hand. Every Dockerfile that names an SDK
/// image follows <c>global.json</c>, which is the one place the pin lives. MSBuild SDK packages and tools move through
/// the NuGet feed like a plain version: the newest stable of their major, or of any major under <c>--major</c>.</summary>
public static partial class ToolchainDependencies {
    private const string Ecosystem = DependencyEcosystems.Sdk;
    private const string ReleaseIndex = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

    [GeneratedRegex(pattern: @"(?<head>^\s*FROM\s+mcr\.microsoft\.com/dotnet/sdk:)(?<version>[^\s]+)", options: (RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline))]
    private static partial Regex SdkImage();

    private static async Task MoveSdkAsync(DependencyContext context, string globalJson, JsonObject root) {
        if (!DependencyVersion.TryParse(text: (string?)root["sdk"]?["version"], version: out var current)) { return; }
        using var document = JsonDocument.Parse(json: await context.Http.GetStringAsync(cancellationToken: context.CancellationToken, requestUri: ReleaseIndex).ConfigureAwait(continueOnCapturedContext: false));
        var channels = document.RootElement.GetProperty(propertyName: "releases-index").EnumerateArray()
            .Select(selector: static channel => (Channel: DependencyVersion.Parse(text: channel.GetProperty(propertyName: "channel-version").GetString()!), Sdk: channel.GetProperty(propertyName: "latest-sdk").GetString()!, Phase: channel.GetProperty(propertyName: "support-phase").GetString()))
            .ToArray();
        var latestOfMajor = channels.Where(predicate: channel => (channel.Channel.Major == current.Major)).Select(selector: static channel => DependencyVersion.TryParse(text: channel.Sdk, version: out var sdk) ? sdk : null).OfType<DependencyVersion>().Where(predicate: static sdk => sdk.IsStable).Order().LastOrDefault();

        if ((latestOfMajor is not null) && (latestOfMajor.CompareTo(other: current) > 0)) {
            var text = File.ReadAllText(path: globalJson);

            context.Files.Write(path: globalJson, text: Regex.Replace(evaluator: match => $"{match.Groups["head"].Value}{latestOfMajor.Text}{match.Groups["tail"].Value}", input: text, options: RegexOptions.CultureInvariant, pattern: $"(?<head>\"sdk\"\\s*:\\s*\\{{[^}}]*?\"version\"\\s*:\\s*\"){Regex.Escape(str: current.Text)}(?<tail>\")"));
            context.Report.Add(move: new DependencyMove(Ecosystem: Ecosystem, From: current.Text, Name: ".NET SDK (global.json)", To: latestOfMajor.Text));
        }
        foreach (var channel in channels.Where(predicate: channel => ((channel.Channel.Major > current.Major) && (channel.Phase is "active" or "maintenance")) && DependencyVersion.TryParse(text: channel.Sdk, version: out var sdk) && sdk.IsStable)) {
            context.Report.Add(hold: new DependencyHold(Available: channel.Sdk, Current: current.Text, Ecosystem: Ecosystem, Name: ".NET SDK (global.json)", Reason: $"the .NET {channel.Channel.Text} SDK is a new major; the target framework moves by hand"));
        }
    }
    private static async Task MoveMsBuildSdksAsync(DependencyContext context, string globalJson, JsonObject root) {
        if (root["msbuild-sdks"] is not JsonObject sdks) { return; }
        foreach (var (id, node) in sdks.ToArray()) {
            if (!DependencyVersion.TryParse(text: (string?)node, version: out var current)) { continue; }
            if (await NuGetDependencies.FloorTargetAsync(context: context, current: current, ecosystem: Ecosystem, holdReason: "an MSBuild SDK stays within its major", id: id).ConfigureAwait(continueOnCapturedContext: false) is not { } target) { continue; }
            context.Files.Write(path: globalJson, text: DependencyFiles.ReplaceJsonPair(name: id, newSpec: target.Text, oldSpec: current.Text, text: File.ReadAllText(path: globalJson)));
            context.Report.Add(move: new DependencyMove(Ecosystem: Ecosystem, From: current.Text, Name: $"{id} (global.json)", To: target.Text));
        }
    }
    // Every SDK image tag follows global.json's pin.
    private static void SyncImages(DependencyContext context, string version) {
        if (!CliGit.TryListFiles(paths: out var paths, reason: out var reason, repository: context.RepositoryRoot)) { throw new InvalidOperationException(message: $"git could not list the repository's files: {reason}"); }
        foreach (var relative in paths.Where(predicate: static path => Path.GetFileName(path: path).StartsWith(value: "Dockerfile", comparisonType: StringComparison.Ordinal) && !path.StartsWith(value: "experimental/", comparisonType: StringComparison.Ordinal)).Order(comparer: StringComparer.Ordinal)) {
            var path = Path.Combine(path1: context.RepositoryRoot, path2: relative);
            var text = File.ReadAllText(path: path);
            var edited = SdkImage().Replace(evaluator: match => $"{match.Groups["head"].Value}{version}", input: text);

            if (edited == text) { continue; }
            foreach (Match match in SdkImage().Matches(input: text)) {
                if (match.Groups["version"].Value != version) { context.Report.Add(move: new DependencyMove(Ecosystem: Ecosystem, From: match.Groups["version"].Value, Name: $"dotnet/sdk image ({relative})", To: version)); }
            }
            context.Files.Write(path: path, text: edited);
        }
    }
    private static async Task MoveToolsAsync(DependencyContext context) {
        var manifest = Path.Combine(path1: context.RepositoryRoot, path2: ".config", path3: "dotnet-tools.json");

        if (!File.Exists(path: manifest) || (JsonNode.Parse(json: File.ReadAllText(path: manifest)) is not JsonObject { } root) || (root["tools"] is not JsonObject tools)) { return; }
        foreach (var (id, node) in tools) {
            if (!DependencyVersion.TryParse(text: (string?)node?["version"], version: out var current)) { continue; }
            if (await NuGetDependencies.FloorTargetAsync(context: context, current: current, ecosystem: DependencyEcosystems.Tools, holdReason: "a tool stays within its major", id: id).ConfigureAwait(continueOnCapturedContext: false) is not { } target) { continue; }
            var text = File.ReadAllText(path: manifest);
            var edited = Regex.Replace(evaluator: match => $"{match.Groups["head"].Value}{target.Text}{match.Groups["tail"].Value}", input: text, options: RegexOptions.CultureInvariant, pattern: $"(?<head>\"{Regex.Escape(str: id)}\"\\s*:\\s*\\{{[^}}]*?\"version\"\\s*:\\s*\"){Regex.Escape(str: current.Text)}(?<tail>\")");

            context.Files.Write(path: manifest, text: edited);
            context.Report.Add(move: new DependencyMove(Ecosystem: DependencyEcosystems.Tools, From: current.Text, Name: id, To: target.Text));
        }
    }

    /// <summary>Moves the SDK pin, the MSBuild SDK packages and the SDK images that follow them.</summary>
    /// <param name="context">The run.</param>
    /// <returns>A task that completes when the files are rewritten.</returns>
    public static async Task RunSdkAsync(DependencyContext context) {
        var globalJson = Path.Combine(path1: context.RepositoryRoot, path2: "global.json");

        if (JsonNode.Parse(json: File.ReadAllText(path: globalJson)) is not JsonObject root) { return; }
        await MoveSdkAsync(context: context, globalJson: globalJson, root: root).ConfigureAwait(continueOnCapturedContext: false);
        await MoveMsBuildSdksAsync(context: context, globalJson: globalJson, root: root).ConfigureAwait(continueOnCapturedContext: false);
        if (JsonNode.Parse(json: File.ReadAllText(path: globalJson))?["sdk"]?["version"]?.GetValue<string>() is { } pinned) { SyncImages(context: context, version: pinned); }
    }
    /// <summary>Moves the .NET tools of <c>.config/dotnet-tools.json</c>.</summary>
    /// <param name="context">The run.</param>
    /// <returns>A task that completes when the manifest is rewritten.</returns>
    public static Task RunToolsAsync(DependencyContext context) => MoveToolsAsync(context: context);
}
