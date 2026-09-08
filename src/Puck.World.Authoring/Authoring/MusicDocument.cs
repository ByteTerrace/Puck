using System.Text.Json;
using Puck.Assets.Documents;

namespace Puck.World.Authoring;

/// <summary>When an armed transition actually swaps segments, mirroring
/// <c>Puck.Audio.Simulation.MusicTransitionBoundary</c> as a document-side, string-persisted spelling — this
/// document layer never references the sim-side enum (a lower-rank engine-services project cannot be referenced
/// from an authoring-time document type without inverting the dependency).</summary>
public enum MusicTransitionBoundary {
    /// <summary>Swaps on the same tick the condition is observed.</summary>
    Immediate,
    /// <summary>Swaps on the next beat boundary at or after the condition is observed.</summary>
    BeatEnd,
    /// <summary>Swaps on the next bar boundary at or after the condition is observed.</summary>
    BarEnd,
}
/// <summary>The authored tempo map: one beat's engine-tick length plus how many beats make a bar.</summary>
/// <param name="BeatsPerBar">The beat count of one bar (null = 4).</param>
/// <param name="TicksPerBeat">The positive engine-tick length of one beat. Must evenly divide the engine's fixed
/// ticks-per-second base — the same divisibility discipline <c>simulation.rateHz</c> already carries.</param>
public sealed record MusicTempoDocument(int? BeatsPerBar, int TicksPerBeat);
/// <summary>One authored segment transition: the destination segment, the sense condition that arms it, and the
/// boundary it waits for before committing.</summary>
/// <param name="To">The destination segment's <see cref="MusicSegmentDocument.Id"/> (must resolve to a sibling
/// segment).</param>
/// <param name="When">The event token that arms this transition (must be one of the world schema's sense-mappable
/// <c>when</c> subset, <c>WorldAudioCue.MusicWhenTokens</c> — validated one layer up, not here, since this document
/// layer cannot reference that vocabulary's owner).</param>
/// <param name="At">The boundary the armed transition waits for (null = <see cref="MusicTransitionBoundary.BarEnd"/>).</param>
public sealed record MusicTransitionDocument(string To, string When, MusicTransitionBoundary? At);
/// <summary>One authored conditional audio layer: a tune that plays as a simultaneous bed alongside the segment's own
/// score while its condition holds — a segment can declare several, layering (an "alert" segment might always carry
/// a percussion bed plus a brass layer that only joins while a boss body is present). Never queues or waits for a
/// boundary like <see cref="MusicTransitionDocument"/>: a layer is level-triggered, active exactly the ticks its
/// condition holds.</summary>
/// <param name="TuneId">The referenced tune's declared name (must resolve to a sibling <c>WorldTune</c> row —
/// validated one layer up, not here, since this document layer cannot reference that vocabulary's owner).</param>
/// <param name="GainThousandths">The layer's authored gain in thousandths (1000 = unity), or <see langword="null"/>
/// for unity; range-checked against the shared audio gain ceiling one layer up, not here (same reason as
/// <see cref="MusicTransitionDocument.When"/>). Reserved for a future presentation wiring pass — round-trips through
/// validation/canonicalization but does not yet affect the derived bed's live gain (the same "authored, not yet a
/// producer" posture <c>WorldAudioCue.PlayerJump</c> already carries).</param>
/// <param name="When">The event token gating this layer on top of segment membership (same sense-mappable
/// vocabulary <see cref="MusicTransitionDocument.When"/> already uses — validated one layer up), or
/// <see langword="null"/> for "active whenever this segment is current" (the base, unconditional case).</param>
public sealed record MusicLayerDocument(string TuneId, int? GainThousandths, string? When);
/// <summary>One authored director embellishment: a one-shot stinger voiced the instant its condition is observed —
/// never queued, never waiting for a beat/bar boundary like <see cref="MusicTransitionDocument"/>, and never a
/// looping bed like <see cref="MusicLayerDocument"/>. A segment can declare several, each with its own patch (a
/// jump-scare cue, a fanfare on an objective edge).</summary>
/// <param name="PatchId">The referenced synth patch's declared name (must resolve to a sibling <c>WorldPatch</c> row —
/// validated one layer up, not here).</param>
/// <param name="When">The event token that fires this embellishment (same sense-mappable vocabulary
/// <see cref="MusicTransitionDocument.When"/> already uses — validated one layer up). Required: an embellishment
/// with no firing condition can never sound.</param>
/// <param name="GainThousandths">The embellishment's authored gain in thousandths (1000 = unity), or
/// <see langword="null"/> for unity; range-checked one layer up, not here — see
/// <see cref="MusicLayerDocument.GainThousandths"/>'s remarks. Reserved for a future presentation wiring pass — see
/// the same remarks.</param>
public sealed record MusicEmbellishmentDocument(string PatchId, string When, int? GainThousandths);
/// <summary>One authored segment: its stable id, the transitions it can arm, the conditional audio layers it can
/// carry, and the one-shot embellishments it can fire — evaluated in declared order.</summary>
/// <param name="Id">The segment's stable id, unique within the document.</param>
/// <param name="Transitions">The segment's outgoing transitions (null = none — a terminal segment).</param>
/// <param name="Layers">The segment's conditional audio layers (null = none — the segment's own score plays alone).</param>
/// <param name="Embellishments">The segment's one-shot director embellishments (null = none).</param>
public sealed record MusicSegmentDocument(
    string Id,
    IReadOnlyList<MusicTransitionDocument>? Transitions,
    IReadOnlyList<MusicLayerDocument>? Layers = null,
    IReadOnlyList<MusicEmbellishmentDocument>? Embellishments = null
);
/// <summary>
/// The <c>puck.music.v1</c> document — the adaptive segment-transition layer over the tracker grain
/// <see cref="AudioDocument"/> already establishes: a tempo map plus named segments and the conditions that
/// transition between them. Sits one layer above <see cref="AudioDocument"/>; does not replace it. Every field is
/// tick-denominated — no wall-clock, no float — so a <c>MusicClock</c> renders the document without a conversion
/// layer.
/// </summary>
/// <param name="Schema">The document version tag (<c>puck.music.v1</c>).</param>
/// <param name="Name">The score's display name (null = "score").</param>
/// <param name="Tempo">The authored tempo map. Required — a score without a tempo has no defined clock.</param>
/// <param name="Segments">The declared segments, at least one; the first is where a new director starts.</param>
public sealed record MusicDocument(
    string? Schema,
    string? Name,
    MusicTempoDocument Tempo,
    IReadOnlyList<MusicSegmentDocument> Segments
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.music.v1";

    /// <summary>Unknown members preserved across a round-trip. Null when the document carries no unknown members.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}
/// <summary>
/// THE strict validate → normalize → canonicalize boundary every <see cref="MusicDocument"/> crosses before it is
/// trusted, persisted, or embedded — mirrors <see cref="SynthPatchCanonicalizer"/>'s shape exactly. Structural
/// checks only (schema tag, tempo positivity, segment/transition/layer/embellishment shape, cross-segment reference
/// resolution): the sense-mappable <c>when</c> vocabulary a transition/layer/embellishment must name
/// (<c>WorldAudioCue.MusicWhenTokens</c>), the tune/patch ids a layer/embellishment references, and the tempo's
/// engine-tick divisibility, are all checked one layer up, by the world schema that owns those facts.
/// </summary>
public static class MusicCanonicalizer {
    private static readonly HashSet<string> KnownMemberNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "schema", "name", "tempo", "segments",
    };

    /// <summary>Validates a document's schema and structural invariants in one pass — every violation is collected
    /// rather than throwing on the first.</summary>
    /// <param name="document">The document to validate, as deserialized — not yet normalized.</param>
    /// <returns>Every violation found; empty when the document is a valid <c>puck.music.v1</c> value.</returns>
    public static IReadOnlyList<DocumentValidationError> Validate(MusicDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (DocumentCanonicalizer.SchemaViolationMessage(declared: document.Schema, recognized: MusicDocument.CurrentSchema) is { } schemaViolation) {
            return [new DocumentValidationError(Message: schemaViolation, Path: "schema")];
        }

        var errors = new List<DocumentValidationError>();

        if (document.Tempo is not { } tempo) {
            errors.Add(item: new(Message: "tempo is required.", Path: "tempo"));
        } else {
            if (tempo.TicksPerBeat <= 0) {
                errors.Add(item: new(Message: $"{tempo.TicksPerBeat} must be positive.", Path: "tempo.ticksPerBeat"));
            }

            if ((tempo.BeatsPerBar is { } beatsPerBar) && (beatsPerBar <= 0)) {
                errors.Add(item: new(Message: $"{beatsPerBar} must be positive.", Path: "tempo.beatsPerBar"));
            }
        }

        if (document.Segments is not { Count: > 0 } segments) {
            errors.Add(item: new(Message: "at least one segment is required.", Path: "segments"));
        } else {
            var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < segments.Count); index++) {
                var segment = segments[index];
                var path = $"segments[{index}]";

                if (segment is null) {
                    errors.Add(item: new(Message: "is required.", Path: path));

                    continue;
                }

                if (string.IsNullOrWhiteSpace(value: segment.Id)) {
                    errors.Add(item: new(Message: "id is required.", Path: $"{path}.id"));
                } else if (!ids.Add(item: segment.Id)) {
                    errors.Add(item: new(Message: $"'{segment.Id}' is duplicated.", Path: $"{path}.id"));
                }

                var transitions = (segment.Transitions ?? []);

                for (var transitionIndex = 0; (transitionIndex < transitions.Count); transitionIndex++) {
                    var transition = transitions[transitionIndex];
                    var transitionPath = $"{path}.transitions[{transitionIndex}]";

                    if (transition is null) {
                        errors.Add(item: new(Message: "is required.", Path: transitionPath));

                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(value: transition.To)) {
                        errors.Add(item: new(Message: "to is required.", Path: $"{transitionPath}.to"));
                    }

                    if (string.IsNullOrWhiteSpace(value: transition.When)) {
                        errors.Add(item: new(Message: "when is required.", Path: $"{transitionPath}.when"));
                    }

                    if ((transition.At is { } at) && !Enum.IsDefined(value: at)) {
                        errors.Add(item: new(Message: $"'{((int)at)}' is not a defined boundary.", Path: $"{transitionPath}.at"));
                    }
                }

                var layers = (segment.Layers ?? []);

                for (var layerIndex = 0; (layerIndex < layers.Count); layerIndex++) {
                    var layer = layers[layerIndex];
                    var layerPath = $"{path}.layers[{layerIndex}]";

                    if (layer is null) {
                        errors.Add(item: new(Message: "is required.", Path: layerPath));

                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(value: layer.TuneId)) {
                        errors.Add(item: new(Message: "tuneId is required.", Path: $"{layerPath}.tuneId"));
                    }
                }

                var embellishments = (segment.Embellishments ?? []);

                for (var embellishmentIndex = 0; (embellishmentIndex < embellishments.Count); embellishmentIndex++) {
                    var embellishment = embellishments[embellishmentIndex];
                    var embellishmentPath = $"{path}.embellishments[{embellishmentIndex}]";

                    if (embellishment is null) {
                        errors.Add(item: new(Message: "is required.", Path: embellishmentPath));

                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(value: embellishment.PatchId)) {
                        errors.Add(item: new(Message: "patchId is required.", Path: $"{embellishmentPath}.patchId"));
                    }

                    if (string.IsNullOrWhiteSpace(value: embellishment.When)) {
                        errors.Add(item: new(Message: "when is required.", Path: $"{embellishmentPath}.when"));
                    }
                }
            }

            // Cross-segment references resolve once every id is known, in a second pass over the same list.
            for (var index = 0; (index < segments.Count); index++) {
                if (segments[index] is not { } segment) {
                    continue;
                }

                var path = $"segments[{index}]";

                foreach (var transition in (segment.Transitions ?? [])) {
                    if (
                        (transition is not null) &&
                        !string.IsNullOrWhiteSpace(value: transition.To) &&
                        !ids.Contains(item: transition.To)
                    ) {
                        errors.Add(item: new(Message: $"'{transition.To}' does not resolve to a declared segment.", Path: $"{path}.transitions.to"));
                    }
                }
            }
        }

        DocumentCanonicalizer.ValidateExtensions(
            addError: (path, message) => errors.Add(item: new(Message: message, Path: path)),
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
    public static void ValidateOrThrow(MusicDocument document, string? source = null) =>
        DocumentCanonicalizer.ThrowIfInvalid(errors: Validate(document: document), source: source);
    /// <summary>Normalizes an already-schema-valid document: defaults every optional member. Idempotent. Does NOT
    /// itself validate; <see cref="Canonicalize"/> always crosses <see cref="ValidateOrThrow"/> first.</summary>
    /// <param name="document">The document to normalize.</param>
    /// <returns>The normalized document.</returns>
    public static MusicDocument Normalize(MusicDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return (document with {
            Name = (string.IsNullOrWhiteSpace(value: document.Name) ? "score" : document.Name.Trim()),
            Schema = MusicDocument.CurrentSchema,
            Segments = [.. document.Segments.Select(selector: segment => (segment with {
                Transitions = [.. (segment.Transitions ?? []).Select(selector: transition => (transition with {
                    At = (transition.At ?? MusicTransitionBoundary.BarEnd),
                }))],
            }))],
            Tempo = (document.Tempo with {
                BeatsPerBar = (document.Tempo.BeatsPerBar ?? 4),
            }),
        });
    }
    /// <summary>THE full pipeline: validates (throwing on failure), normalizes, then serializes to canonical UTF-8
    /// bytes and hashes them.</summary>
    /// <param name="document">The document to canonicalize.</param>
    /// <param name="source">An optional source label for a validation-failure message.</param>
    /// <returns>The validated, normalized document plus its canonical bytes and hash.</returns>
    public static CanonicalDocument<MusicDocument> Canonicalize(MusicDocument document, string? source = null) {
        ValidateOrThrow(document: document, source: source);

        return DocumentCanonicalizer.Canonicalize(document: Normalize(document: document));
    }
}
