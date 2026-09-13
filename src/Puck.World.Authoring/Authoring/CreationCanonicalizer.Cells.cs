using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

public static partial class CreationCanonicalizer {
    private static void ValidateCells(CreationDocument document, ShapeDocument shape, List<DocumentValidationError> errors, string path) {
        if (shape.Cells is not { } cells) { return; }
        void Refuse(string field, string message) => errors.Add(item: new(
            Message: message,
            Path: ((path + ".") + field)
        ));
        if (
            !float.IsFinite(f: cells.Frequency) ||
            (cells.Frequency <= 0f) ||
            (cells.Frequency > CreationNoiseDocument.MaxFrequency)
        ) {
            Refuse(
                field: "frequency",
                message: $"cells frequency must be finite and in (0, {CreationNoiseDocument.MaxFrequency}]."
            );
        }
        if (
            !float.IsFinite(f: cells.Amplitude) ||
            (cells.Amplitude < 0f) ||
            (cells.Amplitude > CreationNoiseDocument.MaxAmplitude)
        ) {
            Refuse(
                field: "amplitude",
                message: $"cells amplitude must be finite and in [0, {CreationNoiseDocument.MaxAmplitude}]."
            );
        }
        if (!Enum.IsDefined(value: cells.Mode)) { Refuse(
            field: "mode",
            message: "cells mode must be F1 or F2MinusF1."
        ); } else if (
            !float.IsFinite(f: cells.Randomness) ||
            (cells.Randomness < 0f) ||
            (cells.Randomness > SdfCellDisplacement.MaxRandomness(mode: cells.Mode))
        ) {
            Refuse(
                field: "randomness",
                message: $"cells randomness must be finite and in [0, {SdfCellDisplacement.MaxRandomness(mode: cells.Mode)}] for {cells.Mode}."
            );
        }
        if (
            !float.IsFinite(f: cells.Parameters.StepFactor) ||
            (cells.Parameters.StepFactor > CreationNoiseDocument.MaxStepFactor)
        ) {
            Refuse(
                field: "amplitude",
                message: $"cells derivative bound exceeds {CreationNoiseDocument.MaxStepFactor}."
            );
        }
        // The VM has one field-scope save. A second scope cannot isolate this shape from a group's
        // accumulated siblings or a creation-wide field operation.
        if (
            (shape.Group is not (null or 0)) ||
            CreationStampEmitter.RequiresScope(document: document)
        ) {
            errors.Add(item: new(
                Message: "cells requires a per-shape field scope; grouped shapes and creations requiring an outer field scope cannot isolate it.",
                Path: path
            ));
        }
        if (shape.Type == SdfSolidPrimitive.Sweep) {
            errors.Add(item: new(
                Message: "cells requires a closed primitive; Sweep has no bounded solid offset contract.",
                Path: path
            ));
        }
        if (shape.Detail == true) {
            errors.Add(item: new(
                Message: "cells cannot follow a detail-only shape, which is absent from the primary field.",
                Path: path
            ));
        }
    }
}
