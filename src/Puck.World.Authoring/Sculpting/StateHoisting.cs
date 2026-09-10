using System.Numerics;
using System.Text.Json.Nodes;
using Puck.Assets.Documents;

namespace Puck.World.Authoring.Sculpting;

/// <summary>
/// Generalizes the creation-generator post-pass every rig needs: every rotation used by two or more shapes (or
/// every named rotation, under <see cref="Options.HoistAllRotations"/>) becomes a shared <c>Text</c> state cell,
/// every scale used by two or more shapes becomes one too, and a swing's LITERAL pivot/amplitude that equals a
/// named joint/tuning value binds to it. A pivot or amplitude that already carries a
/// <see cref="Puck.Assets.Documents.DocumentSpatialValue{T}.Reference"/> (carried forward from an existing document
/// by <see cref="CreationBuilder.CarryRig"/>) is left untouched — this pass only ever promotes a LITERAL, so
/// applying it twice over the same document changes nothing the second time.
/// </summary>
public static class StateHoisting {
    /// <summary>The hoisting policy and the row/cell vocabulary it writes into.</summary>
    /// <param name="RotationsRow">The state row every hoisted rotation lands in (e.g. "mothRot").</param>
    /// <param name="ScalesRow">The state row every hoisted scale lands in (e.g. "mothScale").</param>
    /// <param name="JointsRow">The state row swing pivots resolve against (e.g. "mothJoints").</param>
    /// <param name="TuningRow">The state row swing/slide amplitudes resolve against (e.g. "mothTuning").</param>
    /// <param name="HoistAllRotations">When true, EVERY interned rotation becomes a cell regardless of use count;
    /// when false, only one used by two or more shapes does.</param>
    /// <param name="Joints">The named joint positions a literal swing pivot is matched against.</param>
    /// <param name="Tuning">The named tuning values a literal swing/slide amplitude is matched against.</param>
    /// <param name="TuningCellSelector">Given a driver name and the owning shape's name, names the tuning cell an
    /// amplitude on that (driver, shape) pair is eligible to bind to, or null when none applies. Binding still
    /// requires the literal amplitude to equal the named value within <see cref="AmplitudeEpsilon"/> — a caller
    /// whose policy would bind an amplitude it never actually authored to that value is a caller error, not this
    /// pass's to silently correct.</param>
    public sealed record Options(
        string RotationsRow,
        string ScalesRow,
        string JointsRow,
        string TuningRow,
        bool HoistAllRotations,
        IReadOnlyList<SculptJoint> Joints,
        IReadOnlyDictionary<string, float> Tuning,
        Func<string, string, string?> TuningCellSelector
    );
    /// <summary>The hoisted document plus the state rows to upsert alongside it.</summary>
    /// <param name="Document">The document with shared rotations/scales, and matching swing pivots/amplitudes,
    /// rewritten to <c>state.&lt;row&gt;.&lt;name&gt;</c> references.</param>
    /// <param name="StateRows">One <c>WorldStateRow</c>-shaped JSON object per row this pass produced (only rows it
    /// actually populated — <see cref="Options.RotationsRow"/> always, since <see cref="CreationBuilder.Identity"/>
    /// alone guarantees one entry; <see cref="Options.ScalesRow"/> only when at least one scale group qualified).</param>
    public sealed record Result(CreationDocument Document, IReadOnlyList<JsonObject> StateRows);

    /// <summary>The largest amplitude difference still treated as "this literal IS the named tuning value".</summary>
    public const float AmplitudeEpsilon = 1e-6f;
    /// <summary>The largest joint-position distance (squared) still treated as "this literal pivot IS the named
    /// joint" — matches the 4-decimal authoring rounding <see cref="CreationBuilder.Round4(Vector3)"/> applies.</summary>
    public const float JointEpsilonSquared = 1e-7f;

