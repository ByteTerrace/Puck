using System.Text.Json;

namespace Puck.Authoring;

/// <summary>Specifies the oscillator a synth voice renders — the deterministic fixed-point repertoire of the world
/// voice synth (sine as a complex rotor, phase-accumulator pulse/saw/triangle, a seeded pseudo-random noise
/// stream).</summary>
public enum SynthOscillator {
    /// <summary>A rectangular pulse wave; <see cref="SynthPatchDocument.DutyThousandths"/> sets its duty cycle.</summary>
    Pulse,
    /// <summary>A sawtooth wave.</summary>
    Saw,
    /// <summary>A triangle wave.</summary>
    Triangle,
    /// <summary>A sine wave.</summary>
    Sine,
    /// <summary>A seeded pseudo-random noise stream; <see cref="SynthPatchDocument.Polynomial"/> selects its
    /// character.</summary>
    Noise,
}

/// <summary>
/// The <c>puck.synth.v1</c> document — one deterministic synth PATCH as data: the parameter set a world voice synth
/// renders a creature/phenomenon sound from, integer end to end (no wall-clock, RNG seeds arrive per-trigger, no
/// float anywhere in the schema). Every field is a RUNTIME unit — frames are mixer-rate audio frames (48000 per
/// second) and pitch rides integer millihertz — so a renderer consumes the document
/// without a conversion layer, and the parameter space stays hardware-kin (compilable to APU register writes if ever
/// wanted). A patch is used without any tune: it is the contract creature sounds and world emission facets embed.
/// Document doctrine applies throughout: every OPTIONAL member is nullable, validated only when present, and
/// normalized through <see cref="SynthPatchCanonicalizer"/>.
/// </summary>
/// <param name="Schema">The document version tag (<c>puck.synth.v1</c>).</param>
/// <param name="Name">The patch's display name (null = "patch").</param>
/// <param name="Oscillator">The oscillator kind (null = <see cref="SynthOscillator.Pulse"/>).</param>
/// <param name="DutyThousandths">The pulse duty cycle in thousandths of a period, 1..999 (null = 500 — a square
/// wave). Meaningful only when <paramref name="Oscillator"/> is <see cref="SynthOscillator.Pulse"/>; normalization
/// clears it elsewhere.</param>
/// <param name="Polynomial">The noise character selector, 0..255 — hardware-kin (an NR43-style polynomial byte; 0 =
/// the synth's neutral character). Meaningful only when <paramref name="Oscillator"/> is
/// <see cref="SynthOscillator.Noise"/>; normalization clears it elsewhere.</param>
/// <param name="AttackFrames">The envelope attack length in audio frames, 0..<see cref="MaxFrames"/> (null = 0 —
/// instant on).</param>
/// <param name="DecayFrames">The envelope decay length in audio frames, 0..<see cref="MaxFrames"/> (null = 0).</param>
/// <param name="SustainThousandths">The sustain level in thousandths of full scale, 0..1000 (null = 1000 — sustain
/// at peak).</param>
/// <param name="ReleaseFrames">The envelope release length in audio frames, 0..<see cref="MaxFrames"/> (null = 0 —
/// instant off).</param>
/// <param name="PitchMillihertz">The base pitch in millihertz, 1..<see cref="MaxPitchMillihertz"/> (440 Hz =
/// 440000). Required — a patch without a pitch has no defined voice.</param>
/// <param name="SweepMillihertzPerFrame">The linear pitch sweep in millihertz per audio frame, signed,
/// |value| ≤ <see cref="MaxPitchMillihertz"/> (null = no sweep).</param>
/// <param name="VibratoDepthMillihertz">The vibrato depth (peak pitch deviation) in millihertz,
/// 0..<see cref="MaxPitchMillihertz"/>. Travels with <paramref name="VibratoRateMillihertz"/> — declaring one
/// without the other is a validation error (null pair = no vibrato).</param>
/// <param name="VibratoRateMillihertz">The vibrato rate (modulation frequency) in millihertz,
/// 1..<see cref="MaxPitchMillihertz"/>.</param>
/// <param name="DurationFrames">The voice's total length in audio frames, 1..<see cref="MaxFrames"/>; null = loop
/// until released.</param>
public sealed record SynthPatchDocument(
    string? Schema,
    string? Name,
    SynthOscillator? Oscillator,
    int? DutyThousandths,
    int? Polynomial,
    int? AttackFrames,
    int? DecayFrames,
    int? SustainThousandths,
    int? ReleaseFrames,
    int PitchMillihertz,
    int? SweepMillihertzPerFrame = null,
    int? VibratoDepthMillihertz = null,
    int? VibratoRateMillihertz = null,
    int? DurationFrames = null
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.synth.v1";

    /// <summary>The largest pitch-shaped value the schema admits, in millihertz: the Nyquist frequency of the
    /// 48000 Hz mixer — a contract invariant of the runtime unit, not a tunable (a pitch beyond it cannot be
    /// rendered at the mixer rate).</summary>
    public const int MaxPitchMillihertz = 24_000_000;

    /// <summary>The largest frame-count value the schema admits (60 seconds at the 48000 Hz mixer rate) — the
    /// sanity ceiling that separates a VOICE from a STREAM: content longer than this is a tune (machine-backed
    /// music is the tracker's job), not a synth patch.</summary>
    public const int MaxFrames = (48_000 * 60);

    /// <summary>Unknown members preserved across a round-trip — the data-side plugin extensibility posture. Null
    /// when the document carries no unknown members. A settable (not <c>init</c>)
    /// accessor is required: System.Text.Json appends to it during deserialization.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>
/// THE strict validate → normalize → canonicalize boundary every <see cref="SynthPatchDocument"/> crosses before it
/// is trusted, persisted, or embedded — the synth family's adapter over <see cref="DocumentCanonicalizer"/>: an
/// absent or foreign schema rejects loudly (never a silent relabel), every violation is collected in one pass, and
/// canonical bytes + hash always come from the same result.
/// </summary>
public static class SynthPatchCanonicalizer {
    private static readonly HashSet<string> KnownMemberNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "schema", "name", "oscillator", "dutyThousandths", "polynomial", "attackFrames", "decayFrames",
        "sustainThousandths", "releaseFrames", "pitchMillihertz", "sweepMillihertzPerFrame",
        "vibratoDepthMillihertz", "vibratoRateMillihertz", "durationFrames",
    };

    /// <summary>Validates a document's schema and structural invariants in one pass — every violation is collected
    /// rather than throwing on the first. An absent or foreign <see cref="SynthPatchDocument.Schema"/>
    /// short-circuits to that one violation, since no other check has a defined meaning against an unrecognized
    /// document shape. Every bound is loud: an out-of-range value is an authoring error a clamp would silently
    /// rewrite, so nothing here self-heals except the cross-oscillator field clears
    /// <see cref="Normalize"/> owns.</summary>
    /// <param name="document">The document to validate, as deserialized — not yet normalized.</param>
    /// <returns>Every violation found; empty when the document is a valid <c>puck.synth.v1</c> value.</returns>
    public static IReadOnlyList<DocumentValidationError> Validate(SynthPatchDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (DocumentCanonicalizer.SchemaViolationMessage(declared: document.Schema, recognized: SynthPatchDocument.CurrentSchema) is { } schemaViolation) {
            return [new DocumentValidationError(Path: "schema", Message: schemaViolation)];
        }

        var errors = new List<DocumentValidationError>();

        if ((document.Oscillator is { } oscillator) && !Enum.IsDefined(value: oscillator)) {
            errors.Add(item: new(Path: "oscillator", Message: $"'{(int)oscillator}' is not a defined oscillator kind."));
        }

        ValidateRange(errors: errors, max: 999, min: 1, path: "dutyThousandths", value: document.DutyThousandths);
        ValidateRange(errors: errors, max: 255, min: 0, path: "polynomial", value: document.Polynomial);
        ValidateRange(errors: errors, max: SynthPatchDocument.MaxFrames, min: 0, path: "attackFrames", value: document.AttackFrames);
        ValidateRange(errors: errors, max: SynthPatchDocument.MaxFrames, min: 0, path: "decayFrames", value: document.DecayFrames);
        ValidateRange(errors: errors, max: 1000, min: 0, path: "sustainThousandths", value: document.SustainThousandths);
        ValidateRange(errors: errors, max: SynthPatchDocument.MaxFrames, min: 0, path: "releaseFrames", value: document.ReleaseFrames);
        ValidateRange(errors: errors, max: SynthPatchDocument.MaxPitchMillihertz, min: 1, path: "pitchMillihertz", value: document.PitchMillihertz);
        ValidateRange(errors: errors, max: SynthPatchDocument.MaxPitchMillihertz, min: -SynthPatchDocument.MaxPitchMillihertz, path: "sweepMillihertzPerFrame", value: document.SweepMillihertzPerFrame);
        ValidateRange(errors: errors, max: SynthPatchDocument.MaxPitchMillihertz, min: 0, path: "vibratoDepthMillihertz", value: document.VibratoDepthMillihertz);
        ValidateRange(errors: errors, max: SynthPatchDocument.MaxPitchMillihertz, min: 1, path: "vibratoRateMillihertz", value: document.VibratoRateMillihertz);
        ValidateRange(errors: errors, max: SynthPatchDocument.MaxFrames, min: 1, path: "durationFrames", value: document.DurationFrames);

        // The vibrato pair travels together: a depth with no rate (or a rate with no depth) has no renderable
        // meaning, and inventing the missing half would be a silent rewrite.
        if ((document.VibratoDepthMillihertz is not null) != (document.VibratoRateMillihertz is not null)) {
            errors.Add(item: new(Path: "vibratoDepthMillihertz", Message: "vibrato depth and rate travel together — declare both or neither."));
        }

        DocumentCanonicalizer.ValidateExtensions(
            addError: (path, message) => errors.Add(item: new(Path: path, Message: message)),
            extensions: document.Extensions,
            knownMemberNames: KnownMemberNames
        );

        return errors;
    }

    /// <summary>Runs <see cref="Validate"/> and throws when it finds anything.</summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="source">An optional source label (a file path or asset id) for the exception message.</param>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a
    /// structural invariant.</exception>
    public static void ValidateOrThrow(SynthPatchDocument document, string? source = null) {
        var errors = Validate(document: document);

        if (errors.Count > 0) {
            throw new DocumentValidationException(errors: errors, source: source);
        }
    }

    /// <summary>Normalizes an already-schema-valid document: defaults every optional member so a renderer never sees
    /// a null it has to reason about, and clears the cross-oscillator fields that carry no meaning for the declared
    /// kind (a duty on a noise patch, a polynomial on a sine — the self-heal twin of the creation family's stale-
    /// reference drops). Idempotent — <c>Normalize(Normalize(x))</c> equals <c>Normalize(x)</c>. Does NOT itself
    /// validate; callers cross <see cref="ValidateOrThrow"/> first (<see cref="Canonicalize"/> always does).</summary>
    /// <param name="document">The document to normalize.</param>
    /// <returns>The normalized document (unknown-member <see cref="SynthPatchDocument.Extensions"/> ride along).</returns>
    public static SynthPatchDocument Normalize(SynthPatchDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var oscillator = (document.Oscillator ?? SynthOscillator.Pulse);

        return (document with {
            AttackFrames = (document.AttackFrames ?? 0),
            DecayFrames = (document.DecayFrames ?? 0),
            DutyThousandths = ((oscillator == SynthOscillator.Pulse) ? (document.DutyThousandths ?? 500) : null),
            Name = (string.IsNullOrWhiteSpace(value: document.Name) ? "patch" : document.Name.Trim()),
            Oscillator = oscillator,
            Polynomial = ((oscillator == SynthOscillator.Noise) ? (document.Polynomial ?? 0) : null),
            ReleaseFrames = (document.ReleaseFrames ?? 0),
            Schema = SynthPatchDocument.CurrentSchema,
            SustainThousandths = (document.SustainThousandths ?? 1000),
        });
    }

    /// <summary>THE full pipeline: validates schema + structural invariants (throwing on either), normalizes the
    /// defaults, then serializes to canonical UTF-8 bytes and hashes them through
    /// <see cref="DocumentCanonicalizer.Canonicalize"/>. Two calls against value-equal input documents always
    /// produce byte-identical bytes and therefore the same hash — the identity contract an inline-canonical world
    /// patch row pins.</summary>
    /// <param name="document">The document to canonicalize.</param>
    /// <param name="source">An optional source label (a file path or asset id) for a validation-failure message.</param>
    /// <returns>The validated, normalized document plus its canonical bytes and hash.</returns>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a
    /// structural invariant.</exception>
    public static CanonicalDocument<SynthPatchDocument> Canonicalize(SynthPatchDocument document, string? source = null) {
        ValidateOrThrow(document: document, source: source);

        return DocumentCanonicalizer.Canonicalize(document: Normalize(document: document));
    }

    private static void ValidateRange(List<DocumentValidationError> errors, int max, int min, string path, int? value) {
        if ((value is { } present) && ((present < min) || (present > max))) {
            errors.Add(item: new(Path: path, Message: $"{present} is outside [{min}, {max}]."));
        }
    }
}
