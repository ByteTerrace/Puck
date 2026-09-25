using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>The two-group binding spike's package: an interface for each of two existing passes, the generated include
/// beside a variant of each pass, and a DXC build of both for both backends. The variants live under
/// <c>Assets/Interfaces/&lt;interface&gt;/</c>; the passes they copy keep their own sources and bindings.</summary>
internal static class ShaderInterfaceSpike {
    /// <summary>One variant pass: its interface, its source file, and the stage it compiles as.</summary>
    internal sealed record Pass(ShaderInterface Interface, string SourceFileName, string Profile, string EntryPoint);
    /// <summary>One DXC build of a pass: the SPIR-V module and the DXIL container.</summary>
    internal sealed record Build(byte[] Spirv, byte[] Dxil);

    internal static ShaderInterface FilmGrain { get; } = new(
        members: [
            ShaderInterfaceMember.Value(
                group: ShaderInterfaceGroup.Frame,
                name: "tick",
                type: ShaderValueType.Uint
            ),
            ShaderInterfaceMember.Value(
                group: ShaderInterfaceGroup.Frame,
                name: "extent",
                type: ShaderValueType.Uint2
            ),
            ShaderInterfaceMember.Value(
                group: ShaderInterfaceGroup.Pass,
                name: "intensity",
                type: ShaderValueType.Float
            ),
            ShaderInterfaceMember.Value(
                group: ShaderInterfaceGroup.Pass,
                name: "tint",
                type: ShaderValueType.Float3
            ),
            ShaderInterfaceMember.Value(
                group: ShaderInterfaceGroup.Pass,
                name: "cellSize",
                type: ShaderValueType.Float
            ),
            ShaderInterfaceMember.Value(
                group: ShaderInterfaceGroup.Pass,
                name: "seed",
                type: ShaderValueType.Uint
            ),
            ShaderInterfaceMember.Value(
                group: ShaderInterfaceGroup.Pass,
                name: "flickerTicks",
                type: ShaderValueType.Uint
            ),
            ShaderInterfaceMember.SampledImage(
                group: ShaderInterfaceGroup.Pass,
                name: "source",
                type: ShaderValueType.Float4
            ),
            ShaderInterfaceMember.Sampler(
                group: ShaderInterfaceGroup.Pass,
                name: "sourceSampler"
            ),
        ],
        name: "film-grain"
    );
    internal static ShaderInterface Pixelate { get; } = new(
        members: [
            ShaderInterfaceMember.Value(
                group: ShaderInterfaceGroup.Frame,
                name: "tick",
                type: ShaderValueType.Uint
            ),
            ShaderInterfaceMember.Value(
                group: ShaderInterfaceGroup.Frame,
                name: "extent",
                type: ShaderValueType.Uint2
            ),
            ShaderInterfaceMember.Value(
                group: ShaderInterfaceGroup.Pass,
                name: "cellSize",
                type: ShaderValueType.Uint
            ),
            ShaderInterfaceMember.Array(
                group: ShaderInterfaceGroup.Pass,
                length: 3,
                name: "channelLevels",
                type: ShaderValueType.Uint
            ),
            ShaderInterfaceMember.StorageImage(
                format: GpuPixelFormat.R8G8B8A8Unorm,
                group: ShaderInterfaceGroup.Pass,
                name: "output",
                type: ShaderValueType.Float4
            ),
            ShaderInterfaceMember.StorageImage(
                format: GpuPixelFormat.R8G8B8A8Unorm,
                group: ShaderInterfaceGroup.Pass,
                name: "source",
                type: ShaderValueType.Float4
            ),
        ],
        name: "pixelate"
    );
    internal static IReadOnlyList<Pass> Passes { get; } = [
        new Pass(
            EntryPoint: "PSMain",
            Interface: FilmGrain,
            Profile: "ps_6_6",
            SourceFileName: "film-grain.frag.hlsl"
        ),
        new Pass(
            EntryPoint: "CSMain",
            Interface: Pixelate,
            Profile: "cs_6_6",
            SourceFileName: "pixelate.comp.hlsl"
        ),
    ];

    /// <summary>Gets the <c>dxc</c> the default toolchain resolves on the search path, or <see langword="null"/>.</summary>
    internal static string? Dxc => new ShaderToolchain().Locate(name: "dxc");