    /// <summary>Runs the hoisting pass over <paramref name="builder"/>'s built document.</summary>
    public static Result Apply(CreationBuilder builder, Options options) {
        ArgumentNullException.ThrowIfNull(argument: builder);
        ArgumentNullException.ThrowIfNull(argument: options);

        var document = builder.Build();
        var shapes = (document.Shapes ?? []);
        var rotationUsage = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var shape in shapes) {
            if (builder.TryRotationName(
                name: out var name,
                value: shape.Rotation.Value
            )) {
                rotationUsage[name] = (rotationUsage.GetValueOrDefault(key: name) + 1);
            }
        }

        var hoistedRotations = builder.NamedRotations
            .Where(predicate: entry => (options.HoistAllRotations || (rotationUsage.GetValueOrDefault(key: entry.Name) >= 2)))
            .ToList();
        var hoistedRotationNames = hoistedRotations.Select(selector: entry => entry.Name).ToHashSet(comparer: StringComparer.Ordinal);
        var scaleGroups = GroupScales(shapes: shapes);
        var scaleNameByValue = new Dictionary<Vector3, string>();

        foreach (var group in scaleGroups) {
            scaleNameByValue[group.Value] = group.Name;
        }

        var hoistedShapes = new List<ShapeDocument>(capacity: shapes.Count);

        foreach (var shape in shapes) {
            var rotation = shape.Rotation;

            if (builder.TryRotationName(
                name: out var rotationName,
                value: rotation.Value
            ) && hoistedRotationNames.Contains(item: rotationName)) {
                rotation = DocumentReferences.Quaternion(reference: $"state.{options.RotationsRow}.{rotationName}");
            }

            var scale = shape.Scale;

            if (scaleNameByValue.TryGetValue(
                key: scale.Value,
                value: out var scaleName
            )) {
                scale = DocumentReferences.Vector3(reference: $"state.{options.ScalesRow}.{scaleName}");
            }

            hoistedShapes.Add(item: shape with {
                Rotation = rotation,
                Scale = scale,
                Slides = HoistSlides(
                    options: options,
                    shapeName: (shape.Name?.Value ?? string.Empty),
                    slides: shape.Slides
                ),
                Swings = HoistSwings(
                    options: options,
                    shapeName: (shape.Name?.Value ?? string.Empty),
                    swings: shape.Swings
                ),
            });
        }

        var hoisted = (document with { Shapes = hoistedShapes });
        var rows = new List<JsonObject> {
            BuildTextRow(
                cells: hoistedRotations.ToDictionary(
                    keySelector: static entry => entry.Name,
                    elementSelector: static entry => VectorText(new[] { entry.Value.X, entry.Value.Y, entry.Value.Z, entry.Value.W })
                ),
                name: options.RotationsRow
            ),
        };

        if (scaleGroups.Count > 0) {
            rows.Add(item: BuildTextRow(
                cells: scaleGroups.ToDictionary(
                    keySelector: static group => group.Name,
                    elementSelector: static group => VectorText(new[] { group.Value.X, group.Value.Y, group.Value.Z })
                ),
                name: options.ScalesRow
            ));
        }

