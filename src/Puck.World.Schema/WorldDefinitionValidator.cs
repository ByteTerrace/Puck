using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Presentation;
using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>The one thick startup gate for World-local authored data. Runtime systems may assume these structural and
/// platform-facing invariants instead of silently overwriting duplicate indices or discovering bad extents on a GPU
/// allocation path.</summary>
/// <remarks>
/// <para><b>Field taxonomy — the checklist a future field lands against.</b> A definition field is one of two kinds,
/// and that kind decides its numeric contract:</para>
/// <para><b>Sim-affecting</b> — quantized to fixed point once at compile via the <c>Fixed*</c> compilers (exactly the
/// <see cref="FixedWorldKit"/> / <see cref="FixedMotionTuning"/> pattern) and then deterministic: the profileless
/// <see cref="WorldDefinition.Motion"/> tuning, every kit's tuning/producer/action rows, the named
/// <see cref="WorldDefinition.SpawnPoints"/>, the
/// <see cref="WorldDefinition.Population"/> census, and the <see cref="WorldDefinition.Assignment"/> policy. These are
/// gated here for finiteness and physical sign (speeds/gravities/radii positive; durations non-negative) so a
/// bad authored value cannot poison the quantized sim.</para>
/// <para><b>Presentation-only</b> — stays float, never enters sim state: the scene albedos, the camera FOV/extent, the
/// screen geometry/sources, and the render-lever defaults. These are gated only for structural GPU safety (finite
/// frames, bounded extents, non-degenerate bases).</para>
/// </remarks>
public static partial class WorldDefinitionValidator {
    /// <summary>The per-action fact-trigger ceiling — one <see cref="ActionTriggerMode.Edge"/> latch bit each in the
    /// runtime's 64-bit lane word.</summary>
    private const int MaxFactTriggersPerAction = 64;
    // A look scale feeds the stamp pool's per-instance bound radius; an unbounded one is a GPU-SAFETY issue (a
    // spatial-cull metadata blow-up), not a taste one, so it carries a hard ceiling beside MaxSurfaceDimension.
    private const float MaxLookScale = 16f;
    // The largest |dot| of a screen frame's unit right/up pair the document may author. A screen's frame is read twice
    // — the client derives the slab's orientation and UV frame from it, the server's collider projects its half-extents
    // onto the same axes — and only an orthogonal pair makes those two the same solid. The bound matches
    // Puck.SignedDistance's own screen-frame door and Puck.Abstractions' CameraSnapshot basis check.
    private const float MaximumScreenBasisSkew = 1e-3f;
    private const float MinimumBasisLengthSquared = 1e-8f;
    // PlanarImpulse.BodyDirection quantizes to FixedQ4816 (step 2^-16) before reaching the sim; that rounds a unit
    // vector's length by at most ~1.3e-5. This tolerance sits ~8x above that quantization floor and far below any
    // unnormalized axis (e.g. (3, 0, 4) is off by 4).
    private const float PlanarImpulseUnitDirectionTolerance = 1e-4f;

    // World-local CPU/GPU screen sources are intentionally presentation-sized. A bad authored extent must fail here,
    // before it can become an unchecked pixel-buffer or offscreen-render allocation. Public: it is also the structural
    public const int MaxSurfaceDimension = 4096;

