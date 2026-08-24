using System.Numerics;
using System.Text.Json;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World.Client.Sdf;

/// <summary>
/// The <c>puck.sdf.v1</c> front door: turns a JSON geometry document into <see cref="SdfProgramBuilder"/> calls. The
/// document is the builder's argument surface — <see cref="Decode"/> is a switch over op names with typed argument
/// records, never a general serializer, so most builder validation throws (an overflowing torus radius sum, an
/// overflowing cylinder/capsule/box derived bound, <c>AddMaterial</c>'s composed-palette ceiling) stay live and
/// become the document's own validation for free (see <see cref="Replay"/>). Two different reasons the rest run at
/// decode instead: (1) a few rules the builder repairs rather than refuses — a negative scale (silently takes the
/// absolute value and floors it near zero), a negative smooth radius (silently absorbed downstream), an
/// unnormalizable rotation axis (silently renormalizes to something never authored) — throw nothing to inherit, so
/// they are refused explicitly here instead (see <see cref="RequireScale"/>/<see cref="ReadSmooth"/>/
/// <see cref="RequireAxis"/>); (2) a shape's radius/half-extent/round and a material's four channels are also
/// sign-checked here, even though the builder already refuses a negative one via its own <c>RequireNonNegative</c> —
/// a decoder-owned mirror of a builder refusal (never a repair), added so the refusal names the document's own op
/// index/field directly instead of surfacing only once <see cref="Replay"/> reaches the builder (see
/// <see cref="ReadNonNegativeFloat"/>/<see cref="ReadNonNegativeVector3"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this covers</b> (a useful subset, not the whole vocabulary): the primitives <see cref="SdfDocumentOpKind.Sphere"/>/
/// <see cref="SdfDocumentOpKind.Box"/>/<see cref="SdfDocumentOpKind.Capsule"/>/<see cref="SdfDocumentOpKind.Cylinder"/>/
/// <see cref="SdfDocumentOpKind.Torus"/>/<see cref="SdfDocumentOpKind.Plane"/>, the transforms
/// <see cref="SdfDocumentOpKind.Translate"/>/<see cref="SdfDocumentOpKind.Rotate"/>/<see cref="SdfDocumentOpKind.Scale"/>/
/// <see cref="SdfDocumentOpKind.Reset"/>, and the field-scope combinators <see cref="SdfDocumentOpKind.Push"/>/
/// <see cref="SdfDocumentOpKind.Pop"/>. Skipped entirely: glyph/text, every
/// positional-recolor fold (<c>WallpaperFold</c>/<c>RepeatPolar</c>/<c>CellJitter</c> — the builder's scope clamp
/// repairs their stride, and an untrusted-shaped door must refuse rather than inherit a repair, so this prototype
/// simply carries no op that could reach it), every warp/bend/repeat/onion/dilate/log-sphere/symmetry op except the
/// scoped <see cref="SdfDocumentOpKind.NoiseDisplace"/> field op, screens,
/// instances, and sampled regions.
/// </para>
/// <para>
/// <b>Top-level blend restriction.</b> A document's field-scope depth starts at 0;
/// <see cref="SdfDocumentOpKind.Push"/> raises it to 1 (refused past 1 — the builder's own
/// <see cref="SdfProgramBuilder.MaxFieldScopeDepth"/>) and <see cref="SdfDocumentOpKind.Pop"/> lowers it back to 0. A
/// shape op's <c>blend</c> at depth 0 must be a union-family value (<see cref="SdfBlendOp.Union"/>/
/// <see cref="SdfBlendOp.SmoothUnion"/>/<see cref="SdfBlendOp.ChamferUnion"/>); depth 1 (inside one push/pop pair)
/// allows any blend — a document's own field scope is where its subtraction/intersection CSG lives. The same rule
/// applies to a <see cref="SdfDocumentOpKind.Push"/>'s own <c>blend</c>: it is evaluated at the depth the push itself
/// sits at (never a fixed depth), because that blend composes the closed scope back into whatever field the push
/// opened inside — a top-level push is therefore restricted exactly like a top-level shape. Enforced here by
/// validation (never by host-wrapping, which would spend the builder's one field-scope level and strip every
/// document of its own intra-document CSG).
/// </para>
/// <para>
/// <b>The fixed prototype reservation</b> (<see cref="MaxOps"/>/<see cref="MaxMaterials"/>/<see cref="MaxDocumentBytes"/>):
/// this build ships no per-contributor cost ledger. Instead, <see cref="WorldSdfDocumentEmitter"/>'s capacity probe
/// reserves a fixed worst case sized from the first two constants, and a document declaring more ops or materials
/// than they allow is refused here, at decode, before a single builder call runs — so a document that passes decode
/// can never outgrow what the probe already reserved. <see cref="MaxDocumentBytes"/> backstops the other two on the
/// read side: <c>world.sdf.load</c> refuses a file over that size before reading it, so a garbage file cannot spend
/// memory or parse time claiming to be a document at all.
/// </para>
/// </remarks>
public static class SdfDocumentDecoder {
    /// <summary>The largest a <c>puck.sdf.v1</c> document's raw file may be — refused by
    /// <c>world.sdf.load</c> before the read completes (a <see cref="System.IO.FileInfo"/> length check, never a
    /// read-then-measure), so a multi-gigabyte file is rejected before it is read, hashed, or DOM-parsed. Derived from
    /// <see cref="MaxMaterials"/>/<see cref="MaxOps"/> with generous headroom: a maximal material entry
    /// (<c>{"albedo":[...],"emissive":...,"specular":...,"shininess":...}</c>) and a maximal op (the largest is
    /// <c>plane</c>'s 6 members, each a number or 3-array) each run well under 256 bytes even generously
    /// pretty-printed, so <see cref="MaxMaterials"/> materials + <see cref="MaxOps"/> ops top out around 74 KiB
    /// (32 + 256 = 288 entries × 256 bytes); this constant is roughly 100× that, comfortably fitting any legitimately
    /// maximal document while still refusing a garbage file long before it costs real memory or parse time.</summary>
    public const int MaxDocumentBytes = ((8 * 1024) * 1024);
    /// <summary>The most materials one document may declare — see the type remarks on the fixed reservation.</summary>
    public const int MaxMaterials = 32;
    /// <summary>The most entries one document's <c>ops</c> array may declare — see the type remarks.</summary>
    public const int MaxOps = 256;
    /// <summary>The document schema tag every <c>puck.sdf.v1</c> document must carry verbatim.</summary>
    public const string Schema = "puck.sdf.v1";

