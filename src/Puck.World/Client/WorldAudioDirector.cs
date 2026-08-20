using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Abstractions.Machines;
using Puck.Hosting;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.Audio.Mixing;
using Puck.World.Audio;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The client-side audio director — the seam between the world document and the mixer's emitter vocabulary.
/// <see cref="ReconcileSpeakers"/> derives the emitter table from the delivered definition (speaker rows by
/// kind, emission facets keyed by row family, creation-sound emitters per placement of a sound-bearing creation) with
/// stable emitter ids — diff-by-key: a property edit keeps its id (the mixer's coefficient ramps survive), an
/// identity change (kind, anchor kind, source identity, or the referenced asset's content hash — the restart
/// discriminator) releases the id and re-enters from silence. <see cref="Publish"/> resolves each emitter's pose per
/// produced frame — entity roots from the snapshot view, entity parts from the active look's published transform
/// slots, and placements
/// from the stamped transform (the animator's current frame for animated rows) — and publishes a
/// <see cref="AudioSnapshot"/> over a ≥4-deep slab rotation.
/// </summary>
/// <remarks>
/// <para><b>The v1 trigger policy (deliberate, documented):</b> every synth-fed emitter fires exactly one seeded
/// trigger on emitter arrival (a new or identity-recreated key) — a looping patch (no duration) sustains until the
/// emitter departs (the mixer frees unbound voices); a one-shot patch plays once. Periodic/behavioral retriggering
/// is deferred. Seeds derive from the emitter key + content signature, so a voice reproduces bit for bit
/// across runs. A pending trigger rides the next <see cref="TriggerPublishRetention"/> published snapshots: the
/// publish buffer keeps only the latest, so retention ≥ two device quanta guarantees a consumer sees the event once,
/// and the mixer's high-water sequence makes repeats free.</para>
/// <para><b>Source hosting:</b> patch registration and headless tune hosting (acquire while referenced, release when
/// orphaned, the tune hash as the restart discriminator) activate when a mixer is attached
/// (<see cref="AttachMixer"/> — the offline proof and the device pump); unattached, the director only derives and
/// publishes. Machine sources bind through <see cref="MachineSourceResolver"/>: each <see cref="Publish"/> diffs the
/// binder's live machines by reference for every machine-fed plan row, so a boot/eject/live-swap rebinds the mixer
/// source and a machine booting late into a referenced slot self-heals — the keys
/// (<see cref="AudioSourceKey.Machine"/> by slot) stay stable across swaps.</para>
/// <para><b>Threading:</b> derivation and publishing stay on the window-pump thread, and the resolver is only
/// ever invoked there (it reads the binder's pump-owned slot table). The device pump adds two cross-thread callers —
/// <see cref="AttachMixer"/>/<see cref="DetachMixer"/> from the render service's governor and
/// <see cref="TryMixBlock"/> from the endpoint's fill thread — so every member that touches the mixer or the derived
/// plan serializes on one reentrant gate. The gate is uncontended in steady state (reconciles are rare, a mix block
/// is microseconds), which is the deliberate trade: one honest lock instead of a lock-free mixer-mutation protocol.</para>
/// </remarks>
internal sealed class WorldAudioDirector : IWorldAudioLever, IWorldAudioFrameFeed {
    /// <summary>The default per-publish clock advance for cue aging: one 240 Hz sim step (the offline drivers'
    /// cadence — one publish per mixed 200-frame block). The live frame source passes its real presentation delta.</summary>
    public const float DefaultPublishDeltaSeconds = (1f / 240f);
    /// <summary>The life cap for a cue voicing a looping patch (no authored duration): 2 s of audio frames. A cue is
    /// a transient by definition — a finite patch's life derives from its own envelope (data); only the loop cap is
    /// an invariant.</summary>
    public const long LoopingCueLifeFrames = (2L * AudioMixer.SampleRate);
    /// <summary>The slab-rotation depth: the consumer holds one snapshot for one ~5.33 ms block; the
    /// producer needs ≥33 ms to lap four slabs — safe by an order of magnitude.</summary>
    public const int SnapshotRotation = 4;
    /// <summary>The transient cue-emitter pool size — capacity structure like the snapshot's emitter cap, an
    /// engine invariant rather than world data: these slots are reserved off <see cref="AudioSnapshot.DefaultMaxEmitters"/>
    /// (the reconcile overflow warning charges them), so a full derived plan can never starve a cue. A cue arriving
    /// with the pool full evicts the transient nearest its own expiry (its voice releases with the departed emitter).</summary>
    public const int TransientCueCapacity = 4;
    /// <summary>How many published snapshots a pending trigger rides (see the type remarks).</summary>
    public const int TriggerPublishRetention = 8;

    private readonly WorldStampPool? m_animator;
    private readonly WorldClient? m_client;
    private readonly AudioSnapshot[] m_slabs;

    private ulong m_cueOrdinal;
    private WorldDefinition? m_definition;
    // Set on attach: the next pump-thread sync re-applies every cached binding into the (new) mixer.
    private bool m_machineBindingsDirty;
    private AudioMixer? m_mixer;
    private ulong m_nextTriggerSequence;
    // The world.volume session lever (the render-levers asymmetry): null until touched — the document's
    // MasterGain then owns the live gain (reconcile follows it; the offline drivers stay purely document-driven);
    // once set, the lever owns "now" for the rest of the session and world.save folds it back into the document.
    private float? m_sessionMasterVolume;
    private int m_slabIndex;

    private readonly PublishBuffer<AudioSnapshot> m_buffer = new();
    private readonly List<EmitterPlan> m_plan = new();
    // The stable-id registry: emitter key → (id, identity signature). Survives reconciles so property edits keep
    // their mixer ramp state; an identity change re-keys (a fresh id ramps in from silence).
    private readonly Dictionary<string, EmitterIdentity> m_registry = new(comparer: StringComparer.Ordinal);
    private readonly List<PendingTrigger> m_pendingTriggers = new();
    // The mixer-facing patch registration set (world patch rows by id + inline creation-sound patches by emitter
    // key) — applied on attach and on every reconcile while attached.
    private readonly List<(string Id, VoicePatch Patch)> m_patchSet = new();
    // The headless tune hosts, by tune id (live only while a mixer is attached).
    private readonly Dictionary<string, TuneHost> m_tuneHosts = new(comparer: StringComparer.Ordinal);
    // The live machine bindings by screen slot: which IAudioMachine each Machine-source key currently drains.
    // Gate-guarded (Publish syncs it, DetachMixer clears it); the RESOLVER is only invoked from Publish.
    private readonly Dictionary<int, MachineBinding> m_machineBindings = new();
    private readonly List<int> m_machineBindingScratch = new();
    // THE serialization gate (see the type remarks): reentrant, so Admit's SubmitTrigger nests under ReconcileSpeakers.
    private readonly Lock m_gate = new();
    // THE CUE TABLE, derived at reconcile: event token → its cue rows (gain in Q16, placement resolved to a
    // kind + optional speaker name). Cue patches are world patch rows, so the ordinary patch-set registration covers them.
    private readonly Dictionary<string, List<CueRow>> m_cueRows = new(comparer: StringComparer.Ordinal);
    // The live transient cue emitters (bounded by TransientCueCapacity; aged by the publish clock).
    private readonly List<TransientCue> m_transients = new(capacity: TransientCueCapacity);
    private int m_nextEmitterId = 1;
    private FixedQ4816 m_defaultCueRadius = FixedQ4816.FromInteger(value: 8L);
    private FixedComplex m_lastListenerYaw = FixedComplex.MultiplicativeIdentity;

    /// <summary>The live master gain in Q16 — the value an attached mixer's <c>MasterGainQ16</c> follows: the
    /// document master gain until the <c>world.volume</c> session lever engages, the lever thereafter (see
    /// <see cref="SetMasterVolume"/>).</summary>
    public int MasterGainQ16 { get; private set; } = 65536;

    /// <summary>Initializes the director over the client view and the animated-placement pool. Both are nullable so
    /// the offline driver (the audio-mix proof) runs the same derivation headlessly: without a client, entity-anchored
    /// emitters resolve absent (honest silence); without an animator, placements resolve through the static stamp math.</summary>
    /// <param name="client">The snapshot-fed entity view, or <see langword="null"/> headless.</param>
    /// <param name="animator">The animated-placement replay pool, or <see langword="null"/> headless.</param>
    public WorldAudioDirector(WorldClient? client, WorldStampPool? animator) {
        m_client = client;
        m_animator = animator;
        m_slabs = new AudioSnapshot[SnapshotRotation];

        for (var index = 0; (index < SnapshotRotation); index++) {
            m_slabs[index] = new AudioSnapshot();
        }
    }

