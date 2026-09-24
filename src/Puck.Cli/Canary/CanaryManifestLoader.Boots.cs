using System.Text.Json;

namespace Puck.Cli.Canary;

internal static partial class CanaryManifestLoader {
    // A relaunch names the document by its file name inside the leg's run directory, which the first boot writes
    // (world.save {run}/<name>), so the manifest can name it before any run directory exists.
    private static CanaryRelaunch ReadRelaunch(JsonElement element, string context, string repositoryRoot, string canaryDirectory) {
        var row = CliStrictJson.RequireObject(
            context: context,
            element: element,
            refusal: Refusal
        );

        CliStrictJson.RequireOnlyMembers(
            element: row,
            context: context,
            unknownMemberDetail: UnknownMemberDetail,
            refusal: Refusal,
            "commands",
            "script",
            "world"
        );

        var world = CliStrictJson.ReadRequiredString(
            context: context,
            element: row,
            member: "world",
            refusal: Refusal
        );

        if (
            (world.IndexOfAny(anyOf: ['/', '\\']) >= 0) ||
            (world is "." or "..") ||
            string.IsNullOrWhiteSpace(value: world)
        ) {
            throw new CanaryManifestRefusal(message: $"{context} world '{world}' must be a bare file name the first boot writes into the leg's run directory.");
        }

        var scriptPath = ResolveFile(
            basePath: canaryDirectory,
            containmentRoot: repositoryRoot,
            context: $"{context} script",
            rawPath: CliStrictJson.ReadRequiredString(
                context: context,
                element: row,
                member: "script",
                refusal: Refusal
            )
        );

        return new CanaryRelaunch(
            Commands: ReadCommands(
                context: context,
                element: row,
                scriptPath: scriptPath
            ),
            ScriptPath: scriptPath,
            WorldFileName: world
        );
    }
    // A package is prepared in the leg's run directory before its one process boots, so a script names it as
    // {run}/<output>. The source is a file in the repository, resolved from the canary's directory, which the runner
    // packages from a copy of the directory holding it, so canaries share one fixture; alter is a logical path inside
    // the package.
    private static CanaryPackage? ReadPackage(JsonElement leg, string context, bool oneProcess, string repositoryRoot, string canaryDirectory) {
        if (!leg.TryGetProperty(
            propertyName: "package",
            value: out var element
        )) {
            return null;
        }

        context = $"{context} package";

        if (!oneProcess) {
            throw new CanaryManifestRefusal(message: $"{context} is prepared for one process, so it takes no authorities or authorityWorld.");
        }

        var row = CliStrictJson.RequireObject(
            context: context,
            element: element,
            refusal: Refusal
        );

        CliStrictJson.RequireOnlyMembers(
            element: row,
            context: context,
            unknownMemberDetail: UnknownMemberDetail,
            refusal: Refusal,
            "alter",
            "output",
            "source"
        );

        var output = CliStrictJson.ReadRequiredString(
            context: context,
            element: row,
            member: "output",
            refusal: Refusal
        );

        if (
            (output.IndexOfAny(anyOf: ['/', '\\']) >= 0) ||
            (output is "." or ".." or "state" or "package-source" or "package-build") ||
            string.IsNullOrWhiteSpace(value: output)
        ) {
            throw new CanaryManifestRefusal(message: $"{context} output '{output}' must be a bare directory name the runner creates in the leg's run directory.");
        }

        string? alter = null;

        if (row.TryGetProperty(
            propertyName: "alter",
            value: out var alterElement
        )) {
            alter = ((alterElement.ValueKind == JsonValueKind.String)
                ? alterElement.GetString()
                : null);

            if (
                string.IsNullOrWhiteSpace(value: alter) ||
                (alter.IndexOf(value: '\\') >= 0) ||
                Path.IsPathRooted(path: alter) ||
                alter.Split('/').Any(predicate: static segment => (segment is "" or "." or ".."))
            ) {
                throw new CanaryManifestRefusal(message: $"{context} alter must be a logical path inside the package: relative, with forward slashes and no '.' or '..' segment.");
            }
        }

        return new CanaryPackage(
            Alter: alter,
            OutputName: output,
            SourcePath: ResolveFile(
                basePath: canaryDirectory,
                containmentRoot: repositoryRoot,
                context: $"{context} source",
                rawPath: CliStrictJson.ReadRequiredString(
                    context: context,
                    element: row,
                    member: "source",
                    refusal: Refusal
                )
            )
        );
    }
}
