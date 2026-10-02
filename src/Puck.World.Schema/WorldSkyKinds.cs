using System.Reflection;
using System.Text.Json.Serialization;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>Declares a layer kind's evaluation and absent-field behavior on its schema record. The JSON
/// discriminator remains the authored name; shader generation reads this same inventory.</summary>
/// <param name="evaluationClass">The kind's evaluation class.</param>
/// <param name="blend">Its ordinary blend operation.</param>
/// <param name="visibility">Its ordinary camera and lighting visibility.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WorldSkyKindAttribute(SdfSkyLayerClass evaluationClass, SdfSkyBlend blend,
    SdfSkyVisibility visibility) : Attribute {
    /// <summary>The kind's evaluation class.</summary>
    public SdfSkyLayerClass Class { get; } = evaluationClass;
    /// <summary>The blend used when a layer leaves it unauthored.</summary>
    public SdfSkyBlend Blend { get; } = blend;
    /// <summary>The consumers used when a layer leaves visibility unauthored.</summary>
    public SdfSkyVisibility Visibility { get; } = visibility;
}

/// <summary>A registered sky kind, discovered from its schema record. Numeric tags are generated together with
/// the dispatcher and are never authored or retained across a structural shader revision.</summary>
/// <param name="Name">The JSON and source discriminator.</param>
/// <param name="Tag">The generated ordinal within this inventory.</param>
/// <param name="Type">The layer's schema record type.</param>
/// <param name="Class">The evaluation class.</param>
/// <param name="Blend">The kind's default blend.</param>
/// <param name="Visibility">The kind's default visibility.</param>
public sealed record WorldSkyKind(string Name, uint Tag, Type Type, SdfSkyLayerClass Class,
    SdfSkyBlend Blend, SdfSkyVisibility Visibility) {
    /// <summary>The conventionally named native record that shader generation verifies exists.</summary>
    public string NativeRecord => ("SdfSky" + Type.Name + "Data");
    /// <summary>The kind's HLSL module under the shared SDF shader tree.</summary>
    public string Module => ("sky/kinds/" + Name + ".hlsli");
}

/// <summary>The one sky-kind inventory. Adding a kind means adding its schema record and metadata plus its
/// native record and HLSL module; the generated dispatcher uses this inventory rather than another kind list.</summary>
public static class WorldSkyKinds {
    private static readonly WorldSkyKind[] Kinds = Prepare();
    private static readonly IReadOnlyDictionary<Type, WorldSkyKind> ByType = Kinds.ToDictionary(static kind => kind.Type);

    /// <summary>Gets the registered kinds in stable ordinal-name order.</summary>
    public static IReadOnlyList<WorldSkyKind> All => Kinds;
    /// <summary>Finds a layer's registered kind without reflection during presentation.</summary>
    /// <param name="layer">The authored layer.</param>
    /// <returns>The kind, or null for a retired legacy arm pending migration.</returns>
    public static WorldSkyKind? Of(WorldRenderSkyLayer layer) => ByType.GetValueOrDefault(layer.GetType());

    private static WorldSkyKind[] Prepare() {
        var discovered = typeof(WorldRenderSkyLayer).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(static arm => (Arm: arm, Kind: arm.DerivedType.GetCustomAttribute<WorldSkyKindAttribute>()))
            .Where(static row => row.Kind is not null)
            .OrderBy(static row => (string)row.Arm.TypeDiscriminator!, StringComparer.Ordinal).ToArray();
        var kinds = new WorldSkyKind[discovered.Length];

        for (var index = 0; index < discovered.Length; index++) {
            var (arm, kind) = discovered[index];

            kinds[index] = new((string)arm.TypeDiscriminator!, checked((uint)index), arm.DerivedType,
                kind!.Class, kind.Blend, kind.Visibility);
        }
        return kinds;
    }
}
