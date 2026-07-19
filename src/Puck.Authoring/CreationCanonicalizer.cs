using System.Numerics;
using Puck.SdfVm;

namespace Puck.Authoring;

/// <summary>
/// THE strict validate → normalize → canonicalize boundary every <see cref="CreationDocument"/> crosses before it is
/// trusted, persisted, or embedded — the one public pipeline the World UI/editor implementation review's UIE-6
/// finding asked for, replacing <see cref="CreationStore"/>'s old load-time behavior of silently relabeling an
/// absent/foreign schema as the current one. <see cref="CreationStore"/> rides this exclusively: <see cref="CreationStore.Save"/>
/// writes <see cref="Canonicalize"/>'s bytes, and <see cref="CreationStore.Load"/> calls <see cref="ValidateOrThrow"/>
/// then <see cref="Normalize"/> — nothing in this project deserializes a creation without crossing it.
/// </summary>
public static class CreationCanonicalizer {
    private static readonly HashSet<string> KnownMemberNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "schema", "name", "intent", "bakeStyle", "palette", "shapes", "frames", "chains", "cameras", "behavior", "textRuns",
    };

    /// <summary>Validates a document's schema and structural invariants in one pass — every violation is collected
    /// rather than throwing on the first. An absent or foreign <see cref="CreationDocument.Schema"/> short-circuits to
    /// that one violation, since no other check has a defined meaning against an unrecognized document shape.
    /// Missing/malformed references that <see cref="Normalize"/> can safely self-heal (a chain or face naming a shape
    /// that no longer exists — the post-edit-deletion case) are deliberately NOT validation failures; only invariants
    /// normalization cannot repair without silently discarding meaning (duplicate ids, non-finite numerics, a
    /// palette overflowing its 16 slots, an orphaned frame transform, a feed-name collision) are.</summary>
    /// <param name="document">The document to validate, as deserialized — not yet normalized.</param>
    /// <returns>Every violation found; empty when the document is a valid <c>puck.creation.v1</c> value.</returns>
    public static IReadOnlyList<DocumentValidationError> Validate(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (DocumentCanonicalizer.SchemaViolationMessage(declared: document.Schema, recognized: CreationDocument.CurrentSchema) is { } schemaViolation) {
            return [new DocumentValidationError(Path: "schema", Message: schemaViolation)];
        }

        var errors = new List<DocumentValidationError>();
        var shapeIds = new HashSet<int>();

        for (var i = 0; i < (document.Shapes?.Count ?? 0); i++) {
            var shape = document.Shapes![i];

            if (!shapeIds.Add(item: shape.Id)) {
                errors.Add(item: new(Path: $"shapes[{i}].id", Message: $"duplicate shape id {shape.Id}."));
            }
            if (!IsFinite(vector: shape.Position)) {
                errors.Add(item: new(Path: $"shapes[{i}].position", Message: "position is non-finite."));
            }
            if (!IsFinite(vector: shape.Scale)) {
                errors.Add(item: new(Path: $"shapes[{i}].scale", Message: "scale is non-finite."));
            }
            if (!IsFinite(quaternion: shape.Rotation)) {
                errors.Add(item: new(Path: $"shapes[{i}].rotation", Message: "rotation is non-finite."));
            }
        }

        ValidatePalette(document: document, errors: errors);
        ValidateFrames(document: document, errors: errors, shapeIds: shapeIds);
        ValidateChains(document: document, errors: errors);
        ValidateCameras(document: document, errors: errors);
        ValidateBehavior(document: document, errors: errors);
        ValidateTextRuns(document: document, errors: errors);
        ValidateExtensions(document: document, errors: errors);

        return errors;
    }

    /// <summary>Runs <see cref="Validate"/> and throws when it finds anything.</summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="source">An optional source label (a file path or save handle) for the exception message.</param>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a
    /// structural invariant.</exception>
    public static void ValidateOrThrow(CreationDocument document, string? source = null) {
        var errors = Validate(document: document);

        if (errors.Count > 0) {
            throw new DocumentValidationException(errors: errors, source: source);
        }
    }

    /// <summary>Normalizes an already-schema-valid document: clamps/defaults every optional member so the in-memory
    /// model never sees a null or an out-of-range value it has to reason about (the load-time half of the document
    /// doctrine). Idempotent — <c>Normalize(Normalize(x))</c> equals <c>Normalize(x)</c> — which is what makes a
    /// saved file's own reload round-trip byte-identically. Does NOT itself validate; callers cross
    /// <see cref="ValidateOrThrow"/> first (<see cref="Canonicalize"/> always does).</summary>
    /// <param name="document">The document to normalize.</param>
    /// <returns>The normalized document.</returns>
    public static CreationDocument Normalize(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var shapes = new List<ShapeDocument>(capacity: (document.Shapes?.Count ?? 0));
        var shapeIds = new HashSet<int>();

        foreach (var shape in (document.Shapes ?? [])) {
            shapes.Add(item: shape with {
                Bend = Math.Clamp(value: (shape.Bend ?? 0f), max: ShapeDocument.MaxBend, min: -ShapeDocument.MaxBend),
                Blend = (shape.Blend ?? SdfBlendOp.Union),
                Dilate = Math.Clamp(value: (shape.Dilate ?? 0f), max: ShapeDocument.MaxDilate, min: 0f),
                Group = Math.Max(val1: (shape.Group ?? 0), val2: 0),
                Material = Math.Clamp(value: (shape.Material ?? 0), max: (CreationDocument.PaletteSize - 1), min: 0),
                Mirror = (shape.Mirror ?? false),
                Onion = Math.Clamp(value: (shape.Onion ?? 0f), max: ShapeDocument.MaxOnion, min: 0f),
                Rotation = ((shape.Rotation == default) ? Quaternion.Identity : Quaternion.Normalize(value: shape.Rotation)),
                Scale = ((shape.Scale == default) ? Vector3.One : shape.Scale),
                Smooth = Math.Clamp(value: (shape.Smooth ?? 0f), max: ShapeDocument.MaxSmooth, min: 0f),
                Twist = Math.Clamp(value: (shape.Twist ?? 0f), max: ShapeDocument.MaxTwist, min: -ShapeDocument.MaxTwist),
            });
            _ = shapeIds.Add(item: shape.Id);
        }

        List<ChainDocument>? chains = null;

        if (document.Chains is { Count: > 0 } sourceChains) {
            chains = new List<ChainDocument>(capacity: sourceChains.Count);

            foreach (var chain in sourceChains) {
                // A chain naming any missing shape id is dropped outright — its rest geometry can never be
                // recaptured against a shape that is not there, and a partial chain has no sound IK meaning.
                if ((chain.Shapes is not { Count: > 0 } memberIds) || !memberIds.All(predicate: shapeIds.Contains)) {
                    continue;
                }

                var kind = (chain.Kind ?? ((memberIds.Count == 3) ? ChainDocument.KindLimb : ChainDocument.KindSpine));

                // "limb" is a structural invariant: exactly 3 shapes (2 bones) or it demotes to "spine" — the spine
                // solver degrades gracefully to any length, so this can never leave a chain unsolvable.
                if (string.Equals(a: kind, b: ChainDocument.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase) && (memberIds.Count != 3)) {
                    kind = ChainDocument.KindSpine;
                }

                chains.Add(item: chain with { Kind = kind });
            }
        }

        return (document with {
            BakeStyle = (string.Equals(a: document.BakeStyle, b: "bold", comparisonType: StringComparison.OrdinalIgnoreCase) ? "bold" : "classic"),
            Behavior = NormalizeBehavior(behavior: document.Behavior, shapeIds: shapeIds),
            Cameras = NormalizeCreationCameras(cameras: document.Cameras, shapeIds: shapeIds),
            Chains = chains,
            Intent = (document.Intent ?? CreatorIntent.Object),
            Name = CreationStore.Sanitize(name: (document.Name ?? "creation")),
            Schema = CreationDocument.CurrentSchema,
            Shapes = shapes,
            TextRuns = NormalizeTextRuns(textRuns: document.TextRuns),
        });
    }

    /// <summary>THE full pipeline: validates schema + structural invariants (throwing on either), normalizes the
    /// self-heal, then serializes to canonical UTF-8 bytes and hashes them through
    /// <see cref="DocumentCanonicalizer.Canonicalize"/>. Two calls against value-equal input documents always produce
    /// byte-identical bytes and therefore the same hash — cite THIS guarantee wherever a creation's identity is pinned
    /// (the §D6 world-row hash).</summary>
    /// <param name="document">The document to canonicalize.</param>
    /// <param name="source">An optional source label (a file path or save handle) for a validation-failure message.</param>
    /// <returns>The validated, normalized document plus its canonical bytes and hash.</returns>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a
    /// structural invariant.</exception>
    public static CanonicalDocument<CreationDocument> Canonicalize(CreationDocument document, string? source = null) {
        ValidateOrThrow(document: document, source: source);

        return DocumentCanonicalizer.Canonicalize(document: Normalize(document: document));
    }

    // The default extrude half-depth a text run relies on when it declares none, and the floors every run clamps to —
    // a zero-depth glyph slab has no relief (it would be coplanar with the surface), so the depth is floored positive.
    private const float DefaultTextDepth = 0.02f;
    private const float MinTextDepth = 0.001f;
    private const float MinTextEmHeight = 0.01f;

    // Text runs normalize to a canonical mode name, a clamped material slot / positive depth+em, and a normalized
    // rotation; an empty-text run drops (it carries no geometry). A fully absent list collapses to null so a text-free
    // creation round-trips byte-identically (the member is JsonIgnore-when-null too).
    private static List<TextRunDocument>? NormalizeTextRuns(IReadOnlyList<TextRunDocument>? textRuns) {
        if (textRuns is not { Count: > 0 } source) {
            return null;
        }

        var normalized = new List<TextRunDocument>(capacity: source.Count);

        foreach (var run in source) {
            if (run.Text is not { Length: > 0 } text) {
                continue;
            }

            normalized.Add(item: run with {
                Depth = MathF.Max(x: (run.Depth ?? DefaultTextDepth), y: MinTextDepth),
                EmHeight = MathF.Max(x: run.EmHeight, y: MinTextEmHeight),
                Material = Math.Clamp(value: (run.Material ?? 0), max: (CreationDocument.PaletteSize - 1), min: 0),
                Mode = (string.Equals(a: run.Mode, b: TextRunDocument.ModeEngrave, comparisonType: StringComparison.OrdinalIgnoreCase) ? TextRunDocument.ModeEngrave : TextRunDocument.ModeEmboss),
                Rotation = ((run.Rotation == default) ? Quaternion.Identity : Quaternion.Normalize(value: run.Rotation)),
                Text = text,
            });
        }

        return ((normalized.Count > 0) ? normalized : null);
    }

    // A creation camera rides one of the creation's own shapes; a camera naming a missing shape (or carrying a
    // non-finite offset) is dropped, mirroring the chain rule. Optional angles/fov coerce to finite; the feed name
    // defaults to null (the consumer falls back to the eye id).
    private static List<CreationCameraDocument>? NormalizeCreationCameras(IReadOnlyList<CreationCameraDocument>? cameras, HashSet<int> shapeIds) {
        if (cameras is not { Count: > 0 } sourceCameras) {
            return null;
        }

        var normalized = new List<CreationCameraDocument>(capacity: sourceCameras.Count);

        foreach (var camera in sourceCameras) {
            if (!shapeIds.Contains(item: camera.ShapeId) ||
                !float.IsFinite(f: camera.Position.X) || !float.IsFinite(f: camera.Position.Y) || !float.IsFinite(f: camera.Position.Z)) {
                continue;
            }

            normalized.Add(item: camera with {
                Feed = ((camera.Feed is { Length: > 0 } feed) ? feed : null),
                Focus = (((camera.Focus is { } focus) && float.IsFinite(f: focus)) ? (float?)MathF.Max(x: focus, y: 0.01f) : null),
                Fov = (((camera.Fov is { } fov) && float.IsFinite(f: fov)) ? (float?)Math.Clamp(value: fov, max: 170f, min: 1f) : null),
                Pitch = (((camera.Pitch is { } pitch) && float.IsFinite(f: pitch)) ? (float?)Math.Clamp(value: pitch, max: 85f, min: -85f) : null),
                Yaw = (((camera.Yaw is { } yaw) && float.IsFinite(f: yaw)) ? (float?)yaw : null),
            });
        }

        return ((normalized.Count > 0) ? normalized : null);
    }

    // The behavior manifest normalizes to a canonical locomotion member name and drops a face/sound naming a missing
    // shape. A manifest that is entirely default (walk, no faces, no sounds) collapses to null so a creation without
    // behavioral facts round-trips byte-identically to one that never carried the manifest at all.
    private static CreationBehaviorDocument? NormalizeBehavior(CreationBehaviorDocument? behavior, HashSet<int> shapeIds) {
        if (behavior is null) {
            return null;
        }

        var locomotion = (behavior.Locomotion?.ToLowerInvariant() switch {
            "swim" => "swim",
            "hover" => "hover",
            _ => "walk",
        });

        List<CreationFaceDocument>? faces = null;

        if (behavior.Faces is { Count: > 0 } sourceFaces) {
            faces = new List<CreationFaceDocument>(capacity: sourceFaces.Count);

            foreach (var face in sourceFaces) {
                // A face may name a specific shape surface or the creation's canonical face (null/-1). A named shape
                // that is missing drops the face (its surface is not there).
                if ((face.ShapeId is { } shapeId) && (shapeId >= 0) && !shapeIds.Contains(item: shapeId)) {
                    continue;
                }

                faces.Add(item: face with {
                    DefaultSource = ((face.DefaultSource is { Length: > 0 } source) ? source : null),
                    Name = ((face.Name is { Length: > 0 } name) ? name : "face"),
                    ShapeId = (((face.ShapeId is { } id) && (id >= 0)) ? (int?)id : null),
                });
            }
        }

        var sounds = NormalizeSounds(sounds: behavior.Sounds, shapeIds: shapeIds);

        // Fully default → null (byte-stable round-trip with a manifest-less creation).
        if (string.Equals(a: locomotion, b: "walk", comparisonType: StringComparison.Ordinal) && (faces is not { Count: > 0 }) && (sounds is not { Count: > 0 })) {
            return null;
        }

        return new CreationBehaviorDocument(Faces: faces, Locomotion: locomotion, Sounds: sounds);
    }

    // A sound naming a missing shape drops (the faces rule — its emission point is not there); the survivors carry a
    // defaulted name, a clamped level, and the inline patch normalized through the synth family's own pipeline (so
    // the creation hash always covers the patch's canonical form). An empty result collapses to null (byte-stable
    // round-trip with a sound-free creation).
    private static List<CreationSoundDocument>? NormalizeSounds(IReadOnlyList<CreationSoundDocument>? sounds, HashSet<int> shapeIds) {
        if (sounds is not { Count: > 0 } source) {
            return null;
        }

        var normalized = new List<CreationSoundDocument>(capacity: source.Count);

        foreach (var sound in source) {
            if ((sound.ShapeId is { } shapeId) && (shapeId >= 0) && !shapeIds.Contains(item: shapeId)) {
                continue;
            }

            normalized.Add(item: sound with {
                Level = Math.Clamp(value: (sound.Level ?? 1f), max: CreationSoundDocument.MaxLevel, min: 0f),
                Name = ((sound.Name is { Length: > 0 } name) ? name : "sound"),
                Patch = SynthPatchCanonicalizer.Normalize(document: sound.Patch),
                ShapeId = (((sound.ShapeId is { } id) && (id >= 0)) ? (int?)id : null),
            });
        }

        return ((normalized.Count > 0) ? normalized : null);
    }

    private static void ValidatePalette(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Palette is not { Count: > 0 } palette) {
            return;
        }

        if (palette.Count > CreationDocument.PaletteSize) {
            errors.Add(item: new(Path: "palette", Message: $"{palette.Count} entries exceeds the {CreationDocument.PaletteSize}-slot palette."));
        }

        for (var i = 0; i < palette.Count; i++) {
            var entry = palette[i];

            if (!IsFinite(vector: entry.Albedo)) {
                errors.Add(item: new(Path: $"palette[{i}].albedo", Message: "albedo is non-finite."));
            }
            if ((entry.Emissive is { } emissive) && !float.IsFinite(f: emissive)) {
                errors.Add(item: new(Path: $"palette[{i}].emissive", Message: "emissive is non-finite."));
            }
            if ((entry.Specular is { } specular) && !float.IsFinite(f: specular)) {
                errors.Add(item: new(Path: $"palette[{i}].specular", Message: "specular is non-finite."));
            }
            if ((entry.Shininess is { } shininess) && !float.IsFinite(f: shininess)) {
                errors.Add(item: new(Path: $"palette[{i}].shininess", Message: "shininess is non-finite."));
            }
        }
    }

    private static void ValidateFrames(CreationDocument document, List<DocumentValidationError> errors, HashSet<int> shapeIds) {
        if (document.Frames is not { Count: > 0 } frames) {
            return;
        }

        for (var i = 0; i < frames.Count; i++) {
            var frame = frames[i];

            if (frame.Name is not { Length: > 0 }) {
                errors.Add(item: new(Path: $"frames[{i}].name", Message: "a frame must be named."));
            }

            var seenShapeIds = new HashSet<int>();

            for (var j = 0; j < (frame.Transforms?.Count ?? 0); j++) {
                var transform = frame.Transforms![j];

                // Unlike a chain/camera/face's stale reference (self-healed by dropping — the post-edit-deletion
                // case), a frame transform for a shape that no longer exists is unrecoverable captured animation
                // data: there is no "current pose" to fall back to, so this rejects rather than silently vanishing.
                if (!shapeIds.Contains(item: transform.Id)) {
                    errors.Add(item: new(Path: $"frames[{i}].transforms[{j}].id", Message: $"references missing shape id {transform.Id}."));
                }
                if (!seenShapeIds.Add(item: transform.Id)) {
                    errors.Add(item: new(Path: $"frames[{i}].transforms[{j}].id", Message: $"duplicate transform for shape id {transform.Id}."));
                }
                if (!IsFinite(vector: transform.Position)) {
                    errors.Add(item: new(Path: $"frames[{i}].transforms[{j}].position", Message: "position is non-finite."));
                }
                if (!IsFinite(vector: transform.Scale)) {
                    errors.Add(item: new(Path: $"frames[{i}].transforms[{j}].scale", Message: "scale is non-finite."));
                }
                if (!IsFinite(quaternion: transform.Rotation)) {
                    errors.Add(item: new(Path: $"frames[{i}].transforms[{j}].rotation", Message: "rotation is non-finite."));
                }
            }
        }
    }

    private static void ValidateChains(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Chains is not { Count: > 0 } chains) {
            return;
        }

        var chainIds = new HashSet<int>();

        for (var i = 0; i < chains.Count; i++) {
            if (!chainIds.Add(item: chains[i].Id)) {
                errors.Add(item: new(Path: $"chains[{i}].id", Message: $"duplicate chain id {chains[i].Id}."));
            }
        }
    }

    private static void ValidateCameras(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Cameras is not { Count: > 0 } cameras) {
            return;
        }

        var cameraIds = new HashSet<int>();
        var feedNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < cameras.Count; i++) {
            var camera = cameras[i];

            if (!cameraIds.Add(item: camera.Id)) {
                errors.Add(item: new(Path: $"cameras[{i}].id", Message: $"duplicate camera id {camera.Id}."));
            }
            if (!IsFinite(vector: camera.Position)) {
                errors.Add(item: new(Path: $"cameras[{i}].position", Message: "position is non-finite."));
            }

            var feed = ((camera.Feed is { Length: > 0 } name) ? name : camera.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));

            if (!feedNames.Add(item: feed)) {
                errors.Add(item: new(Path: $"cameras[{i}].feed", Message: $"feed name '{feed}' collides with another camera's feed."));
            }
        }
    }

    private static void ValidateBehavior(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Behavior?.Faces is { Count: > 0 } faces) {
            var faceNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < faces.Count; i++) {
                var name = ((faces[i].Name is { Length: > 0 } faceName) ? faceName : "face");

                if (!faceNames.Add(item: name)) {
                    errors.Add(item: new(Path: $"behavior.faces[{i}].name", Message: $"face name '{name}' collides with another face."));
                }
            }
        }

        ValidateSounds(document: document, errors: errors);
    }

    // The declared sounds: unique names, a finite level/radius, and the INLINE puck.synth.v1 patch validated through
    // the synth family's OWN canonicalizer (the one pipeline — never a re-implementation), its violations re-pathed
    // under this creation. A sound naming a missing shape is NOT a failure — Normalize drops it (the faces rule).
    private static void ValidateSounds(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Behavior?.Sounds is not { Count: > 0 } sounds) {
            return;
        }

        var soundNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sounds.Count; i++) {
            var sound = sounds[i];
            var name = ((sound.Name is { Length: > 0 } soundName) ? soundName : "sound");

            if (!soundNames.Add(item: name)) {
                errors.Add(item: new(Path: $"behavior.sounds[{i}].name", Message: $"sound name '{name}' collides with another sound."));
            }

            if ((sound.Level is { } level) && (!float.IsFinite(f: level) || (level < 0f) || (level > CreationSoundDocument.MaxLevel))) {
                errors.Add(item: new(Path: $"behavior.sounds[{i}].level", Message: $"level {level} is outside [0, {CreationSoundDocument.MaxLevel}]."));
            }

            if ((sound.Radius is { } radius) && (!float.IsFinite(f: radius) || (radius <= 0f))) {
                errors.Add(item: new(Path: $"behavior.sounds[{i}].radius", Message: "radius must be finite and positive."));
            }

            if (sound.Patch is null) {
                errors.Add(item: new(Path: $"behavior.sounds[{i}].patch", Message: "a sound requires an inline puck.synth.v1 patch."));

                continue;
            }

            foreach (var violation in SynthPatchCanonicalizer.Validate(document: sound.Patch)) {
                errors.Add(item: new(Path: $"behavior.sounds[{i}].patch.{violation.Path}", Message: violation.Message));
            }
        }
    }

    private static void ValidateTextRuns(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.TextRuns is not { Count: > 0 } runs) {
            return;
        }

        for (var i = 0; i < runs.Count; i++) {
            var run = runs[i];

            if (!IsFinite(vector: run.Position)) {
                errors.Add(item: new(Path: $"textRuns[{i}].position", Message: "position is non-finite."));
            }
            if (!IsFinite(quaternion: run.Rotation)) {
                errors.Add(item: new(Path: $"textRuns[{i}].rotation", Message: "rotation is non-finite."));
            }
            if (!float.IsFinite(f: run.EmHeight)) {
                errors.Add(item: new(Path: $"textRuns[{i}].emHeight", Message: "emHeight is non-finite."));
            }
            if ((run.Depth is { } depth) && !float.IsFinite(f: depth)) {
                errors.Add(item: new(Path: $"textRuns[{i}].depth", Message: "depth is non-finite."));
            }
        }
    }

    private static void ValidateExtensions(CreationDocument document, List<DocumentValidationError> errors) =>
        DocumentCanonicalizer.ValidateExtensions(
            addError: (path, message) => errors.Add(item: new(Path: path, Message: message)),
            extensions: document.Extensions,
            knownMemberNames: KnownMemberNames
        );

    private static bool IsFinite(Vector3 vector) =>
        (float.IsFinite(f: vector.X) && float.IsFinite(f: vector.Y) && float.IsFinite(f: vector.Z));
    private static bool IsFinite(Quaternion quaternion) =>
        (float.IsFinite(f: quaternion.X) && float.IsFinite(f: quaternion.Y) && float.IsFinite(f: quaternion.Z) && float.IsFinite(f: quaternion.W));
}
