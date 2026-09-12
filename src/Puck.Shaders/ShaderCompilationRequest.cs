using System.Collections.ObjectModel;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>Describes all source stages and external channel bindings for one compilation.</summary>
public sealed record ShaderCompilationRequest
{
    public ShaderCompilationRequest(string name, IReadOnlyList<ShaderStageSource> stages, IReadOnlyDictionary<string, uint>? channels = null,
        GpuPixelFormat outputFormat = GpuPixelFormat.R8G8B8A8Unorm,
        IReadOnlyDictionary<string, ShaderConfigField>? config = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stages);
        if (stages.Count == 0) { throw new ArgumentException("At least one stage is required.", nameof(stages)); }
        Name = name;
        Stages = new ReadOnlyCollection<ShaderStageSource>(stages.ToArray());
        Channels = new ReadOnlyDictionary<string, uint>(new Dictionary<string, uint>(channels ?? new Dictionary<string, uint>(), StringComparer.Ordinal));
        OutputFormat = outputFormat;
        Config = new ReadOnlyDictionary<string, ShaderConfigField>(new Dictionary<string, ShaderConfigField>(config ?? new Dictionary<string, ShaderConfigField>(), StringComparer.Ordinal));
    }

    public ShaderCompilationRequest(string name, IReadOnlyList<ShaderStageSource> stages, IReadOnlyList<ShaderChannelBinding> channels)
        : this(name, stages, channels.ToDictionary(static channel => channel.Name, static channel => channel.Binding, StringComparer.Ordinal)) { }

    public string Name { get; }
    public IReadOnlyList<ShaderStageSource> Stages { get; }
    public IReadOnlyDictionary<string, uint> Channels { get; }
    public GpuPixelFormat OutputFormat { get; }
    public IReadOnlyDictionary<string, ShaderConfigField> Config { get; }

    public static ShaderCompilationRequest Compute(string name, string sourcePath, string sourceText,
        ShaderSourceLanguage language = ShaderSourceLanguage.ShadertoyGlsl, string entryPoint = "mainImage",
        IReadOnlyDictionary<string, uint>? channels = null, GpuPixelFormat outputFormat = GpuPixelFormat.R8G8B8A8Unorm,
        IReadOnlyDictionary<string, ShaderConfigField>? config = null) =>
        new(name, [new ShaderStageSource(ShaderStage.Compute, sourcePath, sourceText, language, entryPoint)], channels, outputFormat, config);
}