    private static AssetCheck? CheckAsset<TDocument, TViolation>(
        TDocument? document,
        string source,
        Func<TDocument, IEnumerable<TViolation>> validate,
        Func<TViolation, string> path,
        Func<TViolation, string> message,
        Func<TDocument, string, string> canonicalHash
    ) where TDocument : class {
        if (document is null) {
            return null;
        }

        var violations = validate(document).Select(selector: violation => (path(violation), message(violation))).ToArray();

        return new AssetCheck(
            Violations: violations,
            CanonicalHash: ((violations.Length == 0)
            ? canonicalHash(
                    document,
                    source
                )
            : null)
        );
    }
    // Loads the referenced document (never required to exist until here — the row itself is a plain Name/Source/Hash
    // triple with nothing to validate offline), then runs the SAME structural check CheckAsset already gives every
    // embedded family — see CheckMusic's remarks, the same load-then-check shape.
    private static AssetCheck? CheckPatch(WorldPatch patch) {
        if (!WorldAssetRowLoader.TryLoadPatch(
            document: out var document,
            error: out var loadError,
            row: patch
        )) {
            return new AssetCheck(
                CanonicalHash: null,
                Violations: [("source", loadError!)]
            );
        }

        return CheckAsset(
            document: document,
            source: patch.Name,
            validate: static document => SynthPatchCanonicalizer.Validate(document: document),
            path: static violation => violation.Path,
            message: static violation => violation.Message,
            canonicalHash: static (document, source) => SynthPatchCanonicalizer.Canonicalize(
                document: document,
                source: source
            ).Hash
        );
    }
    // Loads the referenced document (never required to exist until here — the row itself is a plain Name/Source/Hash
    // triple with nothing to validate offline), then runs the SAME structural check CheckAsset already gives every
    // embedded family, plus facts MusicCanonicalizer alone cannot check: it validates one document at a time
    // against only what Puck.World.Authoring itself can see. ticksPerBeat's divisibility duplicates
    // ValidateSimulation's own FixedTickConversion.TicksPerSecond reasoning (Puck.World.Authoring cannot reference
    // that constant's true owner either); a transition/layer/embellishment `when` token resolves against
    // WorldAudioCue.MusicWhenTokens — the sense-mappable subset the director compiler maps, not the full cue
    // vocabulary — which this document family's own project cannot reference without inverting the dependency; and a
    // layer/embellishment `gainThousandths` rides the same CreationSoundDocument.MaxLevel ceiling ValidateCues
    // enforces on a cue row, so the ceiling stays the one place — this validator — that enforces it everywhere.
    private static AssetCheck? CheckMusic(WorldMusicRow row, HashSet<string> tuneIds, HashSet<string> patchIds) {
        if (!WorldAssetRowLoader.TryLoadMusic(
            document: out var document,
            error: out var loadError,
            row: row
        )) {
            return new AssetCheck(
                CanonicalHash: null,
                Violations: [("source", loadError!)]
            );
        }

        var violations = new List<(string Path, string Message)>();

        foreach (var violation in MusicCanonicalizer.Validate(document: document!)) {
            violations.Add(item: (violation.Path, violation.Message));
        }

        if (
            (document!.Tempo is { } tempo) &&
            (tempo.TicksPerBeat > 0) &&
            ((FixedTickConversion.TicksPerSecond % ((ulong)tempo.TicksPerBeat)) != 0UL)
        ) {
            violations.Add(item: ("tempo.ticksPerBeat", $"{tempo.TicksPerBeat} must evenly divide {FixedTickConversion.TicksPerSecond}."));
        }

        foreach (var segment in (document.Segments ?? [])) {
            if (segment is null) {
                continue;
            }

            foreach (var transition in (segment.Transitions ?? [])) {
                if (
                    (transition is not null) &&
                    !string.IsNullOrWhiteSpace(value: transition.When) &&
                    !WorldAudioCue.IsMusicWhenToken(token: transition.When)
                ) {
                    violations.Add(item: ($"segments[{segment.Id}].transitions.when", $"'{transition.When}' is not a sense-mappable when token ({string.Join(separator: " | ", values: WorldAudioCue.MusicWhenTokens)})."));
                }
            }

            foreach (var layer in (segment.Layers ?? [])) {
                if (layer is null) {
                    continue;
                }

                if (
                    !string.IsNullOrWhiteSpace(value: layer.TuneId) &&
                    !tuneIds.Contains(item: layer.TuneId)
                ) {
                    violations.Add(item: ($"segments[{segment.Id}].layers.tuneId", $"'{layer.TuneId}' does not resolve to a declared tune."));
                }

                // A null When is the unconditional layer; any non-null token — whitespace included — must be
                // sense-mappable, so nothing the director compiler cannot arm survives validation.
                if (
                    (layer.When is { } when) &&
                    !WorldAudioCue.IsMusicWhenToken(token: when)
                ) {
                    violations.Add(item: ($"segments[{segment.Id}].layers.when", $"'{when}' is not a sense-mappable when token ({string.Join(separator: " | ", values: WorldAudioCue.MusicWhenTokens)})."));
                }

                if (
                    (layer.GainThousandths is { } layerGain) &&
                    ((layerGain < 0) || (layerGain > ((int)(CreationSoundDocument.MaxLevel * 1000f))))
                ) {
                    violations.Add(item: ($"segments[{segment.Id}].layers.gainThousandths", $"{layerGain} must be within [0, {((int)(CreationSoundDocument.MaxLevel * 1000f))}]."));
                }
            }

            foreach (var embellishment in (segment.Embellishments ?? [])) {
                if (embellishment is null) {
                    continue;
                }

                if (
                    !string.IsNullOrWhiteSpace(value: embellishment.PatchId) &&
                    !patchIds.Contains(item: embellishment.PatchId)
                ) {
                    violations.Add(item: ($"segments[{segment.Id}].embellishments.patchId", $"'{embellishment.PatchId}' does not resolve to a declared patch."));
                }

                if (
                    !string.IsNullOrWhiteSpace(value: embellishment.When) &&
                    !WorldAudioCue.IsMusicWhenToken(token: embellishment.When)
                ) {
                    violations.Add(item: ($"segments[{segment.Id}].embellishments.when", $"'{embellishment.When}' is not a sense-mappable when token ({string.Join(separator: " | ", values: WorldAudioCue.MusicWhenTokens)})."));
                }

                if (
                    (embellishment.GainThousandths is { } embellishmentGain) &&
                    ((embellishmentGain < 0) || (embellishmentGain > ((int)(CreationSoundDocument.MaxLevel * 1000f))))
                ) {
                    violations.Add(item: ($"segments[{segment.Id}].embellishments.gainThousandths", $"{embellishmentGain} must be within [0, {((int)(CreationSoundDocument.MaxLevel * 1000f))}]."));
                }
            }
        }

        return new AssetCheck(
            CanonicalHash: ((violations.Count == 0)
            ? MusicCanonicalizer.Canonicalize(
                    document: document,
                    source: row.Name
                ).Hash
            : null),
            Violations: [.. violations]
        );
    }
    // See CheckPatch's remarks — the same load-then-check shape.
    private static AssetCheck? CheckTune(WorldTune tune) {
        if (!WorldAssetRowLoader.TryLoadTune(
            document: out var document,
            error: out var loadError,
            row: tune
        )) {
            return new AssetCheck(
                CanonicalHash: null,
                Violations: [("source", loadError!)]
            );
        }

        return CheckAsset(
            document: document,
            source: tune.Name,
            validate: static document => AudioCanonicalizer.Validate(document: document),
            path: static violation => violation.Path,
            message: static violation => violation.Message,
            canonicalHash: static (document, source) => AudioCanonicalizer.Canonicalize(
                document: document,
                source: source
            ).Hash
        );
    }
    // THIS document's channel table, for the binding-overlay vocabulary check — or null when the channels section is
    // itself too malformed to compile (a null row, or more rows than ordinals exist). Null is safe rather than
    // permissive: every condition that produces it is already an error ValidateChannels added, so the document is
    // refused on the channel section instead of being judged against a table that would misreport which names are
    // declared.
    private static WorldChannelTable? CompilableChannelTable(IReadOnlyList<WorldChannel> channels) {
        if (
            (channels is null) ||
            (channels.Count > ChannelLimits.MaxChannels)
        ) {
            return null;
        }

        for (var index = 0; (index < channels.Count); index++) {
            if (channels[index] is null) {
                return null;
            }
        }

        return WorldChannelTable.Compile(channels: channels);
    }
    private static bool IsFinite(Vector3 value) => (float.IsFinite(f: value.X) && float.IsFinite(f: value.Y) && float.IsFinite(f: value.Z));
    // |cos| between a screen frame's two axes: 0 for the orthogonal pair the slab's geometry and collider both assume,
    // 1 for a parallel one. Callers must have cleared finiteness and non-degeneracy first, or this reads NaN.
    private static float ScreenBasisSkew(WorldScreen screen) => MathF.Abs(x: Vector3.Dot(
        vector1: Vector3.Normalize(value: screen.Right),
        vector2: Vector3.Normalize(value: screen.Up)
    ));
    // Held pointer steer carries full 3D camera facing and therefore needs every Face role. Axis2D look-steer is the
    // upright action-game arm: yaw turns FaceX/FaceZ while vertical input remains camera pitch. Both need a world yaw
    // reference — a camera that follows the body cannot also lead it.
    private static void ValidateSteerBinding(WorldDefinition definition, List<string> errors) {
        var bindsPointerSteer = BindsCommand(
            command: "player.steer",
            definition: definition
        );
        var bindsLookSteer = BindsCommand(
            command: "player.look.steer",
            definition: definition
        );

        if (
            !bindsPointerSteer &&
            !bindsLookSteer
        ) {
            return;
        }

        var hasFaceX = false;
        var hasFaceY = false;
        var hasFaceZ = false;

        foreach (var channel in definition.Channels) {
            hasFaceX |= (channel.Role == ChannelRole.FaceX);
            hasFaceY |= (channel.Role == ChannelRole.FaceY);
            hasFaceZ |= (channel.Role == ChannelRole.FaceZ);
        }

        if (
            bindsPointerSteer &&
            (!hasFaceX || !hasFaceY || !hasFaceZ)
        ) {
            errors.Add(item: "bindingOverlays bind player.steer, which needs channels claiming FaceX, FaceY, and FaceZ.");
        }
        if (
            bindsLookSteer &&
            (!hasFaceX || !hasFaceZ)
        ) {
            errors.Add(item: "bindingOverlays bind player.look.steer, which needs channels claiming FaceX and FaceZ.");
        }

        if (definition.Views.SeatControl.YawReference != WorldSeatYawReference.World) {
            var bindingNames = ((bindsPointerSteer && bindsLookSteer)
                ? "player.steer and player.look.steer"
                : (bindsPointerSteer
                    ? "player.steer"
                    : "player.look.steer"
            ));

            errors.Add(item: $"bindingOverlays bind {bindingNames}, which needs views.seatControl.yawReference 'World'.");
        }
    }
    private static bool BindsCommand(WorldDefinition definition, string command) {
        foreach (var overlay in definition.BindingOverlays) {
            foreach (var chord in overlay.Document.Chords) {
                if (string.Equals(
                    a: chord.Command?.Command,
                    b: command,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return true;
                }

                foreach (var entry in (chord.Page?.Entries ?? [])) {
                    if (string.Equals(
                        a: entry.Command,
                        b: command,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        return true;
                    }
                }
            }

            foreach (var wheel in (overlay.Document.Wheels ?? [])) {
                foreach (var ring in wheel.Rings) {
                    foreach (var entry in ring.Entries) {
                        if (string.Equals(
                            a: entry.Command,
                            b: command,
                            comparisonType: StringComparison.Ordinal
                        )) {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }
    private static void ValidateRenderCycle(WorldDefinition definition, List<string> errors) {
        if (definition.Render.Cycle is not { } cycle) {
            return;
        }

        var row = definition.State.FirstOrDefault(predicate: candidate => string.Equals(
            a: candidate.Name.Value,
            b: cycle.State,
            comparisonType: StringComparison.Ordinal
        ));

        if (row is null) {
            errors.Add(item: $"render.cycle.state names no state row '{cycle.State}'.");
        } else if (row.Kind is not (CellKind.Fixed or CellKind.Int)) {
            errors.Add(item: $"render.cycle.state '{cycle.State}' must be a Fixed or Int row.");
        }

        if (cycle.Keys is not { Count: >= 2 }) {
            errors.Add(item: "render.cycle.keys must carry at least two keys.");

            return;
        }

        for (var index = 0; (index < cycle.Keys.Count); index++) {
            var key = cycle.Keys[index];
            var path = $"render.cycle.keys[{index}]";

            if (
                !float.IsFinite(f: key.At) ||
                (key.At < 0f) ||
                (key.At >= 1f)
            ) {
                errors.Add(item: $"{path}.at must be finite and in [0, 1).");
            } else if (
                (index > 0) &&
                (key.At <= cycle.Keys[(index - 1)].At)
            ) {
                errors.Add(item: $"{path}.at must exceed the previous key's.");
            }

            ValidateRenderLighting(
                definition: definition,
                errors: errors,
                lighting: key.Lighting,
                path: $"{path}.lighting"
            );
            ValidateRenderSky(
                definition: definition,
                errors: errors,
                path: $"{path}.sky",
                sky: key.Sky
            );
        }
    }
    // definition null = identity scope: a document authored independent of any world cannot resolve a placement id
    // or a state row, so those checks are skipped there and the reference is refused at the seam instead.
    private static void ValidateOverlaySubject(OverlaySubject? subject, string path, List<string> errors, WorldDefinition? definition) {
        switch (subject) {
            case null:
                errors.Add(item: $"{path} is required.");

                return;
            case OverlaySubject.Seat seat:
                if ((seat.Number is { } number) && ((number < 1) || (number > WorldBodiesLimits.LocalSeatCount))) {
                    errors.Add(item: $"{path}.number {number} is outside 1..{WorldBodiesLimits.LocalSeatCount}.");
                }

                return;
            case OverlaySubject.Entity entity:
                if ((entity.Index < 0) || ((definition is not null) && (entity.Index >= definition.Population.Capacity))) {
                    errors.Add(item: $"{path}.index {entity.Index} is outside the authored population capacity.");
                }

                return;
            case OverlaySubject.Placement placement:
                if (string.IsNullOrWhiteSpace(value: placement.PlacementId)) {
                    errors.Add(item: $"{path}.placementId is required.");
                } else if ((definition is not null) && (WorldDefinitionRows.FindPlacement(placements: definition.Placements, id: placement.PlacementId) is null)) {
                    errors.Add(item: $"{path}.placementId '{placement.PlacementId}' names no placement row.");
                }

                return;
            case OverlaySubject.AnySeat:
            case OverlaySubject.RecentSpeaker:
                return;
            default:
                errors.Add(item: $"{path} is an unknown subject kind.");

                return;
        }
    }

    internal static void ValidateOverlayPredicate(OverlayPredicate? predicate, string path, List<string> errors, WorldDefinition? definition = null, IReadOnlyDictionary<string, WorldStateRow>? stateRows = null) {
        switch (predicate) {
            case null:
                return;
            case OverlayPredicate.Speaking speaking:
                ValidateOverlaySubject(
                    definition: definition,
                    errors: errors,
                    path: $"{path}.subject",
                    subject: speaking.Subject
                );

                if (!float.IsFinite(f: speaking.WindowSeconds) || (speaking.WindowSeconds < 0f)) {
                    errors.Add(item: $"{path}.windowSeconds must be finite and non-negative.");
                }

                if (!float.IsFinite(f: speaking.FadeSeconds) || (speaking.FadeSeconds < 0f)) {
                    errors.Add(item: $"{path}.fadeSeconds must be finite and non-negative.");
                }

                return;
            case OverlayPredicate.Near near:
                ValidateOverlaySubject(
                    definition: definition,
                    errors: errors,
                    path: $"{path}.subject",
                    subject: near.Subject
                );

                if (near.Of is { } of) {
                    ValidateOverlaySubject(
                        definition: definition,
                        errors: errors,
                        path: $"{path}.of",
                        subject: of
                    );
                }

                if (!float.IsFinite(f: near.Distance) || (near.Distance < 0f)) {
                    errors.Add(item: $"{path}.distance must be finite and non-negative.");
                }

                return;
            case OverlayPredicate.State state:
                if (!BindableState.TryParseBinding(
                    key: out var stateKey,
                    row: out var stateRow,
                    value: state.Binding
                )) {
                    errors.Add(item: $"{path}.binding '{state.Binding}' must be spelled state.<row>[.<key>].");
                } else if (definition is not null) {
                    // stateRows is the SAME name-keyed map ValidateState builds once per whole-document validate
                    // (threaded down from ValidateCore); falling back to a scan of definition.State keeps this
                    // correct for a caller that has definition but not the map.
                    var row = ((stateRows is not null)
                        ? (stateRows.TryGetValue(key: stateRow, value: out var mappedRow) ? mappedRow : null)
                        : definition.State.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Name.ToString(), b: stateRow, comparisonType: StringComparison.Ordinal)));

                    if (row is null) {
                        errors.Add(item: $"{path}.binding '{state.Binding}' names no declared state row.");
                    } else if ((row.Kind == CellKind.Text) != (state.Text is not null)) {
                        errors.Add(item: $"{path} compares {((row.Kind == CellKind.Text) ? "a text row, which needs 'text'" : "a numeric row, which needs 'value'")}.");
                    }
                }

                if ((state.Value is null) == (state.Text is null)) {
                    errors.Add(item: $"{path} needs exactly one of 'value' or 'text'.");
                }

                if ((state.Text is not null) && (state.Comparison is not (ActionStateComparison.Equal or ActionStateComparison.NotEqual))) {
                    errors.Add(item: $"{path}.comparison {state.Comparison} is not a text comparison (equal or notEqual).");
                }

                if ((state.Value is { } value) && !float.IsFinite(f: value)) {
                    errors.Add(item: $"{path}.value must be finite.");
                }

                if (!Enum.IsDefined(value: state.Comparison)) {
                    errors.Add(item: $"{path}.comparison is not a defined comparison.");
                }

                return;
            case OverlayPredicate.Now now when !Enum.IsDefined(value: now.Fact):
                errors.Add(item: $"{path}.fact is not a defined OverlayFact.");
                return;
            case OverlayPredicate.Recently recently:
                if (!Enum.IsDefined(value: recently.Fact)) {
                    errors.Add(item: $"{path}.fact is not a defined OverlayFact.");
                }

                if (
                    !float.IsFinite(f: recently.WindowSeconds) ||
                    (recently.WindowSeconds < 0f)
                ) {
                    errors.Add(item: $"{path}.windowSeconds must be finite and non-negative.");
                }

                if (
                    !float.IsFinite(f: recently.FadeSeconds) ||
                    (recently.FadeSeconds < 0f)
                ) {
                    errors.Add(item: $"{path}.fadeSeconds must be finite and non-negative.");
                }

                return;
            case OverlayPredicate.All all:
                for (var index = 0; (index < (all.Predicates?.Count ?? 0)); index++) {
                    ValidateOverlayPredicate(
                        definition: definition,
                        errors: errors,
                        path: $"{path}.predicates[{index}]",
                        predicate: all.Predicates![index],
                        stateRows: stateRows
                    );
                }

                return;
            case OverlayPredicate.Any any:
                for (var index = 0; (index < (any.Predicates?.Count ?? 0)); index++) {
                    ValidateOverlayPredicate(
                        definition: definition,
                        errors: errors,
                        path: $"{path}.predicates[{index}]",
                        predicate: any.Predicates![index],
                        stateRows: stateRows
                    );
                }

                return;
            case OverlayPredicate.Not not:
                ValidateOverlayPredicate(
                    definition: definition,
                    errors: errors,
                    path: $"{path}.predicate",
                    predicate: not.Predicate,
                    stateRows: stateRows
                );

                return;
            default:
                return;
        }
    }

    // Identity and profile colors stay literal: they are persisted per identity (RgbToHex into the owned-world
    // catalog) and travel between worlds, so nothing there can bind to one world's state.
    private static bool IsHexColor(string? value) =>
        HexColor.TryParse(
            rgb: out _,
            value: value
        );
    private static bool IsColor(WorldDefinition definition, string? value) => WorldColor.IsAuthorable(
        definition: definition,
        value: value
    );
    // Hand-rolled rather than System.Text.Regex — the canonical form is a "sha256-64/" prefix followed by exactly
    // 16 lowercase hex digits (AssetContentHash.ToString's "x16" format).
    private static bool IsValidAddonHash(string hash) {
        const string Prefix = "sha256-64/";

        if (
            !hash.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Prefix
        ) ||
            (hash.Length != (Prefix.Length + 16))
        ) {
            return false;
        }

        for (var index = Prefix.Length; (index < hash.Length); index++) {
            if (!char.IsAsciiHexDigitLower(c: hash[index])) {
                return false;
            }
        }

        return true;
    }
    // The dangling-reference door every "names a row this document must declare" check opens: blank or unresolved
    // against declaredSet is one "{path}[.field] '{value}' names no {rowNoun} row." refusal. field may be empty when
    // path already names the leaf. Returns whether the reference resolved, so a caller gating a further
    // declared-only check (a rate/coefficient derived from the resolved row) can do so without re-deriving this
    // same check.
    private static bool RequireDeclared(string? value, ISet<string> declaredSet, string path, string field, string rowNoun, List<string> errors) {
        if (
            string.IsNullOrWhiteSpace(value: value) ||
            !declaredSet.Contains(item: value)
        ) {
            var fullPath = (string.IsNullOrEmpty(value: field) ? path : $"{path}.{field}");

            errors.Add(item: $"{fullPath} '{value}' names no {rowNoun} row.");

            return false;
        }

        return true;
    }
    // The same dangling-reference door as RequireDeclared, refused LOUDER: the message enumerates what the world did
    // declare so an author reading it can see the whole admissible set (or that it is empty). subject is the caller's
    // already-composed leading clause and rowNoun its whole noun phrase, so each call site keeps its own wording.
    private static void RequireDeclaredListing(string? value, ISet<string> declaredSet, string subject, string rowNoun, List<string> errors) {
        if (
            string.IsNullOrWhiteSpace(value: value) ||
            !declaredSet.Contains(item: value)
        ) {
            errors.Add(item: ((declaredSet.Count > 0)
                ? $"{subject} names no {rowNoun}; the world declares: {string.Join(
                    separator: ", ",
                    values: declaredSet
                )}."
                : $"{subject} names no {rowNoun}; the world declares none."));
        }
    }
    private static void RequireFinite(float value, string name, List<string> errors) {
        if (!float.IsFinite(f: value)) {
            errors.Add(item: $"{name} must be finite.");
        }
    }
    private static void RequireIntRange(int value, int min, int max, string name, List<string> errors) {
        if (
            (value < min) ||
            (value > max)
        ) {
            errors.Add(item: $"{name} {value} is outside {min}..{max}.");
        }
    }
    private static void RequireNonNegative(float value, string name, List<string> errors) {
        if (
            !float.IsFinite(f: value) ||
            (value < 0f)
        ) {
            errors.Add(item: $"{name} must be finite and non-negative.");
        }
    }
    private static void RequireNonNegativeEpoch(long value, string name, List<string> errors) {
        if (value < 0) {
            errors.Add(item: $"{name} {value} must be non-negative.");
        }
    }
    private static void RequirePositive(float value, string name, List<string> errors) {
        if (
            !float.IsFinite(f: value) ||
            (value <= 0f)
        ) {
            errors.Add(item: $"{name} must be finite and positive.");
        }
    }
    // The per-axis effective-extent check for a box solidity facet: a margin that inverts any axis (halfExtent + margin
    // <= 0) is rejected by name, not turned into a negative-extent collider.
    private static void RequirePositiveEffectiveExtent(Vector3 halfExtents, float margin, string path, List<string> errors) {
        if (!float.IsFinite(f: margin)) {
            return;
        }

        if (
            ((halfExtents.X + margin) <= 0f) ||
            ((halfExtents.Y + margin) <= 0f) ||
            ((halfExtents.Z + margin) <= 0f)
        ) {
            errors.Add(item: $"{path} {margin} inverts the collider (halfExtent + margin must be > 0 on every axis).");
        }
    }
    // The general bounded-float door every closed-interval check (unit alphas, gain ceilings, half-angle cones, …)
    // folds onto: finite, and within [min, max] with either edge switchable to an open bound (e.g. a half-angle's
    // 0 is excluded — a zero-width cone senses nothing — while its 180 ceiling is admitted).
    private static void RequireRange(float value, float min, float max, string name, List<string> errors, bool minExclusive = false, bool maxExclusive = false) {
        if (
            !float.IsFinite(f: value) ||
            (minExclusive ? (value <= min) : (value < min)) ||
            (maxExclusive ? (value >= max) : (value > max))
        ) {
            var openBracket = (minExclusive ? "(" : "[");
            var closeBracket = (maxExclusive ? ")" : "]");

            errors.Add(item: $"{name} {value} must be finite and within {openBracket}{min}, {max}{closeBracket}.");
        }
    }
    // The required-then-unique name door every row loop needing an ordinal identity opens: absent/blank is
    // "{path}[.field] is required.", a repeat against the caller's own seen-set is "…is duplicated.". field may be
    // empty when path already names the leaf (e.g. a tags[] entry) — no trailing dot is emitted in that case.
    // Returns whether the name is admitted, so a caller gating further per-row work on it (an ordinal table insert,
    // a nested loop) can do so without re-deriving the same check.
    private static bool RequireUniqueName(string? value, HashSet<string> seen, string path, string field, List<string> errors) {
        var fullPath = (string.IsNullOrEmpty(value: field) ? path : $"{path}.{field}");

        if (string.IsNullOrWhiteSpace(value: value)) {
            errors.Add(item: $"{fullPath} is required.");

            return false;
        }

        if (!seen.Add(item: value)) {
            errors.Add(item: $"{fullPath} '{value}' is duplicated.");

            return false;
        }

        return true;
    }
    private static void RequireUnitInterval(float value, string name, List<string> errors) {
        if (
            !float.IsFinite(f: value) ||
            (value < 0f) ||
            (value > 1f)
        ) {
            errors.Add(item: $"{name} {value} must be within 0..1.");
        }
    }
    // The null-tolerant twin of ValidateAssets, for an OPTIONAL asset section (null = none authored) rather than a
    // required one (Tunes/Patches) — a document declaring no music is not missing anything.
    private static HashSet<string> ValidateOptionalAssets<T>(IReadOnlyList<T>? rows, string section, Func<T, string> id, Func<T, string> hash, Func<T, AssetCheck?> check, List<string> errors) where T : class =>
        ValidateAssets(
            check: check,
            errors: errors,
            hash: hash,
            id: id,
            rows: (rows ?? []),
            section: section
        );
    private static HashSet<string> ValidateAssets<T>(IReadOnlyList<T> rows, string section, Func<T, string> id, Func<T, string> hash, Func<T, AssetCheck?> check, List<string> errors) where T : class {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (rows is null) {
            errors.Add(item: $"{section} is required.");

            return ids;
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"{section}[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            var rowId = id(row);

            RequireUniqueName(
                errors: errors,
                field: "id",
                path: path,
                seen: ids,
                value: rowId
            );

            if (check(row) is not { } result) {
                errors.Add(item: $"{path}.document is required.");

                continue;
            }

            if (result.Violations.Length > 0) {
                foreach (var violation in result.Violations) {
                    errors.Add(item: $"{path}.document.{violation.Path}: {violation.Message}");
                }

                continue;
            }

            var rowHash = hash(row);

            if (!string.Equals(
                a: rowHash,
                b: result.CanonicalHash,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"{path}.hash '{rowHash}' does not match the canonical sha256 '{result.CanonicalHash}'.");
            }
        }

        return ids;
    }

    // The declared-name sets ValidateCore's worst-threaded consumers (ValidatePlacements/ValidateFaceSources,
    // ValidateScreenSource/ValidateMagazine) resolve dangling references against, carried as ONE parameter instead
    // of one positional HashSet per set. Filled progressively as ValidateCore computes each set — a field is safe to
    // read once ValidateCore's own call to its owning Validate* method has run, the same dependency order the
    // un-scoped locals already followed.
    private sealed class ValidationScope {
        public HashSet<string> Cameras { get; set; } = [];
        public HashSet<string> DestinationNames { get; set; } = [];
        public HashSet<string> FontNames { get; set; } = [];
        public bool HasTextCatalog { get; set; }
        public HashSet<string> KitNames { get; set; } = [];
        public HashSet<string> LookNames { get; set; } = [];
        public HashSet<string> PatchIds { get; set; } = [];
        public HashSet<string> PrototypeIds { get; set; } = [];
    }

    private static void ValidateCore(WorldDefinition definition, IWorldNeighbourResolver? neighbours, bool validateAdjacencyClaims) {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = new List<string>();
        var scope = new ValidationScope();

        if (!string.Equals(
            a: definition.Schema,
            b: WorldDefinition.SchemaVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            errors.Add(item: $"schema '{(definition.Schema ?? "(absent)")}' is not '{WorldDefinition.SchemaVersion}'.");
        }

        // The file-load boundary composes and strips `basis` (see WorldDocumentBasis), so one surviving to
        // validation arrived on a path with no directory to resolve it against — a wire document, a replay embed,
        // or a hand-built candidate. It must be a delta someone forgot to compose; running it as-is would silently
        // drop everything it meant to inherit.
        if (definition.Basis is { } basis) {
            errors.Add(item: $"basis '{basis}' survived to validation — a basis resolves only at the file-load boundary; this document arrived somewhere no basis can be resolved, so it must be composed (flattened) before it can run.");
        }

        if (definition.Imports is { } imports) {
            errors.Add(item: $"imports [{string.Join(separator: ", ", values: imports)}] survived to validation — imports resolve only at the file-load boundary; this document arrived somewhere no import can be resolved, so it must be composed (flattened) before it can run.");
        }

        ValidateExtensions(
            extensions: definition.Extensions,
            errors: errors
        );

        ValidateMetadata(
            metadata: definition.Metadata,
            errors: errors
        );

        ValidateRenderExtensions(
            extensions: definition.Render.Extensions,
            errors: errors
        );

        ValidateRenderFarDistance(
            farDistance: definition.Render.FarDistance,
            errors: errors
        );

        ValidateRenderLighting(
            definition: definition,
            lighting: definition.Render.Lighting,
            errors: errors
        );

        ValidateRenderSky(
            definition: definition,
            sky: definition.Render.Sky,
            errors: errors
        );
        ValidateRenderCycle(
            definition: definition,
            errors: errors
        );

        ValidateUpdate(
            errors: errors,
            update: definition.Update
        );

        // Validated FIRST among the sim-affecting sections: definition.SimulationRateHz (read by
        // PopulationReconnectGraceTicks and CompiledInputHold below, and by every downstream Puck.World.Server/
        // Puck.Launcher consumer) must be a sane rate before anything derived from it is worth checking.
        ValidateSimulation(
            simulation: definition.Simulation,
            errors: errors
        );

        // The profileless fallback tuning is SIM-AFFECTING and compiles to fixed point.
        ValidateMotionDefaults(
            motion: definition.Motion,
            path: "motion",
            errors: errors
        );

        // Dynamics validates BEFORE every consumer that names a row by dynamics/row: state, kits, looks, and camera
        // programs all refuse a dangling reference against THIS set.
        var dynamicsNames = ValidateDynamics(
            dynamics: definition.Dynamics,
            errors: errors
        );

        // Curves validates before every consumer that names a row by curves/curve — today the camera path op — the
        // same ordering dynamicsNames already gives dynamics consumers.
        var curveNames = ValidateCurves(
            curves: definition.Curves,
            errors: errors
        );

        ValidatePlayerDefaults(
            defaults: definition.PlayerDefaults,
            errors: errors
        );
        ValidateSteerBinding(
            definition: definition,
            errors: errors
        );

        var localSeats = definition.Population.LocalSeats;

        if (
            (localSeats < 0) ||
            (localSeats > WorldBodiesLimits.LocalSeatCount)
        ) {
            errors.Add(item: $"bodies.localSeats {localSeats} is outside 0..{WorldBodiesLimits.LocalSeatCount} (the host's seat ceiling).");
        }

        ValidateSeatActivation(
            seatActivation: definition.Population.SeatActivation,
            localSeats: localSeats,
            errors: errors
        );

        if (
            (definition.Population.Capacity < localSeats) ||
            (definition.Population.Capacity > WorldBodiesLimits.CapacityCeiling)
        ) {
            errors.Add(item: $"bodies.capacity {definition.Population.Capacity} is outside {localSeats}..{WorldBodiesLimits.CapacityCeiling}.");
        }

        if (definition.Population.CapacityRow is { } capacityRow) {
            // A literal beside the row is legitimate AFTER the boot resolver settles the row into it — the row is
            // the source and overwrites the literal on every fresh load, so nothing shadows silently.

            if (WorldDefinitionRows.FindStateRow(
                rows: definition.State,
                name: capacityRow
            ) is not { } censusRow) {
                errors.Add(item: $"bodies.capacityRow names state row '{capacityRow}', which the document does not declare.");
            } else if (
                (censusRow.Kind != CellKind.Int) ||
                censusRow.IsKeyed ||
                (censusRow.Field is not null)
            ) {
                errors.Add(item: $"bodies.capacityRow names state row '{capacityRow}', which must be a scalar kind=int row.");
            }
        }

        if (definition.Population.ScaleRow is { } scaleRow) {
            if (WorldDefinitionRows.FindStateRow(
                rows: definition.State,
                name: scaleRow
            ) is not { } scaleStateRow) {
                errors.Add(item: $"bodies.scaleRow names state row '{scaleRow}', which the document does not declare.");
            } else if (
                (scaleStateRow.Kind != CellKind.Fixed) ||
                !scaleStateRow.IsKeyed ||
                (scaleStateRow.Field is not null) ||
                (scaleStateRow.Min is null) ||
                (scaleStateRow.Max is null)
            ) {
                errors.Add(item: $"bodies.scaleRow names state row '{scaleRow}', which must be a keyed kind=fixed row declaring both min and max — the world's own body-scale envelope.");
            } else {
                var scaleMin = ((float)((double)FixedQ4816.FromRawBits(value: scaleStateRow.Min.Value)));
                var scaleMax = ((float)((double)FixedQ4816.FromRawBits(value: scaleStateRow.Max.Value)));

                if (scaleMin <= 0f) {
                    errors.Add(item: $"bodies.scaleRow names state row '{scaleRow}', whose min {scaleMin} is not positive — a body's Scale is a multiplier, never zero or inverted.");
                }
                if (scaleMax > definition.Authoring.MaxPlacementScale) {
                    errors.Add(item: $"bodies.scaleRow names state row '{scaleRow}', whose max {scaleMax} exceeds authoring.maxPlacementScale {definition.Authoring.MaxPlacementScale} — the render capacity probe's worst case would not cover the largest live body.");
                }
                foreach (var cell in (scaleStateRow.Cells ?? [])) {
                    if (
                        (cell.Advance is not null) ||
                        (cell.Cycle is not null) ||
                        (cell.Dynamics is not null)
                    ) {
                        errors.Add(item: $"bodies.scaleRow names state row '{scaleRow}', whose cell '{cell.Key}' declares an advance/cycle/dynamics trait — WorldPopulation.SyncBodyScale resyncs it at tick 0 only, so a value-over-time cell would read its base value forever instead of progressing.");
                    }
                    // The SAME parse WorldPopulation.SyncBodyScale runs at every resync: a key that fails it is
                    // silently skipped there rather than read, so an author typo (a non-integer key, or an index
                    // outside the authored population) would otherwise author a cell nothing ever reads.
                    if (
                        !int.TryParse(
                        s: cell.Key,
                        style: NumberStyles.Integer,
                        provider: CultureInfo.InvariantCulture,
                        result: out var scaleBodyIndex
                    ) ||
                        (scaleBodyIndex < 0) ||
                        (scaleBodyIndex >= definition.Population.Capacity)
                    ) {
                        errors.Add(item: $"bodies.scaleRow names state row '{scaleRow}', whose cell key '{cell.Key}' is not a 0-based body index inside 0..{(definition.Population.Capacity - 1)}.");
                    }
                }
            }
        }

        var peerCapacity = Math.Max(
            val1: 0,
            val2: (definition.Population.Capacity - localSeats)
        );

        if (
            (definition.Population.NetworkPlayers < 0) ||
            (definition.Population.NetworkPlayers > peerCapacity)
        ) {
            errors.Add(item: $"bodies.networkPlayers {definition.Population.NetworkPlayers} is outside 0..{peerCapacity} for the authored capacity.");
        }

        // 0 disables the reconnect grace window (immediate teardown); the 600s ceiling mirrors world.wait's own
        // ten-minute bound. Validated in authored seconds, not derived ticks — at rate 0 a positive value means
        // NEVER, not a tick count (see CompiledTickDuration).
        RequireRange(
            value: definition.Population.ReconnectGraceSeconds,
            min: 0f,
            max: 600f,
            name: "bodies.reconnectGraceSeconds",
            errors: errors
        );

        // The audio asset sections come FIRST among the row sets: emission facets on scene rows/placements and the
        // speaker rows below all resolve against the tune/patch id sets.
        var tuneIds = ValidateAssets(
            rows: definition.Tunes,
            section: "tunes",
            id: static tune => tune.Name,
            hash: static tune => tune.Hash,
            check: CheckTune,
            errors: errors
        );
        var patchIds = ValidateAssets(
            rows: definition.Patches,
            section: "patches",
            id: static patch => patch.Name,
            hash: static patch => patch.Hash,
            check: CheckPatch,
            errors: errors
        );

        scope.PatchIds = patchIds;
        // Music validates right beside tunes/patches — the same asset-row shape, optional (null = none) rather
        // than required, since a required section would refuse every world checked in before this arc.
        _ = ValidateOptionalAssets(
            rows: definition.Music,
            section: "music",
            id: static row => row.Name,
            hash: static row => row.Hash,
            check: row => CheckMusic(
                patchIds: patchIds,
                row: row,
                tuneIds: tuneIds
            ),
            errors: errors
        );

        // The one-live-score ceiling: WorldServer's constructor reads only music[0], so a second authored row would
        // validate cleanly and boot silently truncated.
        if (definition.Music is { Count: > 1 } musicRows) {
            errors.Add(item: $"music declares {musicRows.Count} rows, but at most one is honored — the server reads only music[0].");
        }

        var spawnPointIds = ValidateSpawnPoints(
            spawnPoints: definition.SpawnPoints,
            errors: errors
        );

        ValidateSeatSpawns(
            seatSpawns: definition.Population.SeatSpawns,
            spawnPointIds: spawnPointIds,
            localSeats: localSeats,
            errors: errors
        );
        // Validated in the AUTHORED seconds, not definition.CompiledInputHold: every compiled *Ticks field
        // collapses to 0 at simulation.rateHz 0 (a legal, non-stepping world), so a ticks-domain check would refuse
        // an ordinary input-hold section on such a world. The rate is threaded through so ValidateInputHold can also
        // prove the authored value actually compiles (see its own remarks).
        ValidateInputHold(
            settings: definition.InputHold,
            ratePerSecond: ((uint)definition.SimulationRateHz),
            populationCapacity: definition.Population.Capacity,
            errors: errors
        );

        // Population distributions run after spawn points so an explicit-points region can resolve every name.
        ValidateDistribution(
            distribution: definition.Population.Distribution,
            path: "bodies.distribution",
            spawnPointIds: spawnPointIds,
            allowDisc: true,
            allowPoints: true,
            allowLattice: false,
            allowZeroDisc: false,
            errors: errors
        );
        ValidatePopulationVariation(
            variation: definition.Population.PeerVariation,
            path: "bodies.peerVariation",
            minIndex: localSeats,
            errors: errors
        );
        ValidatePopulationVariation(
            variation: definition.Population.SeatVariation,
            path: "bodies.seatVariation",
            minIndex: 0,
            errors: errors
        );
        ValidateObserverDisclosure(
            disclosure: definition.Population.Disclosure,
            errors: errors
        );
        ValidateSequence(
            sequence: definition.Population.PeerColors,
            path: "bodies.peerColors",
            minIndex: WorldBodiesLimits.LocalSeatCount,
            errors: errors,
            WorldSequence.Additive,
            WorldSequence.R1
        );

        // State validates BEFORE Kits and Hud: the rows-by-name map it returns is what a kit's own `generate` effect
        // resolves against (a kit naming an undeclared generator or destination row refuses HERE, at load, rather
        // than at first fire) and what a state.<row>/state.<row>.<key> HUD binding resolves against —
        // refuse-unknown-by-name, the same discipline every other HudBindingVocabulary token gets.
        var stateRows = ValidateState(
            rows: definition.State,
            generators: definition.Generators,
            dynamicsNames: dynamicsNames,
            errors: errors
        );
        var actionStateSlots = ValidateActionState(
            definition: definition,
            errors: errors
        );
        var targetRegisterNames = ValidateTargetRegisters(
            registers: definition.TargetRegisters,
            errors: errors
        );
        var navigationDomainNames = ValidateNavigation(
            definition: definition,
            errors: errors
        );
        var programs = ValidateBodyMotionPrograms(
            programs: definition.BodyMotionPrograms,
            targetRegisterNames: targetRegisterNames,
            navigationDomainNames: navigationDomainNames,
            curveNames: curveNames,
            simulationRateHz: definition.SimulationRateHz,
            errors: errors
        );

        var (allChannelNames, compositionChannelNames) = ValidateChannels(
            definition: definition,
            errors: errors,
            programs: programs
        );
        // Channels and target registers share one Drive-reach ordinal space, and every per-ordinal table below is
        // sized by ChannelLimits.MaxChannels. An over-budget count cannot be collected and walked past: the first
        // compile that indexes one of those tables by an authored count faults out of its own array before the
        // collected refusals are raised. This is a gate, not another collected error.
        var declaredOrdinals = (definition.Channels.Count + definition.TargetRegisters.Count);

        if (declaredOrdinals > ChannelLimits.MaxChannels) {
            errors.Add(item: $"channels and targetRegisters declare {declaredOrdinals} shared Drive-reach ordinals; the maximum is {ChannelLimits.MaxChannels}.");
            RefuseCollected(errors: errors);
        }
        var kitNames = ValidateKits(
            allChannelNames: allChannelNames,
            compositionChannelNames: compositionChannelNames,
            definition: definition,
            dynamicsNames: dynamicsNames,
            errors: errors,
            programs: programs,
            stateRows: stateRows,
            stateSlots: actionStateSlots,
            targetRegisterNames: targetRegisterNames
        );

        scope.KitNames = kitNames;

        ValidateAssignment(
            assignment: definition.Assignment,
            kitNames: kitNames,
            errors: errors
        );
        ValidateDefaultPeerSource(
            definition: definition,
            errors: errors
        );
        var addonNames = ValidateAddons(
            addons: definition.Addons,
            populationCapacity: definition.Population.Capacity,
            errors: errors
        );

        var (iconNames, iconsAuthored) = ValidateIconography(
            definition: definition,
            errors: errors
        );

        ValidateBindingOverlays(
            overlays: definition.BindingOverlays,
            channels: CompilableChannelTable(channels: definition.Channels),
            stateRows: stateRows,
            seatModes: definition.SeatModes,
            iconNames: iconNames,
            iconsAuthored: iconsAuthored,
            errors: errors
        );

        ValidateTheme(
            definition: definition,
            errors: errors
        );
        ValidateMarkers(
            definition: definition,
            errors: errors,
            iconNames: iconNames,
            iconsAuthored: iconsAuthored
        );

        ValidateStorage(
            storage: definition.Storage,
            errors: errors
        );

        ValidateIdentityMotionState(
            identity: definition.Identity,
            stateRows: stateRows,
            errors: errors
        );
        ValidateIdentityVoiceProfile(
            identity: definition.Identity,
            patchIds: patchIds,
            errors: errors
        );

        // The declared SOURCES validate before anything resolves a reference against them, so a site's refusal is
        // "no such source" rather than a second reading of a source this pass would have refused anyway.
        ValidateGenerators(
            generators: definition.Generators,
            errors: errors
        );

        // Rules validate LAST among the row sets: WorldRuleCompiler resolves every name a rule can spell against the
        // whole candidate (state rows, generator rows, placement regions), so it runs once those are known good. The
        // compiler is the ONE authority on what a rule may say — this pass calls it rather than restating its rules,
        // and turns its by-name refusal into an aggregated error line.
        ValidatePatterns(
            definition: definition,
            errors: errors
        );
        ValidateRules(
            rules: definition.Rules,
            definition: definition,
            errors: errors
        );

        // Properties/Interactions validate right after Rules: the property registry's own shape check (each name
        // backed by a declared keyed int state row) needs only stateRows (already built above), and Interactions
        // compiles through the SAME WorldRuleCompiler path Rules just did, over the whole candidate.
        ValidateProperties(
            properties: definition.Properties,
            stateRows: stateRows,
            errors: errors
        );
        ValidateInteractions(
            interactions: definition.Interactions,
            definition: definition,
            errors: errors
        );
        if (errors.Count == 0) {
            var ruleBudget = WorldRuleWorkBudget.Measure(definition: definition);
            if (ruleBudget.WorkUnitsPerTick > WorldRuleCapacity.MaxWorkUnitsPerTick) {
                errors.Add(item: $"rules/interactions/flock affinities derive {ruleBudget.WorkUnitsPerTick} worst-case work units per tick, exceeding the maximum of {WorldRuleCapacity.MaxWorkUnitsPerTick}.");
            }
        }

        // Called early — the host section references no other section. Only an AUTHORED section validates: absence
        // reads the inert WorldHostDefaults.Absent (no presentation), whose zero extent is the meaning of "no
        // window", not an authored value to range-check.
        if (definition.HostRaw is { } authoredHost) {
            ValidateHost(
                host: authoredHost,
                generators: definition.Generators,
                stateRows: definition.State,
                errors: errors
            );
        }

        ValidateFields(
            definition: definition,
            errors: errors
        );

        // Groups validates HERE — ahead of its original position ahead of Grants below — because a destination row's
        // scope=group selector resolves against the SAME declared group-id set
        // Grants already needed: both are threaded from this ONE call rather than validating groups twice. Only
        // stateRows (already built above) is a dependency, so the move changes no other ordering.
        var groupIds = ValidateGroups(
            groups: definition.Groups,
            stateRows: stateRows,
            errors: errors
        );

        // A destination row's own `reference` resolves against this set — captured here rather than re-walked, the
        // same forward-threading prototypeIds/lookNames already ride.
        var referenceNames = ValidateReferences(
            references: definition.References,
            errors: errors
        );

        ValidatePortals(
            portals: definition.Portals,
            errors: errors
        );

        // A portal facet's destination resolves against THIS set (below, inside ValidatePlacements), never against
        // referenceNames directly — the destinations section is the layer a portal facet now names, exactly the
        // model docs/vision.md "Reference, destination and session are different facts" describes. A scope=group
        // row's selector resolves against groupIds (see the move above).
        var destinationNames = ValidateDestinations(
            destinations: definition.Destinations,
            references: definition.References,
            referenceNames: referenceNames,
            groupIds: groupIds,
            errors: errors
        );

        scope.DestinationNames = destinationNames;

        ValidateAdjacencies(
            definition: definition,
            destinationNames: destinationNames,
            errors: errors,
            neighbours: (validateAdjacencyClaims
            ? neighbours
            : null),
            proveNeighbours: validateAdjacencyClaims
        );

        var authoring = definition.Authoring;

        // Only an AUTHORED section validates — absence reads the derived WorldPlacementPolicyDefaults.DeriveFrom
        // policy (zero headroom, the scale envelope the placement rows themselves span), whose values are derived
        // facts, not authored values to range-check.
        if (definition.PlacementsRaw?.Policy is { } authoredAuthoring) {
            ValidateAuthoring(
                authoring: authoredAuthoring,
                errors: errors
            );
        }

        // The contact solver. The engine holds no tuning of its own, so collision is REQUIRED exactly when the
        // census implies a body to resolve contacts for (the kits/views derived refusal, not a flat floor): a
        // bodyless document may author none and reads the inert WorldCollision.Absent.
        if (definition.CollisionRaw is null) {
            if (definition.Population.Capacity > 0) {
                errors.Add(item: "collision is required when the census implies a body (bodies.capacity > 0) — author it, or inherit a basis (e.g. standard.world.json) that does.");
            }
        } else {
            ValidateCollision(
                collision: definition.CollisionRaw,
                errors: errors
            );
        }
        ValidateGravity(
            errors: errors,
            gravity: definition.Gravity,
            placements: definition.Placements
        );
        var fontNames = ValidateTextCatalog(
            text: definition.Text,
            errors: errors
        );

        scope.FontNames = fontNames;
        scope.HasTextCatalog = (definition.Text is not null);

        var prototypeIds = ValidateCreations(
            definition: definition,
            creations: definition.Creations,
            fontNames: fontNames,
            hasTextCatalog: (definition.Text is not null),
            errors: errors
        );

        scope.PrototypeIds = prototypeIds;

        // The LOOK rows go AFTER ValidateCreations (a creation look resolves its PrototypeId against the id-set that
        // returns) and BEFORE ValidatePlacements (a future Inhabit facet will resolve its Look against the look-name set
        // this returns) — the same forward-threading prototypeIds already rides.
        var lookNames = ValidateLooks(
            creations: definition.Creations,
                looks: definition.Looks,
            prototypeIds: prototypeIds,
            dynamicsNames: dynamicsNames,
            errors: errors
        );

        scope.LookNames = lookNames;

        ValidateLookAssignment(
            assignment: definition.LookAssignment,
            lookNames: lookNames,
            errors: errors
        );

        var placementIds = ValidatePlacements(
            placements: definition.Placements,
            definition: definition,
            authoring: authoring,
            requiresField: WorldContactSelection.RequiresField(collision: definition.Collision),
            scope: scope,
            errors: errors
        );

        // Document-wide, independent of any single placement's own row checks above — see its own remarks.
        ValidateSessionWindowBudget(
            placements: definition.Placements,
            errors: errors
        );

        var cameras = new HashSet<string>(comparer: StringComparer.Ordinal);

        {
            var authoredCameras = definition.Cameras;

            if (authoredCameras.Count > OffscreenRenderBudget.RegisteredViews) {
                errors.Add(item: $"cameras count {authoredCameras.Count} exceeds the maximum of {OffscreenRenderBudget.RegisteredViews} (each camera can carry a persistent offscreen render; the runtime registers no more views than that).");
            }

            for (var index = 0; (index < authoredCameras.Count); index++) {
                var camera = authoredCameras[index];
                var path = $"cameras[{index}]";

                if (camera is null) {
                    errors.Add(item: $"{path} is required.");
                    continue;
                }

                RequireUniqueName(
                    value: camera.Name,
                    seen: cameras,
                    path: path,
                    field: "name",
                    errors: errors
                );

                // A null anchor resolves the world reference frame.
                if (camera.Anchor is { } anchor) {
                    ValidateAnchor(
                        anchor: anchor,
                        placements: definition.Placements,
                        placementIds: placementIds,
                        creations: definition.Creations,
                        populationCapacity: definition.Population.Capacity,
                        path: $"{path}.anchor",
                        errors: errors
                    );
                }

                if (camera.Anchors is { } candidates) {
                    if (camera.Anchor is not null) {
                        errors.Add(item: $"{path} authors both anchor and anchors — a single unconditional anchor is 'anchor', a ranked list is 'anchors'.");
                    }

                    if (candidates.Count == 0) {
                        errors.Add(item: $"{path}.anchors must carry at least one candidate.");
                    }

                    for (var candidateIndex = 0; (candidateIndex < candidates.Count); candidateIndex++) {
                        var candidate = candidates[candidateIndex];
                        var candidatePath = $"{path}.anchors[{candidateIndex}]";

                        if (candidate is null) {
                            errors.Add(item: $"{candidatePath} is required.");

                            continue;
                        }

                        ValidateAnchor(
                            anchor: candidate.Anchor,
                            placements: definition.Placements,
                            placementIds: placementIds,
                            creations: definition.Creations,
                            populationCapacity: definition.Population.Capacity,
                            path: $"{candidatePath}.anchor",
                            errors: errors
                        );
                        ValidateOverlayPredicate(
                            definition: definition,
                            errors: errors,
                            path: $"{candidatePath}.when",
                            predicate: candidate.When,
                            stateRows: stateRows
                        );
                    }
                }

                ValidateProgram(
                    curveNames: curveNames,
                    definition: definition,
                    dynamicsNames: dynamicsNames,
                    errors: errors,
                    path: $"{path}.rig",
                    placementIds: placementIds,
                    program: camera.Rig
                );

                if (
                    (camera.RenderWidth == 0U) ||
                    (camera.RenderHeight == 0U) ||
                    (camera.RenderWidth > MaxSurfaceDimension) ||
                    (camera.RenderHeight > MaxSurfaceDimension)
                ) {
                    errors.Add(item: $"{path} render dimensions must be within 1..{MaxSurfaceDimension}.");
                }
            }
        }

        scope.Cameras = cameras;

        // An owned world (Identity not null) authors seat-scope panels: WorldHudCapacity.MaxSeatPanels of them, each
        // capped at MaxElementsPerSeatPanel, WorldHudLayer.Replace refused (a panel confined to one seat's viewport
        // has no base slot to take over). Either scope is capped at MaxFrameSources structurally unique frame sources.
        // A plain world document keeps the world-scope panel/element ceilings and admits Replace. Runs here (not
        // beside State, above) because a hud.panels Frame element's source resolves against the SAME declared camera
        // set a screen row's own 'view' arm does — the cameras set just built above.
        ValidateHud(
            cameras: cameras,
            definition: definition,
            hud: definition.Hud,
            stateRows: stateRows,
            isIdentityScope: (definition.Identity is not null),
            errors: errors
        );

        // A probe socket's 'view' arm resolves against the SAME declared camera set a screen row's own 'view' arm
        // does, so this runs only once that set is fully built above — never earlier, and never twice.
        ValidateProbes(
            cameras: cameras,
            definition: definition,
            errors: errors
        );

        ValidateCaptures(
            definition: definition,
            errors: errors
        );

        // The window composition. The engine holds no rig of its own, so views is REQUIRED exactly when the census
        // implies a body to look at (a derived refusal, not a flat floor, mirroring kits): a seatless document
        // composes no seat view and may author none. Named cameras a layout slot references must resolve against the
        // camera set just built.
        ValidateViews(
            capacity: definition.Population.Capacity,
            cameras: cameras,
            curveNames: curveNames,
            definition: definition,
            dynamicsNames: dynamicsNames,
            errors: errors,
            placementIds: placementIds,
            views: definition.ViewsRaw
        );

        // The whole document's camera-program name table (every cameras[].rig, plus views.seatRig/cameraRig) — the
        // namespace a blend op resolves against. Assembled once here so dangling names and blend cycles are checked
        // exactly once, over the whole graph, never per-program. A duplicated program name is refused: a blend
        // resolves by name alone, so two programs under one name resolve nothing honestly.
        var cameraPrograms = new Dictionary<string, WorldCameraProgram>(comparer: StringComparer.Ordinal);

        void AddCameraProgram(WorldCameraProgram? program, string path) {
            if (program is not { Name: { Length: > 0 } }) {
                return;
            }

            if (!cameraPrograms.TryAdd(
                key: program.Name,
                value: program
            )) {
                errors.Add(item: $"{path}.name '{program.Name}' is duplicated across the document's camera programs.");
            }
        }

        for (var index = 0; (index < definition.Cameras.Count); index++) {
            AddCameraProgram(
                path: $"cameras[{index}].rig",
                program: definition.Cameras[index]?.Rig
            );
        }

        if (definition.ViewsRaw is { } viewsForBlend) {
            AddCameraProgram(
                path: "views.seatRig",
                program: viewsForBlend.SeatRig
            );
            AddCameraProgram(
                path: "views.cameraRig",
                program: viewsForBlend.CameraRig
            );
        }

        ValidateCameraPrograms(
            errors: errors,
            programs: cameraPrograms
        );

        ValidateSeatModes(
            definition: definition,
            errors: errors
        );

        var screenIndices = new HashSet<int>();
        // The declared-live console sources (screens[*].source, NOT magazine entries): the feed owns ONE upload surface,
        // so at most one may be live at a time. A console entry sitting unselected in a magazine is legal.
        var consoleLiveIndices = new List<int>();
        // The derived-face slots the binder reserves up front (Program.cs concatenates them after the document screens):
        // a document screen at one of these indices would silently collide with the reserved placeholder in the binder's
        // dict-fill, so the range is carved out of the authored screen-index space here. The membership test itself is
        // WorldPrototypeFacets.IsReservedFaceIndex — shared with the authoring-headroom scan, which excludes the same
        // band; these two are the only rules that hand out a screen index, and they must exclude ONE set.
        var reservedFaceStart = WorldPlacementPolicy.DerivedFaceBase;
        var reservedFaceEnd = (reservedFaceStart + authoring.DerivedFaceScreens);
        // The kits a screen route may name: only a kit carrying a pad map assigns channels a meaning at a machine,
        // so a route naming any other kit would compile to an empty pad and silently press nothing.
        var padKits = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var padKit in definition.Kits) {
            if (padKit is { Name: { Length: > 0 } padKitName, PadRaw: { Count: > 0 } }) {
                _ = padKits.Add(item: padKitName);
            }
        }

        {
            var screens = definition.Screens;

            for (var index = 0; (index < screens.Count); index++) {
                var screen = screens[index];
                var path = $"screens[{index}]";

                if (screen is null) {
                    errors.Add(item: $"{path} is required.");
                    continue;
                }

                if (
                    (screen.Index < 0) ||
                    (screen.Index >= SdfProgramBuilder.MaxScreenSurfaces)
                ) {
                    errors.Add(item: $"{path}.index {screen.Index} is outside 0..{(SdfProgramBuilder.MaxScreenSurfaces - 1)}.");
                } else if (!screenIndices.Add(item: screen.Index)) {
                    errors.Add(item: $"{path}.index {screen.Index} is duplicated.");
                } else if (WorldPlacementPolicy.IsReservedFaceIndex(
                    index: screen.Index,
                    derivedFaceScreens: authoring.DerivedFaceScreens
                )) {
                    errors.Add(item: $"{path}.index {screen.Index} is inside the reserved derived-face range {reservedFaceStart}..{(reservedFaceEnd - 1)} (creation faces bind there — author screens below {reservedFaceStart}).");
                }

                if (
                    !IsFinite(value: screen.Origin) ||
                    !IsFinite(value: screen.Right) ||
                    !IsFinite(value: screen.Up)
                ) {
                    errors.Add(item: $"{path} frame vectors must contain finite coordinates.");
                } else if (
                    (screen.Right.LengthSquared() <= MinimumBasisLengthSquared) ||
                    (screen.Up.LengthSquared() <= MinimumBasisLengthSquared) ||
                    (Vector3.Cross(
                    vector1: screen.Right,
                    vector2: screen.Up
                ).LengthSquared() <= MinimumBasisLengthSquared)
                ) {
                    errors.Add(item: $"{path} right/up vectors must be non-zero and linearly independent.");
                } else if (ScreenBasisSkew(screen: screen) > MaximumScreenBasisSkew) {
                    errors.Add(item: $"{path} right/up vectors must be orthogonal (the unit axes' dot product is {ScreenBasisSkew(screen: screen)}, and at most {MaximumScreenBasisSkew} is accepted). They are the slab's local +X/+Y: the render path derives the slab's orientation and sampled UV from them while the collider projects its half-extents onto them, so a skewed pair renders and collides as different solids.");
                }

                if (
                    !float.IsFinite(f: screen.HalfWidth) ||
                    !float.IsFinite(f: screen.HalfHeight) ||
                    !float.IsFinite(f: screen.HalfDepth) ||
                    (screen.HalfWidth <= 0f) ||
                    (screen.HalfHeight <= 0f) ||
                    (screen.HalfDepth <= 0f)
                ) {
                    errors.Add(item: $"{path} half extents must be finite and positive.");
                }

                // The declared source and each magazine entry cross the SAME source gate (a magazine entry could
                // otherwise name an undeclared camera). A declared console source counts against the one-live ceiling;
                // a console entry sitting in the magazine does not.
                if (ValidateScreenSource(
                    definition: definition,
                    source: screen.Source,
                    path: $"{path}.source",
                    scope: scope,
                    cablePermitted: true,
                    errors: errors
                )) {
                    consoleLiveIndices.Add(item: screen.Index);
                }

                ValidateRoute(
                    route: screen.Route,
                    path: $"{path}.route",
                    channelNames: allChannelNames,
                    padKits: padKits,
                    errors: errors
                );
                ValidateMagazine(
                    definition: definition,
                    magazine: screen.Magazine,
                    path: $"{path}.magazine",
                    scope: scope,
                    errors: errors
                );

                // The screen's solidity facet — a box collider from the slab's frame + margin. The effective
                // per-axis extent must stay positive (a margin that inverts the box is rejected by name).
                if (screen.Solid is { } screenSolid) {
                    RequireFinite(
                        value: screenSolid.Margin,
                        name: $"{path}.solid.margin",
                        errors: errors
                    );
                    RequirePositiveEffectiveExtent(
                        halfExtents: new Vector3(
                            x: screen.HalfWidth,
                            y: screen.HalfHeight,
                            z: screen.HalfDepth
                        ),
                        margin: screenSolid.Margin,
                        path: $"{path}.solid.margin",
                        errors: errors
                    );
                }
            }

            var availableHeadroom = ((SdfProgramBuilder.MaxScreenSurfaces - authoring.DerivedFaceScreens) - screenIndices.Count);

            if (authoring.AuthoringHeadroomScreens > availableHeadroom) {
                errors.Add(item: $"placements.policy.authoringHeadroomScreens asks for {authoring.AuthoringHeadroomScreens} slot(s), but only {Math.Max(
                    val1: 0,
                    val2: availableHeadroom
                )} remain after {screenIndices.Count} authored screen(s) and {authoring.DerivedFaceScreens} derived-face reservation(s).");
            }
        }

        // The one-live-console ceiling: the console feed owns a single upload surface, so a second declared console
        // screen is an error naming both indices.
        if (consoleLiveIndices.Count > 1) {
            errors.Add(item: $"at most one screen may declare a console source, but screens {string.Join(
                separator: " and ",
                values: consoleLiveIndices
            )} both do.");
        }

        // The machine cable groups derive from the declared screens rows' own machine sources, validated beside them.
        ValidateMachineCables(
            screens: definition.Screens,
            errors: errors
        );

        // Speakers and the audio defaults validate LAST: their references span every earlier row set (the screen
        // index set, the placement rows, the tune/patch ids, the camera names — and the cue table's emitter
        // placements name speaker rows, so the speaker pass hands its name set forward).
        var speakerNames = ValidateSpeakers(
            definition: definition,
            errors: errors,
            patchIds: patchIds,
            placementIds: placementIds,
            screenIndices: screenIndices,
            tuneIds: tuneIds
        );

        // Only an AUTHORED audio section validates: absence reads the inert WorldAudioDefaults.Absent (silent),
        // whose zeros are the meaning of "no audio", not authored values to range-check.
        if (definition.AudioRaw is { } authoredAudio) {
            ValidateAudioDefaults(
                audio: authoredAudio,
                cameras: cameras,
                patchIds: patchIds,
                speakerNames: speakerNames,
                rules: definition.Rules,
                localSeats: definition.Population.LocalSeats,
                errors: errors
            );
        }

        // Groups validates before Grants: a grant row may target a group principal, so Grants needs the declared
        // group-id set already resolved.
        ValidateGrants(
            grants: definition.Grants,
            addonNames: addonNames,
            groupIds: groupIds,
            populationCapacity: definition.Population.Capacity,
            localSeats: localSeats,
            errors: errors
        );

        // Admission validates last of all: it references nothing else in the document except the population
        // capacity its templates' body subjects bound against (already in scope), and it is the newest section,
        // appended over the already-shipped set for the same reason Grants/Links are.
        ValidateAdmission(
            entries: definition.Admission,
            populationCapacity: definition.Population.Capacity,
            errors: errors
        );

        RefuseCollected(errors: errors);
    }
    private static void RefuseCollected(List<string> errors) {
        if (errors.Count > 0) {
            throw new InvalidOperationException(message: $"Invalid WorldDefinition:{Environment.NewLine} - {string.Join(
                separator: $"{Environment.NewLine} - ",
                values: errors
            )}");
        }
    }
    // Unknown top-level members were captured into the root's [JsonExtensionData] rather than rejected. This document
    // validates them through the shared DocumentExtensionsPolicy regime: a reserved-prefix key ('$' schema-like keys,
    // '_' comments) is an intentional escape hatch; anything else at the top level is an authoring mistake (most
    // often a mis-cased or mistyped section name), reported here rather than silently absorbed.
    private static void ValidateExtensions(IDictionary<string, JsonElement>? extensions, List<string> errors) {
        DocumentExtensionsPolicy.ValidateKeys(
            extensions: extensions,
            report: key => errors.Add(item: $"unknown top-level member '{key}'; top-level keys are case-sensitive camelCase.")
        );
    }
    private static void ValidateSequence(WorldSequence sequence, string path, int minIndex, List<string> errors, params string[] allowed) {
        if (sequence is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if (!allowed.Contains(
            value: sequence.Name,
            comparer: StringComparer.Ordinal
        )) {
            errors.Add(item: $"{path}.name '{(sequence.Name ?? "(absent)")}' must be one of: {string.Join(
                separator: ", ",
                values: allowed
            )}.");
        }

        if ((((long)minIndex) + sequence.Offset) < 0L) {
            errors.Add(item: $"{path}.offset {sequence.Offset} makes the first consumed index {minIndex} negative.");
        }

        if (string.Equals(
            a: sequence.Name,
            b: WorldSequence.Additive,
            comparisonType: StringComparison.Ordinal
        )) {
            RequirePositive(
                value: sequence.Step,
                name: $"{path}.step",
                errors: errors
            );
        } else if (sequence.Step != 0f) {
            errors.Add(item: $"{path}.step must be 0 for the '{sequence.Name}' sequence.");
        }
    }

    /// <summary>Validates a candidate definition, including its cross-document adjacency claims, without throwing. On
    /// failure, <paramref name="reason"/> carries the collapsed one-line error list.</summary>
    /// <param name="definition">The candidate definition.</param>
    /// <param name="reason">The collapsed failure reason, or empty on success.</param>
    /// <param name="neighbours">The injected neighbour resolver a cross-document adjacency proof reads (see
    /// <see cref="Validate"/>). Required — not defaulted — so every call site states whether it can resolve a
    /// neighbour rather than silently opting out; <see langword="null"/> remains a legitimate, explicit answer for a
    /// call site that must refuse an unprovable claim. It is consulted only when a document authors a
    /// <see cref="WorldDefinition.Adjacencies"/>.</param>
    /// <returns><see langword="true"/> when the candidate is valid.</returns>
    public static bool TryValidate(WorldDefinition definition, out string reason, IWorldNeighbourResolver? neighbours) {
        try {
            ValidateCore(
                definition: definition,
                neighbours: neighbours,
                validateAdjacencyClaims: true
            );
            reason = string.Empty;

            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }
    /// <summary>Validates every fact owned by one document without re-reading neighbour documents. This is the
    /// apply-time gate for a candidate whose cross-document adjacency claims were already proved at its load boundary;
    /// callers must separately refuse edits that change an existing claim or any input to its derived floor.</summary>
    /// <param name="definition">The candidate definition.</param>
    /// <param name="reason">The collapsed failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the document-local facts are valid.</returns>
    public static bool TryValidateLocally(WorldDefinition definition, out string reason) {
        try {
            ValidateCore(
                definition: definition,
                neighbours: null,
                validateAdjacencyClaims: false
            );
            reason = string.Empty;

            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }
    /// <summary>Determines whether <paramref name="motion"/> supplies every tuning facet <paramref name="program"/>'s
    /// selected operations read — the one check <see cref="ValidateMotionRow"/> (at boot) and the runtime
    /// body-motion-program switch (the <c>body.motion</c> door) share, so a document-legal kit can never
    /// runtime-switch into a program its declared motion row cannot back.</summary>
    /// <param name="motion">The kit's declared motion row.</param>
    /// <param name="program">The compiled body motion program the switch targets.</param>
    /// <param name="reason">On failure, the refusal detail naming the missing facet(s) and the program; <c>""</c> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="motion"/> supplies every facet <paramref name="program"/> requires.</returns>
    public static bool TryValidateProgramCoherence(WorldMotion motion, CompiledBodyMotionProgram program, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: motion);
        ArgumentNullException.ThrowIfNull(argument: program);

        var missing = RequiredMotionTuningFacets(program: program) & ~SuppliedMotionTuningFacets(motion: motion);

        if (missing == MotionTuningFacet.None) {
            reason = "";

            return true;
        }

        reason = $"motion row does not supply {missing} required by body motion program '{program.Name}'.";

        return false;
    }
    /// <summary>Validates a candidate definition, throwing <see cref="InvalidOperationException"/> naming every
    /// collected failure when it is not sound.</summary>
    /// <param name="definition">The candidate definition.</param>
    /// <param name="neighbours">The injected seam an adjacency uses to resolve its counterpart document. The
    /// implementation must obtain an authority-delivered document,
    /// not assume that the neighbour shares a host, process, or filesystem (see <see cref="IWorldNeighbourResolver"/>).
    /// Required — not defaulted — so a caller states its answer rather than silently declining to decide; pass
    /// <see langword="null"/> explicitly for a call site with no reachable resolver (see <see cref="TryValidate"/>).
    /// A document authoring no adjacency is unaffected either way. A document that does author one, validated with
    /// a null (or unreachable) resolver, is refused by name.</param>
    public static void Validate(WorldDefinition definition, IWorldNeighbourResolver? neighbours) {
        ValidateCore(
            definition: definition,
            neighbours: neighbours,
            validateAdjacencyClaims: true
        );
    }

    private readonly record struct AssetCheck((string Path, string Message)[] Violations, string? CanonicalHash);
}
