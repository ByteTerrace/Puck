using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Puck.Shaders;

/// <summary>
/// Compiles HLSL stages with DXC and Shadertoy GLSL stages through the explicit
/// adapter, producing SPIR-V and DXIL candidates for both graphics and compute.
/// </summary>
public sealed partial class ShaderCompiler
{
    private const string CompilerVersion = "puck-shader-compiler-2";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> m_gates = new(StringComparer.Ordinal);
    private readonly string m_cacheDirectory;
    private readonly IShaderProcessRunner m_processRunner;
    private readonly ShaderToolchain m_toolchain;

    public ShaderCompiler(string cacheDirectory, string? toolchainDirectory = null)
        : this(cacheDirectory, new ShaderProcessRunner(), toolchainDirectory) { }

    internal ShaderCompiler(string cacheDirectory, IShaderProcessRunner processRunner, string? toolchainDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(processRunner);
        m_cacheDirectory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(m_cacheDirectory);
        m_processRunner = processRunner;
        m_toolchain = new ShaderToolchain(toolchainDirectory);
    }

    /// <summary>Gets the cache directory used by this compiler.</summary>
    public string CacheDirectory => m_cacheDirectory;
    /// <summary>Gets the resolved toolchain configuration.</summary>
    public ShaderToolchain Toolchain => m_toolchain;

    /// <summary>Compiles one Shadertoy source as a compute stage.</summary>
    public CompiledShader Compile(string name, string sourcePath, string sourceText) =>
        CompileAsync(name, sourcePath, sourceText).GetAwaiter().GetResult();

    public Task<CompiledShader> CompileAsync(string name, string sourcePath, string sourceText, CancellationToken cancellationToken = default) =>
        CompileAsync(ShaderCompilationRequest.Compute(name, sourcePath, sourceText), cancellationToken);

    public CompiledShader Compile(ShaderCompilationRequest descriptor) =>
        CompileAsync(descriptor).GetAwaiter().GetResult();

    /// <summary>Compiles every stage in a descriptor away from the presentation thread.</summary>
    public Task<CompiledShader> CompileAsync(ShaderCompilationRequest descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Task.Run(() => CompileCoreAsync(descriptor, cancellationToken), cancellationToken);
    }