        return new Result(
            Document: hoisted,
            StateRows: rows
        );
    }

    private static IReadOnlyList<ShapeSwingDocument>? HoistSwings(IReadOnlyList<ShapeSwingDocument>? swings, string shapeName, Options options) {
        if (swings is not { Count: > 0 }) {
            return swings;
        }

        var result = new List<ShapeSwingDocument>(capacity: swings.Count);

        foreach (var swing in swings) {
            var pivot = swing.Pivot;

            if (
                (pivot.Reference is null) &&
                TryFindJoint(
                    joint: out var joint,
                    joints: options.Joints,
                    position: pivot.Value
                )
            ) {
                pivot = DocumentReferences.Vector3(reference: $"state.{options.JointsRow}.{joint}");
            }

            var amplitude = HoistAmplitude(
                amplitude: swing.Amplitude,
                driver: swing.Driver,
                options: options,
                shapeName: shapeName
            );

            result.Add(item: swing with { Amplitude = amplitude, Pivot = pivot });
        }

        return result;
    }
    private static IReadOnlyList<ShapeSlideDocument>? HoistSlides(IReadOnlyList<ShapeSlideDocument>? slides, string shapeName, Options options) {
        if (slides is not { Count: > 0 }) {
            return slides;
        }

        var result = new List<ShapeSlideDocument>(capacity: slides.Count);

        foreach (var slide in slides) {
            result.Add(item: (slide with {
                Amplitude = HoistAmplitude(
                    amplitude: slide.Amplitude,
                    driver: slide.Driver,
                    options: options,
                    shapeName: shapeName
                ),
            }));
        }

        return result;
    }
    private static DocumentScalar HoistAmplitude(DocumentScalar amplitude, string driver, string shapeName, Options options) {
        if (amplitude.Reference is not null) {
            return amplitude;
        }

        if (
            (options.TuningCellSelector(driver, shapeName) is not { } cell) ||
            !options.Tuning.TryGetValue(
                key: cell,
                value: out var tuned
            ) ||
            (MathF.Abs(x: (amplitude.Value - tuned)) >= AmplitudeEpsilon)
        ) {
            return amplitude;
        }

        return DocumentReferences.Scalar(reference: $"state.{options.TuningRow}.{cell}");
    }
    private static bool TryFindJoint(IReadOnlyList<SculptJoint> joints, Vector3 position, out string joint) {
        foreach (var candidate in joints) {
            if (Vector3.DistanceSquared(value1: candidate.Position, value2: position) <= JointEpsilonSquared) {
                joint = candidate.Name;

                return true;
            }
        }

        joint = string.Empty;

        return false;
    }
    // Groups shapes by their (already 4-decimal-rounded) scale, in first-seen order, keeping only groups with two
    // or more members — mirroring the Python post-pass's usage threshold. A group's name derives from its FIRST
    // member's shape name with trailing digits, then a lone trailing "L"/"R", stripped — "thighL"/"thighR" both
    // name the group "thigh"; a name collision (a second unrelated group deriving the same base) appends "x".
    private static IReadOnlyList<(string Name, Vector3 Value)> GroupScales(IReadOnlyList<ShapeDocument> shapes) {
        var order = new List<Vector3>();
        var membersByValue = new Dictionary<Vector3, List<string>>();

        foreach (var shape in shapes) {
            var scale = shape.Scale.Value;

            if (!membersByValue.TryGetValue(
                key: scale,
                value: out var members
            )) {
                members = [];
                membersByValue[scale] = members;
                order.Add(item: scale);
            }

            members.Add(item: (shape.Name?.Value ?? string.Empty));
        }

        var usedNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var groups = new List<(string, Vector3)>();

        foreach (var value in order) {
            var members = membersByValue[value];

            if (members.Count < 2) {
                continue;
            }

            var name = DeriveScaleBaseName(shapeName: members[0]);

            while (!usedNames.Add(item: name)) {
                name += "x";
            }

            groups.Add(item: (name, value));
        }

        return groups;
    }
    private static string DeriveScaleBaseName(string shapeName) {
        var end = shapeName.Length;

        while ((end > 0) && char.IsAsciiDigit(c: shapeName[end - 1])) {
            end--;
        }

        var trimmed = shapeName[..end];

        if ((trimmed.Length > 1) && (trimmed[^1] is ('L' or 'R'))) {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }
    private static JsonObject BuildTextRow(string name, IReadOnlyDictionary<string, string> cells) {
        var cellArray = new JsonArray();

        foreach (var (key, value) in cells) {
            cellArray.Add(value: new JsonObject { ["key"] = key, ["value"] = value });
        }

        return new JsonObject { ["cells"] = cellArray, ["kind"] = "Text", ["name"] = name };
    }
    private static string VectorText(IReadOnlyList<float> components) => $"[{string.Join(separator: ", ", values: components.Select(selector: static c => c.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)))}]";
}
