using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>
/// THE strict validate → normalize → canonicalize boundary every <see cref="CreationDocument"/> crosses before it is
/// trusted, persisted, or embedded — the one public canonicalization pipeline: strict schema, no silent relabel;
/// doc and hash always come from the same call. Every consumer rides this exclusively — a world's compose boundary
/// canonicalizes an upserted creation row through <see cref="Canonicalize"/> and validates a loaded one through
/// <see cref="Validate"/>; nothing anywhere deserializes a creation without crossing it.
/// </summary>
public static class CreationCanonicalizer {
    // The default extrude half-depth a text run relies on when it declares none, and the floors every run clamps to —
    // a zero-depth glyph slab has no relief (it would be coplanar with the surface), so the depth is floored positive.
    private const float DefaultTextDepth = 0.02f;
    private const float MinTextDepth = 0.001f;
    private const float MinTextEmHeight = 0.01f;

    private static readonly HashSet<string> KnownMemberNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "schema", "name", "palette", "shapes", "frames", "chains", "cameras", "behavior", "textRuns", "parts",
        "noise", "drivers", "effectors",
    };

    private static bool IsFinite(Vector3 vector) =>
        (float.IsFinite(f: vector.X) && float.IsFinite(f: vector.Y) && float.IsFinite(f: vector.Z));
    private static bool IsFinite(Quaternion quaternion) =>
        (float.IsFinite(f: quaternion.X) && float.IsFinite(f: quaternion.Y) && float.IsFinite(f: quaternion.Z) && float.IsFinite(f: quaternion.W));
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
                if (
                    (face.ShapeId is { } shapeId) &&
                    (shapeId >= 0) &&
                    !shapeIds.Contains(item: shapeId)
                ) {
                    continue;
                }

                faces.Add(item: face with {
                    DefaultSource = ((face.DefaultSource is { Length: > 0 } source)
                    ? source
                    : null),
                    Name = ((face.Name is { Length: > 0 } name)
                    ? name
                    : "face"),
                    ShapeId = (((face.ShapeId is { } id) && (id >= 0))
                    ? (int?)id
                    : null),
                });
            }
        }

        var sounds = NormalizeSounds(
            sounds: behavior.Sounds,
            shapeIds: shapeIds
        );

        // Fully default → null (byte-stable round-trip with a manifest-less creation).
        if (
            string.Equals(
            a: locomotion,
            b: "walk",
            comparisonType: StringComparison.Ordinal
        ) &&
            (faces is not { Count: > 0 }) &&
            (sounds is not { Count: > 0 })
        ) {
            return null;
        }

        return new CreationBehaviorDocument(
            Faces: faces,
            Locomotion: locomotion,
            Sounds: sounds
        );
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
            if (
                !shapeIds.Contains(item: camera.ShapeId) ||
                !float.IsFinite(f: camera.Position.X) ||
                !float.IsFinite(f: camera.Position.Y) ||
                !float.IsFinite(f: camera.Position.Z)
            ) {
                continue;
            }

            normalized.Add(item: camera with {
                Feed = ((camera.Feed is { Length: > 0 } feed)
                ? feed
                : null),
                Focus = (((camera.Focus is { } focus) && float.IsFinite(f: focus))
                ? (float?)MathF.Max(
                    x: focus,
                    y: 0.01f
                )
                : null),
                Fov = (((camera.Fov is { } fov) && float.IsFinite(f: fov))
                ? (float?)Math.Clamp(
                    max: 170f,
                    min: 1f,
                    value: fov
                )
                : null),
                Pitch = (((camera.Pitch is { } pitch) && float.IsFinite(f: pitch))
                ? (float?)Math.Clamp(
                    max: 85f,
                    min: -85f,
                    value: pitch
                )
                : null),
                Yaw = (((camera.Yaw is { } yaw) && float.IsFinite(f: yaw))
                ? (float?)yaw
                : null),
            });
        }

        return ((normalized.Count > 0)
            ? normalized
            : null
        );
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
            if (
                (sound.ShapeId is { } shapeId) &&
                (shapeId >= 0) &&
                !shapeIds.Contains(item: shapeId)
            ) {
                continue;
            }

            normalized.Add(item: sound with {
                Level = Math.Clamp(
                value: (sound.Level ?? 1f),
                max: CreationSoundDocument.MaxLevel,
                min: 0f
            ),
                Name = ((sound.Name is { Length: > 0 } name)
                ? name
                : "sound"),
                Patch = SynthPatchCanonicalizer.Normalize(document: sound.Patch),
                ShapeId = (((sound.ShapeId is { } id) && (id >= 0))
                ? (int?)id
                : null),
            });
        }

        return ((normalized.Count > 0)
            ? normalized
            : null
        );
    }
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
                Align = (string.Equals(
                a: run.Align,
                b: TextRunDocument.AlignCenter,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )
                ? TextRunDocument.AlignCenter
                : (string.Equals(
                    a: run.Align,
                    b: TextRunDocument.AlignRight,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )
                    ? TextRunDocument.AlignRight
                    : null)),
                Depth = MathF.Max(
                x: (run.Depth ?? DefaultTextDepth),
                y: MinTextDepth
            ),
                EmHeight = MathF.Max(
                x: run.EmHeight,
                y: MinTextEmHeight
            ),
                LineSpacing = ((run.LineSpacing is { } lineSpacing)
                ? Math.Clamp(
                    max: TextRunDocument.MaxLineSpacing,
                    min: TextRunDocument.MinLineSpacing,
                    value: lineSpacing
                )
                : null),
                Material = Math.Clamp(
                value: (run.Material ?? 0),
                max: (CreationDocument.PaletteSize - 1),
                min: 0
            ),
                MaxWidth = (((run.MaxWidth is { } maxWidth) && (maxWidth > 0f))
                ? maxWidth
                : null),
                Mode = (string.Equals(
                a: run.Mode,
                b: TextRunDocument.ModeEngrave,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )
                ? TextRunDocument.ModeEngrave
                : TextRunDocument.ModeEmboss),
                Rotation = ((run.Rotation == default)
                ? Quaternion.Identity
                : Quaternion.Normalize(value: run.Rotation)),
                Text = text,
                Tracking = ((run.Tracking is { } tracking)
                ? Math.Clamp(
                    max: TextRunDocument.MaxTracking,
                    min: -TextRunDocument.MaxTracking,
                    value: tracking
                )
                : null),
            });
        }

        return ((normalized.Count > 0)
            ? normalized
            : null
        );
    }
    // A creation's name doubles as its handle wherever one is needed (a world row id, a save file stem), so
    // normalization narrows it to letters, digits, dashes, and underscores here — at the ONE place a creation's name
    // is decided — rather than leaving every consumer to re-derive a safe form of it.
    private static string SanitizeName(string name) {
        var builder = new System.Text.StringBuilder(capacity: name.Length);

        foreach (var character in name) {
            _ = builder.Append(value: ((char.IsAsciiLetterOrDigit(c: character) || (character is '-' or '_'))
                ? character
                : '-'));
        }

        return ((builder.Length > 0)
            ? builder.ToString()
            : "creation"
        );
    }
    private static void ValidateBehavior(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Behavior?.Faces is { Count: > 0 } faces) {
            var faceNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

            for (var i = 0; (i < faces.Count); i++) {
                var name = ((faces[i].Name is { Length: > 0 } faceName)
                    ? faceName
                    : "face"
                );

                if (!faceNames.Add(item: name)) {
                    errors.Add(item: new(
                        Message: $"face name '{name}' collides with another face.",
                        Path: $"behavior.faces[{i}].name"
                    ));
                }
            }
        }

        ValidateSounds(
            document: document,
            errors: errors
        );
    }
    private static void ValidateCameras(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Cameras is not { Count: > 0 } cameras) {
            return;
        }

        var cameraIds = new HashSet<int>();
        var feedNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        for (var i = 0; (i < cameras.Count); i++) {
            var camera = cameras[i];

            if (!cameraIds.Add(item: camera.Id)) {
                errors.Add(item: new(
                    Path: $"cameras[{i}].id",
                    Message: $"duplicate camera id {camera.Id}."
                ));
            }
            if (!IsFinite(vector: camera.Position)) {
                errors.Add(item: new(
                    Message: "position is non-finite.",
                    Path: $"cameras[{i}].position"
                ));
            }

            var feed = ((camera.Feed is { Length: > 0 } name)
                ? name
                : camera.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
            );

            if (!feedNames.Add(item: feed)) {
                errors.Add(item: new(
                    Message: $"feed name '{feed}' collides with another camera's feed.",
                    Path: $"cameras[{i}].feed"
                ));
            }
        }
    }
    private static void ValidateChains(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Chains is not { Count: > 0 } chains) {
            return;
        }

        var chainIds = new HashSet<int>();

        for (var i = 0; (i < chains.Count); i++) {
            if (!chainIds.Add(item: chains[i].Id)) {
                errors.Add(item: new(
                    Path: $"chains[{i}].id",
                    Message: $"duplicate chain id {chains[i].Id}."
                ));
            }
        }
    }
    private static void ValidateDrivers(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Drivers is not { Count: > 0 } drivers) {
            return;
        }

        if (drivers.Count > CreationDocument.MaxDrivers) {
            errors.Add(item: new(
                Message: $"{drivers.Count} entries exceeds the {CreationDocument.MaxDrivers}-driver list.",
                Path: "drivers"
            ));
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var i = 0; (i < drivers.Count); i++) {
            var driver = drivers[i];

            if (string.IsNullOrWhiteSpace(value: driver.Name)) {
                errors.Add(item: new(
                    Message: "name is empty.",
                    Path: $"drivers[{i}].name"
                ));
            } else if (!names.Add(item: driver.Name)) {
                errors.Add(item: new(
                    Message: $"duplicate driver name '{driver.Name}'.",
                    Path: $"drivers[{i}].name"
                ));
            }
            if (!CreationDriverDocument.IsSignal(signal: driver.Signal)) {
                errors.Add(item: new(
                    Message: $"signal '{driver.Signal}' is not recognized; the signals are '{CreationDriverDocument.SignalPlanarTravel}', '{CreationDriverDocument.SignalTravel}', '{CreationDriverDocument.SignalTime}', '{CreationDriverDocument.SignalSpeed}', '{CreationDriverDocument.SignalVerticalSpeed}', and '{CreationDriverDocument.SignalTurnRate}'.",
                    Path: $"drivers[{i}].signal"
                ));
            }
            if (
                (driver.Cadence.Reference is null) &&
                (!float.IsFinite(f: driver.Cadence.Value) ||
                (MathF.Abs(x: driver.Cadence.Value) > CreationDriverDocument.MaxCadence))
            ) {
                errors.Add(item: new(
                    Message: $"cadence {driver.Cadence.Value} is outside ±{CreationDriverDocument.MaxCadence}.",
                    Path: $"drivers[{i}].cadence"
                ));
            }

            ValidateGate(
                errors: errors,
                gate: driver.When,
                path: $"drivers[{i}].when"
            );
        }
    }
    // A bone's index in `shapes`, or −1. Names are the same handle `parent` resolves against, so the two agree by
    // construction.
    private static int ShapeIndex(IReadOnlyList<ShapeDocument> shapes, string? name) {
        if (name is null) {
            return -1;
        }
        for (var index = 0; (index < shapes.Count); index++) {
            if (string.Equals(
                a: shapes[index].Name?.Value,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return index;
            }
        }

        return -1;
    }
    // Whether `descendant` reaches `ancestor` by walking `parent`. A parent is validated to be declared earlier, so
    // the walk strictly decreases and terminates even on a document that bypassed that check.
    private static bool DescendsFrom(IReadOnlyList<ShapeDocument> shapes, int descendant, int ancestor) {
        var cursor = descendant;

        while (cursor > ancestor) {
            var next = ShapeIndex(
                name: shapes[cursor].Parent,
                shapes: shapes
            );

            if (next >= cursor) {
                return false;
            }

            cursor = next;
        }

        return (cursor == ancestor);
    }
    private static void ValidateEffectors(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Effectors is not { Count: > 0 } effectors) {
            return;
        }

        if (effectors.Count > CreationDocument.MaxEffectors) {
            errors.Add(item: new(
                Message: $"{effectors.Count} entries exceeds the {CreationDocument.MaxEffectors}-effector list.",
                Path: "effectors"
            ));
        }

        var shapes = (document.Shapes ?? []);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var i = 0; (i < effectors.Count); i++) {
            var effector = effectors[i];
            var path = $"effectors[{i}]";

            if (string.IsNullOrWhiteSpace(value: effector.Name)) {
                errors.Add(item: new(
                    Message: "name is empty.",
                    Path: $"{path}.name"
                ));
            } else if (!names.Add(item: effector.Name)) {
                errors.Add(item: new(
                    Message: $"duplicate effector name '{effector.Name}'.",
                    Path: $"{path}.name"
                ));
            }

            ValidateGate(
                errors: errors,
                gate: effector.When,
                path: $"{path}.when"
            );

            if (
                (effector.Weight is { Reference: null } weight) &&
                (!float.IsFinite(f: weight.Value) ||
                (weight.Value < 0f) ||
                (weight.Value > 1f))
            ) {
                errors.Add(item: new(
                    Message: $"weight {weight.Value} is outside [0, 1].",
                    Path: $"{path}.weight"
                ));
            }

            ValidateEffectorChain(
                effector: effector,
                errors: errors,
                path: path,
                shapes: shapes
            );
            ValidateEffectorTarget(
                errors: errors,
                path: $"{path}.target",
                target: effector.Target
            );
            ValidateEffectorPlant(
                document: document,
                errors: errors,
                path: $"{path}.plant",
                plant: effector.Plant
            );
        }
    }
    private static void ValidateEffectorChain(IReadOnlyList<ShapeDocument> shapes, CreationEffectorDocument effector, List<DocumentValidationError> errors, string path) {
        var chain = (effector.Chain ?? []);

        if (chain.Count < CreationEffectorDocument.MinChainBones) {
            errors.Add(item: new(
                Message: $"{chain.Count} bones is fewer than the {CreationEffectorDocument.MinChainBones} a chain needs; one bone has no joint to bend at, which a swing already says.",
                Path: $"{path}.chain"
            ));

            return;
        }
        if (chain.Count > CreationEffectorDocument.MaxChainBones) {
            errors.Add(item: new(
                Message: $"{chain.Count} bones exceeds the {CreationEffectorDocument.MaxChainBones}-bone chain.",
                Path: $"{path}.chain"
            ));

            return;
        }

        var bones = new HashSet<string>(comparer: StringComparer.Ordinal);
        var previous = -1;
        var resolved = true;

        for (var i = 0; (i < chain.Count); i++) {
            var index = ShapeIndex(
                name: chain[i],
                shapes: shapes
            );

            if (index < 0) {
                errors.Add(item: new(
                    Message: $"names no shape '{chain[i]}'.",
                    Path: $"{path}.chain[{i}]"
                ));
                resolved = false;

                continue;
            }
            if (!bones.Add(item: chain[i])) {
                errors.Add(item: new(
                    Message: $"duplicate bone '{chain[i]}'.",
                    Path: $"{path}.chain[{i}]"
                ));
                resolved = false;

                continue;
            }
            if (shapes[index].Domain is { Count: > 0 }) {
                errors.Add(item: new(
                    Message: $"bone '{chain[i]}' carries domain operators, so it rides the placement root's transform and a solve could not move it.",
                    Path: $"{path}.chain[{i}]"
                ));
            }
            if (
                (previous >= 0) &&
                !DescendsFrom(
                ancestor: previous,
                descendant: index,
                shapes: shapes
            )
            ) {
                errors.Add(item: new(
                    Message: $"bone '{chain[i]}' does not descend from '{chain[i - 1]}' through parent, so the chain is not one limb.",
                    Path: $"{path}.chain[{i}]"
                ));
                resolved = false;
            }

            previous = index;
        }

        var tip = ShapeIndex(
            name: effector.Tip,
            shapes: shapes
        );

        if (tip < 0) {
            errors.Add(item: new(
                Message: $"names no shape '{effector.Tip}'.",
                Path: $"{path}.tip"
            ));
        } else if (
            resolved &&
            (previous >= 0) &&
            !DescendsFrom(
            ancestor: previous,
            descendant: tip,
            shapes: shapes
        )
        ) {
            errors.Add(item: new(
                Message: $"tip '{effector.Tip}' does not descend from the chain's last bone '{chain[^1]}', so a solve could not move it.",
                Path: $"{path}.tip"
            ));
        }
    }
    private static void ValidateEffectorTarget(CreationEffectorTargetDocument? target, List<DocumentValidationError> errors, string path) {
        if (target is null) {
            errors.Add(item: new(
                Message: "target is required.",
                Path: path
            ));

            return;
        }
        if (!CreationEffectorTargetDocument.IsKind(kind: target.Kind)) {
            errors.Add(item: new(
                Message: $"kind '{target.Kind}' is not recognized; the kinds are '{CreationEffectorTargetDocument.KindSurface}', '{CreationEffectorTargetDocument.KindBody}', and '{CreationEffectorTargetDocument.KindState}'.",
                Path: $"{path}.kind"
            ));

            return;
        }

        switch (target.Kind) {
            case CreationEffectorTargetDocument.KindSurface: {
                if (target.Direction is not { } direction) {
                    errors.Add(item: new(
                        Message: "direction is required for a surface target.",
                        Path: $"{path}.direction"
                    ));
                } else if (!IsFinite(vector: direction)) {
                    errors.Add(item: new(
                        Message: "direction is non-finite.",
                        Path: $"{path}.direction"
                    ));
                } else if (direction.Value == Vector3.Zero) {
                    errors.Add(item: new(
                        Message: "direction is zero, which names no direction.",
                        Path: $"{path}.direction"
                    ));
                }
                if (target.Reach is not { } reach) {
                    errors.Add(item: new(
                        Message: "reach is required for a surface target.",
                        Path: $"{path}.reach"
                    ));
                } else if (
                    (reach.Reference is null) &&
                    (!float.IsFinite(f: reach.Value) ||
                    (reach.Value <= 0f) ||
                    (reach.Value > CreationEffectorTargetDocument.MaxReach))
                ) {
                    errors.Add(item: new(
                        Message: $"reach {reach.Value} is outside (0, {CreationEffectorTargetDocument.MaxReach}] world units.",
                        Path: $"{path}.reach"
                    ));
                }
                if (
                    (target.Standoff is { Reference: null } standoff) &&
                    (!float.IsFinite(f: standoff.Value) ||
                    (standoff.Value < 0f) ||
                    (standoff.Value > CreationEffectorTargetDocument.MaxStandoff))
                ) {
                    errors.Add(item: new(
                        Message: $"standoff {standoff.Value} is outside [0, {CreationEffectorTargetDocument.MaxStandoff}] world units.",
                        Path: $"{path}.standoff"
                    ));
                }

                break;
            }
            case CreationEffectorTargetDocument.KindBody: {
                if (target.Index is not { } index) {
                    errors.Add(item: new(
                        Message: "index is required for a body target.",
                        Path: $"{path}.index"
                    ));
                } else if (index < 0) {
                    errors.Add(item: new(
                        Message: $"index {index} is negative.",
                        Path: $"{path}.index"
                    ));
                }
                if (target.Offset is { } offset) {
                    if (!IsFinite(vector: offset)) {
                        errors.Add(item: new(
                            Message: "offset is non-finite.",
                            Path: $"{path}.offset"
                        ));
                    } else if (offset.Length() > CreationEffectorTargetDocument.MaxOffset) {
                        errors.Add(item: new(
                            Message: $"offset reaches {offset.Length()}, past the {CreationEffectorTargetDocument.MaxOffset}-unit bound.",
                            Path: $"{path}.offset"
                        ));
                    }
                }

                break;
            }
            default: {
                if (!CreationDriverDocument.IsStateSignal(signal: target.Reference)) {
                    errors.Add(item: new(
                        Message: $"reference '{target.Reference}' is not a '{CreationDriverDocument.SignalStatePrefix}<row>[.<key>]' state reference.",
                        Path: $"{path}.reference"
                    ));
                }

                break;
            }
        }
    }
    private static void ValidateEffectorPlant(CreationDocument document, CreationPlantDocument? plant, List<DocumentValidationError> errors, string path) {
        if (plant is null) {
            return;
        }
        if ((document.Drivers ?? []).All(predicate: row => !string.Equals(
            a: row.Name,
            b: plant.Driver,
            comparisonType: StringComparison.Ordinal
        ))) {
            errors.Add(item: new(
                Message: $"names no declared driver '{plant.Driver}'.",
                Path: $"{path}.driver"
            ));
        }
        if (plant.Window.Reference is not null) {
            return;
        }

        var window = plant.Window.Value;

        if (
            !float.IsFinite(f: window.X) ||
            !float.IsFinite(f: window.Y) ||
            (window.X < 0f) ||
            (window.Y < 0f) ||
            (window.X >= CreationPlantDocument.TwoPi) ||
            (window.Y >= CreationPlantDocument.TwoPi)
        ) {
            errors.Add(item: new(
                Message: $"window [{window.X}, {window.Y}] is outside [0, 2π) radians; a driver's phase is wrapped, so a window past one turn names phases that never occur.",
                Path: $"{path}.window"
            ));
        }
    }
    // The token vocabulary is split across two assemblies on purpose: the BodyFacts names belong to the simulation's
    // motion vocabulary, which this document family does not reference, so only the shape of the gate and the two
    // presentation tokens are decidable here. An unresolvable fact name gates the driver off at the consumer.
    private static void ValidateGate(IReadOnlyList<string>? gate, List<DocumentValidationError> errors, string path) {
        if (gate is not { Count: > 0 } tokens) {
            return;
        }

        if (tokens.Count > CreationDriverDocument.MaxGateTokens) {
            errors.Add(item: new(
                Message: $"{tokens.Count} tokens exceeds the {CreationDriverDocument.MaxGateTokens}-token gate.",
                Path: path
            ));
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var i = 0; (i < tokens.Count); i++) {
            var token = tokens[i];

            if (string.IsNullOrWhiteSpace(value: token)) {
                errors.Add(item: new(
                    Message: "token is empty.",
                    Path: $"{path}[{i}]"
                ));

                continue;
            }
            if (!seen.Add(item: token)) {
                errors.Add(item: new(
                    Message: $"duplicate gate token '{token}'.",
                    Path: $"{path}[{i}]"
                ));
            }
            if (
                (tokens.Count > 1) &&
                string.Equals(
                a: token,
                b: CreationDriverDocument.WhenAlways,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                errors.Add(item: new(
                    Message: $"'{CreationDriverDocument.WhenAlways}' is the absence of a condition, so it cannot join a conjunction; drop it or drop the other tokens.",
                    Path: $"{path}[{i}]"
                ));
            }
        }

        if (
            seen.Contains(item: CreationDriverDocument.TokenMoving) &&
            seen.Contains(item: CreationDriverDocument.TokenStill)
        ) {
            errors.Add(item: new(
                Message: $"'{CreationDriverDocument.TokenMoving}' and '{CreationDriverDocument.TokenStill}' are negations, so the gate can never hold.",
                Path: path
            ));
        }
    }
    // A swing or a slide is presentation-only, so nothing downstream throws on a bad one — it would simply draw wrong
    // (a zero axis is an identity rotation, an unresolvable driver never advances, either facet beside a domain op
    // composes onto a transform nothing reads for that shape's geometry). Each of those is refused rather than folded
    // to a default: a limb that silently never moves reads as a rig defect, not as authored data.
    private static void ValidateShapeAnimation(CreationDocument document, ShapeDocument shape, List<DocumentValidationError> errors, string path) {
        var swings = (shape.Swings ?? []);
        var slides = (shape.Slides ?? []);

        if (shape.Parent is { } parent) {
            var shapes = (document.Shapes ?? []);
            var index = 0;
            var parentIndex = -1;

            while ((index < shapes.Count) && !ReferenceEquals(objA: shapes[index], objB: shape)) {
                index++;
            }

            for (var candidate = 0; (candidate < shapes.Count); candidate++) {
                if (string.Equals(
                    a: shapes[candidate].Name?.Value,
                    b: parent,
                    comparisonType: StringComparison.Ordinal
                )) {
                    parentIndex = candidate;

                    break;
                }
            }

            if (parentIndex < 0) {
                errors.Add(item: new(
                    Message: $"names no shape '{parent}'.",
                    Path: $"{path}.parent"
                ));
            } else if (parentIndex >= index) {
                errors.Add(item: new(
                    Message: $"parent '{parent}' must be declared before the shape it carries.",
                    Path: $"{path}.parent"
                ));
            }
            if (shape.Domain is { Count: > 0 }) {
                errors.Add(item: new(
                    Message: "a shape carrying domain operators rides the placement root's transform, so a parent's motion could not carry it.",
                    Path: $"{path}.parent"
                ));
            }
        }

        if ((swings.Count + slides.Count) == 0) {
            return;
        }

        if (swings.Count > ShapeDocument.MaxSwings) {
            errors.Add(item: new(
                Message: $"{swings.Count} entries exceeds the {ShapeDocument.MaxSwings}-swing list.",
                Path: $"{path}.swings"
            ));
        }
        if (slides.Count > ShapeDocument.MaxSlides) {
            errors.Add(item: new(
                Message: $"{slides.Count} entries exceeds the {ShapeDocument.MaxSlides}-slide list.",
                Path: $"{path}.slides"
            ));
        }
        if (shape.Domain is { Count: > 0 }) {
            errors.Add(item: new(
                Message: "a shape carrying domain operators rides the placement root's transform, so an animated facet on it would compose onto a transform its geometry does not read.",
                Path: path
            ));
        }

        for (var i = 0; (i < swings.Count); i++) {
            var swing = swings[i];
            var swingPath = $"{path}.swings[{i}]";

            ValidateAnimationFacet(
                amplitude: swing.Amplitude,
                amplitudeUnit: "radians",
                axis: swing.Axis,
                document: document,
                driver: swing.Driver,
                errors: errors,
                maxAmplitude: ShapeSwingDocument.MaxAmplitude,
                path: swingPath,
                phase: swing.Phase,
                wave: swing.Wave
            );

            if (!IsFinite(vector: swing.Pivot)) {
                errors.Add(item: new(
                    Message: "pivot is non-finite.",
                    Path: $"{swingPath}.pivot"
                ));
            }
        }

        for (var i = 0; (i < slides.Count); i++) {
            var slide = slides[i];

            ValidateAnimationFacet(
                amplitude: slide.Amplitude,
                amplitudeUnit: "creation units",
                axis: slide.Axis,
                document: document,
                driver: slide.Driver,
                errors: errors,
                maxAmplitude: ShapeSlideDocument.MaxAmplitude,
                path: $"{path}.slides[{i}]",
                phase: slide.Phase,
                wave: slide.Wave
            );
        }
    }
    private static void ValidateAnimationFacet(CreationDocument document, List<DocumentValidationError> errors, string path, string driver, DocumentVector3 axis, DocumentScalar amplitude, float maxAmplitude, string amplitudeUnit, DocumentScalar? phase, string? wave) {
        if ((document.Drivers ?? []).All(predicate: row => !string.Equals(
            a: row.Name,
            b: driver,
            comparisonType: StringComparison.Ordinal
        ))) {
            errors.Add(item: new(
                Message: $"names no declared driver '{driver}'.",
                Path: $"{path}.driver"
            ));
        }
        if (!IsFinite(vector: axis)) {
            errors.Add(item: new(
                Message: "axis is non-finite.",
                Path: $"{path}.axis"
            ));
        } else if (axis.Value == Vector3.Zero) {
            errors.Add(item: new(
                Message: "axis is zero, which names no direction.",
                Path: $"{path}.axis"
            ));
        }
        if (
            (amplitude.Reference is null) &&
            (!float.IsFinite(f: amplitude.Value) ||
            (MathF.Abs(x: amplitude.Value) > maxAmplitude))
        ) {
            errors.Add(item: new(
                Message: $"amplitude {amplitude.Value} is outside ±{maxAmplitude} {amplitudeUnit}.",
                Path: $"{path}.amplitude"
            ));
        }
        if (
            (phase is { Reference: null } offset) &&
            !float.IsFinite(f: offset.Value)
        ) {
            errors.Add(item: new(
                Message: "phase is non-finite.",
                Path: $"{path}.phase"
            ));
        }
        if (!CreationWave.IsEvaluable(wave: wave)) {
            errors.Add(item: new(
                Message: $"wave '{wave}' is not recognized; the waveforms are '{CreationWave.Sine}', '{CreationWave.HalfSine}', '{CreationWave.Linear}', '{CreationWave.Constant}', and '{CreationWave.CurvePrefix}<row>'.",
                Path: $"{path}.wave"
            ));
        }
    }
    // A NaN/infinite param would reach SdfProgramBuilder's own throwing guards at emission time (e.g.
    // RequireDirection/RequireFinite), well past the point a document author could see a reason why — refused here
    // instead, alongside the position/rotation/scale finite checks every other shape field already gets. Range clamps
    // (spacing floors, non-negative limits, the enum fallbacks) are Normalize's job, mirroring NormalizeWallpaper's
    // old clamp-not-refuse posture; only what Normalize cannot safely repair is refused here.
    private static void ValidateDomain(IReadOnlyList<ShapeDomainOp>? domain, List<DocumentValidationError> errors, string path) {
        if (domain is not { Count: > 0 } ops) {
            return;
        }

        if (ops.Count > ShapeDocument.MaxDomainOps) {
            errors.Add(item: new(
                Message: $"{ops.Count} entries exceeds the {ShapeDocument.MaxDomainOps}-op domain list.",
                Path: path
            ));
        }

        for (var i = 0; (i < ops.Count); i++) {
            var opPath = $"{path}[{i}]";

            switch (ops[i]) {
                case ShapeDomainOp.Symmetry symmetry: {
                        if (!IsFinite(vector: symmetry.Normal)) {
                            errors.Add(item: new(
                                Message: "normal is non-finite.",
                                Path: $"{opPath}.normal"
                            ));
                        }
                        if (
                            (symmetry.Offset is { } offset) &&
                            !float.IsFinite(f: offset)
                        ) {
                            errors.Add(item: new(
                                Message: "offset is non-finite.",
                                Path: $"{opPath}.offset"
                            ));
                        }

                        break;
                    }
                case ShapeDomainOp.Repeat repeat: {
                        if (!IsFinite(vector: repeat.Spacing)) {
                            errors.Add(item: new(
                                Message: "spacing is non-finite.",
                                Path: $"{opPath}.spacing"
                            ));
                        }
                        if (repeat.Limit is { } limit) {
                            if (!IsFinite(vector: limit)) {
                                errors.Add(item: new(
                                    Message: "limit is non-finite.",
                                    Path: $"{opPath}.limit"
                                ));
                            } else if (
                                (limit.X > ShapeDomainOp.Repeat.UnboundedLimit) ||
                                (limit.Y > ShapeDomainOp.Repeat.UnboundedLimit) ||
                                (limit.Z > ShapeDomainOp.Repeat.UnboundedLimit)
                            ) {
                                errors.Add(item: new(
                                    Message: $"limit exceeds {ShapeDomainOp.Repeat.UnboundedLimit}, which an absent limit already means.",
                                    Path: $"{opPath}.limit"
                                ));
                            }
                        }

                        break;
                    }
                case ShapeDomainOp.Polar polar: {
                        if (
                            (polar.Axis is { } axis) &&
                            !Enum.IsDefined(value: axis)
                        ) {
                            errors.Add(item: new(
                                Message: $"axis '{axis}' is not recognized.",
                                Path: $"{opPath}.axis"
                            ));
                        }
                        // The render fold bakes the sector count into the program as a float, and past 2^24 the
                        // shader reads back a different count than the host claims.
                        if (polar.Count > SdfProgramBuilder.MaxExactFloatSectorCount) {
                            errors.Add(item: new(
                                Message: $"count {polar.Count} exceeds the {SdfProgramBuilder.MaxExactFloatSectorCount} sectors the packed program represents exactly.",
                                Path: $"{opPath}.count"
                            ));
                        }

                        break;
                    }
                case ShapeDomainOp.Wallpaper wallpaper: {
                        if (!Enum.IsDefined(value: wallpaper.Group)) {
                            errors.Add(item: new(
                                Message: $"group '{wallpaper.Group}' is not recognized.",
                                Path: $"{opPath}.group"
                            ));
                        }
                        if (
                            (wallpaper.Plane is { } plane) &&
                            !Enum.IsDefined(value: plane)
                        ) {
                            errors.Add(item: new(
                                Message: $"plane '{plane}' is not recognized.",
                                Path: $"{opPath}.plane"
                            ));
                        }
                        if (
                            !float.IsFinite(f: wallpaper.Cell.X) ||
                            !float.IsFinite(f: wallpaper.Cell.Y)
                        ) {
                            errors.Add(item: new(
                                Message: "cell is non-finite.",
                                Path: $"{opPath}.cell"
                            ));
                        }

                        break;
                    }
            }
        }
    }
    private static void ValidateExtensions(CreationDocument document, List<DocumentValidationError> errors) =>
        DocumentCanonicalizer.ValidateExtensions(
            addError: (path, message) => errors.Add(item: new(
                Message: message,
                Path: path
            )),
            extensions: document.Extensions,
            knownMemberNames: KnownMemberNames
        );
    private static void ValidateFrames(CreationDocument document, List<DocumentValidationError> errors, HashSet<int> shapeIds) {
        if (document.Frames is not { Count: > 0 } frames) {
            return;
        }

        for (var i = 0; (i < frames.Count); i++) {
            var frame = frames[i];

            if (frame.Name is not { Length: > 0 }) {
                errors.Add(item: new(
                    Message: "a frame must be named.",
                    Path: $"frames[{i}].name"
                ));
            }

            var seenShapeIds = new HashSet<int>();

            for (var j = 0; (j < (frame.Transforms?.Count ?? 0)); j++) {
                var transform = frame.Transforms![j];

                // Unlike a chain/camera/face's stale reference (self-healed by dropping — the post-edit-deletion
                // case), a frame transform for a shape that no longer exists is unrecoverable captured animation
                // data: there is no "current pose" to fall back to, so this rejects rather than silently vanishing.
                if (!shapeIds.Contains(item: transform.Id)) {
                    errors.Add(item: new(
                        Path: $"frames[{i}].transforms[{j}].id",
                        Message: $"references missing shape id {transform.Id}."
                    ));
                }
                if (!seenShapeIds.Add(item: transform.Id)) {
                    errors.Add(item: new(
                        Path: $"frames[{i}].transforms[{j}].id",
                        Message: $"duplicate transform for shape id {transform.Id}."
                    ));
                }
                if (!IsFinite(vector: transform.Position)) {
                    errors.Add(item: new(
                        Message: "position is non-finite.",
                        Path: $"frames[{i}].transforms[{j}].position"
                    ));
                }
                if (!IsFinite(vector: transform.Scale)) {
                    errors.Add(item: new(
                        Message: "scale is non-finite.",
                        Path: $"frames[{i}].transforms[{j}].scale"
                    ));
                }
                if (!IsFinite(quaternion: transform.Rotation)) {
                    errors.Add(item: new(
                        Message: "rotation is non-finite.",
                        Path: $"frames[{i}].transforms[{j}].rotation"
                    ));
                }
            }
        }
    }
    private static void ValidatePalette(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Palette is not { Count: > 0 } palette) {
            return;
        }

        if (palette.Count > CreationDocument.PaletteSize) {
            errors.Add(item: new(
                Path: "palette",
                Message: $"{palette.Count} entries exceeds the {CreationDocument.PaletteSize}-slot palette."
            ));
        }

        for (var i = 0; (i < palette.Count); i++) {
            var entry = palette[i];

            if (
                !HexColor.TryParse(
                    rgb: out _,
                    value: entry.Color
                ) &&
                !HexColor.IsStateBinding(value: entry.Color)
            ) {
                errors.Add(item: new(
                    Message: "color must be #RRGGBB or a state.<row>[.<key>] binding.",
                    Path: $"palette[{i}].color"
                ));
            }
            if (
                (entry.Emissive is { } emissive) &&
                !float.IsFinite(f: emissive)
            ) {
                errors.Add(item: new(
                    Message: "emissive is non-finite.",
                    Path: $"palette[{i}].emissive"
                ));
            }
            if (
                (entry.Specular is { } specular) &&
                !float.IsFinite(f: specular)
            ) {
                errors.Add(item: new(
                    Message: "specular is non-finite.",
                    Path: $"palette[{i}].specular"
                ));
            }
            if (
                (entry.Shininess is { } shininess) &&
                !float.IsFinite(f: shininess)
            ) {
                errors.Add(item: new(
                    Message: "shininess is non-finite.",
                    Path: $"palette[{i}].shininess"
                ));
            }
        }
    }
    private static void ValidateParts(CreationDocument document, List<DocumentValidationError> errors, HashSet<int> shapeIds) {
        if (document.Parts is not { Count: > 0 } parts) {
            return;
        }

        var partIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < parts.Count); index++) {
            var part = parts[index];

            if (string.IsNullOrWhiteSpace(value: part.Id)) {
                errors.Add(item: new(
                    Message: "a part id is required.",
                    Path: $"parts[{index}].id"
                ));
            } else if (!partIds.Add(item: part.Id)) {
                errors.Add(item: new(
                    Path: $"parts[{index}].id",
                    Message: $"duplicate part id '{part.Id}'."
                ));
            }

            if (!shapeIds.Contains(item: part.ShapeId)) {
                errors.Add(item: new(
                    Path: $"parts[{index}].shapeId",
                    Message: $"references missing shape id {part.ShapeId}."
                ));
            }
        }
    }
    // The declared sounds: unique names, a finite level/radius, and the INLINE puck.synth.v1 patch validated through
    // the synth family's OWN canonicalizer (the one pipeline — never a re-implementation), its violations re-pathed
    // under this creation. A sound naming a missing shape is NOT a failure — Normalize drops it (the faces rule).
    private static void ValidateSounds(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Behavior?.Sounds is not { Count: > 0 } sounds) {
            return;
        }

        var soundNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        for (var i = 0; (i < sounds.Count); i++) {
            var sound = sounds[i];
            var name = ((sound.Name is { Length: > 0 } soundName)
                ? soundName
                : "sound"
            );

            if (!soundNames.Add(item: name)) {
                errors.Add(item: new(
                    Message: $"sound name '{name}' collides with another sound.",
                    Path: $"behavior.sounds[{i}].name"
                ));
            }

            if (
                (sound.Level is { } level) &&
                (!float.IsFinite(f: level) || (level < 0f) || (level > CreationSoundDocument.MaxLevel))
            ) {
                errors.Add(item: new(
                    Message: $"level {level} is outside [0, {CreationSoundDocument.MaxLevel}].",
                    Path: $"behavior.sounds[{i}].level"
                ));
            }

            if (
                (sound.Radius is { } radius) &&
                (!float.IsFinite(f: radius) || (radius <= 0f))
            ) {
                errors.Add(item: new(
                    Message: "radius must be finite and positive.",
                    Path: $"behavior.sounds[{i}].radius"
                ));
            }

            if (sound.Patch is null) {
                errors.Add(item: new(
                    Message: "a sound requires an inline puck.synth.v1 patch.",
                    Path: $"behavior.sounds[{i}].patch"
                ));

                continue;
            }

            foreach (var violation in SynthPatchCanonicalizer.Validate(document: sound.Patch)) {
                errors.Add(item: new(
                    Path: $"behavior.sounds[{i}].patch.{violation.Path}",
                    Message: violation.Message
                ));
            }
        }
    }
    private static void ValidateTextRuns(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.TextRuns is not { Count: > 0 } runs) {
            return;
        }

        for (var i = 0; (i < runs.Count); i++) {
            var run = runs[i];

            if (
                (run.ShapeId is { } shapeId) &&
                ((document.Shapes ?? []).All(predicate: shape => (shape.Id != shapeId)))
            ) {
                errors.Add(item: new(
                    Message: $"names no shape with id {shapeId}.",
                    Path: $"textRuns[{i}].shapeId"
                ));
            }

            if (!IsFinite(vector: run.Position)) {
                errors.Add(item: new(
                    Message: "position is non-finite.",
                    Path: $"textRuns[{i}].position"
                ));
            }
            if (!IsFinite(quaternion: run.Rotation)) {
                errors.Add(item: new(
                    Message: "rotation is non-finite.",
                    Path: $"textRuns[{i}].rotation"
                ));
            }
            if (!float.IsFinite(f: run.EmHeight)) {
                errors.Add(item: new(
                    Message: "emHeight is non-finite.",
                    Path: $"textRuns[{i}].emHeight"
                ));
            }
            if (
                (run.Depth is { } depth) &&
                !float.IsFinite(f: depth)
            ) {
                errors.Add(item: new(
                    Message: "depth is non-finite.",
                    Path: $"textRuns[{i}].depth"
                ));
            }
            if (
                (run.MaxWidth is { } maxWidth) &&
                !float.IsFinite(f: maxWidth)
            ) {
                errors.Add(item: new(
                    Message: "maxWidth is non-finite.",
                    Path: $"textRuns[{i}].maxWidth"
                ));
            }
            if (
                (run.Tracking is { } tracking) &&
                !float.IsFinite(f: tracking)
            ) {
                errors.Add(item: new(
                    Message: "tracking is non-finite.",
                    Path: $"textRuns[{i}].tracking"
                ));
            }
            if (
                (run.LineSpacing is { } lineSpacing) &&
                !float.IsFinite(f: lineSpacing)
            ) {
                errors.Add(item: new(
                    Message: "lineSpacing is non-finite.",
                    Path: $"textRuns[{i}].lineSpacing"
                ));
            }
        }
    }

    /// <summary>THE full pipeline: validates schema + structural invariants (throwing on either), normalizes the
    /// self-heal, then serializes to canonical UTF-8 bytes and hashes them through
    /// <see cref="DocumentCanonicalizer.Canonicalize"/>. Two calls against value-equal input documents always produce
    /// byte-identical bytes and therefore the same hash — cite THIS guarantee wherever a creation's identity is
    /// pinned (the world placement row's inline-canonical hash).</summary>
    /// <param name="document">The document to canonicalize.</param>
    /// <param name="source">An optional source label (a file path or save handle) for a validation-failure message.</param>
    /// <returns>The validated, normalized document plus its canonical bytes and hash.</returns>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a
    /// structural invariant.</exception>
    public static CanonicalDocument<CreationDocument> Canonicalize(CreationDocument document, string? source = null) {
        ValidateOrThrow(
            document: document,
            source: source
        );

        return DocumentCanonicalizer.Canonicalize(document: Normalize(document: document));
    }

    // A non-finite/zero-length direction has no fold plane to normalize to, so it floors to the retired Mirror:
    // true flag's exact plane (UnitX) rather than reaching SdfProgramBuilder.SymmetryPlane's own throwing guard.
    private static Vector3 NormalizeDirection(Vector3 value) {
        if (
            !IsFinite(vector: value) ||
            (value == Vector3.Zero)
        ) {
            return Vector3.UnitX;
        }

        return Vector3.Normalize(value: value);
    }
    // Absent stays absent so a creation authored without domain ops keeps its canonical bytes and hash; a present
    // list is clamped to what each op's own SdfProgramBuilder method accepts (mirroring NormalizeWallpaper's old
    // clamp-in-full posture) and every optional member is written out in full.
    private static List<ShapeDomainOp>? NormalizeDomain(IReadOnlyList<ShapeDomainOp>? domain) {
        if (domain is not { Count: > 0 } source) {
            return null;
        }

        var normalized = new List<ShapeDomainOp>(capacity: source.Count);

        foreach (var op in source) {
            normalized.Add(item: NormalizeDomainOp(op: op));
        }

        return normalized;
    }
    // Absent stays absent through all three animation lists, so a creation authored without them keeps its canonical
    // bytes and hash; a present list writes every optional member out in full with a unit axis, the form the client's
    // per-frame composition reads without re-deciding anything.
    private static List<CreationDriverDocument>? NormalizeDrivers(IReadOnlyList<CreationDriverDocument>? drivers) {
        if (drivers is not { Count: > 0 } source) {
            return null;
        }

        var normalized = new List<CreationDriverDocument>(capacity: source.Count);

        foreach (var driver in source) {
            normalized.Add(item: driver with {
                Cadence = ((driver.Cadence.Reference is not null)
                ? driver.Cadence
                : Math.Clamp(
                value: (float.IsFinite(f: driver.Cadence.Value)
                ? driver.Cadence.Value
                : 0f),
                max: CreationDriverDocument.MaxCadence,
                min: -CreationDriverDocument.MaxCadence
            )),
                When = ((driver.When is { Count: > 0 } gate)
                ? [.. gate]
                : [CreationDriverDocument.WhenAlways]),
            });
        }

        return normalized;
    }
    // Absent stays absent so a creation authored without effectors keeps its canonical bytes and hash; a present list
    // writes the gate and the weight out in full and normalizes a surface probe's direction to a unit vector, so the
    // per-frame solve reads a settled form.
    private static List<CreationEffectorDocument>? NormalizeEffectors(IReadOnlyList<CreationEffectorDocument>? effectors) {
        if (effectors is not { Count: > 0 } source) {
            return null;
        }

        var normalized = new List<CreationEffectorDocument>(capacity: source.Count);

        foreach (var effector in source) {
            normalized.Add(item: effector with {
                Chain = [.. (effector.Chain ?? [])],
                Target = (effector.Target with {
                    Direction = ((effector.Target.Direction is { } direction)
                    ? ((direction.Reference is not null)
                        ? direction
                        : NormalizeDirection(value: direction.Value))
                    : null),
                }),
                Weight = ((effector.Weight is { Reference: not null } bound)
                ? bound
                : new DocumentScalar(value: Math.Clamp(
                    value: (float.IsFinite(f: (effector.Weight?.Value ?? 1f))
                    ? (effector.Weight?.Value ?? 1f)
                    : 1f),
                    max: 1f,
                    min: 0f
                ))),
                When = ((effector.When is { Count: > 0 } gate)
                ? [.. gate]
                : [CreationDriverDocument.WhenAlways]),
            });
        }

        return normalized;
    }
    private static List<ShapeSlideDocument>? NormalizeSlides(IReadOnlyList<ShapeSlideDocument>? slides) {
        if (slides is not { Count: > 0 } source) {
            return null;
        }

        var normalized = new List<ShapeSlideDocument>(capacity: source.Count);

        foreach (var slide in source) {
            normalized.Add(item: slide with {
                Amplitude = ((slide.Amplitude.Reference is not null)
                ? slide.Amplitude
                : NormalizeAmplitude(
                max: ShapeSlideDocument.MaxAmplitude,
                value: slide.Amplitude.Value
            )),
                Axis = NormalizeDirection(value: slide.Axis),
                Phase = ((slide.Phase is { Reference: not null } boundPhase)
                ? boundPhase
                : ((NormalizePhase(value: slide.Phase?.Value) is { } literalPhase)
                    ? new DocumentScalar(value: literalPhase)
                    : null)),
                Wave = (slide.Wave ?? CreationWave.Sine),
            });
        }

        return normalized;
    }
    private static List<ShapeSwingDocument>? NormalizeSwings(IReadOnlyList<ShapeSwingDocument>? swings) {
        if (swings is not { Count: > 0 } source) {
            return null;
        }

        var normalized = new List<ShapeSwingDocument>(capacity: source.Count);

        foreach (var swing in source) {
            normalized.Add(item: swing with {
                Amplitude = ((swing.Amplitude.Reference is not null)
                ? swing.Amplitude
                : NormalizeAmplitude(
                max: ShapeSwingDocument.MaxAmplitude,
                value: swing.Amplitude.Value
            )),
                Axis = NormalizeDirection(value: swing.Axis),
                Phase = ((swing.Phase is { Reference: not null } boundPhase)
                ? boundPhase
                : ((NormalizePhase(value: swing.Phase?.Value) is { } literalPhase)
                    ? new DocumentScalar(value: literalPhase)
                    : null)),
                Wave = (swing.Wave ?? CreationWave.Sine),
            });
        }

        return normalized;
    }
    private static float NormalizeAmplitude(float value, float max) =>
        Math.Clamp(
            value: (float.IsFinite(f: value)
            ? value
            : 0f),
            max: max,
            min: -max
        );
    private static float NormalizePhase(float? value) => (float.IsFinite(f: (value ?? 0f))
        ? (value ?? 0f)
        : 0f
    );
    private static ShapeDomainOp NormalizeDomainOp(ShapeDomainOp op) {
        return op switch {
            ShapeDomainOp.Symmetry symmetry => new ShapeDomainOp.Symmetry(
                Normal: NormalizeDirection(value: symmetry.Normal),
                Offset: (float.IsFinite(f: (symmetry.Offset ?? 0f))
                ? (symmetry.Offset ?? 0f)
                : 0f)
            ),
            ShapeDomainOp.Repeat repeat => new ShapeDomainOp.Repeat(
                Limit: NormalizeCellLimit(value: (repeat.Limit ?? new Vector3(value: ShapeDomainOp.Repeat.UnboundedLimit))),
                Spacing: NormalizeSpacing(value: repeat.Spacing)
            ),
            ShapeDomainOp.Polar polar => new ShapeDomainOp.Polar(
                Axis: (Enum.IsDefined(value: (polar.Axis ?? SdfPolarAxis.Y))
                ? (polar.Axis ?? SdfPolarAxis.Y)
                : SdfPolarAxis.Y),
                Count: Math.Clamp(value: polar.Count, min: 1, max: SdfProgramBuilder.MaxExactFloatSectorCount),
                MaterialStride: Math.Max(val1: (polar.MaterialStride ?? 0), val2: 0),
                Mirror: (polar.Mirror ?? false)
            ),
            ShapeDomainOp.Wallpaper wallpaper => new ShapeDomainOp.Wallpaper(
                Cell: new Vector2(
                    x: Math.Max(val1: wallpaper.Cell.X, val2: 0.001f),
                    y: Math.Max(val1: wallpaper.Cell.Y, val2: 0.001f)
                ),
                Group: (Enum.IsDefined(value: wallpaper.Group)
                ? wallpaper.Group
                : SdfWallpaperGroup.P1),
                Limit: new Vector2(
                    x: Math.Max(val1: (wallpaper.Limit?.X ?? ShapeDomainOp.Wallpaper.UnboundedLimit), val2: 0f),
                    y: Math.Max(val1: (wallpaper.Limit?.Y ?? ShapeDomainOp.Wallpaper.UnboundedLimit), val2: 0f)
                ),
                LodDistance: Math.Max(val1: (wallpaper.LodDistance ?? 0f), val2: 0f),
                MaterialStride: Math.Max(val1: (wallpaper.MaterialStride ?? 0), val2: 0),
                Plane: (Enum.IsDefined(value: (wallpaper.Plane ?? SdfWallpaperPlane.XZ))
                ? (wallpaper.Plane ?? SdfWallpaperPlane.XZ)
                : SdfWallpaperPlane.XZ)
            ),
            _ => op,
        };
    }
    private static float ClampCellLimit(float value) =>
        Math.Clamp(
            value: (float.IsFinite(f: value)
            ? value
            : 0f),
            min: 0f,
            max: ShapeDomainOp.Repeat.UnboundedLimit
        );
    private static Vector3 NormalizeCellLimit(Vector3 value) =>
        new(
            x: ClampCellLimit(value: value.X),
            y: ClampCellLimit(value: value.Y),
            z: ClampCellLimit(value: value.Z)
        );
    // A polar domain's positional material recolor adds sector·stride to the shape's base material id BEFORE the
    // material load, so the palette must already hold every id the recolor reaches — an uncovered reach reads past
    // the packed material table on the GPU (or into a neighbouring creation's palette).
    private static void ValidatePolarStride(CreationDocument document, List<DocumentValidationError> errors) {
        var paletteCount = Math.Clamp(
            value: (document.Palette?.Count ?? 1),
            max: CreationDocument.PaletteSize,
            min: 1
        );

        for (var index = 0; (index < (document.Shapes?.Count ?? 0)); index++) {
            var shape = document.Shapes![index];

            foreach (var op in (shape.Domain ?? [])) {
                if (op is not ShapeDomainOp.Polar { MaterialStride: > 0 } polar) {
                    continue;
                }

                var reach = ((shape.Material ?? 0) + (polar.MaterialStride.Value * Math.Max(
                    val1: (polar.Count - 1),
                    val2: 0
                )));

                if (reach >= paletteCount) {
                    errors.Add(item: new(
                        Message: $"the polar material recolor reaches palette slot {reach}, past the {paletteCount}-entry palette — add the entries the stride reaches, or lower the stride/count.",
                        Path: $"shapes[{index}].domain"
                    ));
                }
            }
        }
    }
    // The step-factor door: normalization clamps each parameter into its own range, but the derived march cost is a
    // product of all of them, so an over-budget combination is refused by name (never silently reshaped) — the whole
    // program's march divides its steps by this factor.
    private static void ValidateNoise(CreationDocument document, List<DocumentValidationError> errors) {
        if (document.Noise is not { } noise) {
            return;
        }

        var priorErrorCount = errors.Count;

        if (
            !float.IsFinite(f: noise.Frequency) ||
            (noise.Frequency <= 0f) ||
            (noise.Frequency > CreationNoiseDocument.MaxFrequency)
        ) {
            errors.Add(item: new(
                Message: $"frequency must be finite in (0, {CreationNoiseDocument.MaxFrequency}]; got {noise.Frequency}.",
                Path: "noise.frequency"
            ));
        }

        if (
            !float.IsFinite(f: noise.Amplitude) ||
            (noise.Amplitude <= 0f) ||
            (noise.Amplitude > CreationNoiseDocument.MaxAmplitude)
        ) {
            errors.Add(item: new(
                Message: $"amplitude must be finite in (0, {CreationNoiseDocument.MaxAmplitude}]; got {noise.Amplitude}.",
                Path: "noise.amplitude"
            ));
        }

        if ((noise.Octaves is { } octaves) && ((octaves < 1) || (octaves > SdfProgramBuilder.MaxNoiseOctaves))) {
            errors.Add(item: new(
                Message: $"octaves must be in 1..{SdfProgramBuilder.MaxNoiseOctaves}; got {octaves}.",
                Path: "noise.octaves"
            ));
        }

        if ((noise.Gain is { } gain) && (!float.IsFinite(f: gain) || (gain < CreationNoiseDocument.MinGain) || (gain > CreationNoiseDocument.MaxGain))) {
            errors.Add(item: new(
                Message: $"gain must be finite in [{CreationNoiseDocument.MinGain}, {CreationNoiseDocument.MaxGain}]; got {gain}.",
                Path: "noise.gain"
            ));
        }

        if ((noise.Lacunarity is { } lacunarity) && (!float.IsFinite(f: lacunarity) || (lacunarity < CreationNoiseDocument.MinLacunarity) || (lacunarity > CreationNoiseDocument.MaxLacunarity))) {
            errors.Add(item: new(
                Message: $"lacunarity must be finite in [{CreationNoiseDocument.MinLacunarity}, {CreationNoiseDocument.MaxLacunarity}]; got {lacunarity}.",
                Path: "noise.lacunarity"
            ));
        }

        if (errors.Count > priorErrorCount) {
            return;
        }

        var stepFactor = noise.StepFactor();

        if (stepFactor > CreationNoiseDocument.MaxStepFactor) {
            errors.Add(item: new(
                Message: $"the derived march step factor {stepFactor:0.###} exceeds the {CreationNoiseDocument.MaxStepFactor} budget — reduce amplitude, frequency, octaves, or lacunarity so amplitude·frequency·(15/4)·√3·Σ(gain·lacunarity)ᵏ/Σgainᵏ stays within it.",
                Path: "noise"
            ));
        }
    }
    // An inert facet (zero/non-finite frequency or amplitude after clamping) drops to null so absence and inertness
    // share one spelling; every optional resolves to its documented default so the round-trip is idempotent.
    private static CreationNoiseDocument? NormalizeNoise(CreationNoiseDocument? noise) {
        if (noise is null) {
            return null;
        }

        var frequency = Math.Clamp(
            value: (float.IsFinite(f: noise.Frequency) ? noise.Frequency : 0f),
            max: CreationNoiseDocument.MaxFrequency,
            min: 0f
        );
        var amplitude = Math.Clamp(
            value: (float.IsFinite(f: noise.Amplitude) ? noise.Amplitude : 0f),
            max: CreationNoiseDocument.MaxAmplitude,
            min: 0f
        );

        if ((frequency <= 0f) || (amplitude <= 0f)) {
            return null;
        }

        return new CreationNoiseDocument(
            Amplitude: amplitude,
            Frequency: frequency,
            Gain: Math.Clamp(
                value: (float.IsFinite(f: (noise.Gain ?? 0.5f)) ? (noise.Gain ?? 0.5f) : 0.5f),
                max: CreationNoiseDocument.MaxGain,
                min: CreationNoiseDocument.MinGain
            ),
            Lacunarity: Math.Clamp(
                value: (float.IsFinite(f: (noise.Lacunarity ?? 2f)) ? (noise.Lacunarity ?? 2f) : 2f),
                max: CreationNoiseDocument.MaxLacunarity,
                min: CreationNoiseDocument.MinLacunarity
            ),
            Octaves: Math.Clamp(
                value: (noise.Octaves ?? 4),
                max: SdfProgramBuilder.MaxNoiseOctaves,
                min: 1
            ),
            Seed: (noise.Seed ?? 0u)
        );
    }
    private static Vector3 NormalizeSpacing(Vector3 value) =>
        new(
            x: Math.Max(val1: (float.IsFinite(f: value.X) ? value.X : 0f), val2: 0.001f),
            y: Math.Max(val1: (float.IsFinite(f: value.Y) ? value.Y : 0f), val2: 0.001f),
            z: Math.Max(val1: (float.IsFinite(f: value.Z) ? value.Z : 0f), val2: 0.001f)
        );

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
                Domain = NormalizeDomain(domain: shape.Domain),
                Slides = NormalizeSlides(slides: shape.Slides),
                Swings = NormalizeSwings(swings: shape.Swings),
                Bend = Math.Clamp(
                value: (shape.Bend ?? 0f),
                max: ShapeDocument.MaxBend,
                min: -ShapeDocument.MaxBend
            ),
                Blend = (shape.Blend ?? SdfBlendOp.Union),
                Dilate = Math.Clamp(
                value: (shape.Dilate ?? 0f),
                max: ShapeDocument.MaxDilate,
                min: 0f
            ),
                Group = Math.Max(
                val1: (shape.Group ?? 0),
                val2: 0
            ),
                Material = Math.Clamp(
                value: (shape.Material ?? 0),
                max: (CreationDocument.PaletteSize - 1),
                min: 0
            ),
                Onion = Math.Clamp(
                value: (shape.Onion ?? 0f),
                max: ShapeDocument.MaxOnion,
                min: 0f
            ),
                Rotation = ((shape.Rotation == default)
                ? Quaternion.Identity
                : Quaternion.Normalize(value: shape.Rotation)),
                Scale = ((shape.Scale == default)
                ? Vector3.One
                : shape.Scale),
                Smooth = Math.Clamp(
                value: (shape.Smooth ?? 0f),
                max: ShapeDocument.MaxSmooth,
                min: 0f
            ),
                Twist = Math.Clamp(
                value: (shape.Twist ?? 0f),
                max: ShapeDocument.MaxTwist,
                min: -ShapeDocument.MaxTwist
            ),
            });
            _ = shapeIds.Add(item: shape.Id);
        }

        List<ChainDocument>? chains = null;

        if (document.Chains is { Count: > 0 } sourceChains) {
            chains = new List<ChainDocument>(capacity: sourceChains.Count);

            foreach (var chain in sourceChains) {
                // A chain naming any missing shape id is dropped outright — its rest geometry can never be
                // recaptured against a shape that is not there, and a partial chain has no sound IK meaning.
                if (
                    (chain.Shapes is not { Count: > 0 } memberIds) ||
                    !memberIds.All(predicate: shapeIds.Contains)
                ) {
                    continue;
                }

                var kind = (chain.Kind ?? ((memberIds.Count == 3)
                    ? ChainDocument.KindLimb
                    : ChainDocument.KindSpine));

                // "limb" is a structural invariant: exactly 3 shapes (2 bones) or it demotes to "spine" — the spine
                // solver degrades gracefully to any length, so this can never leave a chain unsolvable.
                if (
                    string.Equals(
                    a: kind,
                    b: ChainDocument.KindLimb,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                ) &&
                    (memberIds.Count != 3)
                ) {
                    kind = ChainDocument.KindSpine;
                }

                chains.Add(item: chain with { Kind = kind });
            }
        }

        return (document with {
            Behavior = NormalizeBehavior(
            behavior: document.Behavior,
            shapeIds: shapeIds
        ),
            Noise = NormalizeNoise(noise: document.Noise),
            Cameras = NormalizeCreationCameras(
            cameras: document.Cameras,
            shapeIds: shapeIds
        ),
            Chains = chains,
            Drivers = NormalizeDrivers(drivers: document.Drivers),
            Effectors = NormalizeEffectors(effectors: document.Effectors),
            // A world-backed name has already resolved for validation, but its reference token remains the authored
            // source of truth. Preserve that token just like the creation's spatial document values do.
            Name = ((document.Name?.Reference is not null)
                ? document.Name
                : SanitizeName(name: (document.Name?.Value ?? "creation"))),
            Parts = ((document.Parts is { Count: > 0 } parts)
            ? parts.ToArray()
            : null),
            Schema = CreationDocument.CurrentSchema,
            Shapes = shapes,
            TextRuns = NormalizeTextRuns(textRuns: document.TextRuns),
        });
    }
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

        if (DocumentCanonicalizer.SchemaViolationMessage(
            declared: document.Schema,
            recognized: CreationDocument.CurrentSchema
        ) is { } schemaViolation) {
            return [new DocumentValidationError(
                    Message: schemaViolation,
                    Path: "schema"
                )];
        }

        var errors = new List<DocumentValidationError>();
        var shapeIds = new HashSet<int>();

        for (var i = 0; (i < (document.Shapes?.Count ?? 0)); i++) {
            var shape = document.Shapes![i];

            if (!shapeIds.Add(item: shape.Id)) {
                errors.Add(item: new(
                    Path: $"shapes[{i}].id",
                    Message: $"duplicate shape id {shape.Id}."
                ));
            }
            if (!Enum.IsDefined(value: shape.Type)) {
                errors.Add(item: new(
                    Path: $"shapes[{i}].type",
                    Message: $"primitive '{shape.Type}' is not recognized."
                ));
            }
            if (
                (shape.Blend is { } blend) &&
                !Enum.IsDefined(value: blend)
            ) {
                errors.Add(item: new(
                    Message: $"blend '{blend}' is not recognized.",
                    Path: $"shapes[{i}].blend"
                ));
            }
            if (!IsFinite(vector: shape.Position)) {
                errors.Add(item: new(
                    Message: "position is non-finite.",
                    Path: $"shapes[{i}].position"
                ));
            }
            if (!IsFinite(vector: shape.Scale)) {
                errors.Add(item: new(
                    Message: "scale is non-finite.",
                    Path: $"shapes[{i}].scale"
                ));
            } else {
                // Every emission path reads a scale's magnitude, so a negative component changes no geometry it can
                // reach — it only makes the shape's reach disagree with the bound its placement ships. Mirroring
                // already has a spelling: a symmetry domain op.
                if (
                    (shape.Scale.X < 0f) ||
                    (shape.Scale.Y < 0f) ||
                    (shape.Scale.Z < 0f)
                ) {
                    errors.Add(item: new(
                        Message: "scale has a negative component; emission reads scale magnitudes, so the sign mirrors nothing — author a mirror as a symmetry domain op.",
                        Path: $"shapes[{i}].scale"
                    ));
                }

                if (
                    Enum.IsDefined(value: shape.Type) &&
                    !SdfSolidGeometry.TryValidateScaledPrimitive(
                    refusal: out var scaleRefusal,
                    scale: shape.Scale,
                    type: shape.Type
                )
                ) {
                    errors.Add(item: new(
                        Message: $"scale authors {scaleRefusal}.",
                        Path: $"shapes[{i}].scale"
                    ));
                }
            }
            if (!IsFinite(quaternion: shape.Rotation)) {
                errors.Add(item: new(
                    Message: "rotation is non-finite.",
                    Path: $"shapes[{i}].rotation"
                ));
            }
            if (
                (shape.Joint is { } joint) &&
                !IsFinite(vector: joint)
            ) {
                errors.Add(item: new(
                    Message: "joint is non-finite.",
                    Path: $"shapes[{i}].joint"
                ));
            }

            ValidateDomain(
                domain: shape.Domain,
                errors: errors,
                path: $"shapes[{i}].domain"
            );
            ValidateShapeAnimation(
                document: document,
                errors: errors,
                path: $"shapes[{i}]",
                shape: shape
            );
        }

        ValidateDrivers(
            document: document,
            errors: errors
        );
        ValidateEffectors(
            document: document,
            errors: errors
        );

        ValidatePalette(
            document: document,
            errors: errors
        );
        ValidateFrames(
            document: document,
            errors: errors,
            shapeIds: shapeIds
        );
        ValidateParts(
            document: document,
            errors: errors,
            shapeIds: shapeIds
        );
        ValidateChains(
            document: document,
            errors: errors
        );
        ValidateCameras(
            document: document,
            errors: errors
        );
        ValidateBehavior(
            document: document,
            errors: errors
        );
        ValidateTextRuns(
            document: document,
            errors: errors
        );
        ValidateExtensions(
            document: document,
            errors: errors
        );
        ValidateNoise(
            document: document,
            errors: errors
        );
        ValidatePolarStride(
            document: document,
            errors: errors
        );

        return errors;
    }
    /// <summary>Runs <see cref="Validate"/> and throws when it finds anything.</summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="source">An optional source label (a file path or save handle) for the exception message.</param>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a
    /// structural invariant.</exception>
    public static void ValidateOrThrow(CreationDocument document, string? source = null) =>
        DocumentCanonicalizer.ThrowIfInvalid(
            errors: Validate(document: document),
            source: source
        );
}
