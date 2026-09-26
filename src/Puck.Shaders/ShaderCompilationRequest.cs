using System.Collections.ObjectModel;
using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

/// <summary>Owns a snapshot of the HLSL source stages one asynchronous compilation builds.</summary>
public sealed record ShaderCompilationRequest {
    /// <summary>Initializes a request.</summary>
    /// <param name="name">The compile's name, which names the <see cref="CompiledShader"/> it produces.</param>
    /// <param name="stages">The stages, at most one of each <see cref="ShaderStage"/>.</param>
    /// <param name="generatedIncludes">The text of each include the caller generates, by full path, which the compile
    /// reads instead of a file at that path; <see langword="null"/> for none.</param>
    /// <param name="tier">The quality tier every stage compiles for (<see cref="ShaderCompiler.StepsOf"/>), or
    /// <see langword="null"/> for the variant no tier names.</param>
    /// <exception cref="ArgumentException">There is no stage, a stage is incomplete or undefined, a stage is declared
    /// twice, or <paramref name="tier"/> is not a declared tier.</exception>
    public ShaderCompilationRequest(string name, IReadOnlyList<ShaderStageSource> stages, IReadOnlyDictionary<string, string>? generatedIncludes = null, QualityTier? tier = null) {
        if (
            (tier is { } named) &&
            !Enum.IsDefined(value: named)
        ) {
            throw new ArgumentException(
                message: $"Quality tier {named} is not a declared tier.",
                paramName: nameof(tier)
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stages);
        if (stages.Count == 0) {
            throw new ArgumentException(
            message: "At least one stage is required.",
            paramName: nameof(stages)
        );
        }
        var uniqueStages = new HashSet<ShaderStage>();

        foreach (var stage in stages) {
            if (
                (stage is null) ||
                !Enum.IsDefined(value: stage.Stage) ||
                string.IsNullOrWhiteSpace(value: stage.Path) ||
                string.IsNullOrWhiteSpace(value: stage.EntryPoint) ||
                (stage.Source is null)
            ) {
                throw new ArgumentException(
                    message: "Every stage requires a supported stage, source path, source text, and entry point.",
                    paramName: nameof(stages)
                );
            }
            if (!uniqueStages.Add(item: stage.Stage)) {
                throw new ArgumentException(
                    message: $"Stage '{stage.Stage}' is declared more than once.",
                    paramName: nameof(stages)
                );
            }
        }
        Name = name;
        Tier = tier;
        Stages = new ReadOnlyCollection<ShaderStageSource>(list: stages.ToArray());
        GeneratedIncludes = new ReadOnlyDictionary<string, string>(dictionary: new Dictionary<string, string>(
            collection: (generatedIncludes ?? new Dictionary<string, string>()).Select(selector: static pair => new KeyValuePair<string, string>(
                key: Path.GetFullPath(path: pair.Key),
                value: pair.Value
            )),
            comparer: Puck.Abstractions.PuckPaths.Comparer
        ));
    }

    /// <summary>Gets the text of each generated include, by full path.</summary>
    public IReadOnlyDictionary<string, string> GeneratedIncludes { get; }
    /// <summary>Gets the compile's name.</summary>
    public string Name { get; }
    /// <summary>Gets the stages, in the order they compile.</summary>
    public IReadOnlyList<ShaderStageSource> Stages { get; }
    /// <summary>Gets the quality tier every stage compiles for, or <see langword="null"/> for the variant no tier
    /// names.</summary>
    public QualityTier? Tier { get; }
}