    private static readonly string[] RootMembers = ["schema", "materials", "ops"];
    private static readonly string[] MaterialMembers = ["albedo", "emissive", "specular", "shininess"];
    private static readonly Dictionary<string, SdfDocumentOpKind> OpKinds = new(comparer: StringComparer.Ordinal) {
        ["reset"] = SdfDocumentOpKind.Reset,
        ["translate"] = SdfDocumentOpKind.Translate,
        ["rotate"] = SdfDocumentOpKind.Rotate,
        ["scale"] = SdfDocumentOpKind.Scale,
        ["push"] = SdfDocumentOpKind.Push,
        ["pop"] = SdfDocumentOpKind.Pop,
        ["sphere"] = SdfDocumentOpKind.Sphere,
        ["box"] = SdfDocumentOpKind.Box,
        ["capsule"] = SdfDocumentOpKind.Capsule,
        ["cylinder"] = SdfDocumentOpKind.Cylinder,
        ["torus"] = SdfDocumentOpKind.Torus,
        ["plane"] = SdfDocumentOpKind.Plane,
        ["noiseDisplace"] = SdfDocumentOpKind.NoiseDisplace,
        ["cellJitter"] = SdfDocumentOpKind.CellJitter,
    };
    private static readonly Dictionary<SdfDocumentOpKind, string[]> OpMembers = new() {
        [SdfDocumentOpKind.Reset] = ["op"],
        [SdfDocumentOpKind.Translate] = ["op", "offset"],
        [SdfDocumentOpKind.Rotate] = ["op", "axis", "angleDegrees"],
        [SdfDocumentOpKind.Scale] = ["op", "scale"],
        [SdfDocumentOpKind.Push] = ["op", "blend", "smooth"],
        [SdfDocumentOpKind.Pop] = ["op"],
        [SdfDocumentOpKind.Sphere] = ["op", "radius", "material", "blend", "smooth"],
        [SdfDocumentOpKind.Box] = ["op", "halfExtents", "round", "material", "blend", "smooth"],
        [SdfDocumentOpKind.Capsule] = ["op", "endpoint", "radius", "material", "blend", "smooth"],
        [SdfDocumentOpKind.Cylinder] = ["op", "radius", "halfHeight", "material", "blend", "smooth"],
        [SdfDocumentOpKind.Torus] = ["op", "majorRadius", "minorRadius", "material", "blend", "smooth"],
        [SdfDocumentOpKind.Plane] = ["op", "normal", "offset", "material", "blend", "smooth"],
        [SdfDocumentOpKind.NoiseDisplace] = ["op", "frequency", "amplitude", "octaves", "gain", "lacunarity", "seed"],
        [SdfDocumentOpKind.CellJitter] = ["op", "spacing", "jitter", "seed", "tumble", "flavor"],
    };
    private static readonly Dictionary<string, SdfBlendOp> BlendNames = new(comparer: StringComparer.Ordinal) {
        ["union"] = SdfBlendOp.Union,
        ["smoothUnion"] = SdfBlendOp.SmoothUnion,
        ["subtraction"] = SdfBlendOp.Subtraction,
        ["intersection"] = SdfBlendOp.Intersection,
        ["xor"] = SdfBlendOp.Xor,
        ["smoothIntersection"] = SdfBlendOp.SmoothIntersection,
        ["smoothSubtraction"] = SdfBlendOp.SmoothSubtraction,
        ["chamferUnion"] = SdfBlendOp.ChamferUnion,
        ["chamferIntersection"] = SdfBlendOp.ChamferIntersection,
        ["chamferSubtraction"] = SdfBlendOp.ChamferSubtraction,
    };
    private static readonly HashSet<SdfBlendOp> TopLevelBlends = [SdfBlendOp.Union, SdfBlendOp.SmoothUnion, SdfBlendOp.ChamferUnion];
    private static readonly Dictionary<string, SdfNoiseFlavor> NoiseFlavorNames = new(comparer: StringComparer.Ordinal) {
        ["white"] = SdfNoiseFlavor.White,
        ["blue"] = SdfNoiseFlavor.Blue,
        ["gaussian"] = SdfNoiseFlavor.Gaussian,
    };

