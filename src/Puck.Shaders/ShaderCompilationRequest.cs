using System.Collections.ObjectModel;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>Owns a snapshot of source stages, channel bindings, descriptor metadata, and parameter defaults for one asynchronous compilation.</summary>
public sealed record ShaderCompilationRequest
{
    public ShaderCompilationRequest(
        string name,
        IReadOnlyList<ShaderStageSource> stages,
        IReadOnlyDictionary<string, uint>? channels = null,
        GpuPixelFormat outputFormat = GpuPixelFormat.R8G8B8A8Unorm,
        IReadOnlyDictionary<string, ShaderConfigField>? config = null,
        IReadOnlyList<ShaderDescriptorBinding>? descriptorBindings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stages);
        if (stages.Count == 0) { throw new ArgumentException("At least one stage is required.", nameof(stages)); }
        var uniqueStages = new HashSet<ShaderStage>();
        foreach (var stage in stages) {
            if (stage is null || !Enum.IsDefined(stage.Stage) || !Enum.IsDefined(stage.Language) ||
                string.IsNullOrWhiteSpace(stage.Path) || string.IsNullOrWhiteSpace(stage.EntryPoint) || stage.Source is null) {
                throw new ArgumentException("Every stage requires a supported stage/language, source path, source text, and entry point.", nameof(stages));
            }
            if (!uniqueStages.Add(stage.Stage)) {
                throw new ArgumentException($"Stage '{stage.Stage}' is declared more than once.", nameof(stages));
            }
        }
        ShaderConfigBinding.ValidateSchema(config, name);
        Name = name;
        Stages = new ReadOnlyCollection<ShaderStageSource>(stages.ToArray());
        Channels = new ReadOnlyDictionary<string, uint>(new Dictionary<string, uint>(channels ?? new Dictionary<string, uint>(), StringComparer.Ordinal));
        OutputFormat = outputFormat;
        Config = new ReadOnlyDictionary<string, ShaderConfigField>(
            (config ?? new Dictionary<string, ShaderConfigField>()).ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value with { Default = pair.Value.Default?.Clone() },
                StringComparer.Ordinal));

        if (descriptorBindings is null)
        {
            DescriptorBindings = Array.Empty<ShaderDescriptorBinding>();
        }
        else
        {
            var bindings = new HashSet<uint>();
            foreach (var binding in descriptorBindings)
            {
                if (binding is null || !Enum.IsDefined(binding.Kind) || binding.Count == 0 || !bindings.Add(binding.VulkanBinding))
                {
                    throw new ArgumentException("Descriptor metadata requires unique bindings, defined kinds, and non-zero counts.", nameof(descriptorBindings));
                }
            }
            DescriptorBindings = new ReadOnlyCollection<ShaderDescriptorBinding>(descriptorBindings.ToArray());
        }
    }

    public ShaderCompilationRequest(string name, IReadOnlyList<ShaderStageSource> stages, IReadOnlyList<ShaderChannelBinding> channels)
        : this(name, stages, channels.ToDictionary(static channel => channel.Name, static channel => channel.Binding, StringComparer.Ordinal)) { }

    public string Name { get; }
    public IReadOnlyList<ShaderStageSource> Stages { get; }
    public IReadOnlyDictionary<string, uint> Channels { get; }
    public GpuPixelFormat OutputFormat { get; }
    public IReadOnlyDictionary<string, ShaderConfigField> Config { get; }
    /// <summary>Gets descriptors in the exact order used to build the runtime binding list.</summary>
    public IReadOnlyList<ShaderDescriptorBinding> DescriptorBindings { get; }

    public static ShaderCompilationRequest Compute(
        string name,
        string sourcePath,
        string sourceText,
        ShaderSourceLanguage language = ShaderSourceLanguage.ShadertoyGlsl,
        string entryPoint = "mainImage",
        IReadOnlyDictionary<string, uint>? channels = null,
        GpuPixelFormat outputFormat = GpuPixelFormat.R8G8B8A8Unorm,
        IReadOnlyDictionary<string, ShaderConfigField>? config = null,
        IReadOnlyList<ShaderDescriptorBinding>? descriptorBindings = null) =>
        new(name, [new ShaderStageSource(ShaderStage.Compute, sourcePath, sourceText, language, entryPoint)], channels, outputFormat, config, descriptorBindings);
}
