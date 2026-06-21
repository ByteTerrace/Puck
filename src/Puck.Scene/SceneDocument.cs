using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The scene section: a positional material palette plus the objects placed against it. Building this produces the
/// <c>SdfProgram</c> the GPU runs; nothing here is backend-specific.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SceneDocument {
    /// <summary>The material palette; an object's <see cref="SceneObject.Material"/> indexes into this array.</summary>
    public IReadOnlyList<Material> Materials { get; init; } = [];
    /// <summary>The placed objects, melded into the field in order.</summary>
    public IReadOnlyList<SceneObject> Objects { get; init; } = [];
}
