using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using Puck.Assets;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Baking;

namespace Puck.World.Authoring;

/// <summary>What one bake is keyed by: the creation's content pin, the baker's version, and the quality tier. Two bakes
/// with one key are the same bytes, so the key's <see cref="Pin"/> names a bake in every cache.</summary>
/// <param name="CreationPin">The creation's pin: the SHA-256 hex of its canonical bytes
/// (<see cref="CreationCanonicalizer.Canonicalize"/>), which a world's prototype row carries as its hash.</param>
/// <param name="BakerVersion">The baker's version (<see cref="SdfBaker.Version"/>).</param>
/// <param name="Quality">The quality tier.</param>
public readonly record struct CreationBakeKey(string CreationPin, uint BakerVersion, SdfBakeQuality Quality) {
    /// <summary>Gets the key's own pin: the SHA-256 of its three parts, which names the bake in a store.</summary>
    public ContentPin Pin => ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: $"puck.creation.bake\n{CreationPin}\n{BakerVersion}\n{((int)Quality)}"));

    /// <summary>Returns the key of a bake the running baker makes of a creation at <paramref name="quality"/>.</summary>
    /// <param name="creationPin">The creation's pin.</param>
    /// <param name="quality">The quality tier.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException"><paramref name="creationPin"/> is <see langword="null"/>, empty, or white
    /// space.</exception>
    public static CreationBakeKey For(string creationPin, SdfBakeQuality quality) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: creationPin);

        return new CreationBakeKey(
            BakerVersion: SdfBaker.Version,
            CreationPin: creationPin,
            Quality: quality
        );
    }
}
/// <summary>
/// Bakes a creation into presentation assets (<see cref="SdfBake"/>). The creation is converted to the engine frame
/// (<see cref="CreationFrame.ToEngine"/>) and emitted at its own origin and unit scale through
/// <see cref="CreationStampEmitter.EmitFixed"/>, the emission deterministic contact reads, so a bake shows exactly the
/// shapes the fixed-point interpreter answers for: detail shapes, sweeps, text runs, noise relief, volumes, and the
/// warp facets contact omits are absent from it. Each palette slot becomes one program material, so a material id in
/// a bake is the palette slot; a slot whose color is bound to a state cell bakes the fallback gray albedo, and its
/// material texels still name the slot. A creation the interpreter refuses, one past the program's capacity, or one
/// with no solid shape has no bake and is refused by name.
/// </summary>
public static class CreationBaker {
    private static readonly Vector3 FallbackAlbedo = new(value: 0.7f);

    /// <summary>Bakes <paramref name="document"/> at <paramref name="quality"/>.</summary>
    /// <param name="document">The creation, in the author frame, as a world's prototype row carries it.</param>
    /// <param name="quality">The quality tier.</param>
    /// <param name="bake">The bake, or <see langword="null"/> when the creation has none.</param>
    /// <param name="reason">Why the creation has no bake, or empty on success.</param>
    /// <returns><see langword="true"/> when the creation baked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static bool TryBake(CreationDocument document, SdfBakeQuality quality, [NotNullWhen(returnValue: true)] out SdfBake? bake, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: document);
        bake = null;

        var engine = CreationFrame.ToEngine(document: document);
        var builder = new SdfProgramBuilder();
        var palette = (engine.Palette ?? []);
        var materials = new SdfMaterial[Math.Max(val1: 1, val2: palette.Count)];

        for (var slot = 0; (slot < materials.Length); slot++) {
            materials[slot] = new SdfMaterial(Albedo: ((slot < palette.Count)
                ? HexColor.Parse(fallback: FallbackAlbedo, value: palette[slot].Color)
                : FallbackAlbedo));
            _ = builder.AddMaterial(material: materials[slot]);
        }

        try {
            CreationStampEmitter.EmitFixed(
                builder: builder,
                document: engine,
                materialFor: shape => Math.Clamp(value: (shape.Material ?? 0), min: 0, max: (materials.Length - 1)),
                transform: new FixedCreationStampTransform(
                    Origin: FixedVector3.Zero,
                    ReflectionNormal: null,
                    Rotation: FixedQuaternion.Identity,
                    Scale: FixedQ4816.One
                )
            );
            bake = SdfBaker.Bake(
                center: Vector3.Zero,
                materials: materials,
                program: builder.Build(buildInstanceGrid: false),
                reach: CreationStampEmitter.RenderReach(document: WithoutFlares(document: engine), fontFor: null, scale: 1f),
                tier: SdfBakeTier.For(quality: quality)
            );
        } catch (Exception exception) when ((exception is ArgumentException or SdfProgramCapacityException)) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }

        reason = string.Empty;
        return true;
    }

    // The reach sizes the bake's lattice, so it must be the same float on every machine: every term of the render reach
    // is IEEE arithmetic, a correctly rounded square root, a vector length summed (x^2 + y^2) + z^2 on every instruction
    // set, or a flare's extrema through SdfProgram.PortableAcos. EmitFixed emits no flare, so the lattice needs no room
    // for one and the reach is taken without them.
    private static CreationDocument WithoutFlares(CreationDocument document) => (((document.Shapes is { } shapes) && shapes.Any(predicate: static shape => (shape.Flare is not null)))
        ? (document with { Shapes = [.. shapes.Select(selector: static shape => (shape with { Flare = null }))] })
        : document);
}
