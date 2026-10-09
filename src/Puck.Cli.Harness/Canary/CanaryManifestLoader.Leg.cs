using System.Text.Json;

namespace Puck.Cli.Canary;

public static partial class CanaryManifestLoader {
    private static CanaryLeg ReadLeg(JsonElement element, string id, string name, string repositoryRoot, string canaryDirectory) {
        var context = $"canary '{id}' {name} leg";

        CliStrictJson.RequireOnlyMembers(
            element: element,
            context: context,
            unknownMemberDetail: UnknownMemberDetail,
            refusal: Refusal,
            "authorities",
            "authorityWorld",
            "commands",
            "connect",
            "entry",
            "expect",
            "hideShaderCompiler",
            "package",
            "relaunch",
            "runSchedule",
            "script",
            "world"
        );

        var worldText = CliStrictJson.ReadRequiredString(
            context: context,
            element: element,
            member: "world",
            refusal: Refusal
        );
        var scriptText = CliStrictJson.ReadRequiredString(
            context: context,
            element: element,
            member: "script",
            refusal: Refusal
        );
        var worldPath = ResolveFile(
            basePath: repositoryRoot,
            containmentRoot: repositoryRoot,
            context: $"{context} world",
            rawPath: worldText
        );
        var scriptPath = ResolveFile(
            basePath: canaryDirectory,
            containmentRoot: repositoryRoot,
            context: $"{context} script",
            rawPath: scriptText
        );
        string? authorityWorldPath = null;
        string? entry = null;
        var connect = false;

        if (element.TryGetProperty(
            propertyName: "entry",
            value: out var entryElement
        )) {
            if (
                (entryElement.ValueKind != JsonValueKind.String) ||
                string.IsNullOrWhiteSpace(value: entryElement.GetString())
            ) {
                throw new CanaryManifestRefusal(message: $"{context} entry must be a non-empty world name.");
            }
            if (!worldPath.EndsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: ".puck"
            )) {
                throw new CanaryManifestRefusal(message: $"{context} entry names a declared world of a composition source, and its world is not a .puck source.");
            }

            entry = entryElement.GetString();
        }

        if (element.TryGetProperty(
            propertyName: "authorityWorld",
            value: out var authorityElement
        )) {
            if (
                (authorityElement.ValueKind != JsonValueKind.String) ||
                string.IsNullOrWhiteSpace(value: authorityElement.GetString())
            ) {
                throw new CanaryManifestRefusal(message: $"{context} authorityWorld must be a non-empty path string.");
            }

            authorityWorldPath = ResolveFile(
                rawPath: authorityElement.GetString()!,
                basePath: repositoryRoot,
                containmentRoot: repositoryRoot,
                context: $"{context} authorityWorld"
            );
        }
        if (element.TryGetProperty(
            propertyName: "connect",
            value: out var connectElement
        )) {
            connect = connectElement.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new CanaryManifestRefusal(message: $"{context} connect must be true or false."),
            };
        }
        if (
            connect &&
            (authorityWorldPath is null)
        ) {
            throw new CanaryManifestRefusal(message: $"{context} connect requires authorityWorld so the runner owns the endpoint it dials.");
        }

        var hideShaderCompiler = false;

        if (element.TryGetProperty(
            propertyName: "hideShaderCompiler",
            value: out var hideElement
        )) {
            hideShaderCompiler = hideElement.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new CanaryManifestRefusal(message: $"{context} hideShaderCompiler must be true or false."),
            };
        }

        var runSchedule = false;

        if (element.TryGetProperty(propertyName: "runSchedule", value: out var scheduleElement)) {
            runSchedule = scheduleElement.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new CanaryManifestRefusal(message: $"{context} runSchedule must be true or false."),
            };
        }

        var authorities = ReadAuthorities(
            canaryDirectory: canaryDirectory,
            context: context,
            element: element,
            legScriptPath: scriptPath,
            legWorldPath: worldPath,
            repositoryRoot: repositoryRoot
        );

        if (
            (authorities.Count != 0) &&
            (authorityWorldPath is not null)
        ) {
            throw new CanaryManifestRefusal(message: $"{context} authorities and authorityWorld are mutually exclusive transport shapes.");
        }

        var commands = ReadCommands(
            context: context,
            element: element,
            scriptPath: scriptPath
        );
        var assertions = ReadAssertions(
            authorityIds: authorities.Select(selector: static role => role.Id).ToHashSet(comparer: StringComparer.Ordinal),
            context: context,
            element: element
        );
        CanaryRelaunch? relaunch = null;

        if (element.TryGetProperty(
            propertyName: "relaunch",
            value: out var relaunchElement
        )) {
            if (
                (authorities.Count != 0) ||
                (authorityWorldPath is not null)
            ) {
                throw new CanaryManifestRefusal(message: $"{context} relaunch boots one process again, so it takes no authorities or authorityWorld.");
            }

            relaunch = ReadRelaunch(
                canaryDirectory: canaryDirectory,
                context: $"{context} relaunch",
                element: relaunchElement,
                repositoryRoot: repositoryRoot
            );
        }

        if (runSchedule && ((authorities.Count != 0) || (authorityWorldPath is not null))) {
            throw new CanaryManifestRefusal(message: $"{context} runSchedule requires one process without authorities or authorityWorld.");
        }

        return new CanaryLeg(
            Assertions: assertions,
            Authorities: authorities,
            AuthorityWorldPath: authorityWorldPath,
            Commands: commands,
            Connect: connect,
            Entry: entry,
            HideShaderCompiler: hideShaderCompiler,
            Name: name,
            Package: ReadPackage(
                canaryDirectory: canaryDirectory,
                context: context,
                leg: element,
                oneProcess: ((authorities.Count == 0) && (authorityWorldPath is null)),
                repositoryRoot: repositoryRoot
            ),
            Relaunch: relaunch,
            RunSchedule: runSchedule,
            ScriptPath: scriptPath,
            WorldPath: worldPath
        );
    }
}
