using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// A viewport's content source, authored as data — the polymorphic union a viewport binds to. The <c>$type</c> string
/// is the JSON discriminator. A source is either a virtual SDF <see cref="CameraDocument"/> (an <c>orbit</c> or
/// <c>perspective</c> camera) or a <see cref="LiveCameraViewportSource"/> (a <c>live-camera</c> hardware capture device),
/// interchangeable at this exact seam — which is what makes a camera first-class per viewport. Adding a source kind is a
/// new derived record + a <see cref="JsonDerivedTypeAttribute"/> line here, each carrying its own <see cref="Validate"/>.
/// </summary>
[JsonDerivedType(typeof(OrbitCameraDocument), typeDiscriminator: "orbit")]
[JsonDerivedType(typeof(PerspectiveCameraDocument), typeDiscriminator: "perspective")]
[JsonDerivedType(typeof(LiveCameraViewportSource), typeDiscriminator: "live-camera")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ViewportSource {
    internal abstract void Validate(string path, ValidationErrors errors);
}
