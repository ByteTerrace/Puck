using System.Collections.ObjectModel;

namespace Puck.Shaders;

/// <summary>An immutable execution candidate: a planned graph and one compiled shader result per shader pass name. A
/// package pass (<see cref="ShaderPipelinePassKind.Package"/>) compiles nothing and has no result: its package's
/// recorder records it (<see cref="RenderGraphPackageRecorders"/>).</summary>
public sealed class CompiledShaderPipeline {
    /// <summary>Initializes a pipeline candidate. The dictionaries are copied so a background compiler can publish
    /// the candidate without sharing mutable build state with the render thread.</summary>
    public CompiledShaderPipeline(ShaderPipelinePlan plan, IReadOnlyDictionary<string, CompiledShader> shaders) {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(shaders);
        Plan = plan;
        Shaders = new ReadOnlyDictionary<string, CompiledShader>(dictionary: new Dictionary<string, CompiledShader>(
            collection: shaders,
            comparer: StringComparer.Ordinal
        ));
        if (plan.Passes.Any(predicate: pass => ((pass.Kind != ShaderPipelinePassKind.Package) && !Shaders.ContainsKey(key: pass.Name)))) {
            throw new ArgumentException(
                message: "Every planned shader pass needs a compiled shader result.",
                paramName: nameof(shaders)
            );
        }
    }

    /// <summary>Gets whether every shader result succeeded for both backends.</summary>
    public bool IsSuccess => Shaders.Values.All(predicate: static shader => shader.IsSuccess);
    /// <summary>Gets the immutable execution plan.</summary>
    public ShaderPipelinePlan Plan { get; }
    /// <summary>Gets compiled shader results keyed by planned shader pass name.</summary>
    public IReadOnlyDictionary<string, CompiledShader> Shaders { get; }
}
