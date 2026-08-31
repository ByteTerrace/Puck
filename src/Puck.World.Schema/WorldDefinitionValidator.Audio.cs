namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static bool IsAudioCurve(string? curve) =>
        (string.Equals(
            a: curve,
            b: WorldAudioDefaults.CurveSmoothstep,
            comparisonType: StringComparison.Ordinal
        ) ||
        string.Equals(
            a: curve,
            b: WorldAudioDefaults.CurveLinear,
            comparisonType: StringComparison.Ordinal
        ));
    // The audio host-section defaults: the master gain rides the shared ceiling, the coalescing radius/fade are
    // physical, the curve token is v1's one recognized value, the listener policy resolves (focus | seat:<n> |
    // a declared camera name), and every cue-table row resolves (a CLOSED event token, a live patch id, the gain
    // ceiling in thousandths, a placement token whose emitter form names a declared speaker).
    private static void ValidateAudioDefaults(WorldAudioDefaults audio, HashSet<string> cameras, HashSet<string> patchIds, HashSet<string> speakerNames, int localSeats, List<string> errors) {
        RequireRange(
            value: audio.MasterGain,
            min: 0f,
            max: Puck.World.Authoring.CreationSoundDocument.MaxLevel,
            name: "audio.masterGain",
            errors: errors
        );
        RequirePositive(
            value: audio.DefaultSpeakerRadius,
            name: "audio.defaultSpeakerRadius",
            errors: errors
        );
        RequireNonNegative(
            value: audio.DefaultBedFadeSeconds,
            name: "audio.defaultBedFadeSeconds",
            errors: errors
        );

        if (!IsAudioCurve(curve: audio.DefaultCurve)) {
            errors.Add(item: $"audio.defaultCurve '{(audio.DefaultCurve ?? "(absent)")}' must be '{WorldAudioDefaults.CurveSmoothstep}' or '{WorldAudioDefaults.CurveLinear}'.");
        }

        var listener = audio.Listener;

        if (string.IsNullOrWhiteSpace(value: listener)) {
            errors.Add(item: "audio.listener is required ('focus', 'seat:<n>', or a declared camera name).");
        } else if (
            !string.Equals(
            a: listener,
            b: WorldAudioDefaults.ListenerFocus,
            comparisonType: StringComparison.Ordinal
        ) &&
            !cameras.Contains(item: listener)
        ) {
            if (listener.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldAudioDefaults.ListenerSeatPrefix
            )) {
                if (
                    !int.TryParse(
                    s: listener.AsSpan(start: WorldAudioDefaults.ListenerSeatPrefix.Length),
                    result: out var seat
                ) ||
                    (seat < 1) ||
                    (seat > localSeats)
                ) {
                    errors.Add(item: $"audio.listener '{listener}' names no seat (expected seat:1..seat:{localSeats}).");
                }
            } else {
                errors.Add(item: $"audio.listener '{listener}' is not 'focus', 'seat:<n>', or a declared camera name.");
            }
        }

        ValidateCues(
            cues: audio.Cues,
            patchIds: patchIds,
            speakerNames: speakerNames,
            errors: errors
        );
    }
    // THE CUE TABLE: absent is empty; each row's event token must sit in the CLOSED published vocabulary,
    // its patch must resolve, its gain rides the shared ceiling in thousandths, and an emitter placement must name
    // a declared speaker (at-site and listener are the only other recognized placements).
    private static void ValidateCues(IReadOnlyList<WorldAudioCue>? cues, HashSet<string> patchIds, HashSet<string> speakerNames, List<string> errors) {
        if (cues is null) {
            return;
        }

        for (var index = 0; (index < cues.Count); index++) {
            var cue = cues[index];
            var path = $"audio.cues[{index}]";

            if (cue is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!WorldAudioCue.IsEventToken(token: cue.Event)) {
                errors.Add(item: $"{path}.event '{cue.Event}' is not a published cue event token ({string.Join(
                    separator: " | ",
                    values: WorldAudioCue.EventTokens
                )}).");
            } else if (WorldAudioCue.IsProducerBypassedToken(token: cue.Event)) {
                errors.Add(item: $"{path}.event '{cue.Event}' is fired directly by its producer and can never be targeted by an authored audio.cues row.");
            }

            RequireDeclared(
                value: cue.PatchId,
                declaredSet: patchIds,
                path: path,
                field: "patchId",
                rowNoun: "patch",
                errors: errors
            );

            if (
                (cue.GainThousandths is { } gain) &&
                ((gain < 0) || (gain > ((int)(Puck.World.Authoring.CreationSoundDocument.MaxLevel * 1000f))))
            ) {
                errors.Add(item: $"{path}.gainThousandths {gain} must be within [0, {((int)(Puck.World.Authoring.CreationSoundDocument.MaxLevel * 1000f))}].");
            }

            switch (cue.Placement) {
                case WorldAudioCue.PlacementAtSite:
                case WorldAudioCue.PlacementListener:
                    break;
                case { } placement when placement.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldAudioCue.PlacementEmitterPrefix
            ):
                    var speaker = placement[WorldAudioCue.PlacementEmitterPrefix.Length..];

                    if (!speakerNames.Contains(item: speaker)) {
                        errors.Add(item: $"{path}.placement 'emitter:{speaker}' names no declared speaker.");
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.placement '{cue.Placement}' must be '{WorldAudioCue.PlacementAtSite}', '{WorldAudioCue.PlacementListener}', or '{WorldAudioCue.PlacementEmitterPrefix}<speaker-name>'.");

                    break;
            }
        }
    }
    // An emission facet (scene rows + placements): the patch resolves, the level rides the shared gain ceiling, the
    // optional radius is a positive finite support.
    private static void ValidateEmission(WorldEmission? emission, HashSet<string> patchIds, string path, List<string> errors) {
        if (emission is null) {
            return;
        }

        RequireDeclared(
            value: emission.PatchId,
            declaredSet: patchIds,
            path: path,
            field: "patchId",
            rowNoun: "patch",
            errors: errors
        );

        RequireRange(
            value: emission.Level,
            min: 0f,
            max: Puck.World.Authoring.CreationSoundDocument.MaxLevel,
            name: $"{path}.level",
            errors: errors
        );

        if (emission.Radius is { } radius) {
            RequirePositive(
                errors: errors,
                name: $"{path}.radius",
                value: radius
            );
        }
    }
    private static void ValidateFeed(WorldSpeakerFeed? feed, HashSet<int> screenIndices, HashSet<string> tuneIds, HashSet<string> patchIds, string path, List<string> errors) {
        if (feed is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if (feed.Channel is not (WorldSpeakerFeed.ChannelMix or WorldSpeakerFeed.ChannelLeft or WorldSpeakerFeed.ChannelRight)) {
            errors.Add(item: $"{path}.channel '{feed.Channel}' must be '{WorldSpeakerFeed.ChannelMix}', '{WorldSpeakerFeed.ChannelLeft}', or '{WorldSpeakerFeed.ChannelRight}'.");
        }

        RequireRange(
            value: feed.Gain,
            min: 0f,
            max: Puck.World.Authoring.CreationSoundDocument.MaxLevel,
            name: $"{path}.gain",
            errors: errors
        );

        switch (feed.Source) {
            case null:
                errors.Add(item: $"{path}.source is required.");

                break;
            case WorldSpeakerSource.Machine machine when !screenIndices.Contains(item: machine.ScreenIndex):
                errors.Add(item: $"{path}.source.screenIndex {machine.ScreenIndex} names no declared screen.");

                break;
            case WorldSpeakerSource.Tune tune when (string.IsNullOrWhiteSpace(value: tune.TuneId) || !tuneIds.Contains(item: tune.TuneId)):
                errors.Add(item: $"{path}.source.tuneId '{tune.TuneId}' names no tune row.");

                break;
            case WorldSpeakerSource.Synth synth when (string.IsNullOrWhiteSpace(value: synth.PatchId) || !patchIds.Contains(item: synth.PatchId)):
                errors.Add(item: $"{path}.source.patchId '{synth.PatchId}' names no patch row.");

                break;
        }
    }
    // The speaker rows (PRESENTATION-ONLY — audio never enters sim state): name presence/uniqueness, the per-kind
    // pose/extent invariants, the feed (source resolution, channel token, the gain ceiling), and the attenuation
    // policy. A Machine source checks only that the screen row EXISTS — never its declared source kind (runtime
    // inserts overlay declared sources; no live machine at drain time is silence, not a reject). Returns the name
    // set (the cue table's emitter placements resolve against it).
    private static HashSet<string> ValidateSpeakers(WorldDefinition definition, HashSet<int> screenIndices, HashSet<string> placementIds, HashSet<string> tuneIds, HashSet<string> patchIds, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (definition.Speakers is not { } speakers) {
            errors.Add(item: "speakers is required.");

            return names;
        }

        for (var index = 0; (index < speakers.Count); index++) {
            var speaker = speakers[index];
            var path = $"speakers[{index}]";

            if (speaker is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            RequireUniqueName(
                value: speaker.Name,
                seen: names,
                path: path,
                field: "name",
                errors: errors
            );

            switch (speaker) {
                case WorldSpeaker.Fixed fixedSpeaker:
                    if (!IsFinite(value: fixedSpeaker.Position)) {
                        errors.Add(item: $"{path}.position must contain finite coordinates.");
                    }

                    break;
                case WorldSpeaker.Anchored anchoredSpeaker:
                    // Speakers resolve EVERY anchor kind (placements included — unlike the camera pose path), so the
                    // shared anchor gate runs without the camera's placement rejection.
                    ValidateAnchor(
                        anchor: anchoredSpeaker.Anchor,
                        placements: definition.Placements,
                        placementIds: placementIds,
                        creations: definition.Creations,
                        populationCapacity: definition.Population.Capacity,
                        path: $"{path}.anchor",
                        errors: errors
                    );

                    if (!IsFinite(value: anchoredSpeaker.Offset)) {
                        errors.Add(item: $"{path}.offset must contain finite coordinates.");
                    }

                    break;
                case WorldSpeaker.Bed bed:
                    if (!IsFinite(value: bed.Center)) {
                        errors.Add(item: $"{path}.center must contain finite coordinates.");
                    }

                    RequirePositive(
                        value: bed.Radius,
                        name: $"{path}.radius",
                        errors: errors
                    );

                    // The inner radius must leave a live envelope band: the mixer's finite-support law needs
                    // inner < outer (inner == outer would divide the smoothstep by zero support).
                    if (
                        !float.IsFinite(f: bed.InnerRadius) ||
                        (bed.InnerRadius < 0f) ||
                        (float.IsFinite(f: bed.Radius) && (bed.InnerRadius >= bed.Radius))
                    ) {
                        errors.Add(item: $"{path}.innerRadius {bed.InnerRadius} must be finite, non-negative, and less than radius {bed.Radius}.");
                    }

                    if (bed.FadeSeconds is { } fadeSeconds) {
                        RequireNonNegative(
                            errors: errors,
                            name: $"{path}.fadeSeconds",
                            value: fadeSeconds
                        );
                    }

                    break;
                default:
                    errors.Add(item: $"{path} is an unknown speaker kind.");

                    break;
            }

            ValidateFeed(
                feed: speaker.Feed,
                screenIndices: screenIndices,
                tuneIds: tuneIds,
                patchIds: patchIds,
                path: $"{path}.feed",
                errors: errors
            );

            if (speaker.Attenuation is { } attenuation) {
                RequirePositive(
                    value: attenuation.Radius,
                    name: $"{path}.attenuation.radius",
                    errors: errors
                );

                if (
                    (attenuation.Curve is { } curve) &&
                    !IsAudioCurve(curve: curve)
                ) {
                    errors.Add(item: $"{path}.attenuation.curve '{curve}' must be '{WorldAudioDefaults.CurveSmoothstep}', '{WorldAudioDefaults.CurveLinear}', or null.");
                }
            }
        }

        return names;
    }
}
