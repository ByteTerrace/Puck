using System.Collections.ObjectModel;

namespace Puck.Shaders;

/// <summary>An immutable execution candidate: a planned graph and one compiled shader result per pass name.</summary>
public sealed class CompiledShaderPipeline {
    /// <summary>Initializes a pipeline candidate. The dictionaries are copied so a background compiler can publish
    /// the candidate without sharing mutable build state with the render thread.</summary>
    public CompiledShaderPipeline(ShaderPipelinePlan plan, IReadOnlyDictionary<string, CompiledShader> shaders) {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(shaders);
        Plan = plan;
        Shaders = new ReadOnlyDictionary<string, CompiledShader>(new Dictionary<string, CompiledShader>(shaders, StringComparer.Ordinal));
        if (plan.Passes.Any(pass => !Shaders.ContainsKey(pass.Name))) {
            throw new ArgumentException("Every planned pass needs a compiled shader result.", nameof(shaders));
        }
    }

    /// <summary>Gets the immutable execution plan.</summary>
    public ShaderPipelinePlan Plan { get; }
    /// <summary>Gets compiled shader results keyed by planned pass name.</summary>
    public IReadOnlyDictionary<string, CompiledShader> Shaders { get; }
    /// <summary>Gets whether every shader result succeeded for both backends.</summary>
    public bool IsSuccess => Shaders.Values.All(static shader => shader.IsSuccess);
}
