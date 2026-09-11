using System.Text.Json;
using Puck.Assets.Documents;

namespace Puck.World.Authoring.Sculpting;

/// <summary>
/// Constructs <c>state.&lt;row&gt;[.&lt;key&gt;]</c> REFERENCE-typed document spatial values from code. The document
/// families (<see cref="DocumentVector3"/>, <see cref="DocumentQuaternion"/>, <see cref="DocumentScalar"/>,
/// <see cref="DocumentIdentifier"/>) build a reference form only through an internal constructor their own JSON
/// converter calls during deserialization — <see cref="DocumentIdentifier"/> alone exposes a literal-only public
/// surface with no reference factory at all. Round-tripping a one-token JSON string through
/// <see cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions?)"/> reaches that converter as
/// ordinary public API (the converter, not this class, holds the internal access), so this is the one place code
/// under <c>Puck.World.Authoring</c> mints a reference — never reflection, never an <c>InternalsVisibleTo</c> grant.
/// </summary>
public static class DocumentReferences {
    /// <summary>Builds a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> reference vector.</summary>
    /// <param name="reference">The reference string (e.g. <c>state.mothJoints.waist</c>).</param>
    public static DocumentVector3 Vector3(string reference) => JsonSerializer.Deserialize<DocumentVector3>(
        json: JsonSerializer.Serialize(value: reference),
        options: DocumentJsonOptions.Shared
    )!;
    /// <summary>Builds a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> reference quaternion.</summary>
    /// <param name="reference">The reference string (e.g. <c>state.mothRot.z180</c>).</param>
    public static DocumentQuaternion Quaternion(string reference) => JsonSerializer.Deserialize<DocumentQuaternion>(
        json: JsonSerializer.Serialize(value: reference),
        options: DocumentJsonOptions.Shared
    )!;
    /// <summary>Builds a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> reference scalar.</summary>
    /// <param name="reference">The reference string (e.g. <c>state.mothTuning.strideThigh</c>).</param>
    public static DocumentScalar Scalar(string reference) => JsonSerializer.Deserialize<DocumentScalar>(
        json: JsonSerializer.Serialize(value: reference),
        options: DocumentJsonOptions.Shared
    )!;
}
