using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Puck.Shaders.Study;

/// <summary>
/// Compiles one Shadertoy-dialect study source to both backend bytecodes as the compute kernel
/// <see cref="StudyPrelude"/> wraps it into: <c>glslang</c>/<c>glslangValidator</c> (GLSL to SPIR-V),
/// <c>spirv-cross</c> (SPIR-V to HLSL), then <c>dxc</c> (HLSL to DXIL, <c>cs_6_6</c>). <see cref="Compile"/>
/// never throws for a mistake in the author's own source — it returns a <see cref="StudyProgram"/> with empty
/// bytecode and error diagnostics — and throws <see cref="StudyToolMissingException"/> only when a tool itself
/// cannot be found.
/// </summary>
public sealed partial class StudyShaderCompiler {
    private readonly string m_cacheDirectory;
    private readonly IStudyProcessRunner m_processRunner;
    private readonly StudyToolchain m_toolchain;

    public StudyShaderCompiler(string cacheDirectory, string? toolchainDirectory = null)
        : this(cacheDirectory: cacheDirectory, processRunner: new StudyProcessRunner(), toolchainDirectory: toolchainDirectory) {
    }

    internal StudyShaderCompiler(string cacheDirectory, IStudyProcessRunner processRunner, string? toolchainDirectory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: cacheDirectory);
        ArgumentNullException.ThrowIfNull(argument: processRunner);

        m_cacheDirectory = Path.GetFullPath(path: cacheDirectory);
        m_processRunner = processRunner;
        m_toolchain = new StudyToolchain(directory: toolchainDirectory);