    /// <summary>The live master volume — the session lever when engaged, else the document master gain. The
    /// <c>world.save</c> fold and the session-drift hint read this.</summary>
    public float EffectiveMasterVolume {
        get {
            lock (m_gate) {
                return (m_sessionMasterVolume ?? (m_definition?.Audio.MasterGain ?? 1f));
            }
        }
    }
    /// <summary>The derived emitter count (the plan's row count, before any capacity refusal).</summary>
    public int EmitterCount => m_plan.Count;
    /// <summary>The live transient-cue count (the <c>speaker.state</c> echo's cue meter).</summary>
    public int LiveCueCount {
        get {
            lock (m_gate) {
                return m_transients.Count;
            }
        }
    }
    /// <summary>The machine-source resolver: screen slot → the live <see cref="IAudioMachine"/>, or
    /// <see langword="null"/> for an empty (or capability-less) slot. Wired once by the frame source to
    /// <see cref="WorldScreenBinder.AudioMachine"/>; invoked only from <see cref="Publish"/> (the pump thread) —
    /// it reads pump-owned binder state. Null headless: machine-fed emitters then render honest silence.</summary>
    public Func<int, IAudioMachine?>? MachineSourceResolver { get; set; }
    /// <summary>Whether the session lever has been engaged (the drift hint's cheap discriminator).</summary>
    public bool MasterVolumeLeverEngaged {
        get {
            lock (m_gate) {
                return m_sessionMasterVolume.HasValue;
            }
        }
    }
    /// <summary>Whether a mixer is currently attached (the device pump's live/silent echo).</summary>
    public bool MixerAttached => (m_mixer is not null);

