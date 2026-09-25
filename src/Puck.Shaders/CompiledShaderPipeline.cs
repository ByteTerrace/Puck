using System.Collections.ObjectModel;

namespace Puck.Shaders;

/// <summary>An immutable execution candidate: a planned graph of shader passes and one compiled shader result per pass
/// name. Every planned pass has its <see cref="ShaderPipelinePlannedPass.Declaration"/>, so a consumer of a candidate
/// reads each pass's declaration without checking its kind.</summary>
public sealed class CompiledShaderPipeline {
    /// <summary>Initializes a pipeline candidate. The dictionaries are copied so a background compiler can publish
    /// the candidate without sharing mutable build state with the render thread.</summary>
    /// <param name="plan">The plan of the graph's shader passes.</param>
    /// <param name="shaders">The compiled shader results, keyed by planned pass name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> or <paramref name="shaders"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan holds a package's pass, which compiles no shader, or a planned pass
    /// has no compiled shader result.</exception>
    public CompiledShaderPipeline(ShaderPipelinePlan plan, IReadOnlyDictionary<string, CompiledShader> shaders) {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(shaders);
        if (plan.Passes.Any(predicate: static pass => (pass.Declaration is null))) {
            throw new ArgumentException(
                message: "A pipeline candidate runs shader passes alone; a package's pass compiles no shader.",
                paramName: nameof(plan)
            );
        }
        Plan = plan;
        Shaders = new ReadOnlyDictionary<string, CompiledShader>(dictionary: new Dictionary<string, CompiledShader>(
            collection: shaders,
            comparer: StringComparer.Ordinal
        ));
        if (plan.Passes.Any(predicate: pass => !Shaders.ContainsKey(key: pass.Name))) {
            throw new ArgumentException(
                message: "Every planned pass needs a compiled shader result.",
                paramName: nameof(shaders)
            );
        }
    }

    /// <summary>Gets whether every shader result succeeded for both backends.</summary>
    public bool IsSuccess => Shaders.Values.All(predicate: static shader => shader.IsSuccess);
    /// <summary>Gets the immutable execution plan.</summary>
    public ShaderPipelinePlan Plan { get; }
    /// <summary>Gets compiled shader results keyed by planned pass name.</summary>
    public IReadOnlyDictionary<string, CompiledShader> Shaders { get; }
}
