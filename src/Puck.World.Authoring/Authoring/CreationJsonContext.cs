using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World.Authoring;

/// <summary>The creation family's generated metadata with the shared document spelling.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, Converters = new[] { typeof(Puck.Abstractions.Documents.StrictEnumConverter<Puck.Assets.Documents.SynthOscillator>), typeof(Puck.Abstractions.Documents.StrictEnumConverter<Puck.SignedDistance.SdfAxis>), typeof(Puck.Abstractions.Documents.StrictEnumConverter<Puck.SignedDistance.SdfBlendOp>), typeof(Puck.Abstractions.Documents.StrictEnumConverter<Puck.SignedDistance.SdfCellMode>), typeof(Puck.Abstractions.Documents.StrictEnumConverter<Puck.SignedDistance.SdfLift>), typeof(Puck.Abstractions.Documents.StrictEnumConverter<Puck.SignedDistance.SdfPlane>), typeof(Puck.Abstractions.Documents.StrictEnumConverter<Puck.SignedDistance.SdfPrismProfileKind>), typeof(Puck.Abstractions.Documents.StrictEnumConverter<Puck.SignedDistance.SdfSolidPrimitive>), typeof(Puck.Abstractions.Documents.StrictEnumConverter<Puck.SignedDistance.SdfWallpaperGroup>) })]
[JsonSerializable(typeof(CreationDocument))]
public sealed partial class CreationJsonContext : JsonSerializerContext {
    /// <summary>Gets the canonical creation serializer.</summary>
    public static CreationJsonContext Document { get; } = new(new JsonSerializerOptions(options: DocumentJsonOptions.Shared));
}