    private static void Apply(SdfProgramBuilder builder, SdfDocumentOp op, int[] materialIds) {
        try {
            switch (op.Kind) {
                case SdfDocumentOpKind.Reset:
                    _ = builder.ResetPoint();

                    break;
                case SdfDocumentOpKind.Translate:
                    _ = builder.Translate(offset: op.Vector0);

                    break;
                case SdfDocumentOpKind.Rotate: {
                        // RequireAxis (decode time) already refused any axis that would normalize to non-finite or zero —
                        // this normalize can never produce NaN here.
                        var axis = Vector3.Normalize(value: op.Vector0);

                        _ = builder.Rotate(rotation: Quaternion.CreateFromAxisAngle(
                            axis: axis,
                            angle: (op.Scalar0 * (MathF.PI / 180f))
                        ));

                        break;
                    }
                case SdfDocumentOpKind.Scale:
                    _ = builder.Scale(scale: op.Vector0);

                    break;
                case SdfDocumentOpKind.Push:
                    _ = builder.PushField(
                        compose: op.Blend,
                        smooth: op.Smooth
                    );

                    break;
                case SdfDocumentOpKind.Pop:
                    _ = builder.PopField();

                    break;
                case SdfDocumentOpKind.Sphere:
                    _ = builder.Sphere(
                        radius: op.Scalar0,
                        material: materialIds[op.Material],
                        blend: op.Blend,
                        smooth: op.Smooth
                    );

                    break;
                case SdfDocumentOpKind.Box:
                    _ = builder.Box(
                        halfExtents: op.Vector0,
                        round: op.Scalar0,
                        material: materialIds[op.Material],
                        blend: op.Blend,
                        smooth: op.Smooth
                    );

                    break;
                case SdfDocumentOpKind.Capsule:
                    _ = builder.Capsule(
                        endpoint: op.Vector0,
                        radius: op.Scalar0,
                        material: materialIds[op.Material],
                        blend: op.Blend,
                        smooth: op.Smooth
                    );

                    break;
                case SdfDocumentOpKind.Cylinder:
                    _ = builder.Cylinder(
                        radius: op.Scalar0,
                        halfHeight: op.Scalar1,
                        material: materialIds[op.Material],
                        blend: op.Blend,
                        smooth: op.Smooth
                    );

                    break;
                case SdfDocumentOpKind.Torus:
                    _ = builder.Torus(
                        majorRadius: op.Scalar0,
                        minorRadius: op.Scalar1,
                        material: materialIds[op.Material],
                        blend: op.Blend,
                        smooth: op.Smooth
                    );

                    break;
                case SdfDocumentOpKind.Plane:
                    _ = builder.Plane(
                        normal: op.Vector0,
                        offset: op.Scalar0,
                        material: materialIds[op.Material],
                        blend: op.Blend,
                        smooth: op.Smooth
                    );

                    break;
                case SdfDocumentOpKind.NoiseDisplace:
                    // Octave-range / gain / lacunarity refusals are INHERITED from the builder (see the catch below),
                    // carrying the op index/name context; the depth-scoping rule was already enforced at decode.
                    _ = builder.NoiseDisplace(
                        amplitude: op.Scalar1,
                        frequency: op.Scalar0,
                        gain: op.Vector0.X,
                        lacunarity: op.Vector0.Y,
                        octaves: op.Integer0,
                        seed: op.Seed
                    );

                    break;
                case SdfDocumentOpKind.CellJitter:
                    // GEOMETRIC-ONLY on purpose: materialVariants stays 0, so the positional-recolor repair this door
                    // refuses to inherit is unreachable. The in-cell jitter rule is inherited from the builder.
                    _ = builder.CellJitter(
                        flavor: ((SdfNoiseFlavor)op.Integer0),
                        jitter: op.Scalar0,
                        seed: op.Seed,
                        spacing: op.Vector0,
                        tumble: op.Scalar1
                    );

                    break;
                default:
                    throw new SdfDocumentException(
                        reason: SdfRefusal.UnhandledOpKind,
                        message: $"op {op.Index}: unhandled op kind '{op.Kind}'."
                    );
            }
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            // THE INHERITANCE: every builder throw this decoder does not pre-check — a derived-quantity overflow
            // from inputs that are each individually finite (a torus major/minor radius pair whose SUM overflows;
            // the same shape for a cylinder/capsule/box derived bound — see SdfRefusal.BuilderRejectedOp's remarks)
            // — becomes the document's own validation here, unchanged except for the op index/name context a bare
            // ArgumentOutOfRangeException could never carry back to the source file. A non-finite number is refused
            // earlier, at ReadFloat; a negative radius/half-extent/round is refused earlier too, as NumberNegative.
            throw new SdfDocumentException(
                reason: SdfRefusal.BuilderRejectedOp,
                message: $"op {op.Index} ('{OpName(kind: op.Kind)}'): {exception.Message}"
            );
        }
    }
    // The repo's standard fold (Puck.Maths.Fnv1aHash), byte-at-a-time over the RECEIVED bytes — allocation-free and
    // endianness-independent, matching decision 7's "same bytes + same code version -> same program" identity.
    private static ulong ComputeHash(ReadOnlySpan<byte> utf8Json) {
        var hash = Fnv1aHash.Create();

        foreach (var value in utf8Json) {
            hash.Add(value: value);
        }

        return hash.Value;
    }
    private static IReadOnlyList<SdfMaterial> DecodeMaterials(Dictionary<string, JsonElement> root) {
        // Omission is a structural REJECTION, not a repair into an empty list — an author who means "no materials"
        // says so with an explicit [] (the same array this returns for one), never by leaving the key out.
        if (!root.TryGetValue(
            key: "materials",
            value: out var materialsElement
        )) {
            throw new SdfDocumentException(
                message: "document: 'materials' is required — declare an explicit empty array ([]) to author a document with no materials; omitting the member entirely is refused, not defaulted.",
                reason: SdfRefusal.MaterialsRequired
            );
        }

        if (materialsElement.ValueKind != JsonValueKind.Array) {
            throw new SdfDocumentException(
                message: "document: 'materials' must be an array.",
                reason: SdfRefusal.MaterialsNotArray
            );
        }

        var count = materialsElement.GetArrayLength();

        if (count > MaxMaterials) {
            throw new SdfDocumentException(
                message: $"document: 'materials' declares {count} entries, more than the {MaxMaterials} this prototype reserves.",
                reason: SdfRefusal.MaterialsTooMany
            );
        }

        var list = new List<SdfMaterial>(capacity: count);
        var index = 0;

        foreach (var element in materialsElement.EnumerateArray()) {
            var context = $"materials[{index}]";

            if (element.ValueKind != JsonValueKind.Object) {
                throw new SdfDocumentException(
                    message: $"{context}: must be an object.",
                    reason: SdfRefusal.MaterialEntryNotObject
                );
            }

            var members = UniqueMembers(
                context: context,
                element: element
            );

            RequireNoUnknownMembers(
                allowed: MaterialMembers,
                context: context,
                members: members
            );

            if (!members.TryGetValue(
                key: "albedo",
                value: out var albedoElement
            )) {
                throw new SdfDocumentException(
                    message: $"{context}: 'albedo' is required.",
                    reason: SdfRefusal.RequiredMemberMissing
                );
            }

            // AddMaterial's RequireNonNegative covers all four channels (a negative reflectance/emissive/specular
            // strength or Blinn-Phong exponent has no physical reading) — refused HERE now, not inherited from the
            // builder's throw (see the type remarks).
            var albedo = ReadNonNegativeVector3(
                context: $"{context}.albedo",
                element: albedoElement
            );
            var emissive = (members.TryGetValue(
                key: "emissive",
                value: out var emissiveElement
            )
                ? ReadNonNegativeFloat(
                    context: $"{context}.emissive",
                    element: emissiveElement
                )
                : 0f
            );
            var specular = (members.TryGetValue(
                key: "specular",
                value: out var specularElement
            )
                ? ReadNonNegativeFloat(
                    context: $"{context}.specular",
                    element: specularElement
                )
                : 0f
            );
            var shininess = (members.TryGetValue(
                key: "shininess",
                value: out var shininessElement
            )
                ? ReadNonNegativeFloat(
                    context: $"{context}.shininess",
                    element: shininessElement
                )
                : 32f
            );

            list.Add(item: new SdfMaterial(
                Albedo: albedo,
                Emissive: emissive,
                Shininess: shininess,
                Specular: specular
            ));
            index++;
        }

        return list;
    }
    private static IReadOnlyList<SdfDocumentOp> DecodeOps(Dictionary<string, JsonElement> root, int materialCount) {
        // Omission is refused the same way as materials' — an explicit "ops": [] stays the legal way to author (and,
        // via world.sdf.load, to LOAD) a document that clears the composed scene to nothing, since replacing the
        // live document is the only clear path this front door has.
        if (!root.TryGetValue(
            key: "ops",
            value: out var opsElement
        )) {
            throw new SdfDocumentException(
                message: "document: 'ops' is required — declare an explicit empty array ([]) to author a document with no ops (also the way to clear a previously loaded one via world.sdf.load); omitting the member entirely is refused, not defaulted.",
                reason: SdfRefusal.OpsRequired
            );
        }

        if (opsElement.ValueKind != JsonValueKind.Array) {
            throw new SdfDocumentException(
                message: "document: 'ops' must be an array.",
                reason: SdfRefusal.OpsNotArray
            );
        }

        var count = opsElement.GetArrayLength();

        if (count > MaxOps) {
            throw new SdfDocumentException(
                message: $"document: 'ops' declares {count} entries, more than the {MaxOps} this prototype reserves.",
                reason: SdfRefusal.OpsTooMany
            );
        }

        var list = new List<SdfDocumentOp>(capacity: count);
        var depth = 0;
        var index = 0;

        foreach (var element in opsElement.EnumerateArray()) {
            var context = $"ops[{index}]";

            if (element.ValueKind != JsonValueKind.Object) {
                throw new SdfDocumentException(
                    message: $"{context}: must be an object.",
                    reason: SdfRefusal.OpEntryNotObject
                );
            }

            var members = UniqueMembers(
                context: context,
                element: element
            );

            if (
                !members.TryGetValue(
                key: "op",
                value: out var opNameElement
            ) ||
                (opNameElement.ValueKind != JsonValueKind.String)
            ) {
                throw new SdfDocumentException(
                    message: $"{context}: 'op' (a string) is required.",
                    reason: SdfRefusal.OpNameRequired
                );
            }

            var opName = (opNameElement.GetString() ?? string.Empty);

            if (!OpKinds.TryGetValue(
                key: opName,
                value: out var kind
            )) {
                throw new SdfDocumentException(
                    message: $"{context}: unknown op '{opName}'.",
                    reason: SdfRefusal.UnknownOpName
                );
            }

            RequireNoUnknownMembers(
                members: members,
                allowed: OpMembers[kind],
                context: context
            );

            var op = kind switch {
                SdfDocumentOpKind.Reset => new SdfDocumentOp(
                Index: index,
                Kind: kind
            ),
                SdfDocumentOpKind.Translate => new SdfDocumentOp(
                Index: index,
                Kind: kind,
                Vector0: RequireVector3(
                    context: context,
                    key: "offset",
                    members: members
                )
            ),
                SdfDocumentOpKind.Rotate => new SdfDocumentOp(
                Index: index,
                Kind: kind,
                Vector0: RequireAxis(
                    context: context,
                    members: members
                ),
                Scalar0: RequireFloat(
                    context: context,
                    key: "angleDegrees",
                    members: members
                )
            ),
                SdfDocumentOpKind.Scale => new SdfDocumentOp(
                Index: index,
                Kind: kind,
                Vector0: RequireScale(
                    context: context,
                    members: members
                )
            ),
                // The depth is the depth THIS push sits at (0 = top level), never a fixed 1 — a push's compose blend
                // composes the CLOSED scope back into whatever field the push itself opened inside, so it is refused
                // at top level exactly like a top-level shape.
                SdfDocumentOpKind.Push => new SdfDocumentOp(
                Index: index,
                Kind: kind,
                Blend: ReadBlend(
                    context: context,
                    depth: depth,
                    members: members
                ),
                Smooth: ReadSmooth(
                    context: context,
                    members: members
                )
            ),
                SdfDocumentOpKind.Pop => new SdfDocumentOp(
                Index: index,
                Kind: kind
            ),
                // The builder's RequireNonNegative sites this decoder mirrors (sphere/capsule/cylinder radii, cylinder
                // half-height, torus radii, box half-extents and round) — refused HERE now, not inherited from the
                // builder's throw. Plane's normal/offset are NOT in that set (offset is signed by construction; normal
                // is direction-checked, never sign-checked) and stay on RequireFloat/RequireVector3 below.
                SdfDocumentOpKind.Sphere => new SdfDocumentOp(
                Index: index,
                Kind: kind,
                Scalar0: RequireNonNegativeFloat(
                    context: context,
                    key: "radius",
                    members: members
                ),
                Material: RequireMaterial(
                    context: context,
                    materialCount: materialCount,
                    members: members
                ),
                Blend: ReadBlend(
                    context: context,
                    depth: depth,
                    members: members
                ),
                Smooth: ReadSmooth(
                    context: context,
                    members: members
                )
            ),
                SdfDocumentOpKind.Box => new SdfDocumentOp(
                Index: index,
                Kind: kind,
                Vector0: RequireNonNegativeVector3(
                    context: context,
                    key: "halfExtents",
                    members: members
                ),
                Scalar0: ReadOptionalNonNegativeFloat(
                    context: context,
                    fallback: 0f,
                    key: "round",
                    members: members
                ),
                Material: RequireMaterial(
                    context: context,
                    materialCount: materialCount,
                    members: members
                ),
                Blend: ReadBlend(
                    context: context,
                    depth: depth,
                    members: members
                ),
                Smooth: ReadSmooth(
                    context: context,
                    members: members
                )
            ),
                SdfDocumentOpKind.Capsule => new SdfDocumentOp(
                Index: index,
                Kind: kind,
                Vector0: RequireVector3(
                    context: context,
                    key: "endpoint",
                    members: members
                ),
                Scalar0: RequireNonNegativeFloat(
                    context: context,
                    key: "radius",
                    members: members
                ),
                Material: RequireMaterial(
                    context: context,
                    materialCount: materialCount,
                    members: members
                ),
                Blend: ReadBlend(
                    context: context,
                    depth: depth,
                    members: members
                ),
                Smooth: ReadSmooth(
                    context: context,
                    members: members
                )
            ),
                SdfDocumentOpKind.Cylinder => new SdfDocumentOp(
                Index: index,
                Kind: kind,
                Scalar0: RequireNonNegativeFloat(
                    context: context,
                    key: "radius",
                    members: members
                ),
                Scalar1: RequireNonNegativeFloat(
                    context: context,
                    key: "halfHeight",
                    members: members
                ),
                Material: RequireMaterial(
                    context: context,
                    materialCount: materialCount,
                    members: members
                ),
                Blend: ReadBlend(
                    context: context,
                    depth: depth,
                    members: members
                ),
                Smooth: ReadSmooth(
                    context: context,
                    members: members
                )
            ),
                SdfDocumentOpKind.Torus => new SdfDocumentOp(
                Index: index,
                Kind: kind,
                Scalar0: RequireNonNegativeFloat(
                    context: context,
                    key: "majorRadius",
                    members: members
                ),
                Scalar1: RequireNonNegativeFloat(
                    context: context,
                    key: "minorRadius",
                    members: members
                ),
                Material: RequireMaterial(
                    context: context,
                    materialCount: materialCount,
                    members: members
                ),
                Blend: ReadBlend(
                    context: context,
                    depth: depth,
                    members: members
                ),
                Smooth: ReadSmooth(
                    context: context,
                    members: members
                )
            ),
                SdfDocumentOpKind.Plane => new SdfDocumentOp(
                Index: index,
                Kind: kind,
                Vector0: RequireVector3(
                    context: context,
                    key: "normal",
                    members: members
                ),
                Scalar0: RequireFloat(
                    context: context,
                    key: "offset",
                    members: members
                ),
                Material: RequireMaterial(
                    context: context,
                    materialCount: materialCount,
                    members: members
                ),
                Blend: ReadBlend(
                    context: context,
                    depth: depth,
                    members: members
                ),
                Smooth: ReadSmooth(
                    context: context,
                    members: members
                )
            ),
                SdfDocumentOpKind.NoiseDisplace => DecodeNoiseDisplace(
                context: context,
                depth: depth,
                index: index,
                members: members
            ),
                SdfDocumentOpKind.CellJitter => DecodeCellJitter(
                context: context,
                index: index,
                members: members
            ),
                _ => throw new SdfDocumentException(
                message: $"{context}: unhandled op '{opName}'.",
                reason: SdfRefusal.UnhandledOpKind
            ),
            };

            if (kind == SdfDocumentOpKind.Push) {
                if (depth >= SdfProgramBuilder.MaxFieldScopeDepth) {
                    throw new SdfDocumentException(
                        message: $"{context}: 'push' would nest a field scope deeper than this document's depth-{SdfProgramBuilder.MaxFieldScopeDepth} cap — close the open 'push' with 'pop' first.",
                        reason: SdfRefusal.PushTooDeep
                    );
                }

                depth++;
            } else if (kind == SdfDocumentOpKind.Pop) {
                if (depth == 0) {
                    throw new SdfDocumentException(
                        message: $"{context}: 'pop' with no matching 'push'.",
                        reason: SdfRefusal.PopUnmatched
                    );
                }

                depth--;
            }

            list.Add(item: op);
            index++;
        }

        if (depth != 0) {
            throw new SdfDocumentException(
                message: $"document: {depth} 'push' op(s) never closed by a matching 'pop'.",
                reason: SdfRefusal.UnclosedPush
            );
        }

        return list;
    }
    private static string Describe(JsonElement element) => ((element.ValueKind == JsonValueKind.String)
        ? (element.GetString() ?? string.Empty)
        : element.ValueKind.ToString()
    );
    private static string OpName(SdfDocumentOpKind kind) {
        foreach (var pair in OpKinds) {
            if (pair.Value == kind) {
                return pair.Key;
            }
        }

        return kind.ToString();
    }
    // Decision 9: a shape's blend at field-scope depth 0 must be union-family (never host-wrapped — see the type
    // remarks); depth 1 (inside one push/pop pair) allows any blend, because that scope's field never reaches the
    // parent except through the union-family compose PushField itself records.
    private static SdfBlendOp ReadBlend(Dictionary<string, JsonElement> members, string context, int depth) {
        var blend = SdfBlendOp.Union;

        if (members.TryGetValue(
            key: "blend",
            value: out var blendElement
        )) {
            if (
                (blendElement.ValueKind != JsonValueKind.String) ||
                !BlendNames.TryGetValue(
                key: (blendElement.GetString() ?? string.Empty),
                value: out blend
            )
            ) {
                throw new SdfDocumentException(
                    reason: SdfRefusal.UnknownBlendName,
                    message: $"{context}: unknown blend '{Describe(element: blendElement)}'."
                );
            }
        }

        if (
            (depth == 0) &&
            !TopLevelBlends.Contains(item: blend)
        ) {
            throw new SdfDocumentException(
                reason: SdfRefusal.BlendNotTopLevelAllowed,
                message: $"{context}: blend '{blendElement.GetString()}' is not allowed at the document's top level (outside a push/pop pair) — only union, smoothUnion, or chamferUnion may reach the composed scene directly; subtraction/intersection/xor are available INSIDE a 'push'/'pop' pair."
            );
        }

        return blend;
    }
    private static float ReadFloat(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Number) {
            throw new SdfDocumentException(
                message: $"{context}: must be a number.",
                reason: SdfRefusal.NotANumber
            );
        }