    private async Task<CompiledShader> CompileCoreAsync(ShaderCompilationRequest descriptor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dependencies = CollectDependencies(descriptor);
        var cacheKey = CreateCacheKey(descriptor, dependencies);
        var gate = m_gates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CompileCoreLockedAsync(descriptor, dependencies, cacheKey, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<CompiledShader> CompileCoreLockedAsync(ShaderCompilationRequest descriptor,
        IReadOnlyList<ShaderSourceDependency> dependencies, string cacheKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stem = Path.Combine(m_cacheDirectory, cacheKey);
        var sourceHash = Hash(SerializeDescriptor(descriptor));
        var cachedSpirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var cachedDxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var allCached = true;
        foreach (var stage in descriptor.Stages)
        {
            var spirvPath = BytecodePath(stem, stage.Stage, "spv");
            var dxilPath = BytecodePath(stem, stage.Stage, "dxil");
            if (!File.Exists(spirvPath) || !File.Exists(dxilPath)) { allCached = false; break; }
            cachedSpirv[stage.Stage] = File.ReadAllBytes(spirvPath);
            cachedDxil[stage.Stage] = File.ReadAllBytes(dxilPath);
        }
        if (allCached)
        {
            return new CompiledShader(descriptor.Name, FirstPath(descriptor), sourceHash,
                cachedSpirv, cachedDxil, [], dependencies);
        }

        var diagnostics = new List<ShaderDiagnostic>();
        var spirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var dxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        foreach (var stage in descriptor.Stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var adapted = Prepare(stage, descriptor);
            var sourcePath = Path.Combine(m_cacheDirectory, $"{cacheKey}.{StageName(stage.Stage)}.source");
            await File.WriteAllTextAsync(sourcePath, adapted.Text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var stageStem = $"{stem}.{StageName(stage.Stage)}";
            var spvPath = $"{stageStem}.spv.tmp";
            var dxilPath = $"{stageStem}.dxil.tmp";
            var profile = Profile(stage.Stage);
            var stageDiagnostics = new List<ShaderDiagnostic>();

            if (stage.Language == ShaderSourceLanguage.Hlsl)
            {
                var includeDirectory = Path.GetDirectoryName(Path.GetFullPath(stage.Path));
                var spv = await RunDxcAsync(sourcePath, spvPath, profile, stage.EntryPoint, spirv: true, includeDirectory, cancellationToken).ConfigureAwait(false);
                stageDiagnostics.AddRange(ParseDxcDiagnostics(spv.Stdout + "\n" + spv.Stderr, stage, 0));
                if (spv.ExitCode == 0 && File.Exists(spvPath)) { spirv[stage.Stage] = File.ReadAllBytes(spvPath); }
                else { AddToolFailure(stageDiagnostics, "dxc (SPIR-V)", spv.ExitCode, spv.Stdout + "\n" + spv.Stderr); }

                var dx = await RunDxcAsync(sourcePath, dxilPath, profile, stage.EntryPoint, spirv: false, includeDirectory, cancellationToken).ConfigureAwait(false);
                stageDiagnostics.AddRange(ParseDxcDiagnostics(dx.Stdout + "\n" + dx.Stderr, stage, 0));
                if (dx.ExitCode == 0 && File.Exists(dxilPath)) { dxil[stage.Stage] = File.ReadAllBytes(dxilPath); }
                else { AddToolFailure(stageDiagnostics, "dxc (DXIL)", dx.ExitCode, dx.Stdout + "\n" + dx.Stderr); }
            }
            else
            {
                var glsl = await RunGlslAsync(sourcePath, spvPath, stage, cancellationToken).ConfigureAwait(false);
                stageDiagnostics.AddRange(ParseGlslDiagnostics(glsl.Stdout + "\n" + glsl.Stderr, stage, adapted.PrefixLineCount));
                if (glsl.ExitCode == 0 && File.Exists(spvPath)) { spirv[stage.Stage] = File.ReadAllBytes(spvPath); }
                else { AddToolFailure(stageDiagnostics, "glslang", glsl.ExitCode, glsl.Stdout + "\n" + glsl.Stderr); }

                if (spirv.ContainsKey(stage.Stage))
                {
                    var hlslPath = $"{stageStem}.translated.hlsl";
                    var cross = await RunToolAsync("spirv-cross", null, ["--hlsl", "--shader-model", "66", spvPath, "--output", hlslPath], cancellationToken).ConfigureAwait(false);
                    if (cross.ExitCode != 0 || !File.Exists(hlslPath))
                    {
                        AddToolFailure(stageDiagnostics, "spirv-cross", cross.ExitCode, cross.Stdout + "\n" + cross.Stderr);
                    }
                    else
                    {
                        var dx = await RunDxcAsync(hlslPath, dxilPath, profile, "main", spirv: false, Path.GetDirectoryName(Path.GetFullPath(stage.Path)), cancellationToken).ConfigureAwait(false);
                        stageDiagnostics.AddRange(ParseDxcDiagnostics(dx.Stdout + "\n" + dx.Stderr, stage, 0));
                        if (dx.ExitCode == 0 && File.Exists(dxilPath)) { dxil[stage.Stage] = File.ReadAllBytes(dxilPath); }
                        else { AddToolFailure(stageDiagnostics, "dxc (DXIL)", dx.ExitCode, dx.Stdout + "\n" + dx.Stderr); }
                    }
                }
            }

            diagnostics.AddRange(stageDiagnostics);
            if (stageDiagnostics.Any(static diagnostic => diagnostic.IsError))
            {
                return new CompiledShader(descriptor.Name, FirstPath(descriptor), sourceHash, spirv, dxil, diagnostics, dependencies);
            }
        }

        foreach (var stage in descriptor.Stages) {
            var stageStem = $"{stem}.{StageName(stage.Stage)}";
            File.Move($"{stageStem}.spv.tmp", BytecodePath(stem, stage.Stage, "spv"), true);
            File.Move($"{stageStem}.dxil.tmp", BytecodePath(stem, stage.Stage, "dxil"), true);
        }
        return new CompiledShader(descriptor.Name, FirstPath(descriptor), sourceHash, spirv, dxil, diagnostics, dependencies);
    }

    private async Task<ShaderProcessResult> RunDxcAsync(string source, string output, string profile, string entry,
        bool spirv, string? includeDirectory, CancellationToken cancellationToken)
    {
        var args = new List<string>();
        if (spirv) { args.AddRange(["-spirv", "-fspv-target-env=vulkan1.3"]); }
        if (!string.IsNullOrWhiteSpace(includeDirectory)) { args.AddRange(["-I", includeDirectory]); }
        args.AddRange(["-T", profile, "-E", entry, "-Fo", output, source]);
        return await RunToolAsync("dxc", null, args, cancellationToken).ConfigureAwait(false);
    }

    private Task<ShaderProcessResult> RunGlslAsync(string source, string output, ShaderStageSource stage, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-V", "-S", StageName(stage.Stage), "-e", stage.Language == ShaderSourceLanguage.ShadertoyGlsl ? "main" : stage.EntryPoint, "-o", output };
        var includeDirectory = Path.GetDirectoryName(Path.GetFullPath(stage.Path));
        if (!string.IsNullOrWhiteSpace(includeDirectory)) { args.AddRange(["-I", includeDirectory]); }
        args.Add(source);
        return RunToolAsync("glslangValidator", "glslang", args, cancellationToken);
    }

    private async Task<ShaderProcessResult> RunToolAsync(string name, string? fallback, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var executable = m_toolchain.Directory is null ? name : m_toolchain.Resolve(name, fallback);
        try { return await m_processRunner.RunAsync(executable, args, cancellationToken).ConfigureAwait(false); }
        catch (System.ComponentModel.Win32Exception) when (m_toolchain.Directory is null && fallback is not null)
        {
            try { return await m_processRunner.RunAsync(fallback, args, cancellationToken).ConfigureAwait(false); }
            catch (System.ComponentModel.Win32Exception) { throw new ShaderToolMissingException($"{name} (or {fallback})", null); }
        }
        catch (System.ComponentModel.Win32Exception) { throw new ShaderToolMissingException(name, m_toolchain.Directory); }
    }

    private static ShadertoyShaderAdapter.AdaptedSource Prepare(ShaderStageSource stage, ShaderCompilationRequest descriptor)
    {
        if (stage.Language == ShaderSourceLanguage.ShadertoyGlsl)
        {
            return ShadertoyShaderAdapter.Adapt(stage.Source, descriptor.Channels, stage.GroupSizeX, stage.GroupSizeY, stage.GroupSizeZ, descriptor.OutputFormat, descriptor.Config);
        }
        return new ShadertoyShaderAdapter.AdaptedSource(stage.Source, 0, []);
    }

    private List<ShaderSourceDependency> CollectDependencies(ShaderCompilationRequest descriptor)
    {
        var result = new List<ShaderSourceDependency>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in descriptor.Stages)
        {
            CollectIncludes(stage.Path, stage.Source, visited, result);
        }
        return result;
    }
    private static void CollectIncludes(string path, string source, HashSet<string> visited, List<ShaderSourceDependency> result)
    {
        foreach (Match match in IncludePattern().Matches(source))
        {
            var includePath = match.Groups[1].Value;
            var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory, includePath));
            if (!visited.Add(fullPath) || !File.Exists(fullPath))
            {
                continue;
            }
            var contents = File.ReadAllText(fullPath);
            result.Add(new ShaderSourceDependency(fullPath, Hash(contents)));
            CollectIncludes(fullPath, contents, visited, result);
        }
    }
    private string CreateCacheKey(ShaderCompilationRequest descriptor, IReadOnlyList<ShaderSourceDependency> dependencies)
    {
        var builder = new StringBuilder(CompilerVersion).Append('|').Append(SerializeDescriptor(descriptor)).Append('|').Append(ShadertoyShaderAdapter.Version)
            .Append('|').Append(m_toolchain.Identity);
        foreach (var stage in descriptor.Stages)
        {
            builder.Append('|').Append(stage.Stage).Append('|').Append(Profile(stage.Stage)).Append('|').Append(stage.EntryPoint)
                .Append("|-spirv|-fspv-target-env=vulkan1.3|-T|").Append(Profile(stage.Stage)).Append("|-E|").Append(stage.EntryPoint)
                .Append("|-Fo|").Append(StageName(stage.Stage)).Append(".target|glslangValidator|-V|-S|").Append(StageName(stage.Stage))
                .Append("|-e|").Append(stage.EntryPoint).Append("|spirv-cross|--hlsl|--shader-model|66");
        }
        foreach (var dependency in dependencies.OrderBy(static dependency => dependency.Path, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('|').Append(dependency.Path).Append('|').Append(dependency.ContentHash);
        }
        return Hash(builder.ToString());
    }
    private static string SerializeDescriptor(ShaderCompilationRequest descriptor) =>
        string.Join("\n", descriptor.Stages.Select(stage => $"{stage.Stage}|{stage.Path}|{stage.Language}|{stage.EntryPoint}|{stage.GroupSizeX},{stage.GroupSizeY},{stage.GroupSizeZ}|{stage.Source}")) +
        string.Join("\n", descriptor.Channels.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"channel|{pair.Key}|{pair.Value}")) + $"\nformat|{descriptor.OutputFormat}|config|{string.Join(",", descriptor.Config.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}:{pair.Value.Type}"))}";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string FirstPath(ShaderCompilationRequest descriptor) => descriptor.Stages[0].Path;
    private static string BytecodePath(string stem, ShaderStage stage, string target) => $"{stem}.{StageName(stage)}.{target}";
    private static string StageName(ShaderStage stage) => stage switch { ShaderStage.Vertex => "vert", ShaderStage.Fragment => "frag", _ => "comp" };
    private static string Profile(ShaderStage stage) => stage switch { ShaderStage.Vertex => "vs_6_6", ShaderStage.Fragment => "ps_6_6", _ => "cs_6_6" };
    private static void AddToolFailure(List<ShaderDiagnostic> diagnostics, string tool, int exitCode, string output)
    {
        var detail = string.Join(" ", output.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries).Select(static line => line.Trim()).Where(static line => line.Length != 0).Take(4));
        diagnostics.Add(new ShaderDiagnostic(0, 0, string.IsNullOrEmpty(detail) ? $"{tool} exited with code {exitCode}." : $"{tool} exited with code {exitCode}: {detail}", true));
    }
    private static List<ShaderDiagnostic> ParseGlslDiagnostics(string text, ShaderStageSource stage, int prefix)
    {
        var result = new List<ShaderDiagnostic>();
        foreach (var line in text.Split('\n'))
        {
            var match = GlslDiagnosticPattern().Match(line.TrimEnd('\r'));
            if (match.Success && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber))
            {
                result.Add(new ShaderDiagnostic(Math.Max(0, lineNumber - prefix), 0, match.Groups[3].Value.Trim(), match.Groups[1].Value.Equals("ERROR", StringComparison.OrdinalIgnoreCase), stage.Stage, stage.Path));
            }
        }
        return result;
    }
    private static List<ShaderDiagnostic> ParseDxcDiagnostics(string text, ShaderStageSource stage, int prefix)
    {
        var result = new List<ShaderDiagnostic>();
        foreach (var line in text.Split('\n'))
        {
            var match = DxcDiagnosticPattern().Match(line.TrimEnd('\r'));
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber))
            {
                result.Add(new ShaderDiagnostic(Math.Max(0, lineNumber - prefix), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), match.Groups[4].Value.Trim(), match.Groups[3].Value == "error", stage.Stage, stage.Path));
            }
        }
        return result;
    }

    [GeneratedRegex(@"^(ERROR|WARNING):\s*.*:(\d+):\s*(.*)$")]
    private static partial Regex GlslDiagnosticPattern();
    [GeneratedRegex(@"^.*:(\d+):(\d+):\s*(error|warning):\s*(.*)$")]
    private static partial Regex DxcDiagnosticPattern();
    [GeneratedRegex(@"^\s*#include\s*[<""]([^>""]+)[>""]")]
    private static partial Regex IncludePattern();
}
