namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static readonly HashSet<string> ProbeControlNames = new(comparer: StringComparer.Ordinal) {
        "pan", "tilt", "zoom", "exposure", "focus", "brightness", "contrast",
        "saturation", "sharpness", "gain", "whiteBalance", "backlightCompensation", "fieldOfView",
    };

    // Vocabulary and structural shape only — the shallow half of the shallow-then-deep split
    // WorldRenderExtensionEntry.Config already establishes: a kind naming no registered probe kind, an probe
    // reference resolving to nothing, or a source colliding with another axis binding refuses here, at load, by name
    // and index. A channel name is never checkable here (the manifest lives behind WorldProbeVocabularyHook, which
    // answers only "is this kind registered", not "what channels does it declare") — the host checks it by name at
    // boot, the same precedent an extension's own config field follows.
    private static void ValidateProbes(WorldDefinition definition, List<string> errors) {
        if (definition.ProbesRaw is not { } probes) {
            return;
        }

        var probeIds = new HashSet<string>(comparer: StringComparer.Ordinal);
        var axisSources = new HashSet<string>(comparer: StringComparer.Ordinal);
        var localSeats = definition.Population.LocalSeats;

        for (var index = 0; (index < probes.Count); index++) {
            var probe = probes[index];
            var path = $"probes[{index}]";

            if (probe is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!WorldSafeName.TryParse(
                candidate: probe.Id,
                name: out _,
                reason: out var idReason
            )) {
                errors.Add(item: $"{path}.id {idReason}.");
            } else if (!probeIds.Add(item: probe.Id)) {
                errors.Add(item: $"{path}.id '{probe.Id}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(value: probe.Kind)) {
                errors.Add(item: $"{path}.kind is required.");
            } else if (!WorldProbeVocabularyHook.IsRegisteredProbeKind(kindId: probe.Kind)) {
                errors.Add(item: $"{path}.kind '{probe.Kind}' names no registered probe kind.");
            }

            if (
                (probe.RateHz < 1U) ||
                (probe.RateHz > 240U)
            ) {
                errors.Add(item: $"{path}.rateHz {probe.RateHz} is outside 1..240.");
            }

            switch (probe.Input) {
                case null:
                    errors.Add(item: $"{path}.input is required.");

                    break;
                case WorldProbeInput.Camera camera:
                    if (!Enum.IsDefined(value: camera.Sensor)) {
                        errors.Add(item: $"{path}.input.sensor '{camera.Sensor}' is not recognized.");
                    }

                    break;
                case WorldProbeInput.Track track:
                    if (string.IsNullOrWhiteSpace(value: track.Path)) {
                        errors.Add(item: $"{path}.input.path is required.");
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.input is an unrecognized probe input kind.");

                    break;
            }

            if (probe.Bindings is not { } bindings) {
                continue;
            }

            for (var bindingIndex = 0; (bindingIndex < bindings.Count); bindingIndex++) {
                ValidateProbeBinding(
                    axisSources: axisSources,
                    binding: bindings[bindingIndex],
                    definition: definition,
                    errors: errors,
                    localSeats: localSeats,
                    path: $"{path}.bindings[{bindingIndex}]"
                );
            }
        }
    }
    private static void ValidateProbeBinding(WorldDefinition definition, WorldProbeBinding? binding, HashSet<string> axisSources, int localSeats, List<string> errors, string path) {
        if (binding is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        var channel = (binding switch {
            WorldProbeBinding.Axis axis => axis.Channel,
            WorldProbeBinding.Parameter parameter => parameter.Channel,
            WorldProbeBinding.Control control => control.Channel,
            _ => string.Empty,
        });

        if (string.IsNullOrWhiteSpace(value: channel)) {
            errors.Add(item: $"{path}.channel is required.");
        }

        switch (binding) {
            case WorldProbeBinding.Axis axis:
                ValidateProbeAxisBinding(
                    axis: axis,
                    axisSources: axisSources,
                    errors: errors,
                    localSeats: localSeats,
                    path: path
                );

                break;
            case WorldProbeBinding.Parameter parameter:
                ValidateProbeParameterBinding(
                    definition: definition,
                    errors: errors,
                    parameter: parameter,
                    path: path
                );

                break;
            case WorldProbeBinding.Control control:
                ValidateProbeControlBinding(
                    control: control,
                    errors: errors,
                    path: path
                );

                break;
            default:
                errors.Add(item: $"{path} is an unrecognized probe binding kind.");

                break;
        }
    }
    private static void ValidateProbeAxisBinding(WorldProbeBinding.Axis axis, HashSet<string> axisSources, int localSeats, List<string> errors, string path) {
        if (
            !IsKebabCase(value: axis.Source) ||
            (axis.Source.Length > 64)
        ) {
            errors.Add(item: $"{path}.source '{axis.Source}' must be 1..64 lowercase kebab-case characters.");
        } else if (!axisSources.Add(item: axis.Source)) {
            errors.Add(item: $"{path}.source '{axis.Source}' is declared by more than one axis binding.");
        } else if (
            (InputSourceVocabularyHook.IsKnownSourceId is { } isKnown) &&
            !isKnown($"probe.{axis.Source}")
        ) {
            errors.Add(item: $"{path}.source 'probe.{axis.Source}' is not a declared input source id.");
        }

        if (
            !float.IsFinite(f: axis.Deadband) ||
            (axis.Deadband < 0f) ||
            (axis.Deadband >= 1f)
        ) {
            errors.Add(item: $"{path}.deadband {axis.Deadband} is outside [0, 1).");
        }

        if (
            !float.IsFinite(f: axis.Hysteresis) ||
            (axis.Hysteresis < 0f) ||
            (axis.Hysteresis >= 1f)
        ) {
            errors.Add(item: $"{path}.hysteresis {axis.Hysteresis} is outside [0, 1).");
        } else if (float.IsFinite(f: axis.Deadband) && (axis.Deadband >= 0f)) {
            // Activation requires a magnitude above deadband + hysteresis and release a magnitude below
            // deadband - hysteresis; both thresholds must lie inside the [0, 1) axis magnitude domain or one of the
            // two transitions can never happen.
            if (axis.Hysteresis > axis.Deadband) {
                errors.Add(item: $"{path}.hysteresis {axis.Hysteresis} exceeds deadband {axis.Deadband}; the gate could never release.");
            }

            if ((axis.Deadband + axis.Hysteresis) >= 1f) {
                errors.Add(item: $"{path}.deadband {axis.Deadband} plus hysteresis {axis.Hysteresis} reaches 1; the gate could never activate.");
            }
        }

        if (
            !float.IsFinite(f: axis.Smoothing) ||
            (axis.Smoothing < 0f) ||
            (axis.Smoothing > 1f)
        ) {
            errors.Add(item: $"{path}.smoothing {axis.Smoothing} is outside [0, 1].");
        }

        if (
            (axis.QuantizeBits < 1) ||
            (axis.QuantizeBits > 16)
        ) {
            errors.Add(item: $"{path}.quantizeBits {axis.QuantizeBits} is outside 1..16.");
        }

        if (
            !float.IsFinite(f: axis.MaxAgeSeconds) ||
            (axis.MaxAgeSeconds <= 0f)
        ) {
            errors.Add(item: $"{path}.maxAgeSeconds {axis.MaxAgeSeconds} must be finite and positive.");
        }

        if (
            (axis.Seat < 1) ||
            (axis.Seat > localSeats)
        ) {
            errors.Add(item: $"{path}.seat {axis.Seat} is outside 1..{localSeats} for the authored local seat count.");
        }
    }
    private static void ValidateProbeParameterBinding(WorldDefinition definition, WorldProbeBinding.Parameter parameter, List<string> errors, string path) {
        switch (parameter.Target) {
            case null:
                errors.Add(item: $"{path}.target is required.");

                break;
            case WorldProbeParameterTarget.Extension extension:
                if (string.IsNullOrWhiteSpace(value: extension.Field)) {
                    errors.Add(item: $"{path}.target.field is required.");
                }

                if (
                    string.IsNullOrWhiteSpace(value: extension.Id) ||
                    (definition.Render.Extensions is not { } extensions) ||
                    !extensions.Any(predicate: entry => ((entry is not null) && string.Equals(a: entry.Id, b: extension.Id, comparisonType: StringComparison.Ordinal)))
                ) {
                    errors.Add(item: $"{path}.target.id '{extension.Id}' names no composed render.extensions entry.");
                }

                break;
            default:
                errors.Add(item: $"{path}.target is an unrecognized probe parameter target kind.");

                break;
        }

        if (parameter.Range is not { } range) {
            errors.Add(item: $"{path}.range is required.");
        } else if (
            !float.IsFinite(f: range.X) ||
            !float.IsFinite(f: range.Y) ||
            (range.X >= range.Y)
        ) {
            errors.Add(item: $"{path}.range [{range.X}, {range.Y}] must be finite with a minimum below its maximum.");
        }

        if (
            !float.IsFinite(f: parameter.MaxAgeSeconds) ||
            (parameter.MaxAgeSeconds <= 0f)
        ) {
            errors.Add(item: $"{path}.maxAgeSeconds {parameter.MaxAgeSeconds} must be finite and positive.");
        }
    }
    private static void ValidateProbeControlBinding(WorldProbeBinding.Control control, List<string> errors, string path) {
        if (
            string.IsNullOrWhiteSpace(value: control.ControlName) ||
            !ProbeControlNames.Contains(item: control.ControlName)
        ) {
            errors.Add(item: $"{path}.control '{control.ControlName}' names no WorldCameraControls member.");
        }

        if (control.Minimum >= control.Maximum) {
            errors.Add(item: $"{path}.minimum {control.Minimum} must be below {path}.maximum {control.Maximum}.");
        }

        if (
            !float.IsFinite(f: control.MaxAgeSeconds) ||
            (control.MaxAgeSeconds <= 0f)
        ) {
            errors.Add(item: $"{path}.maxAgeSeconds {control.MaxAgeSeconds} must be finite and positive.");
        }
    }
}
