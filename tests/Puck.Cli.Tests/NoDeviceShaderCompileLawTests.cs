using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>No shader compiles on a player's device through the Direct3D HLSL compiler: no Puck assembly in the World's
/// Release output declares a platform invocation into <c>d3dcompiler_*.dll</c>. The camera frame converter's and the
/// probe kernels' Direct3D 11 kernels and the Direct3D 12 surface compositor's blit compile at build and are only
/// created at run time. A pipeline compiles through DXC only where its source row has no package in the build's store,
/// which no shipped world's row lacks; the <c>no-device-compile</c> canary runs that half with DXC hidden.</summary>
public sealed class NoDeviceShaderCompileLawTests {
    [Fact]
    public void No_assembly_the_World_ships_imports_the_Direct3D_HLSL_compiler() {
        var scanned = new List<string>();
        var imports = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
            path: Path.Combine(
                path1: RepositoryPaths.RequireRoot(),
                path2: "src/Puck.World/bin/Release/net10.0"
            ),
            searchPattern: "Puck.*.dll"
        ).Order(comparer: StringComparer.Ordinal)) {
            using var stream = File.OpenRead(path: path);
            using var pe = new PEReader(peStream: stream);

            if (!pe.HasMetadata) {
                continue;
            }

            var reader = pe.GetMetadataReader();
            var name = Path.GetFileName(path: path);

            scanned.Add(item: name);

            foreach (var handle in reader.MethodDefinitions) {
                var import = reader.GetMethodDefinition(handle: handle).GetImport();

                if (import.Module.IsNil) {
                    continue;
                }

                var module = reader.GetString(handle: reader.GetModuleReference(handle: import.Module).Name);

                if (module.StartsWith(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: "d3dcompiler"
                )) {
                    imports.Add(item: $"{name}: {module}!{reader.GetString(handle: import.Name)}");
                }
            }
        }

        // The assemblies that compiled HLSL on a device are among those read, so the law cannot pass by reading none.
        Assert.True(
            condition: scanned.ToHashSet(comparer: StringComparer.Ordinal).IsSupersetOf(other: ["Puck.DirectX.dll", "Puck.DirectX.Presentation.dll", "Puck.Platform.Windows.dll", "Puck.World.dll"]),
            userMessage: $"read only [{string.Join(separator: ", ", values: scanned)}]"
        );
        Assert.Empty(collection: imports);
    }
}