        Directory.CreateDirectory(path: m_cacheDirectory);
    }

    /// <summary>Compiles <paramref name="sourceText"/> — the author's own file content, unwrapped — naming the
    /// result <paramref name="name"/> and carrying <paramref name="sourcePath"/> through for diagnostics and
    /// caller bookkeeping. A prior successful compile of byte-identical <paramref name="sourceText"/> is served
    /// from <see cref="StudyProgram.SourceHash"/>'s cache entry without invoking any tool.</summary>
    public StudyProgram Compile(string name, string sourcePath, string sourceText) {
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(source: Encoding.UTF8.GetBytes(s: sourceText)));

        if (FindChannelReference(sourceText: sourceText) is { } channelDiagnostic) {
            return Failed(diagnostics: [channelDiagnostic], name: name, sourceHash: sourceHash, sourcePath: sourcePath);
        }

        // Keyed by the prelude-wrapped source, not sourceHash alone, so an edit to StudyPrelude itself (a new field,
        // a fixed bug in the flip) invalidates every cache entry automatically rather than serving stale bytecode
        // compiled against an earlier prelude.
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(source: Encoding.UTF8.GetBytes(s: (StudyPrelude.Text + StudyPrelude.Postlude + sourceText))));
        var stem = Path.Combine(path1: m_cacheDirectory, path2: cacheKey);
        var cachedSpirvPath = $"{stem}.comp.spv";
        var cachedDxilPath = $"{stem}.comp.dxil";

        if (File.Exists(path: cachedSpirvPath) && File.Exists(path: cachedDxilPath)) {
            return new StudyProgram(
                Diagnostics: [],
                Dxil: File.ReadAllBytes(path: cachedDxilPath),
                Spirv: File.ReadAllBytes(path: cachedSpirvPath),
                Name: name,
                SourceHash: sourceHash,
                SourcePath: sourcePath
            );
        }

        var fullSource = string.Concat(str0: StudyPrelude.Text, str1: sourceText, str2: StudyPrelude.Postlude);
        var glslPath = $"{stem}.comp";

        File.WriteAllText(contents: fullSource, path: glslPath);

        var spirvPath = $"{stem}.compiled.spv";
        var glslang = RunTool(arguments: ["-V", "-S", "comp", "-o", spirvPath, glslPath], fallbackName: "glslangValidator", name: "glslang");
        var diagnostics = ParseGlslangDiagnostics(text: (glslang.Stdout + glslang.Stderr));

        if ((glslang.ExitCode != 0) || !File.Exists(path: spirvPath)) {
            if (!diagnostics.Any(predicate: static d => d.IsError)) {
                diagnostics.Add(item: new StudyDiagnostic(IsError: true, Line: 0, Message: $"glslang exited with code {glslang.ExitCode}."));
            }

            return Failed(diagnostics: diagnostics, name: name, sourceHash: sourceHash, sourcePath: sourcePath);
        }

        var hlslPath = $"{stem}.hlsl";
        var spirvCross = RunTool(arguments: ["--hlsl", "--shader-model", "66", spirvPath, "--output", hlslPath], fallbackName: null, name: "spirv-cross");

        if ((spirvCross.ExitCode != 0) || !File.Exists(path: hlslPath)) {
            diagnostics.Add(item: new StudyDiagnostic(IsError: true, Line: 0, Message: $"spirv-cross exited with code {spirvCross.ExitCode}. {spirvCross.Stderr}".TrimEnd()));

            return Failed(diagnostics: diagnostics, name: name, sourceHash: sourceHash, sourcePath: sourcePath);
        }

        var dxilPath = $"{stem}.compiled.dxil";
        var dxc = RunTool(arguments: ["-T", "cs_6_6", "-E", "main", "-Fo", dxilPath, hlslPath], fallbackName: null, name: "dxc");

        diagnostics.AddRange(collection: ParseDxcDiagnostics(text: (dxc.Stdout + dxc.Stderr)));

        if ((dxc.ExitCode != 0) || !File.Exists(path: dxilPath) || diagnostics.Any(predicate: static d => d.IsError)) {
            if (!diagnostics.Any(predicate: static d => d.IsError)) {
                diagnostics.Add(item: new StudyDiagnostic(IsError: true, Line: 0, Message: $"dxc exited with code {dxc.ExitCode}."));
            }

            return Failed(diagnostics: diagnostics, name: name, sourceHash: sourceHash, sourcePath: sourcePath);
        }

        // Only a fully successful pipeline is cached — a cache hit means "known good", so re-checking tool
        // availability or diagnostics on the fast path never has to happen.
        File.Copy(destFileName: cachedSpirvPath, overwrite: true, sourceFileName: spirvPath);
        File.Copy(destFileName: cachedDxilPath, overwrite: true, sourceFileName: dxilPath);

        return new StudyProgram(
            Diagnostics: diagnostics,
            Dxil: File.ReadAllBytes(path: cachedDxilPath),
            Spirv: File.ReadAllBytes(path: cachedSpirvPath),
            Name: name,
            SourceHash: sourceHash,
            SourcePath: sourcePath
        );
    }

    private static StudyProgram Failed(IReadOnlyList<StudyDiagnostic> diagnostics, string name, string sourceHash, string sourcePath) =>
        new(Diagnostics: diagnostics, Dxil: ReadOnlyMemory<byte>.Empty, Spirv: ReadOnlyMemory<byte>.Empty, Name: name, SourceHash: sourceHash, SourcePath: sourcePath);
    // iChannel0..N have no bound source in v1 — refused by name before any tool runs, on the line the author wrote it.
    private static StudyDiagnostic? FindChannelReference(string sourceText) {
        var lines = sourceText.Split(separator: '\n');

        for (var index = 0; (index < lines.Length); index++) {
            var match = ChannelReferencePattern().Match(input: lines[index]);

            if (match.Success) {
                return new StudyDiagnostic(IsError: true, Line: (index + 1), Message: $"'{match.Value}' is not supported: studies have no bound textures in v1.");
            }
        }

        return null;
    }
    // glslang emits "ERROR: <file>:<line>: <message>" per location, usually followed by a cascading
    // "compilation terminated" companion at the same line — collapsed here to the first, more specific message.
    private static List<StudyDiagnostic> ParseGlslangDiagnostics(string text) {
        var diagnostics = new List<StudyDiagnostic>();
        var seenLines = new HashSet<int>();

        foreach (var rawLine in text.Split(separator: '\n')) {
            var match = GlslangDiagnosticPattern().Match(input: rawLine.TrimEnd(trimChar: '\r'));

            if (!match.Success) { continue; }

            var originalLine = (int.Parse(s: match.Groups[2].Value, provider: CultureInfo.InvariantCulture) - StudyPrelude.PreludeLineCount);

            if (!seenLines.Add(item: originalLine)) { continue; }

            diagnostics.Add(item: new StudyDiagnostic(IsError: (match.Groups[1].Value == "ERROR"), Line: originalLine, Message: match.Groups[3].Value.Trim()));
        }

        return diagnostics;
    }
    // dxc emits "<file>:<line>:<col>: error|warning: <message>"; the generated HLSL carries no #line mapping back
    // to the GLSL source, so a dxc-stage diagnostic (spirv-cross output rejected by dxc) reports HLSL line numbers
    // verbatim rather than the author's — a case the generated HLSL is not expected to reach in practice, since
    // glslang already rejected anything spirv-cross and dxc would fail on.
    private static List<StudyDiagnostic> ParseDxcDiagnostics(string text) {
        var diagnostics = new List<StudyDiagnostic>();

        foreach (var rawLine in text.Split(separator: '\n')) {
            var match = DxcDiagnosticPattern().Match(input: rawLine.TrimEnd(trimChar: '\r'));

            if (!match.Success) { continue; }

            diagnostics.Add(item: new StudyDiagnostic(IsError: (match.Groups[3].Value == "error"), Line: int.Parse(s: match.Groups[1].Value, provider: CultureInfo.InvariantCulture), Message: match.Groups[4].Value.Trim()));
        }

        return diagnostics;
    }
    // With a toolchain directory, Resolve already tried both candidates against it and throws before any process
    // spawns. Without one, each bare name is tried in turn against the OS's own search path — the only way to
    // detect a missing bare-name tool, short of reading PATH ourselves.
    private StudyProcessResult RunTool(IReadOnlyList<string> arguments, string? fallbackName, string name) {
        if (m_toolchain.Directory is not null) {
            return m_processRunner.Run(arguments: arguments, fileName: m_toolchain.Resolve(fallbackName: fallbackName, name: name));
        }

        try {
            return m_processRunner.Run(arguments: arguments, fileName: name);
        } catch (Win32Exception) when (fallbackName is null) {
            throw new StudyToolMissingException(directory: null, tool: name);
        } catch (Win32Exception) {
            try {
                return m_processRunner.Run(arguments: arguments, fileName: fallbackName!);
            } catch (Win32Exception) {
                throw new StudyToolMissingException(directory: null, tool: $"{name} (or {fallbackName})");
            }
        }
    }

    [GeneratedRegex(pattern: @"\biChannel[0-9]\b")]
    private static partial Regex ChannelReferencePattern();
    [GeneratedRegex(pattern: @"^(ERROR|WARNING):\s*.*:(\d+):\s*(.*)$")]
    private static partial Regex GlslangDiagnosticPattern();
    [GeneratedRegex(pattern: @"^.*:(\d+):(\d+):\s*(error|warning):\s*(.*)$")]
    private static partial Regex DxcDiagnosticPattern();
}
