using System.CommandLine;
using System.Globalization;
using System.Text;

using Puck.SdfVm;

namespace Puck.Cli.Mesh;

/// <summary>
/// <c>puck mesh identity</c>: prints the bake identity a build keys its stored meshes by (<see cref="SdfMeshBakeIdentity"/>)
/// with its parts: the code the bake runs (<see cref="SdfMeshBakeClosure"/>), counted by assembly, and the hashes of its
/// bake kernels' bytecode. With <c>--against</c> it names what differs from another build's identity, member by member, so
/// an edit can be checked to keep or move the stored meshes before any World boots; the value is also the key a cache of
/// the store is restored and saved under. Reads assemblies and kernels only. Exit codes: 0 printed; 1 a build could not be
/// read; 2 a usage error.
/// </summary>
public static class MeshIdentityCommand {
    private const string Verb = "mesh identity";

    /// <summary>Creates the <c>identity</c> subverb.</summary>
    /// <returns>The subverb.</returns>
    public static Command Create() {
        var buildOption = new Option<string?>(name: "--build") { Description = "The directory of the World build to read (its Puck.World.dll's directory); absent reads this tool's own build." };
        var againstOption = new Option<string?>(name: "--against") { Description = "Another build's directory: name each part and code member whose hash differs between the two." };
        var backendOption = new Option<string>(name: "--backend") { DefaultValueFactory = static _ => "directx", Description = "The backend whose bake kernels key the meshes: directx or vulkan." };
        var membersOption = new Option<bool>(name: "--members") { Description = "Also print every code member the bake runs with its hash." };
        var whyOption = new Option<string?>(name: "--why") { Description = "Print, for each code member whose name holds this text, the chain of members that reached it from a root of the bake." };
        var command = new Command(
            description: "Prints the bake identity a build keys its stored meshes by: its value, the code the bake runs counted by assembly, and its bake kernels' hashes; with --against, what differs from another build's; with --why, how the walk reached a member.",
            name: "identity"
        ) { buildOption, againstOption, backendOption, membersOption, whyOption };

        command.SetAction(action: parseResult => Run(
            against: parseResult.GetValue(option: againstOption),
            backend: parseResult.GetValue(option: backendOption)!,
            build: parseResult.GetValue(option: buildOption),
            members: parseResult.GetValue(option: membersOption),
            why: parseResult.GetValue(option: whyOption)
        ));

        return command;
    }
    private static int Run(string? build, string? against, string backend, bool members, string? why) {
        var extension = backend switch {
            "directx" => ".dxil",
            "vulkan" => ".spv",
            _ => null,
        };

        if (extension is null) {
            Console.Error.WriteLine(value: $"{Verb}: unknown --backend '{backend}'; expected directx or vulkan.");
            return 2;
        }

        var buildDirectory = Path.GetFullPath(path: (build ?? AppContext.BaseDirectory));

        if (!TryRead(directory: buildDirectory, extension: extension, identity: out var identity)) { return 1; }

        var output = new StringBuilder();
        var assemblies = identity.Members.Keys
            .GroupBy(keySelector: static member => member[..member.IndexOf(value: '!', comparisonType: StringComparison.Ordinal)], comparer: StringComparer.Ordinal)
            .Select(selector: static group => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{group.Key} {group.Count()}"));

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"mesh identity {identity.Value:x32} ({backend}, build {buildDirectory}, entry version {identity.EntryVersion})\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  code {identity.Code:x32}: {identity.Members.Count} members ({string.Join(separator: ", ", values: assemblies)})\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  mask kernel {identity.MaskKernel:x32}, bake kernel {identity.BakeKernel:x32}\n");
        if (members) {
            foreach (var (member, hash) in identity.Members) {
                output.Append(provider: CultureInfo.InvariantCulture, handler: $"    {hash:x32} {member}\n");
            }
        }
        if (why is not null) {
            var reasons = SdfMeshBakeClosure.Of(directory: buildDirectory).Reasons;

            foreach (var member in reasons.Keys.Where(predicate: member => member.Contains(value: why, comparisonType: StringComparison.Ordinal))) {
                output.Append(value: "  why ").Append(value: member).Append(value: '\n');
                for (var step = reasons[member]; (step != "root") && reasons.TryGetValue(key: step, value: out var next); step = next) {
                    output.Append(value: "    from ").Append(value: step).Append(value: '\n');
                }
            }
        }
        if (against is not null) {
            var againstDirectory = Path.GetFullPath(path: against);

            if (!TryRead(directory: againstDirectory, extension: extension, identity: out var other)) { return 1; }

            var changed = SdfMeshBakeClosure.Changed(earlier: other.Members, members: identity.Members);

            output.Append(provider: CultureInfo.InvariantCulture, handler: $"  against {other.Value:x32} (build {againstDirectory}): {((other.Value == identity.Value) ? "the same identity" : "another identity")}\n");
            output.Append(provider: CultureInfo.InvariantCulture, handler: $"    entry version {((other.EntryVersion == identity.EntryVersion) ? "same" : $"{other.EntryVersion} -> {identity.EntryVersion}")}, mask kernel {((other.MaskKernel == identity.MaskKernel) ? "same" : "differs")}, bake kernel {((other.BakeKernel == identity.BakeKernel) ? "same" : "differs")}, code {changed.Count} member(s) differ\n");
            foreach (var member in changed) {
                output.Append(provider: CultureInfo.InvariantCulture, handler: $"      {(identity.Members.ContainsKey(key: member) ? (other.Members.ContainsKey(key: member) ? "changed" : "added") : "removed")} {member}\n");
            }
        }

        Console.Out.Write(value: output.ToString());

        return 0;
    }
    private static bool TryRead(string directory, string extension, out SdfMeshBakeIdentity identity) {
        try {
            identity = MeshCommand.BuildIdentity(buildDirectory: directory, extension: extension);
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) {
            Console.Error.WriteLine(value: $"{Verb}: cannot read the bake identity of the build in '{directory}' — {exception.Message}");
            identity = new SdfMeshBakeIdentity(value: UInt128.Zero);
            return false;
        }
    }
}