        // Narrows to float HERE (GetSingle) — 1e39 parses as a legal double and becomes +Infinity on this narrowing.
        // Checked HERE, once, for every scalar this decoder ever reads (op arguments included), so a non-finite
        // value is always a document rejection — never an unhandled exception from whatever builder call it
        // eventually reaches (e.g. AddMaterial, whose loop in Replay() runs OUTSIDE Apply()'s try/catch).
        var value = element.GetSingle();

        if (!float.IsFinite(f: value)) {
            throw new SdfDocumentException(
                message: $"{context}: {value} must be finite.",
                reason: SdfRefusal.NumberNotFinite
            );
        }

        return value;
    }
    private static float ReadNonNegativeFloat(JsonElement element, string context) {
        var value = ReadFloat(
            context: context,
            element: element
        );

        if (value < 0f) {
            throw new SdfDocumentException(
                message: $"{context}: {value} must be non-negative.",
                reason: SdfRefusal.NumberNegative
            );
        }

        return value;
    }
    private static Vector3 ReadNonNegativeVector3(JsonElement element, string context) {
        var value = ReadVector3(
            context: context,
            element: element
        );

        if (
            (value.X < 0f) ||
            (value.Y < 0f) ||
            (value.Z < 0f)
        ) {
            throw new SdfDocumentException(
                message: $"{context}: [{value.X}, {value.Y}, {value.Z}] every component must be non-negative.",
                reason: SdfRefusal.NumberNegative
            );
        }

        return value;
    }
    private static float ReadOptionalFloat(Dictionary<string, JsonElement> members, string key, string context, float fallback) {
        return (members.TryGetValue(
            key: key,
            value: out var element
        )
            ? ReadFloat(
                context: $"{context}.{key}",
                element: element
            )
            : fallback
        );
    }
    private static float ReadOptionalNonNegativeFloat(Dictionary<string, JsonElement> members, string key, string context, float fallback) {
        return (members.TryGetValue(
            key: key,
            value: out var element
        )
            ? ReadNonNegativeFloat(
                context: $"{context}.{key}",
                element: element
            )
            : fallback
        );
    }
    // A negative smooth radius has no builder-side refusal to inherit — Shape() only finite-checks it (its sign is
    // absorbed by the shader's own max(0, smooth)) and PopField clamps a negative scope smooth to zero at the C#
    // layer, so BOTH are silent REPAIRS this front door must refuse instead of forwarding.
    // The one field-op arm: refuses outside a push/pop pair (a field op reads the running accumulator, so unscoped it
    // would displace every shape the composed world program holds before this document); octave-count integrality is
    // checked here (the builder takes an int), while its range and the gain/lacunarity positivity refusals are
    // inherited from the builder through Apply's catch.
    private static SdfDocumentOp DecodeNoiseDisplace(Dictionary<string, JsonElement> members, string context, int depth, int index) {
        if (depth < 1) {
            throw new SdfDocumentException(
                message: $"{context}: 'noiseDisplace' is a field op over the running accumulator and must sit inside a 'push'/'pop' pair — unscoped it would displace every shape composed before this document.",
                reason: SdfRefusal.FieldOpNotScoped
            );
        }

        var octavesRaw = ReadOptionalFloat(
            context: context,
            fallback: 4f,
            key: "octaves",
            members: members
        );

        if ((octavesRaw != MathF.Floor(x: octavesRaw)) || (octavesRaw < ((float)int.MinValue)) || (octavesRaw > ((float)int.MaxValue))) {
            throw new SdfDocumentException(
                message: $"{context}.octaves: {octavesRaw} must be an integer.",
                reason: SdfRefusal.NotANumber
            );
        }

        var seedRaw = ReadOptionalFloat(
            context: context,
            fallback: 0f,
            key: "seed",
            members: members
        );

        if ((seedRaw != MathF.Floor(x: seedRaw)) || (seedRaw < 0f) || (seedRaw > 4294967295f)) {
            throw new SdfDocumentException(
                message: $"{context}.seed: {seedRaw} must be an integer in 0..4294967295.",
                reason: SdfRefusal.NotANumber
            );
        }

        return new SdfDocumentOp(
            Index: index,
            Kind: SdfDocumentOpKind.NoiseDisplace,
            Vector0: new Vector3(
                x: ReadOptionalFloat(
                    context: context,
                    fallback: 0.5f,
                    key: "gain",
                    members: members
                ),
                y: ReadOptionalFloat(
                    context: context,
                    fallback: 2f,
                    key: "lacunarity",
                    members: members
                ),
                z: 0f
            ),
            Scalar0: RequireFloat(
                context: context,
                key: "frequency",
                members: members
            ),
            Scalar1: RequireFloat(
                context: context,
                key: "amplitude",
                members: members
            ),
            Integer0: ((int)octavesRaw),
            Seed: ((uint)seedRaw)
        );
    }
    // The scatter fold, geometric-only (no materialVariants lane, so no positional-recolor repair to inherit). A
    // point op: it folds only the document's own subsequent chain, and Replay's trailing ResetPoint fences the tail.
    // The fold tiles space INFINITELY per axis - bound the scattered content with an intersection shape inside a
    // push/pop scope, or give an axis a spacing larger than the region it should not repeat across.
    private static SdfDocumentOp DecodeCellJitter(Dictionary<string, JsonElement> members, string context, int index) {
        var flavor = SdfNoiseFlavor.White;

        if (members.TryGetValue(
            key: "flavor",
            value: out var flavorElement
        )) {
            if (
                (flavorElement.ValueKind != JsonValueKind.String) ||
                !NoiseFlavorNames.TryGetValue(
                key: (flavorElement.GetString() ?? string.Empty),
                value: out flavor
            )
            ) {
                throw new SdfDocumentException(
                    reason: SdfRefusal.UnknownNoiseFlavorName,
                    message: $"{context}: unknown flavor '{Describe(element: flavorElement)}' - expected white, blue, or gaussian."
                );
            }
        }

        var seedRaw = ReadOptionalFloat(
            context: context,
            fallback: 0f,
            key: "seed",
            members: members
        );

        if ((seedRaw != MathF.Floor(x: seedRaw)) || (seedRaw < 0f) || (seedRaw > 4294967295f)) {
            throw new SdfDocumentException(
                message: $"{context}.seed: {seedRaw} must be an integer in 0..4294967295.",
                reason: SdfRefusal.NotANumber
            );
        }

        return new SdfDocumentOp(
            Index: index,
            Kind: SdfDocumentOpKind.CellJitter,
            Vector0: RequireVector3(
                context: context,
                key: "spacing",
                members: members
            ),
            Scalar0: RequireFloat(
                context: context,
                key: "jitter",
                members: members
            ),
            Scalar1: ReadOptionalFloat(
                context: context,
                fallback: 0f,
                key: "tumble",
                members: members
            ),
            Integer0: ((int)flavor),
            Seed: ((uint)seedRaw)
        );
    }
    private static float ReadSmooth(Dictionary<string, JsonElement> members, string context) {
        var smooth = ReadOptionalFloat(
            context: context,
            fallback: 0f,
            key: "smooth",
            members: members
        );

        if (smooth < 0f) {
            throw new SdfDocumentException(
                message: $"{context}.smooth: {smooth} must be non-negative — a negative smooth radius is silently absorbed downstream (a hard blend for a shape, zero for a field scope) rather than honored; author 0 or a positive radius.",
                reason: SdfRefusal.SmoothNegative
            );
        }

        return smooth;
    }
    private static Vector3 ReadVector3(JsonElement element, string context) {
        if (
            (element.ValueKind != JsonValueKind.Array) ||
            (element.GetArrayLength() != 3)
        ) {
            throw new SdfDocumentException(
                message: $"{context}: must be a 3-element number array.",
                reason: SdfRefusal.NotAVector3
            );
        }

        Span<float> components = stackalloc float[3];
        var index = 0;

        foreach (var item in element.EnumerateArray()) {
            components[index++] = ReadFloat(
                context: context,
                element: item
            );
        }

        return new Vector3(
            x: components[0],
            y: components[1],
            z: components[2]
        );
    }
    // Apply() normalizes the authored axis host-side before calling Rotate — a zero axis (0/0 = NaN) or an
    // overflowing one (length -> +Infinity, so axis/length -> the zero vector) normalizes to something that is not
    // the authored rotation, and the builder never sees the original axis to refuse it. ReadFloat already guarantees
    // every component is finite, so the only remaining failure is the NORMALIZED result being non-finite or zero.
    private static Vector3 RequireAxis(Dictionary<string, JsonElement> members, string context) {
        var axis = RequireVector3(
            context: context,
            key: "axis",
            members: members
        );
        var unit = Vector3.Normalize(value: axis);

        if (
            !float.IsFinite(f: unit.X) ||
            !float.IsFinite(f: unit.Y) ||
            !float.IsFinite(f: unit.Z) ||
            (unit.LengthSquared() == 0f)
        ) {
            throw new SdfDocumentException(
                message: $"{context}.axis: [{axis.X}, {axis.Y}, {axis.Z}] does not normalize to a finite unit vector (zero, or overflowing to one) — there is no rotation axis to author here.",
                reason: SdfRefusal.AxisNotNormalizable
            );
        }

        return axis;
    }
    private static float RequireFloat(Dictionary<string, JsonElement> members, string key, string context) {
        var element = RequireMember(
            context: context,
            key: key,
            members: members,
            shape: "a number"
        );

        return ReadFloat(
            context: $"{context}.{key}",
            element: element
        );
    }
    private static int RequireMaterial(Dictionary<string, JsonElement> members, int materialCount, string context) {
        if (
            !members.TryGetValue(
            key: "material",
            value: out var element
        ) ||
            (element.ValueKind != JsonValueKind.Number) ||
            !element.TryGetInt32(value: out var material)
        ) {
            throw new SdfDocumentException(
                message: $"{context}: 'material' (a whole number) is required.",
                reason: SdfRefusal.RequiredMemberMissing
            );
        }

        // Decision 2: a document ordinal, never an absolute id — no spelling for anything outside its own palette.
        if (
            (material < 0) ||
            (material >= materialCount)
        ) {
            throw new SdfDocumentException(
                message: $"{context}: material {material} is outside the document's own palette (0..{(materialCount - 1)}).",
                reason: SdfRefusal.MaterialOutOfPalette
            );
        }

        return material;
    }
    /// <summary>Owns required-member lookup and its document refusal shape for scalar and vector readers.</summary>
    private static JsonElement RequireMember(Dictionary<string, JsonElement> members, string key, string context, string shape) {
        if (!members.TryGetValue(
            key: key,
            value: out var element
        )) {
            throw new SdfDocumentException(
                message: $"{context}: '{key}' ({shape}) is required.",
                reason: SdfRefusal.RequiredMemberMissing
            );
        }

        return element;
    }
    // Unknown members Disallow (decision 7) — a document misspelling a key gets a loud refusal, never a silently
    // ignored field.
    private static void RequireNoUnknownMembers(Dictionary<string, JsonElement> members, IReadOnlyList<string> allowed, string context) {
        foreach (var name in members.Keys) {
            var known = false;

            foreach (var candidate in allowed) {
                if (string.Equals(
                    a: candidate,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    known = true;

                    break;
                }
            }

            if (!known) {
                throw new SdfDocumentException(
                    message: $"{context}: unknown member '{name}'.",
                    reason: SdfRefusal.UnknownMember
                );
            }
        }
    }
    // The sign half of decision "inherit the builder's refusals, never its repairs" (see the type remarks): every
    // field this decoder passes to a RequireNonNegative-guarded builder parameter (a shape radius/half-extent/round,
    // or a material channel) is sign-checked HERE, at decode — mirroring RequireScale/ReadSmooth/RequireAxis's own
    // shape (read the base value, then refuse a rule the builder would otherwise repair or reject further downstream).
    private static float RequireNonNegativeFloat(Dictionary<string, JsonElement> members, string key, string context) {
        var element = RequireMember(
            context: context,
            key: key,
            members: members,
            shape: "a number"
        );

        return ReadNonNegativeFloat(
            context: $"{context}.{key}",
            element: element
        );
    }
    private static Vector3 RequireNonNegativeVector3(Dictionary<string, JsonElement> members, string key, string context) {
        var element = RequireMember(
            context: context,
            key: key,
            members: members,
            shape: "a 3-element number array"
        );

        return ReadNonNegativeVector3(
            context: $"{context}.{key}",
            element: element
        );
    }
    // Scale() takes the absolute value and floors near-zero magnitudes to 0.0001 — a REPAIR this front door must
    // refuse instead of forwarding. ReadFloat already guarantees every component is finite.
    private static Vector3 RequireScale(Dictionary<string, JsonElement> members, string context) {
        var scale = RequireVector3(
            context: context,
            key: "scale",
            members: members
        );

        if (
            (scale.X <= 0f) ||
            (scale.Y <= 0f) ||
            (scale.Z <= 0f)
        ) {
            throw new SdfDocumentException(
                message: $"{context}.scale: [{scale.X}, {scale.Y}, {scale.Z}] — every component must be positive; a non-positive scale is silently repaired (absolute value, floored near zero) by the builder rather than honored.",
                reason: SdfRefusal.ScaleNonPositive
            );
        }

        return scale;
    }
    private static Vector3 RequireVector3(Dictionary<string, JsonElement> members, string key, string context) {
        var element = RequireMember(
            context: context,
            key: key,
            members: members,
            shape: "a 3-element number array"
        );

        return ReadVector3(
            context: $"{context}.{key}",
            element: element
        );
    }
    // JsonDocument preserves every occurrence of a repeated name (unlike JsonSerializer's POCO path, which silently
    // keeps the LAST one — decision 7's duplicate-key hazard) — walking EnumerateObject ourselves is what lets a
    // repeat be refused instead of silently resolved to whichever occurrence STJ would have picked.
    private static Dictionary<string, JsonElement> UniqueMembers(JsonElement element, string context) {
        var members = new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject()) {
            if (!members.TryAdd(
                key: property.Name,
                value: property.Value
            )) {
                throw new SdfDocumentException(
                    reason: SdfRefusal.DuplicateKey,
                    message: $"{context}: duplicate key '{property.Name}'."
                );
            }
        }

        return members;
    }

    /// <summary>Decodes and structurally validates <paramref name="utf8Json"/> as a <c>puck.sdf.v1</c> document.
    /// Never calls the builder — see <see cref="Replay"/> for that half.</summary>
    /// <param name="utf8Json">The document's raw UTF-8 bytes, exactly as received.</param>
    /// <returns>The decoded, replayable program.</returns>
    /// <exception cref="SdfDocumentException">The bytes are not valid JSON, the root is not an object, an unknown or
    /// duplicate member appears anywhere, the schema tag is missing or wrong, <c>materials</c> or <c>ops</c> is
    /// omitted (an explicit empty array is legal; omission is not), an array exceeds this prototype's fixed
    /// reservation, an op or enum name is unrecognized, a material reference is outside the document's own palette,
    /// the field-scope push/pop nesting is unbalanced or too deep, a top-level (or push's own) blend is outside the
    /// union family, a number narrows to a non-finite float, a number that must be non-negative (a shape's
    /// radius/half-extent/round, or a material channel) is negative, a scale component is non-positive, a smooth
    /// radius is negative, or a rotation axis does not normalize to a finite unit vector.</exception>
    public static SdfDocumentProgram Decode(ReadOnlyMemory<byte> utf8Json) {
        // Hash identity is over the RECEIVED bytes, computed BEFORE decode (decision 7) — never a re-serialization,
        // never the packed words.
        var contentHash = ComputeHash(utf8Json: utf8Json.Span);

        JsonDocument document;

        try {
            document = JsonDocument.Parse(utf8Json: utf8Json);
        } catch (JsonException exception) {
            throw new SdfDocumentException(
                reason: SdfRefusal.MalformedJson,
                message: $"malformed JSON: {exception.Message}"
            );
        }

        using (document) {
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                throw new SdfDocumentException(
                    message: "document: the root must be a JSON object.",
                    reason: SdfRefusal.RootNotObject
                );
            }

            var root = UniqueMembers(
                element: document.RootElement,
                context: "document"
            );

            RequireNoUnknownMembers(
                allowed: RootMembers,
                context: "document",
                members: root
            );

            if (
                !root.TryGetValue(
                key: "schema",
                value: out var schemaElement
            ) ||
                (schemaElement.ValueKind != JsonValueKind.String) ||
                !string.Equals(
                a: schemaElement.GetString(),
                b: Schema,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                throw new SdfDocumentException(
                    message: $"document: 'schema' must be the string '{Schema}'.",
                    reason: SdfRefusal.SchemaMismatch
                );
            }

            var materials = DecodeMaterials(root: root);
            var ops = DecodeOps(
                root: root,
                materialCount: materials.Count
            );

            return new SdfDocumentProgram(
                ContentHash: contentHash,
                Materials: materials,
                Ops: ops
            );
        }
    }
    /// <summary>Replays a decoded document's materials and ops into <paramref name="builder"/> — every document
    /// material added first (its returned, scope-translated id resolved once and reused for every shape that
    /// references it), then a leading <see cref="SdfProgramBuilder.ResetPoint"/> (so a predecessor's open transform
    /// never leaks into this document — the same rule contribution seams use), then each op in order. Deterministic:
    /// replaying the same <paramref name="program"/> against a fresh builder always emits the same instructions.</summary>
    /// <param name="builder">The shared program builder (already inside this emitter's material scope).</param>
    /// <param name="program">The decoded document to replay.</param>
    /// <exception cref="SdfDocumentException">A decoded call the builder refuses, wrapped with the refusing op's
    /// index and name.</exception>
    public static void Replay(SdfProgramBuilder builder, SdfDocumentProgram program) {
        ArgumentNullException.ThrowIfNull(argument: builder);
        ArgumentNullException.ThrowIfNull(argument: program);

        var materialIds = new int[program.Materials.Count];

        for (var index = 0; (index < program.Materials.Count); index++) {
            // THE SAME INHERITANCE Apply() gives every op (see its remarks): AddMaterial's own refusals (the
            // composed-palette/ScreenMaterialId collision gate — cross-contributor state this decoder cannot
            // pre-check; a negative or non-finite channel is refused earlier, at decode) must reach world.sdf.load
            // as a document rejection, never an unhandled throw.
            try {
                materialIds[index] = builder.AddMaterial(material: program.Materials[index]);
            } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
                throw new SdfDocumentException(
                    reason: SdfRefusal.BuilderRejectedMaterial,
                    message: $"materials[{index}]: {exception.Message}"
                );
            }
        }

        builder.ResetPoint();

        foreach (var op in program.Ops) {
            Apply(
                builder: builder,
                materialIds: materialIds,
                op: op
            );
        }

        // The chain-tail fence: a document may legitimately end mid point-chain (a trailing translate, or a fold like
        // cellJitter), and the next emitter's content must never inherit it - the composed builder is shared.
        _ = builder.ResetPoint();
    }
}
