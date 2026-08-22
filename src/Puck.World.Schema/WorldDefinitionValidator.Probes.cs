namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static readonly HashSet<string> ProbeControlNames = new(comparer: StringComparer.Ordinal) {
        "pan", "tilt", "zoom", "exposure", "focus", "brightness", "contrast",
        "saturation", "sharpness", "gain", "whiteBalance", "backlightCompensation", "fieldOfView",
    };

    // A socket name: an ASCII letter, then zero or more ASCII letters, digits, or hyphens — the grammar a probe
    // kind's manifest names its typed input slots with (e.g. "color", "strobe-pair"). Distinct from IsKebabCase
    // (WorldDefinitionValidator.Screens.cs), which forbids a leading digit but also forbids upper-case and a
    // trailing/doubled hyphen; a socket name admits both.
    private static bool IsSocketIdentifier(string value) {
        if (
            string.IsNullOrEmpty(value: value) ||
            !char.IsAsciiLetter(c: value[0])
        ) {
            return false;
        }

        for (var index = 1; (index < value.Length); index++) {
            var character = value[index];

            if (
                !char.IsAsciiLetterOrDigit(c: character) &&
                (character != '-')
            ) {
                return false;
            }
        }

        return true;
    }
    // Vocabulary and structural shape only — the shallow half of the shallow-then-deep split
    // WorldRenderExtensionEntry.Config already establishes: a kind naming no registered probe kind, a probe
    // reference resolving to nothing, or a source colliding with another axis binding refuses here, at load, by name
    // and index. A channel name is never checkable here (the manifest lives behind WorldProbeVocabularyHook, which
    // answers only "is this kind registered", not "what channels does it declare") — the host checks it by name at
    // boot, the same precedent an extension's own config field follows.
    private static void ValidateProbes(WorldDefinition definition, HashSet<string> cameras, List<string> errors) {
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

            ValidateProbeStream(
                cameras: cameras,
                definition: definition,
                errors: errors,
                path: path,
                probe: probe
            );

            if (probe.Bindings is not { } bindings) {
                continue;
            }

            var isSeatRelative = IsSeatRelativeProbe(probe: probe);

            for (var bindingIndex = 0; (bindingIndex < bindings.Count); bindingIndex++) {
                ValidateProbeBinding(
                    axisSources: axisSources,
                    binding: bindings[bindingIndex],
                    definition: definition,
                    errors: errors,
                    isSeatRelative: isSeatRelative,
                    localSeats: localSeats,
                    path: $"{path}.bindings[{bindingIndex}]",
                    probeId: probe.Id
                );
            }
        }
    }
    // A probe row is seat-relative — instanced once per occupied local seat rather than once for the whole world —
    // exactly when at least one of its declared camera sockets carries no `seat` of its own (the socket then
    // resolves against its own instance's seat). A track-input row (no camera sockets at all) is never seat-relative.
    private static bool IsSeatRelativeProbe(WorldProbe probe) {
        if (probe.Inputs is not { } inputs) {
            return false;
        }

        foreach (var (_, source) in inputs) {
            if ((source is WorldScreenSource.Camera camera) && (camera.Seat is null)) {
                return true;
            }
        }

        return false;
    }
    // Exactly one of inputs/track: the live-hardware leg (named sockets, each a shared WorldFrameSource — the same
    // vocabulary and gate a screen row's frame arms use, ValidateFrameSource) or the recorded-track leg (one
    // document standing in for the whole set). A socket's own NAME grammar and the map's non-emptiness are checked
    // here; the socket VOCABULARY a kind declares is not (it lives behind WorldProbeVocabularyHook, which answers
    // only "is this kind registered" — the host checks a bound name against the kind's own manifest at boot). The
    // one probe-specific rule ValidateFrameSource itself cannot see: a socket binding a WorldScreenSource.Probe
    // naming its OWN enclosing row — a probe cannot steer itself, the same self-reference refusal
    // ValidateProbeParameterBinding already carries for a parameter target.
    private static void ValidateProbeStream(WorldDefinition definition, WorldProbe probe, string path, HashSet<string> cameras, List<string> errors) {
        var hasInputs = (probe.Inputs is not null);
        var hasTrack = (probe.Track is not null);

        if (hasInputs && hasTrack) {
            errors.Add(item: $"{path} declares both 'inputs' and 'track' — a probe reads live sockets or plays back a recorded track, never both.");
        } else if (!hasInputs && !hasTrack) {
            errors.Add(item: $"{path} declares neither 'inputs' nor 'track' — a probe needs exactly one input leg.");
        }

        if (probe.Inputs is { } inputs) {
            if (inputs.Count == 0) {
                errors.Add(item: $"{path}.inputs must declare at least one socket — omit the member entirely for the track leg instead.");
            }

            var hasCameraSeat = false;
            int? cameraSeat = null;

            foreach (var (socket, source) in inputs) {
                var socketPath = $"{path}.inputs['{socket}']";

                if (!IsSocketIdentifier(value: socket)) {
                    errors.Add(item: $"{socketPath} '{socket}' must be an identifier (a letter, then letters, digits, or hyphens).");
                }

                ValidateFrameSource(
                    cameras: cameras,
                    definition: definition,
                    errors: errors,
                    path: socketPath,
                    source: source
                );

                if (source is WorldScreenSource.Capture) {
                    errors.Add(item: $"{socketPath} binds a capture source, but probe kernels do not host capture inputs.");
                }
                if (source is WorldScreenSource.Camera camera) {
                    if (hasCameraSeat && (camera.Seat != cameraSeat)) {
                        errors.Add(item: $"{socketPath}.camera.seat must match every other camera socket in the probe; one kernel run can bind only one camera graph.");
                    } else if (!hasCameraSeat) {
                        hasCameraSeat = true;
                        cameraSeat = camera.Seat;
                    }
                    if (camera.Controls is not null) {
                        errors.Add(item: $"{socketPath}.camera.controls is not supported on probe inputs; author device controls on a camera screen.");
                    }
                }

                if (
                    (source is WorldScreenSource.Probe target) &&
                    string.Equals(a: target.Id, b: probe.Id, comparisonType: StringComparison.Ordinal)
                ) {
                    errors.Add(item: $"{socketPath}.probe.id '{target.Id}' is the enclosing probe; a socket cannot bind its own probe.");
                }
            }
        }

        if (
            hasTrack &&
            string.IsNullOrWhiteSpace(value: probe.Track)
        ) {
            errors.Add(item: $"{path}.track is required.");
        }
    }
    // A probe id reference resolves against the declared rows by name — the same shallow check a parameter target's
    // extension id gets.
    private static bool DeclaresProbe(WorldDefinition definition, string? id) {
        if (string.IsNullOrWhiteSpace(value: id) || (definition.ProbesRaw is not { } probes)) {
            return false;
        }

        foreach (var probe in probes) {
            if ((probe is not null) && string.Equals(a: probe.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
    private static void ValidateProbeBinding(WorldDefinition definition, WorldProbeBinding? binding, HashSet<string> axisSources, bool isSeatRelative, int localSeats, List<string> errors, string path, string? probeId) {
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
                    isSeatRelative: isSeatRelative,
                    localSeats: localSeats,
                    path: path
                );

                break;
            case WorldProbeBinding.Parameter parameter:
                ValidateProbeParameterBinding(
                    definition: definition,
                    errors: errors,
                    parameter: parameter,
                    path: path,
                    probeId: probeId
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
    private static void ValidateProbeAxisBinding(WorldProbeBinding.Axis axis, HashSet<string> axisSources, bool isSeatRelative, int localSeats, List<string> errors, string path) {
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

        if (isSeatRelative) {
            if (axis.Seat is not null) {
                errors.Add(item: $"{path}.seat is authored on a seat-relative probe's axis binding; a seat-relative probe's axis bindings take the instance's seat.");
            }
        } else if (
            (axis.Seat is { } seat) &&
            ((seat < 1) || (seat > localSeats))
        ) {
            errors.Add(item: $"{path}.seat {seat} is outside 1..{localSeats} for the authored local seat count.");
        }
    }
    private static void ValidateProbeParameterBinding(WorldDefinition definition, WorldProbeBinding.Parameter parameter, List<string> errors, string path, string? probeId) {
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
            case WorldProbeParameterTarget.Probe target:
                if (string.IsNullOrWhiteSpace(value: target.Field)) {
                    errors.Add(item: $"{path}.target.field is required.");
                }

                if (!DeclaresProbe(definition: definition, id: target.Id)) {
                    errors.Add(item: $"{path}.target.id '{target.Id}' names no declared probe.");
                } else if (string.Equals(a: target.Id, b: probeId, comparisonType: StringComparison.Ordinal)) {
                    errors.Add(item: $"{path}.target.id '{target.Id}' is the binding's own probe; a probe cannot steer itself.");
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
