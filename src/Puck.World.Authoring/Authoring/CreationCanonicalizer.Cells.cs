using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

public static partial class CreationCanonicalizer {
    private static void ValidateCells(CreationDocument document, ShapeDocument shape, List<DocumentValidationError> errors, string path) {
        if (shape.Cells is not { } cells) { return; }
        void Refuse(string field, string message) => errors.Add(new(Path: path + "." + field, Message: message));
        if (!float.IsFinite(cells.Frequency) || cells.Frequency <= 0f || cells.Frequency > CreationNoiseDocument.MaxFrequency) {
            Refuse("frequency", $"cells frequency must be finite and in (0, {CreationNoiseDocument.MaxFrequency}].");
        }
        if (!float.IsFinite(cells.Amplitude) || cells.Amplitude < 0f || cells.Amplitude > CreationNoiseDocument.MaxAmplitude) {
            Refuse("amplitude", $"cells amplitude must be finite and in [0, {CreationNoiseDocument.MaxAmplitude}].");
        }
        if (!Enum.IsDefined(cells.Mode)) { Refuse("mode", "cells mode must be F1 or F2MinusF1."); }
        else if (!float.IsFinite(cells.Randomness) || cells.Randomness < 0f || cells.Randomness > SdfCellDisplacement.MaxRandomness(cells.Mode)) {
            Refuse("randomness", $"cells randomness must be finite and in [0, {SdfCellDisplacement.MaxRandomness(cells.Mode)}] for {cells.Mode}.");
        }
        if (!float.IsFinite(cells.Parameters.StepFactor) || cells.Parameters.StepFactor > CreationNoiseDocument.MaxStepFactor) {
            Refuse("amplitude", $"cells derivative bound exceeds {CreationNoiseDocument.MaxStepFactor}.");
        }
        // The VM has one field-scope save. A second scope cannot isolate this shape from a group's
        // accumulated siblings or a creation-wide field operation.
        if (shape.Group is not (null or 0) || CreationStampEmitter.RequiresScope(document)) {
            errors.Add(new(Path: path, Message: "cells requires a per-shape field scope; grouped shapes and creations requiring an outer field scope cannot isolate it."));
        }
        if (shape.Detail == true) {
            errors.Add(new(Path: path, Message: "cells cannot follow a detail-only shape, which is absent from the primary field."));
        }
    }
}
