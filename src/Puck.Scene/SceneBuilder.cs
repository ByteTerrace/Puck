using Puck.SdfVm;

namespace Puck.Scene;

/// <summary>
/// Turns a validated <see cref="SceneDocument"/> into an <see cref="SdfProgram"/> by driving the public
/// <see cref="SdfProgramBuilder"/> verbs — materials in array order (index = id), then each object as
/// <c>ResetPoint -> ops -> terminal shape</c>. Because it reproduces the exact verb sequence a hand-authored scene
/// uses, the resulting <see cref="SdfProgram.Words"/> are bit-identical; the document NEVER carries the packed words
/// (those are a derived GPU cache rebuilt here on every load).
/// </summary>
public static class SceneBuilder {
    /// <summary>Builds the GPU program for a scene.</summary>
    /// <param name="scene">The validated scene section.</param>
    /// <returns>The compiled program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> is <see langword="null"/>.</exception>
    public static SdfProgram Build(SceneDocument scene) {
        ArgumentNullException.ThrowIfNull(argument: scene);

        var builder = new SdfProgramBuilder();

        foreach (var material in (scene.Materials ?? [])) {
            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: JsonVector.ToVector3(components: material.Albedo)));
        }

        foreach (var sceneObject in (scene.Objects ?? [])) {
            sceneObject.Emit(builder: builder);
        }

        return builder.Build();
    }
}