    // Admit one plan row: resolve its stable id against the registry (keep on identical signature; retire + reissue
    // on an identity change — the fresh id re-enters the mixer from silence) and fire the arrival trigger for
    // synth-fed rows (the v1 trigger policy in the type remarks).
    private void Admit(EmitterPlan plan, string signatureToken) {
        var signature = Fnv64(text: signatureToken);
        var arrived = true;

        if (m_registry.TryGetValue(
            key: plan.Key,
            value: out var existing
        )) {
            if (existing.Signature == signature) {
                plan.Id = existing.Id;
                arrived = false;
            } else {
                plan.Id = m_nextEmitterId++;
                m_registry[plan.Key] = new EmitterIdentity(
                    Id: plan.Id,
                    Signature: signature
                );
            }
        } else {
            plan.Id = m_nextEmitterId++;
            m_registry[plan.Key] = new EmitterIdentity(
                Id: plan.Id,
                Signature: signature
            );
        }

        if (
            arrived &&
            (plan.Source.Kind == AudioSourceKind.Synth) &&
            (plan.Source.Id is { } patchId)
        ) {
            // The seed folds the key and the identity signature: the same authored content reproduces the voice bit
            // for bit; a content change re-seeds with the new identity. Gain stays unity — the emitter's own gain
            // spatializes; a voice gain here would double-scale.
            SubmitTrigger(
                patchId: patchId,
                seed: Fnv64(text: plan.Key) ^ signature,
                gainQ16: 65536,
                emitterId: plan.Id
            );
        }

        m_plan.Add(item: plan);
    }
    private void AdmitEmission(string key, EmitterAnchor anchor, WorldEmission emission, WorldAudioDefaults audio, WorldDefinition definition) {
        Admit(
            plan: new EmitterPlan {
                Key = key,
                Kind = AudioEmitterKind.Point,
                Anchor = anchor,
                MinRadius = FixedQ4816.Zero,
                MaxRadius = FixedQ4816.FromDouble(value: (emission.Radius ?? audio.DefaultSpeakerRadius)),
                Curve = CurveOf(token: audio.DefaultCurve),
                FadeFrames = 0,
                GainQ16 = GainQ16(gain: emission.Level),
                Channel = AudioChannel.Mix,
                Source = AudioSourceKey.Synth(patchId: emission.PatchId),
            },
            signatureToken: $"emission|{PatchHash(
                definition: definition,
                patchId: emission.PatchId
            )}"
        );
    }
    private static string AnchorKindToken(WorldAnchor anchor) => anchor switch {
        WorldAnchor.Entity => "entity",
        WorldAnchor.EntityPart => "entityPart",
        _ => "placement",
    };
    // ---- small shared derivations ----------------------------------------------------------------------------------

    private EmitterAnchor AnchorOf(WorldAnchor anchor, Vector3 offset) => anchor switch {
        WorldAnchor.Entity entity => EmitterAnchor.EntityRoot(
        index: entity.Index,
        offset: offset
    ),
        WorldAnchor.EntityPart part => EmitterAnchor.EntityPart(
        index: part.Index,
        partId: part.PartId,
        offset: offset
    ),
        WorldAnchor.Placement placement => EmitterAnchor.PlacementPoint(
        placementId: placement.PlacementId,
        shapeId: placement.ShapeId,
        staticPosition: StaticPlacementPosition(
            placementId: placement.PlacementId,
            shapeId: placement.ShapeId
        ),
        offset: offset
    ),
        _ => EmitterAnchor.FixedPoint(position: offset),
    };
    // ---- source hosting --------------------------------------------------------------------------------------------

    // Apply the derived bindings to the attached mixer: master gain, the patch set, and tune acquire/release with
    // the tune HASH as the restart discriminator. No mixer attached = derivation only.
    private void ApplyMixerBindings() {
        if (
            (m_mixer is not { } mixer) ||
            (m_definition is not { } definition)
        ) {
            return;
        }

        mixer.MasterGainQ16 = MasterGainQ16;

        // Retire patch slots whose id left the derived plan BEFORE re-registering the live set,
        // so the bounded table is not filled by the carcasses of churned sound emitters across reconciles.
        var livePatchIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var (id, _) in m_patchSet) {
            _ = livePatchIds.Add(item: id);
        }

        mixer.RetirePatches(live: livePatchIds);

        foreach (var (id, patch) in m_patchSet) {
            mixer.RegisterPatch(
                id: id,
                patch: in patch
            );
        }

        // The referenced-tune set: acquire a headless host per tune some plan row taps; release orphans; a hash
        // change restarts the host honestly, the same release+recreate approach the placement animator uses.
        List<string>? orphaned = null;

        foreach (var (tuneId, host) in m_tuneHosts) {
            if (FindReferencedTune(
                definition: definition,
                tuneId: tuneId
            ) is not { } tune) {
                (orphaned ??= new List<string>()).Add(item: tuneId);
            } else if (!string.Equals(
                a: tune.Hash,
                b: host.Hash,
                comparisonType: StringComparison.Ordinal
            )) {
                host.Source.Dispose();
                m_tuneHosts[tuneId] = CreateTuneHost(
                    mixer: mixer,
                    tune: tune
                );
            }
        }

        foreach (var tuneId in (orphaned ?? [])) {
            mixer.RemoveSource(key: AudioSourceKey.Tune(id: tuneId));
            m_tuneHosts[tuneId].Source.Dispose();
            _ = m_tuneHosts.Remove(key: tuneId);
        }

        foreach (var plan in m_plan) {
            if (
                (plan.Source.Kind == AudioSourceKind.Tune) &&
                (plan.Source.Id is { } tuneId) &&
                !m_tuneHosts.ContainsKey(key: tuneId) &&
                (FindTune(
                definition: definition,
                tuneId: tuneId
            ) is { } tune)
            ) {
                m_tuneHosts[tuneId] = CreateTuneHost(
                    mixer: mixer,
                    tune: tune
                );
            }
        }
    }
    // Rebuild the event → cue-row table from the delivered Audio section (called under the gate from reconcile).
    private void BuildCueTable(WorldAudioDefaults audio) {
        m_cueRows.Clear();

        foreach (var cue in audio.Cues) {
            CuePlacement placement;
            string? speakerName = null;

            if (string.Equals(
                a: cue.Placement,
                b: WorldAudioCue.PlacementListener,
                comparisonType: StringComparison.Ordinal
            )) {
                placement = CuePlacement.Listener;
            } else if (cue.Placement.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldAudioCue.PlacementEmitterPrefix
            )) {
                placement = CuePlacement.Emitter;
                speakerName = cue.Placement[WorldAudioCue.PlacementEmitterPrefix.Length..];
            } else {
                placement = CuePlacement.AtSite;
            }

            if (!m_cueRows.TryGetValue(
                key: cue.Event,
                value: out var rows
            )) {
                m_cueRows[cue.Event] = rows = new List<CueRow>();
            }

            rows.Add(item: new CueRow(
                PatchId: cue.PatchId,
                GainQ16: ((int)((((long)(cue.GainThousandths ?? 1000)) * 65536L) / 1000L)),
                Placement: placement,
                SpeakerName: speakerName
            ));
        }
    }
    private static string ChannelToken(AudioChannel channel) => channel switch {
        AudioChannel.Left => "left",
        AudioChannel.Right => "right",
        _ => "mix",
    };
    // The distinct external (machine/tune) source identities the derived plan taps — the mixer binds one source slot
    // per identity, so this is the plan's real demand on the bounded source table. Synth-fed rows register a patch,
    // not a source, so they do not count here.
    private int CountDistinctExternalSources() {
        var seen = new HashSet<AudioSourceKey>();

        foreach (var plan in m_plan) {
            if (plan.Source.Kind is AudioSourceKind.Machine or AudioSourceKind.Tune) {
                _ = seen.Add(item: plan.Source);
            }
        }

        return seen.Count;
    }
    private static TuneHost CreateTuneHost(WorldTune tune, AudioMixer mixer) {
        var source = new TuneMachineSource(document: tune.Document);

        mixer.SetSource(
            key: AudioSourceKey.Tune(id: tune.Id),
            source: source
        );

        return new TuneHost(
            TuneId: tune.Id,
            Hash: tune.Hash,
            Source: source
        );
    }
    // A cue's life derives from its own patch envelope (data): a finite patch lives its duration + release plus one
    // sim step of slack; a looping patch takes the invariant cap (a cue is a transient by definition). A patch the
    // table no longer carries gets one step (its trigger would drop in the mixer anyway).
    private long CueLifeFrames(string patchId) {
        foreach (var (id, patch) in m_patchSet) {
            if (string.Equals(
                a: id,
                b: patchId,
                comparisonType: StringComparison.Ordinal
            )) {
                return ((patch.DurationFrames > 0)
                    ? ((((long)patch.DurationFrames) + patch.ReleaseFrames) + AudioMixer.FramesPerSimStep)
                    : LoopingCueLifeFrames
                );
            }
        }

        return AudioMixer.FramesPerSimStep;
    }
    private static AudioAttenuationCurve CurveOf(string token) =>
        (string.Equals(
            a: token,
            b: WorldAudioDefaults.CurveLinear,
            comparisonType: StringComparison.Ordinal
        )
            ? AudioAttenuationCurve.Linear
            : AudioAttenuationCurve.Smoothstep
        );
    private static string CurveToken(AudioAttenuationCurve curve) =>
        ((curve == AudioAttenuationCurve.Linear)
            ? WorldAudioDefaults.CurveLinear
            : WorldAudioDefaults.CurveSmoothstep
        );
    private void DeriveCreationSounds(WorldDefinition definition, WorldAudioDefaults audio) {
        foreach (var placement in definition.Placements) {
            if (
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.CreationId
            ) is not { } creation) ||
                (creation.Document.Behavior?.Sounds is not { Count: > 0 } sounds)
            ) {
                continue;
            }

            foreach (var sound in sounds) {
                // The inline patch registers under the emitter key itself — a per-emitter voice identity that can
                // never collide with a world patch row's id (the "sound:" prefix is not a legal row reference).
                var key = $"sound:{placement.Id}:{sound.Name}";

                m_patchSet.Add(item: (Id: key, Patch: WorldVoicePatchFactory.FromDocument(document: sound.Patch)));
                Admit(
                    plan: new EmitterPlan {
                        Key = key,
                        Kind = AudioEmitterKind.Point,
                        Anchor = EmitterAnchor.PlacementPoint(
                        placementId: placement.Id,
                        shapeId: sound.ShapeId,
                        staticPosition: WorldAnchorGeometry.StaticShapePosition(
                            placement: placement,
                            creation: creation,
                            shapeId: sound.ShapeId
                        )
                    ),
                        MinRadius = FixedQ4816.Zero,
                        MaxRadius = FixedQ4816.FromDouble(value: (sound.Radius ?? audio.DefaultSpeakerRadius)),
                        Curve = CurveOf(token: audio.DefaultCurve),
                        FadeFrames = 0,
                        GainQ16 = GainQ16(gain: (sound.Level ?? 1f)),
                        Channel = AudioChannel.Mix,
                        Source = AudioSourceKey.Synth(patchId: key),
                    },
                    signatureToken: $"sound|{creation.Hash}"
                );
            }
        }
    }
    private void DeriveEmissionFacets(WorldDefinition definition, WorldAudioDefaults audio) {
        foreach (var placement in definition.Placements) {
            if (placement.Emission is { } emission) {
                // Root-only under Pattern (documented on WorldPlacement): the emission binds the placement root.
                // isAttached tells TryResolvePosition to go SILENT rather than fall back to the row's inert static
                // Position when the attach target is not live this frame.
                AdmitEmission(
                    key: $"placement:{placement.Id}",
                    anchor: EmitterAnchor.PlacementPoint(
                        placementId: placement.Id,
                        shapeId: null,
                        staticPosition: placement.Position,
                        isAttached: (placement.Attach is not null)
                    ),
                    emission: emission,
                    audio: audio,
                    definition: definition
                );
            }
        }
    }
    // ---- derivation ------------------------------------------------------------------------------------------------

    private void DeriveSpeakers(WorldDefinition definition, WorldAudioDefaults audio) {
        foreach (var speaker in definition.Speakers) {
            var key = $"speaker:{speaker.Name}";
            var source = SourceKey(source: speaker.Feed.Source);
            var gain = GainQ16(gain: speaker.Feed.Gain);
            var channel = (speaker.Feed.Channel switch {
                WorldSpeakerFeed.ChannelLeft => AudioChannel.Left,
                WorldSpeakerFeed.ChannelRight => AudioChannel.Right,
                _ => AudioChannel.Mix,
            });

            switch (speaker) {
                case WorldSpeaker.Bed bed:
                    Admit(
                        plan: new EmitterPlan {
                            Key = key,
                            Kind = AudioEmitterKind.Bed,
                            Anchor = EmitterAnchor.FixedPoint(position: bed.Center),
                            MinRadius = FixedQ4816.FromDouble(value: bed.InnerRadius),
                            MaxRadius = FixedQ4816.FromDouble(value: bed.Radius),
                            FadeFrames = FadeFrames(seconds: (bed.FadeSeconds ?? audio.DefaultBedFadeSeconds)),
                            GainQ16 = gain,
                            Channel = channel,
                            Source = source,
                        },
                        signatureToken: $"bed|{SourceSignature(
                            source: speaker.Feed.Source,
                            definition: definition
                        )}"
                    );

                    break;
                case WorldSpeaker.Fixed fixedSpeaker:
                    Admit(
                        plan: PointPlan(
                            key: key,
                            anchor: EmitterAnchor.FixedPoint(position: fixedSpeaker.Position),
                            attenuation: speaker.Attenuation,
                            audio: audio,
                            gain: gain,
                            channel: channel,
                            source: source
                        ),
                        signatureToken: $"fixed|{SourceSignature(
                            source: speaker.Feed.Source,
                            definition: definition
                        )}"
                    );

                    break;
                case WorldSpeaker.Anchored anchored:
                    Admit(
                        plan: PointPlan(
                            key: key,
                            anchor: AnchorOf(
                                anchor: anchored.Anchor,
                                offset: anchored.Offset
                            ),
                            attenuation: speaker.Attenuation,
                            audio: audio,
                            gain: gain,
                            channel: channel,
                            source: source
                        ),
                        signatureToken: $"anchored|{AnchorKindToken(anchor: anchored.Anchor)}|{SourceSignature(
                            source: speaker.Feed.Source,
                            definition: definition
                        )}"
                    );

                    break;
            }
        }
    }
    private void EvictNearestExpiry() {
        var victim = 0;

        for (var index = 1; (index < m_transients.Count); index++) {
            if (m_transients[index].RemainingFrames < m_transients[victim].RemainingFrames) {
                victim = index;
            }
        }

        m_transients.RemoveAt(index: victim);
    }
    private static int FadeFrames(float seconds) => ((int)MathF.Round(x: (seconds * AudioMixer.SampleRate)));
    private WorldTune? FindReferencedTune(WorldDefinition definition, string tuneId) {
        foreach (var plan in m_plan) {
            if (
                (plan.Source.Kind == AudioSourceKind.Tune) &&
                string.Equals(
                a: plan.Source.Id,
                b: tuneId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return FindTune(
                    definition: definition,
                    tuneId: tuneId
                );
            }
        }

        return null;
    }
    private static WorldTune? FindTune(WorldDefinition definition, string tuneId) {
        foreach (var tune in definition.Tunes) {
            if (string.Equals(
                a: tune.Id,
                b: tuneId,
                comparisonType: StringComparison.Ordinal
            )) {
                return tune;
            }
        }

        return null;
    }
    private static ulong Fnv64(string text) {
        var hash = Fnv1aHash.Create();

        foreach (var character in text) {
            hash.Add(value: ((uint)character));
        }

        return hash.Value;
    }
    private static int GainQ16(float gain) => ((int)FixedQ4816.FromDouble(value: gain).Value);
    private bool HasPatch(string patchId) {
        foreach (var (id, _) in m_patchSet) {
            if (string.Equals(
                a: id,
                b: patchId,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }
        }

        return false;
    }
    private static string PatchHash(WorldDefinition definition, string patchId) {
        foreach (var patch in definition.Patches) {
            if (string.Equals(
                a: patch.Id,
                b: patchId,
                comparisonType: StringComparison.Ordinal
            )) {
                return patch.Hash;
            }
        }

        return string.Empty;
    }
    private EmitterPlan PointPlan(string key, EmitterAnchor anchor, WorldSpeakerAttenuation? attenuation, WorldAudioDefaults audio, int gain, AudioChannel channel, AudioSourceKey source) => new() {
        Key = key,
        Kind = AudioEmitterKind.Point,
        Anchor = anchor,
        // Points shoulder from their center (min 0); the attenuation radius (row or audio-defaults) is the finite
        // support edge — a full-gain inner band is a bed concept.
        MinRadius = FixedQ4816.Zero,
        MaxRadius = FixedQ4816.FromDouble(value: (attenuation?.Radius ?? audio.DefaultSpeakerRadius)),
        Curve = CurveOf(token: (attenuation?.Curve ?? audio.DefaultCurve)),
        FadeFrames = 0,
        GainQ16 = gain,
        Channel = channel,
        Source = source,
    };
    // Emit the live transient cue emitters into the slab (FIRST — the reserved pool always lands) and age them by
    // this publish's clock advance. Placement resolution per kind: at-site holds the event position; listener rides
    // the slab's already-resolved listener (distance 0 = full gain, and the mixer's on-top-of-listener pan hold
    // centers it); emitter follows the named speaker's live plan pose and support radius (falling back to the
    // listener while the speaker is absent).
    private void PublishTransients(AudioSnapshot slab, ReadOnlySpan<DynamicTransform> transforms, float deltaSeconds) {
        if (m_transients.Count == 0) {
            return;
        }

        var elapsedFrames = ((long)MathF.Round(x: (MathF.Max(
            x: deltaSeconds,
            y: 0f
        ) * AudioMixer.SampleRate)));

        for (var index = (m_transients.Count - 1); (index >= 0); index--) {
            var transient = m_transients[index];
            var position = slab.Listener.Position;
            var minRadius = FixedQ4816.Zero;
            var maxRadius = m_defaultCueRadius;
            var curve = CurveOf(token: (m_definition?.Audio.DefaultCurve ?? WorldAudioDefaults.CurveSmoothstep));

            switch (transient.Placement) {
                case CuePlacement.AtSite:
                    position = FixedVector3.FromVector3(value: transient.Site);

                    break;
                case CuePlacement.Emitter:
                    if (
                        TryFindSpeakerPlan(
                        name: transient.SpeakerName,
                        plan: out var speakerPlan
                    ) &&
                        TryResolvePosition(
                        plan: in speakerPlan,
                        position: out var resolved,
                        transforms: transforms
                    )
                    ) {
                        position = FixedVector3.FromVector3(value: resolved);
                        minRadius = speakerPlan.MinRadius;
                        maxRadius = speakerPlan.MaxRadius;
                        curve = speakerPlan.Curve;
                    }

                    break;
                case CuePlacement.Listener:
                default:
                    break;
            }

            _ = slab.TryAddEmitter(emitter: new AudioEmitter(
                Id: transient.Id,
                Kind: AudioEmitterKind.Point,
                Position: position,
                MinRadius: minRadius,
                MaxRadius: maxRadius,
                Curve: curve,
                FadeFrames: 0,
                GainQ16: transient.GainQ16,
                Channel: AudioChannel.Mix,
                Source: AudioSourceKey.Synth(patchId: transient.PatchId)
            ));

            transient.RemainingFrames -= elapsedFrames;

            if (transient.RemainingFrames <= 0) {
                m_transients.RemoveAt(index: index);
            } else {
                m_transients[index] = transient;
            }
        }
    }
    private (Vector3 Eye, Vector3 Forward)? ResolveCameraListener(string name, ReadOnlySpan<DynamicTransform> transforms) {
        if (m_definition is not { } definition) {
            return null;
        }

        foreach (var camera in definition.Cameras) {
            if (!string.Equals(
                a: camera.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            // Only a program that stays on its own reference pose has a listener pose this derivation can resolve: an
            // anchor op re-seats the eye on a subject only the render path resolves.
            if (camera.Rig.AnchorOp is not null) {
                return null;
            }

            // An unanchored world-axis offset aimed at a world point has a directly resolvable listener pose.
            if (camera.Anchor is null) {
                return (((camera.Rig.OffsetOp is { WorldAxes: true } fixedOffset) && (camera.Rig.LookAtOp is { Subject: WorldCameraSubject.WorldPoint aim }))
                    ? (Eye: fixedOffset.Value.Value, Forward: (aim.Point.Value - fixedOffset.Value.Value))
                    : ((Vector3 Eye, Vector3 Forward)?)null
                );
            }

            if (camera.Rig.OffsetOp is not { WorldAxes: false } follow) {
                return null;
            }

            var plan = new EmitterPlan {
                Anchor = AnchorOf(
                anchor: camera.Anchor,
                offset: follow.Value
            ),
            };

            if (
                TryResolvePosition(
                plan: in plan,
                position: out var eye,
                transforms: transforms
            ) &&
                (m_client is { } client) &&
                (camera.Anchor is WorldAnchor.Entity or WorldAnchor.EntityPart)
            ) {
                var orientation = ((camera.Anchor is WorldAnchor.Entity entity)
                    ? client.Orientation(index: entity.Index)
                    : (((m_animator is { } animator) && WorldEntityPartResolver.TryPackedPose(
                        client: client,
                        stamps: animator,
                        entityIndex: ((WorldAnchor.EntityPart)camera.Anchor).Index,
                        partId: ((WorldAnchor.EntityPart)camera.Anchor).PartId,
                        transforms: transforms,
                        pose: out var partPose
                    ))
                        ? partPose.Orientation
                        : Quaternion.Identity
                ));

                return (Eye: eye, Forward: Vector3.Transform(
                    value: new Vector3(
                        x: 0f,
                        y: 0f,
                        z: -1f
                    ),
                    rotation: orientation
                ));
            }

            return null;
        }

        return null;
    }
    private AudioListener ResolveListener(ReadOnlySpan<WorldSeatCameraPose> seats, ReadOnlySpan<DynamicTransform> transforms) {
        var (eye, forward) = ResolveListenerPose(
            seats: seats,
            transforms: transforms
        );
        // The yaw rotor maps listener-local (X = right, Y = forward) into world (X, Z); building it from the planar
        // RIGHT vector r = (−fz, fx) makes an emitter on the listener's geometric right pan right (front/back fold
        // to center in the mixer's pan law, so only the right axis carries meaning).
        var planar = new Vector2(
            x: forward.X,
            y: forward.Z
        );

        if (planar.LengthSquared() > 1e-6f) {
            planar = Vector2.Normalize(value: planar);
            m_lastListenerYaw = new FixedComplex(
                Real: FixedQ4816.FromDouble(value: -planar.Y),
                Imaginary: FixedQ4816.FromDouble(value: planar.X)
            ).Normalize();
        }

        return new AudioListener(
            Position: FixedVector3.FromVector3(value: eye),
            Yaw: m_lastListenerYaw
        );
    }
    // The listener policy: focus = the first joined seat's resolved view camera (the editor rig when that
    // seat edits — the frame source resolves the SAME rig the seat renders through); seat:<n> pins that seat (falling
    // back to focus while it is unjoined); a camera name resolves the declared camera row. No candidate at all
    // (headless, no seats) listens from the origin facing -Z.
    //
    private (Vector3 Eye, Vector3 Forward) ResolveListenerPose(ReadOnlySpan<WorldSeatCameraPose> seats, ReadOnlySpan<DynamicTransform> transforms) {
        var listener = (m_definition?.Audio.Listener ?? WorldAudioDefaults.ListenerFocus);

        if (
            listener.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldAudioDefaults.ListenerSeatPrefix
        ) &&
            int.TryParse(
            s: listener.AsSpan(start: WorldAudioDefaults.ListenerSeatPrefix.Length),
            result: out var seat
        ) &&
            ((seat - 1) is var slot) &&
            (((uint)slot) < ((uint)seats.Length)) &&
            seats[slot].Joined
        ) {
            return (Eye: seats[slot].Eye, Forward: seats[slot].Forward);
        }

        if (
            !string.Equals(
            a: listener,
            b: WorldAudioDefaults.ListenerFocus,
            comparisonType: StringComparison.Ordinal
        ) &&
            !listener.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldAudioDefaults.ListenerSeatPrefix
        ) &&
            (ResolveCameraListener(
            name: listener,
            transforms: transforms
        ) is { } pinned)
        ) {
            return pinned;
        }

        for (var candidateSlot = 0; (candidateSlot < seats.Length); candidateSlot++) {
            if (seats[candidateSlot].Joined) {
                return (Eye: seats[candidateSlot].Eye, Forward: seats[candidateSlot].Forward);
            }
        }

        return (Eye: Vector3.Zero, Forward: new Vector3(
            x: 0f,
            y: 0f,
            z: -1f
        ));
    }
    // Drop registry rows whose key left the derived plan, so a re-authored row later re-enters from silence with a
    // fresh id rather than inheriting a stale ramp.
    private void RetireDepartedKeys() {
        List<string>? departed = null;

        foreach (var key in m_registry.Keys) {
            var present = false;

            foreach (var plan in m_plan) {
                if (string.Equals(
                    a: plan.Key,
                    b: key,
                    comparisonType: StringComparison.Ordinal
                )) {
                    present = true;

                    break;
                }
            }

            if (!present) {
                (departed ??= new List<string>()).Add(item: key);
            }
        }

        foreach (var key in (departed ?? [])) {
            _ = m_registry.Remove(key: key);
        }
    }
    private static AudioSourceKey SourceKey(WorldSpeakerSource source) => source switch {
        WorldSpeakerSource.Machine machine => AudioSourceKey.Machine(slot: machine.ScreenIndex),
        WorldSpeakerSource.Tune tune => AudioSourceKey.Tune(id: tune.TuneId),
        WorldSpeakerSource.Synth synth => AudioSourceKey.Synth(patchId: synth.PatchId),
        _ => AudioSourceKey.None,
    };
    // The source half of an emitter's identity signature: the source shape plus the referenced asset's content HASH
    // (the restart discriminator — a tune/patch content change re-keys the emitter; a gain edit does not).
    private static string SourceSignature(WorldSpeakerSource source, WorldDefinition definition) => source switch {
        WorldSpeakerSource.Machine machine => $"machine:{machine.ScreenIndex}",
        WorldSpeakerSource.Tune tune => $"tune:{tune.TuneId}:{FindTune(
        definition: definition,
        tuneId: tune.TuneId
    )?.Hash}",
        WorldSpeakerSource.Synth synth => $"synth:{synth.PatchId}:{PatchHash(
        definition: definition,
        patchId: synth.PatchId
    )}",
        _ => "none",
    };
    // One speaker row's live binding status: what its source identity resolves to RIGHT NOW (under the gate).
    private string SourceStatus(in AudioSourceKey source) => source.Kind switch {
        AudioSourceKind.Machine => (m_machineBindings.ContainsKey(key: source.Slot)
        ? "bound"
        : "silent(no-machine)"),
        AudioSourceKind.Tune => (((source.Id is { } tuneId) && m_tuneHosts.ContainsKey(key: tuneId))
        ? "bound"
        : ((m_mixer is null)
            ? "silent(no-device)"
            : "silent(no-tune)")),
        AudioSourceKind.Synth => (((source.Id is { } patchId) && HasPatch(patchId: patchId))
        ? "bound"
        : "faulted(no-patch)"),
        _ => "silent(no-source)",
    };
    private static string SourceToken(in AudioSourceKey source) => source.Kind switch {
        AudioSourceKind.Machine => $"machine:{source.Slot}",
        AudioSourceKind.Tune => $"tune:{source.Id}",
        AudioSourceKind.Synth => $"synth:{source.Id}",
        _ => "none",
    };
    // A static placement anchor's stamped position — the ONE shared resolver cameras and speakers both read.
    private Vector3 StaticPlacementPosition(string placementId, int? shapeId) =>
        ((m_definition is { } definition)
            ? WorldAnchorGeometry.StaticPlacementPosition(
                definition: definition,
                placementId: placementId,
                shapeId: shapeId
            )
            : Vector3.Zero
        );
    // Diff the binder's live machines against the cached bindings for every machine-fed plan row — the per-frame
    // reconcile/self-heal (called under the gate from Publish, the only resolver call site). Reference compares only
    // in steady state; a change rebinds the STABLE Machine(slot) key so the mixer's emitter ramps never notice a
    // swap. An attach marks the set dirty and this re-applies every cached binding into the new mixer.
    private void SyncMachineSources() {
        if (
            (MachineSourceResolver is not { } resolver) ||
            (m_mixer is not { } mixer)
        ) {
            return;
        }

        foreach (var plan in m_plan) {
            if (plan.Source.Kind != AudioSourceKind.Machine) {
                continue;
            }

            var slot = plan.Source.Slot;
            var live = resolver(arg: slot);
            var bound = m_machineBindings.TryGetValue(
                key: slot,
                value: out var binding
            );

            if (live is null) {
                if (bound) {
                    mixer.RemoveSource(key: AudioSourceKey.Machine(slot: slot));
                    _ = m_machineBindings.Remove(key: slot);
                }
            } else if (
                !bound ||
                !ReferenceEquals(
                objA: binding.Machine,
                objB: live
            )
            ) {
                var source = new MachineBlockSource(machine: live);

                m_machineBindings[slot] = new MachineBinding(
                    Machine: live,
                    Source: source
                );
                mixer.SetSource(
                    key: AudioSourceKey.Machine(slot: slot),
                    source: source
                );
            } else if (m_machineBindingsDirty) {
                mixer.SetSource(
                    key: AudioSourceKey.Machine(slot: slot),
                    source: binding.Source
                );
            }
        }

        m_machineBindingsDirty = false;

        // Retire bindings whose slot no longer feeds any plan row (an eject, or the speaker rows departed).
        foreach (var slot in m_machineBindings.Keys) {
            var referenced = false;

            foreach (var plan in m_plan) {
                if (
                    (plan.Source.Kind == AudioSourceKind.Machine) &&
                    (plan.Source.Slot == slot)
                ) {
                    referenced = true;

                    break;
                }
            }

            if (!referenced) {
                m_machineBindingScratch.Add(item: slot);
            }
        }

        foreach (var slot in m_machineBindingScratch) {
            mixer.RemoveSource(key: AudioSourceKey.Machine(slot: slot));
            _ = m_machineBindings.Remove(key: slot);
        }

        m_machineBindingScratch.Clear();
    }
    private bool TryFindSpeakerPlan(string? name, out EmitterPlan plan) {
        if (name is not null) {
            var key = $"speaker:{name}";

            foreach (var candidate in m_plan) {
                if (string.Equals(
                    a: candidate.Key,
                    b: key,
                    comparisonType: StringComparison.Ordinal
                )) {
                    plan = candidate;

                    return true;
                }
            }
        }

        plan = default;

        return false;
    }
    // ---- pose resolution -------------------------------------------------------------------------------------------

    private bool TryResolvePosition(in EmitterPlan plan, ReadOnlySpan<DynamicTransform> transforms, out Vector3 position) {
        var anchor = plan.Anchor;

        switch (anchor.Kind) {
            case EmitterAnchorKind.Fixed:
                position = anchor.Position;

                return true;
            case EmitterAnchorKind.Entity: {
                    if (
                        (m_client is not { } client) ||
                        !client.IsActive(index: anchor.EntityIndex)
                    ) {
                        position = default;

                        return false;
                    }

                    position = (client.Position(index: anchor.EntityIndex) + Vector3.Transform(
                        value: anchor.Offset,
                        rotation: client.Orientation(index: anchor.EntityIndex)
                    ));

                    return true;
                }
            case EmitterAnchorKind.EntityPart: {
                    if (
                        (m_client is not { } client) ||
                        (m_animator is not { } animator) ||
                        !WorldEntityPartResolver.TryPackedPose(
                        client: client,
                        stamps: animator,
                        entityIndex: anchor.EntityIndex,
                        partId: anchor.PartId!,
                        transforms: transforms,
                        pose: out var partPose
                    )
                    ) {
                        position = default;

                        return false;
                    }

                    position = (partPose.Position + Vector3.Transform(
                        value: anchor.Offset,
                        rotation: partPose.Orientation
                    ));

                    return true;
                }
            case EmitterAnchorKind.Placement:
            default: {
                    // Animated placements ride the stamp pool's current frame; an INHABITED placement rides its live body
                    // pose (both through TryShapePosition); a static placement uses the reconcile-time stamp math.
                    if (
                        (m_animator is { } animator) &&
                        (m_client is { } client) &&
                        (anchor.PlacementId is { } placementId) &&
                        animator.TryShapePosition(
                        placementId: placementId,
                        shapeId: anchor.ShapeId,
                        client: client,
                        out var animated
                    )
                    ) {
                        position = (animated + anchor.Offset);

                        return true;
                    }

                    // An ATTACHED row's pool lookup just failed — its target body is not live THIS frame (the SAME
                    // verdict WorldStampPool.PackTransforms already renders as a hidden stamp), so the row is absent
                    // rather than falling back to its INERT static Position (which neither the resolve nor the renderer
                    // reads for an attached row — see WorldPlacement's own doc).
                    if (anchor.IsAttached) {
                        position = default;

                        return false;
                    }

                    position = (anchor.Position + anchor.Offset);

                    return true;
                }
        }
    }

    /// <summary>Attaches a mixer: registers the current patch set, sets its master gain, and activates tune
    /// acquire/release hosting (sources bind now and follow every reconcile until detached). Machine sources apply
    /// on the next pump-thread publish (their resolver reads pump-owned binder state) — at most one frame of
    /// machine silence after an attach.</summary>
    /// <param name="mixer">The mixer to bind sources into.</param>
    public void AttachMixer(AudioMixer mixer) {
        ArgumentNullException.ThrowIfNull(argument: mixer);

        lock (m_gate) {
            m_mixer = mixer;
            m_machineBindingsDirty = true;
            ApplyMixerBindings();
        }
    }
    /// <summary>The deterministic <c>audio.emitters</c> listing: one segment per derived emitter — id, key, kind,
    /// source token, channel, gain, and radii — the document-derived stable facts (never live poses), so a piped
    /// proof asserts the derivation byte-for-byte.</summary>
    public string DescribeEmitters() {
        lock (m_gate) {
            if (m_plan.Count == 0) {
                return "[audio.emitters: none derived]";
            }

            var builder = new StringBuilder(value: "[audio.emitters:");

            for (var index = 0; (index < m_plan.Count); index++) {
                var plan = m_plan[index];

                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{((index == 0)
                    ? " "
                    : " | ")}{plan.Id} {plan.Key} {((plan.Kind == AudioEmitterKind.Bed)
                    ? "bed"
                    : "point")} {SourceToken(source: plan.Source)} {ChannelToken(channel: plan.Channel)} gain={(((double)plan.GainQ16) / 65536.0):0.###} min={((double)plan.MinRadius):0.###} max={((double)plan.MaxRadius):0.###} curve={CurveToken(curve: plan.Curve)}"
                );
            }

            return builder.Append(value: ']').ToString();
        }
    }
    /// <summary>The <c>speaker.state</c> echo — the live per-row status joining <c>audio.state</c>'s device facts:
    /// for every derived speaker row and every placement emission facet (the two point-position facts a live pose
    /// can drive; a placement's attach facet is what makes the latter move) its kind, source token, binding status
    /// (bound / silent-with-reason / faulted), the last published resolved position (or <c>unresolved</c> for an
    /// absent anchor — the verdict for an inactive attach carrier too, since an unresolvable anchor is an absent
    /// emitter), and whether the listener currently sits inside its finite support (<c>inMix</c>); then the live
    /// transient-cue tail (token + remaining life). Live facts move frame to frame — a proof asserts presence/shape,
    /// never exact poses.</summary>
    public string DescribeSpeakerState() {
        lock (m_gate) {
            var builder = new StringBuilder(value: "[speaker.state:");
            var wrote = false;

            foreach (var plan in m_plan) {
                var isSpeaker = plan.Key.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "speaker:"
                );
                var isEmission = plan.Key.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "placement:"
                );

                if (
                    !isSpeaker &&
                    !isEmission
                ) {
                    continue;
                }

                var name = (isSpeaker
                    ? plan.Key["speaker:".Length..]
                    : plan.Key
                );
                var position = (plan.LastResolved
                    ? string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"({plan.LastPosition.X:0.0},{plan.LastPosition.Y:0.0},{plan.LastPosition.Z:0.0})"
                    )
                    : "unresolved"
                );

                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{(wrote
                    ? " | "
                    : " ")}{name} {((plan.Kind == AudioEmitterKind.Bed)
                    ? "bed"
                    : "point")} {SourceToken(source: plan.Source)} {SourceStatus(source: plan.Source)} pos={position} inMix={((plan.LastResolved && plan.LastInSupport)
                    ? "y"
                    : "n")}"
                );
                wrote = true;
            }

            if (!wrote) {
                _ = builder.Append(value: " none declared");
            }

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" | cues {m_transients.Count}"
            );

            foreach (var transient in m_transients) {
                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $" cue:{transient.Token}={transient.PatchId}"
                );
            }

            return builder.Append(value: ']').ToString();
        }
    }
    /// <summary>Releases every hosted tune source, unbinds every machine source, and detaches the mixer.</summary>
    public void DetachMixer() {
        lock (m_gate) {
            foreach (var host in m_tuneHosts.Values) {
                m_mixer?.RemoveSource(key: AudioSourceKey.Tune(id: host.TuneId));
                host.Source.Dispose();
            }

            m_tuneHosts.Clear();

            foreach (var slot in m_machineBindings.Keys) {
                m_mixer?.RemoveSource(key: AudioSourceKey.Machine(slot: slot));
            }

            m_machineBindings.Clear();
            m_mixer = null;
        }
    }
    /// <summary>The at-site position a mutation's cue can derive, or <see langword="null"/>: upserts carry their
    /// row's authored pose in the mutation payload; removals and section-wide edits have no single site (their cues
    /// fall back to the listener placement — honest, documented).</summary>
    /// <param name="mutation">The mutation the edit echo answered, or <see langword="null"/>.</param>
    public static Vector3? MutationSite(WorldMutation? mutation) => mutation switch {
        WorldMutation.UpsertScreen upsert => upsert.Screen.Origin,
        WorldMutation.UpsertPlacement upsert => upsert.Placement.Position,
        WorldMutation.UpsertSpeaker { Speaker: WorldSpeaker.Fixed fixedSpeaker } => fixedSpeaker.Position,
        WorldMutation.UpsertSpeaker { Speaker: WorldSpeaker.Bed bed } => bed.Center,
        WorldMutation.UpsertCamera upsert when (upsert.Camera.Anchor is null) => WorldCameraRigCompiler.AuthoredPosition(program: upsert.Camera.Rig),
        _ => null,
    };
    /// <summary>Resolves this frame's listener and emitter poses and publishes one snapshot from the slab rotation.
    /// Returns the published snapshot (the offline driver mixes it directly).</summary>
    /// <param name="transforms">The frame's packed dynamic transforms (empty headless — entity-part anchors then resolve
    /// absent).</param>
    /// <param name="seats">The per-slot resolved view-camera poses (the listener policy's candidates).</param>
    /// <param name="deltaSeconds">The clock advance since the previous publish — ages the transient cue pool. The
    /// default is one sim step (the offline drivers publish once per mixed block); the live frame source passes its
    /// clamped presentation delta.</param>
    public AudioSnapshot Publish(ReadOnlySpan<DynamicTransform> transforms, ReadOnlySpan<WorldSeatCameraPose> seats, float deltaSeconds = DefaultPublishDeltaSeconds) {
        lock (m_gate) {
            SyncMachineSources();

            var slab = m_slabs[m_slabIndex];

            m_slabIndex = ((m_slabIndex + 1) % SnapshotRotation);
            slab.Reset(listener: ResolveListener(
                seats: seats,
                transforms: transforms
            ));
            // Transients FIRST — the reserved pool must land even when the derived plan overfills the table.
            PublishTransients(
                deltaSeconds: deltaSeconds,
                slab: slab,
                transforms: transforms
            );

            // The listener eye as float, for the per-row support check (a presentation ECHO fact, never mix math).
            var listenerEye = new Vector3(
                x: (slab.Listener.Position.X.Value / 65536f),
                y: (slab.Listener.Position.Y.Value / 65536f),
                z: (slab.Listener.Position.Z.Value / 65536f)
            );

            for (var index = 0; (index < m_plan.Count); index++) {
                var plan = m_plan[index];

                if (!TryResolvePosition(
                    plan: plan,
                    position: out var position,
                    transforms: transforms
                )) {
                    // An unresolvable anchor is an absent emitter — honest silence, zero special cases.
                    plan.LastResolved = false;
                    plan.LastInSupport = false;
                    m_plan[index] = plan;

                    continue;
                }

                plan.LastResolved = true;
                plan.LastPosition = position;

                var maxRadius = (plan.MaxRadius.Value / 65536f);

                plan.LastInSupport = (Vector3.DistanceSquared(
                    value1: position,
                    value2: listenerEye
                ) < (maxRadius * maxRadius));
                m_plan[index] = plan;
                _ = slab.TryAddEmitter(emitter: new AudioEmitter(
                    Id: plan.Id,
                    Kind: plan.Kind,
                    Position: FixedVector3.FromVector3(value: position),
                    MinRadius: plan.MinRadius,
                    MaxRadius: plan.MaxRadius,
                    Curve: plan.Curve,
                    FadeFrames: plan.FadeFrames,
                    GainQ16: plan.GainQ16,
                    Channel: plan.Channel,
                    Source: plan.Source
                ));
            }

            // Pending triggers ride ASCENDING-sequence order — the mixer's once-only high-water mark walks the snapshot
            // array in order, so a descending append would fire only the newest event and skip the rest.
            var write = 0;

            for (var index = 0; (index < m_pendingTriggers.Count); index++) {
                var pending = m_pendingTriggers[index];

                if (slab.TryAddTrigger(trigger: pending.Trigger)) {
                    pending.RemainingPublishes--;
                }

                // A capacity refusal keeps the event pending (untouched) for the next publish.
                if (pending.RemainingPublishes > 0) {
                    m_pendingTriggers[write++] = pending;
                }
            }

            m_pendingTriggers.RemoveRange(
                index: write,
                count: (m_pendingTriggers.Count - write)
            );

            m_buffer.Publish(frame: slab);

            return slab;
        }
    }
    /// <summary>Reconciles the derived emitter table against a delivered definition — call at the delivery boundary
    /// after <see cref="WorldScreenBinder.ReconcileScreens"/> (the chiasmus ordering: speakers consume screen slots)
    /// and after the animator's own reconcile (placement anchors read its registrations).</summary>
    /// <param name="definition">The delivered definition.</param>
    public void ReconcileSpeakers(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        lock (m_gate) {
            m_definition = definition;

            var audio = definition.Audio;

            // The lever precedence: the document master gain owns boot and every reconcile UNTIL world.volume
            // engages the session lever; from then on the lever owns "now" (world.save folds it back).
            MasterGainQ16 = GainQ16(gain: (m_sessionMasterVolume ?? audio.MasterGain));
            m_defaultCueRadius = FixedQ4816.FromDouble(value: audio.DefaultSpeakerRadius);
            m_plan.Clear();
            m_patchSet.Clear();
            BuildCueTable(audio: audio);

            foreach (var patch in definition.Patches) {
                m_patchSet.Add(item: (Id: patch.Id, Patch: WorldVoicePatchFactory.FromDocument(document: patch.Document)));
            }

            DeriveSpeakers(
                audio: audio,
                definition: definition
            );
            DeriveEmissionFacets(
                audio: audio,
                definition: definition
            );
            DeriveCreationSounds(
                audio: audio,
                definition: definition
            );
            RetireDepartedKeys();

            // The reserved transient pool is charged against the snapshot cap: the plan may only fill what cues never need.
            if (m_plan.Count > (AudioSnapshot.DefaultMaxEmitters - TransientCueCapacity)) {
                Console.Error.WriteLine(value: $"[world.audio: {m_plan.Count} derived emitters exceed the {(AudioSnapshot.DefaultMaxEmitters - TransientCueCapacity)}-row plan budget ({AudioSnapshot.DefaultMaxEmitters}-row snapshot table minus the {TransientCueCapacity} reserved cue transients) — the overflow renders silent]");
            }

            // Validate the WHOLE derived plan against the mixer's bounded registries at the compose
            // boundary — the patch set (per-emitter synth voices) and the distinct external-source identities the plan
            // taps — so an overfull registry is a loud, contained warn here rather than a silent drop the mixer only
            // discovers row-by-row at bind time.
            if (m_patchSet.Count > AudioMixer.MaxPatches) {
                Console.Error.WriteLine(value: $"[world.audio: {m_patchSet.Count} derived synth patches exceed the {AudioMixer.MaxPatches}-slot mixer patch table — the overflow renders silent]");
            }

            var distinctSources = CountDistinctExternalSources();

            if (distinctSources > AudioMixer.MaxSources) {
                Console.Error.WriteLine(value: $"[world.audio: {distinctSources} derived machine/tune sources exceed the {AudioMixer.MaxSources}-slot mixer source table — the overflow renders silent]");
            }

            ApplyMixerBindings();
        }
    }
    // ---- the master-volume session lever --------------------------------------------------------------------------

    /// <summary>Engages the <c>world.volume</c> session lever: the live mix gain applies now and owns every later
    /// reconcile; the document's <see cref="WorldAudioDefaults.MasterGain"/> keeps owning boot, and
    /// <c>world.save</c> folds the lever back into it (the render-levers asymmetry). Until first engaged, the
    /// document value flows live (so the offline document-driven proofs and <c>world.row.set audio</c>'s live master
    /// gain keep flowing from the document).</summary>
    /// <param name="value">The master volume (1 = unity), validated by the verb against the shared gain ceiling.</param>
    public void SetMasterVolume(float value) {
        lock (m_gate) {
            m_sessionMasterVolume = value;
            MasterGainQ16 = GainQ16(gain: value);

            if (m_mixer is { } mixer) {
                mixer.MasterGainQ16 = MasterGainQ16;
            }
        }
    }
    // ---- the cue engine ----------------------------------------------------------------------------------------------

    /// <summary>Fires a world-event cue — the producers' one entry (the edit-echo lane, the binder lifecycle, the
    /// gait derivation, the seat roster), gate-safe from any thread: every cue row bound to
    /// <paramref name="eventToken"/> allocates a short-lived transient point emitter (placed per the row) and one
    /// seeded trigger. The trigger and its transient land in the same next published snapshot, so the mixer's
    /// unbound-voice release can never race the voice's own emitter. An unknown or cue-less token is a no-op — cue
    /// coverage is world data, never engine policy.</summary>
    /// <param name="eventToken">The published event token (<see cref="WorldAudioCue.EventTokens"/>).</param>
    /// <param name="site">The event's world position, or <see langword="null"/> when none is derivable — an
    /// <c>at-site</c> row then falls back to the listener placement (documented on <see cref="WorldAudioCue"/>).</param>
    public void SubmitCue(string eventToken, Vector3? site) {
        lock (m_gate) {
            if (!m_cueRows.TryGetValue(
                key: eventToken,
                value: out var rows
            )) {
                return;
            }

            foreach (var row in rows) {
                var placement = (((row.Placement == CuePlacement.AtSite) && (site is null))
                    ? CuePlacement.Listener
                    : row.Placement
                );
                var id = m_nextEmitterId++;

                if (m_transients.Count >= TransientCueCapacity) {
                    EvictNearestExpiry();
                }

                m_transients.Add(item: new TransientCue {
                    Id = id,
                    Token = eventToken,
                    PatchId = row.PatchId,
                    GainQ16 = row.GainQ16,
                    Placement = placement,
                    Site = (site ?? default),
                    SpeakerName = row.SpeakerName,
                    RemainingFrames = CueLifeFrames(patchId: row.PatchId),
                });
                // Voice gain stays unity — the transient emitter's own gain carries the cue level; a voice gain here
                // would double-scale. The seed folds the token with a session ordinal:
                // repeated cues of one event get distinct noise streams.
                SubmitTrigger(
                    patchId: row.PatchId,
                    seed: Fnv64(text: eventToken) ^ ++m_cueOrdinal,
                    gainQ16: 65536,
                    emitterId: id
                );
            }
        }
    }
    /// <summary>Submits one seeded synth trigger request — the one trigger-production seam: stamps the
    /// strictly-increasing sequence and rides the pending ring onto the next published snapshots. Emitter-arrival
    /// policy is just this seam's first caller; the cue producers (world-event cues, footstep derivation, screen
    /// lifecycle) feed the same sequence-stamped path.</summary>
    /// <param name="patchId">The registered patch the voice plays.</param>
    /// <param name="seed">The noise seed — the same seed reproduces the voice bit for bit.</param>
    /// <param name="gainQ16">The voice gain, Q16 (65536 = unity).</param>
    /// <param name="emitterId">The emitter the voice spatializes through.</param>
    public void SubmitTrigger(string patchId, ulong seed, int gainQ16, int emitterId) {
        lock (m_gate) {
            m_pendingTriggers.Add(item: new PendingTrigger {
                Trigger = new SynthTrigger(
                Sequence: ++m_nextTriggerSequence,
                PatchId: patchId,
                Seed: seed,
                GainQ16: gainQ16,
                EmitterId: emitterId
            ),
                RemainingPublishes = TriggerPublishRetention,
            });
        }
    }
    /// <summary>Mixes one block from the latest published snapshot into the attached mixer — the device pump's
    /// per-quantum entry, callable from any thread. Returns <see langword="false"/> (leaving the span untouched —
    /// the caller writes silence) while no mixer is attached or nothing has been published yet.</summary>
    /// <param name="stereoInterleaved">The output block; fully overwritten on <see langword="true"/>.</param>
    public bool TryMixBlock(Span<short> stereoInterleaved) {
        lock (m_gate) {
            if (
                (m_mixer is not { } mixer) ||
                !m_buffer.TrySnapshot(frame: out var snapshot)
            ) {
                return false;
            }

            mixer.MixBlock(
                snapshot: snapshot,
                stereoInterleaved: stereoInterleaved
            );

            return true;
        }
    }
    /// <summary>Resolves a speaker row's live gizmo pose — Fixed/Bed directly, an anchored row through the same
    /// anchor resolution the emitter derivation uses (entity roots/leaves off the frame's packed transforms,
    /// placements off the stamp/animator math). The editor-gizmo feed's read; gate-locked and cheap.</summary>
    /// <param name="speaker">The (possibly drag-composed) speaker row.</param>
    /// <param name="transforms">The frame's packed dynamic transforms.</param>
    /// <param name="position">The resolved world position.</param>
    /// <returns><see langword="false"/> when the anchor is unresolvable this frame (the chip then hides).</returns>
    public bool TryResolveSpeakerPose(WorldSpeaker speaker, ReadOnlySpan<DynamicTransform> transforms, out Vector3 position) {
        switch (speaker) {
            case WorldSpeaker.Fixed fixedSpeaker:
                position = fixedSpeaker.Position;

                return true;
            case WorldSpeaker.Bed bed:
                position = bed.Center;

                return true;
            case WorldSpeaker.Anchored anchored: {
                    lock (m_gate) {
                        var plan = new EmitterPlan {
                            Anchor = AnchorOf(
                            anchor: anchored.Anchor,
                            offset: anchored.Offset
                        ),
                        };

                        return TryResolvePosition(
                            plan: in plan,
                            position: out position,
                            transforms: transforms
                        );
                    }
                }
            default:
                position = default;

                return false;
        }
    }
    /// <summary>Copies the latest published snapshot, when one exists (the raw consumer seam; the device pump uses
    /// <see cref="TryMixBlock"/>, which folds the snapshot read and the mix under the gate).</summary>
    /// <param name="snapshot">The latest snapshot.</param>
    public bool TrySnapshot(out AudioSnapshot snapshot) => m_buffer.TrySnapshot(frame: out snapshot);

    private enum EmitterAnchorKind : byte {
        Fixed,
        Entity,
        EntityPart,
        Placement,
    }
    // WHERE one derived emitter rides — a fixed point, an entity root/part, or a placement (with the static stamp
    // position precomputed at reconcile so per-frame resolution allocates nothing).
    private readonly struct EmitterAnchor {
        public int EntityIndex { get; init; }
        // Whether the carrying placement row itself carries an ATTACH facet — the ONE fact TryResolvePosition needs
        // to tell "the pool has no live position for this row's target body right now" (absent — the row contributes
        // nothing, same as an inactive body's render stamp) apart from "this row was never pool-rooted at all" (an
        // ordinary static/animated placement, where the STATIC-position fallback is correct). Defaults false, so
        // every OTHER PlacementPoint caller (a speaker anchored to a placement's stamp) is unaffected.
        public bool IsAttached { get; init; }
        public EmitterAnchorKind Kind { get; init; }
        public Vector3 Offset { get; init; }
        public string? PartId { get; init; }
        public string? PlacementId { get; init; }
        public Vector3 Position { get; init; }
        public int? ShapeId { get; init; }

        public static EmitterAnchor EntityPart(int index, string partId, Vector3 offset) => new() { EntityIndex = index, Kind = EmitterAnchorKind.EntityPart, Offset = offset, PartId = partId };
        public static EmitterAnchor EntityRoot(int index, Vector3 offset) => new() { EntityIndex = index, Kind = EmitterAnchorKind.Entity, Offset = offset };
        public static EmitterAnchor FixedPoint(Vector3 position) => new() { Kind = EmitterAnchorKind.Fixed, Position = position };
        public static EmitterAnchor PlacementPoint(string placementId, int? shapeId, Vector3 staticPosition, Vector3 offset = default, bool isAttached = false) =>
            new() { IsAttached = isAttached, Kind = EmitterAnchorKind.Placement, Offset = offset, PlacementId = placementId, Position = staticPosition, ShapeId = shapeId };
    }
    // One derived emitter row — the document-derived stable facts Publish resolves a pose for each frame, plus the
    // last publish's LIVE status (the speaker.state echo: where the row resolved and whether the listener sits
    // inside its finite support).
    private struct EmitterPlan {
        public string Key;
        public int Id;
        public AudioEmitterKind Kind;
        public EmitterAnchor Anchor;
        public FixedQ4816 MinRadius;
        public FixedQ4816 MaxRadius;
        public AudioAttenuationCurve Curve;
        public int FadeFrames;
        public int GainQ16;
        public AudioChannel Channel;
        public AudioSourceKey Source;
        public Vector3 LastPosition;
        public bool LastResolved;
        public bool LastInSupport;
    }
    private readonly record struct EmitterIdentity(int Id, ulong Signature);
    private struct PendingTrigger {
        public SynthTrigger Trigger;
        public int RemainingPublishes;
    }
    private enum CuePlacement : byte {
        AtSite,
        Listener,
        Emitter,
    }
    // One cue-table row, placement pre-parsed (BuildCueTable) so SubmitCue allocates nothing per event.
    private readonly record struct CueRow(string PatchId, int GainQ16, CuePlacement Placement, string? SpeakerName);
    // One live transient cue emitter (the reserved pool's unit): its stable id, its voice's patch/gain, where it
    // rides, and its remaining life in audio frames (aged by the publish clock).
    private struct TransientCue {
        public int Id;
        public string Token;
        public string PatchId;
        public int GainQ16;
        public CuePlacement Placement;
        public Vector3 Site;
        public string? SpeakerName;
        public long RemainingFrames;
    }
    private readonly record struct TuneHost(string TuneId, string Hash, TuneMachineSource Source);
    // One live machine binding: the drained machine (reference identity — the swap detector) and its block-source
    // wrapper (reused across attaches so a rebind is one SetSource, no allocation).
    private readonly record struct MachineBinding(IAudioMachine Machine, MachineBlockSource Source);
}