    /// <summary>Generates the pass's include into a fresh directory beside a copy of its variant source, then compiles it
    /// to SPIR-V and DXIL with the flags the shared shader recipe (<c>build/Shaders.targets</c>) passes.</summary>
    internal static Task<Build> CompileAsync(Pass pass, string generatedInclude, CancellationToken cancellationToken) =>
        CompileInAsync(
            cancellationToken: cancellationToken,
            entryPoint: pass.EntryPoint,
            profile: pass.Profile,
            sourceFileName: pass.SourceFileName,
            stage: async (root, token) => {
                File.Copy(
                    destFileName: Path.Combine(
                        path1: root,
                        path2: pass.SourceFileName
                    ),
                    sourceFileName: Path.Combine(paths: [AppContext.BaseDirectory, "Assets", "Interfaces", pass.Interface.Name, pass.SourceFileName])
                );
                await File.WriteAllTextAsync(
                    cancellationToken: token,
                    contents: generatedInclude,
                    path: Path.Combine(
                        path1: root,
                        path2: ShaderInterfaceHlsl.FileName(shaderInterface: pass.Interface)
                    )
                );
            }
        );
    /// <summary>Compiles one source written out as text to SPIR-V and DXIL with the same flags as
    /// <see cref="CompileAsync(Pass, string, CancellationToken)"/>.</summary>
    internal static Task<Build> CompileSourceAsync(string source, string profile, string entryPoint, CancellationToken cancellationToken) =>
        CompileInAsync(
            cancellationToken: cancellationToken,
            entryPoint: entryPoint,
            profile: profile,
            sourceFileName: "source.hlsl",
            stage: (root, token) => File.WriteAllTextAsync(
                cancellationToken: token,
                contents: source,
                path: Path.Combine(
                    path1: root,
                    path2: "source.hlsl"
                )
            )
        );

    // Stages the sources into a fresh directory, then compiles the named one with the flags the shared shader recipe
    // (build/Shaders.targets) passes.
    private static async Task<Build> CompileInAsync(string sourceFileName, string profile, string entryPoint, Func<string, CancellationToken, Task> stage, CancellationToken cancellationToken) {
        var dxc = (Dxc ?? throw new ShaderToolMissingException(
            directory: null,
            tool: "dxc"
        ));
        var root = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-shader-interface-" + Guid.NewGuid().ToString(format: "N"))
        );

        Directory.CreateDirectory(path: root);

        try {
            var source = Path.Combine(
                path1: root,
                path2: sourceFileName
            );

            await stage(
                arg1: root,
                arg2: cancellationToken
            );

            var include = ("-I" + Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: "Assets",
                path3: "Shaders",
                path4: "Sdf"
            ));
            var spirvPath = Path.Combine(
                path1: root,
                path2: "out.spv"
            );
            var dxilPath = Path.Combine(
                path1: root,
                path2: "out.dxil"
            );

            await RunAsync(
                arguments: ["-spirv", "-fspv-target-env=vulkan1.3", "-fspv-entrypoint-name=main", "-enable-16bit-types", "-O3", "-T", profile, "-E", entryPoint, include, "-Fo", spirvPath, source],
                cancellationToken: cancellationToken,
                dxc: dxc
            );
            await RunAsync(
                arguments: ["-Wno-ignored-attributes", "-enable-16bit-types", "-O3", "-T", profile, "-E", entryPoint, include, "-Fo", dxilPath, source],
                cancellationToken: cancellationToken,
                dxc: dxc
            );

            return new Build(
                Dxil: await File.ReadAllBytesAsync(
                    cancellationToken: cancellationToken,
                    path: dxilPath
                ),
                Spirv: await File.ReadAllBytesAsync(
                    cancellationToken: cancellationToken,
                    path: spirvPath
                )
            );
        } finally {
            Directory.Delete(
                path: root,
                recursive: true
            );
        }
    }
    private static async Task RunAsync(string dxc, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
        var result = await ChildProcess.RunAsync(
            arguments: arguments,
            cancellationToken: cancellationToken,
            fileName: dxc
        );

        Assert.True(
            condition: (result.ExitCode == 0),
            userMessage: $"dxc {string.Join(separator: ' ', values: arguments)} exited {result.ExitCode}:\n{result.Stdout}\n{result.Stderr}"
        );
    }
}
