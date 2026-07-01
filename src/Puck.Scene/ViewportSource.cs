using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// A viewport's content source, authored as data — the polymorphic union a viewport binds to. The <c>$type</c> string
/// is the JSON discriminator. Today every source is a virtual SDF <see cref="CameraDocument"/> (an <c>orbit</c> or
/// <c>perspective</c> camera); a live capture source (a hardware camera) slots in later as a new derived record,
/// interchangeable with a camera at this exact seam. Adding a source kind is a new derived record + a
/// <see cref="JsonDerivedTypeAttribute"/> line here, each carrying its own <see cref="Validate"/>.
/// </summary>
[JsonDerivedType(typeof(OrbitCameraDocument), typeDiscriminator: "orbit")]
[JsonDerivedType(typeof(PerspectiveCameraDocument), typeDiscriminator: "perspective")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ViewportSource {
    internal abstract void Validate(string path, ValidationErrors errors);
}
