using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Audio;

namespace Puck.World.Client;

/// <summary>One seat's resolved view-camera pose for the listener policy — filled by the frame source from the SAME
/// rig resolution the seat renders through (the editor rig when the seat edits), so "focus" listens where the active
/// view looks.</summary>
/// <param name="Joined">Whether the seat is joined this frame.</param>
/// <param name="Eye">The resolved camera eye, world space.</param>
/// <param name="Forward">The resolved camera forward (eye → target), world space.</param>
internal readonly record struct WorldSeatCameraPose(bool Joined, Vector3 Eye, Vector3 Forward);

/// <summary>
/// The client-side audio director — the seam between the world DOCUMENT and the mixer's emitter vocabulary
/// (audio plan AP2). <see cref="ReconcileSpeakers"/> derives the emitter TABLE from the delivered definition (speaker rows by
/// kind, emission facets keyed by row family, creation-sound emitters per placement of a sound-bearing creation) with
/// STABLE emitter ids — diff-by-key: a property edit keeps its id (the mixer's coefficient ramps survive), an
/// identity change (kind, anchor kind, source identity, or the referenced asset's content HASH — the restart
/// discriminator) releases the id and re-enters from silence. <see cref="Publish"/> resolves each emitter's pose per
/// produced frame — entity roots from the snapshot view, entity LEAVES from the frame's real packed transforms
/// (<see cref="WorldAvatarCatalog.LeafPose"/> — the gait-swung pose, not the rest-offset approximation), placements
/// from the stamped transform (the animator's current frame for animated rows) — and publishes a
/// <see cref="WorldAudioSnapshot"/> over a ≥4-deep slab rotation.
/// </summary>
/// <remarks>
/// <para><b>The v1 trigger policy (deliberate, documented):</b> every synth-fed emitter fires exactly ONE seeded
/// trigger on emitter ARRIVAL (a new or identity-recreated key) — a looping patch (no duration) sustains until the
/// emitter departs (the mixer frees unbound voices); a one-shot patch plays once. Periodic/behavioral retriggering
/// is deferred (AP4+). Seeds derive from the emitter key + content signature, so a voice reproduces bit for bit
/// across runs. A pending trigger rides the next <see cref="TriggerPublishRetention"/> published snapshots: the
/// publish buffer keeps only the latest, so retention ≥ two device quanta guarantees a consumer sees the event once,
/// and the mixer's high-water sequence makes repeats free.</para>
/// <para><b>Source hosting:</b> patch registration and headless tune hosting (acquire while referenced, release when
/// orphaned, the tune HASH as the restart discriminator) activate when a mixer is attached
/// (<see cref="AttachMixer"/> — the offline proof and AP3's device pump); unattached, the director only derives and
/// publishes. Machine sources are never bound here — AP3's device pump owns the live machine drain; an unbound
/// source renders honest silence.</para>
/// <para>Single-threaded on the window-pump thread (reconcile at the delivery boundary, publish during frame
/// produce), like every client type here; the published snapshot is the one cross-thread seam.</para>
/// </remarks>
internal sealed class WorldAudioDirector {
    /// <summary>The slab-rotation depth (plan A3): the consumer holds one snapshot for one ~5.33 ms block; the
    /// producer needs ≥33 ms to lap four slabs — safe by an order of magnitude.</summary>
    public const int SnapshotRotation = 4;
    /// <summary>How many published snapshots a pending trigger rides (see the type remarks).</summary>
    public const int TriggerPublishRetention = 8;

    private const ulong Fnv64OffsetBasis = 14695981039346656037UL;
    private const ulong Fnv64Prime = 1099511628211UL;

    private readonly WorldClient? m_client;
    private readonly WorldPlacementAnimator? m_animator;
    private readonly WorldAudioSnapshot[] m_slabs;
    private readonly PublishBuffer<WorldAudioSnapshot> m_buffer = new();
    private readonly List<EmitterPlan> m_plan = new();
    // The stable-id registry: emitter key → (id, identity signature). Survives reconciles so property edits keep
    // their mixer ramp state; an identity change re-keys (a fresh id ramps in from silence).
    private readonly Dictionary<string, EmitterIdentity> m_registry = new(comparer: StringComparer.Ordinal);
    private readonly List<PendingTrigger> m_pendingTriggers = new();
    // The mixer-facing patch registration set (world patch rows by id + inline creation-sound patches by emitter
    // key) — applied on attach and on every reconcile while attached.
    private readonly List<(string Id, WorldVoicePatch Patch)> m_patchSet = new();
    // The headless tune hosts, by tune id (live only while a mixer is attached).
    private readonly Dictionary<string, TuneHost> m_tuneHosts = new(comparer: StringComparer.Ordinal);
    private WorldDefinition? m_definition;
    private WorldAudioMixer? m_mixer;
    private int m_nextEmitterId = 1;
    private ulong m_nextTriggerSequence;
    private int m_slabIndex;
    private FixedComplex m_lastListenerYaw = FixedComplex.MultiplicativeIdentity;

    /// <summary>Initializes the director over the client view and the animated-placement pool. Both are nullable so
    /// the OFFLINE driver (the audio-mix proof) runs the same derivation headlessly: without a client, entity-anchored
    /// emitters resolve absent (honest silence); without an animator, placements resolve through the static stamp math.</summary>
    /// <param name="client">The snapshot-fed entity view, or <see langword="null"/> headless.</param>
    /// <param name="animator">The animated-placement replay pool, or <see langword="null"/> headless.</param>
    public WorldAudioDirector(WorldClient? client, WorldPlacementAnimator? animator) {
        m_client = client;
        m_animator = animator;
        m_slabs = new WorldAudioSnapshot[SnapshotRotation];

        for (var index = 0; (index < SnapshotRotation); index++) {
            m_slabs[index] = new WorldAudioSnapshot();
        }
    }

    /// <summary>The document master gain in Q16 — the value an attached mixer's <c>MasterGainQ16</c> follows.</summary>
    public int MasterGainQ16 { get; private set; } = 65536;

    /// <summary>The derived emitter count (the plan's row count, before any capacity refusal).</summary>
    public int EmitterCount => m_plan.Count;

    /// <summary>Copies the latest published snapshot, when one exists (the consumer seam AP3's device pump reads).</summary>
    /// <param name="snapshot">The latest snapshot.</param>
    public bool TrySnapshot(out WorldAudioSnapshot snapshot) => m_buffer.TrySnapshot(frame: out snapshot);

    /// <summary>Attaches a mixer: registers the current patch set, sets its master gain, and activates tune
    /// acquire/release hosting (sources bind now and follow every reconcile until detached).</summary>
    /// <param name="mixer">The mixer to bind sources into.</param>
    public void AttachMixer(WorldAudioMixer mixer) {
        ArgumentNullException.ThrowIfNull(argument: mixer);

        m_mixer = mixer;
        ApplyMixerBindings();
    }

    /// <summary>Releases every hosted tune source and detaches the mixer.</summary>
    public void DetachMixer() {
        foreach (var host in m_tuneHosts.Values) {
            m_mixer?.RemoveSource(key: WorldAudioSourceKey.Tune(id: host.TuneId));
            host.Source.Dispose();
        }

        m_tuneHosts.Clear();
        m_mixer = null;
    }

    /// <summary>Reconciles the derived emitter table against a delivered definition — call at the delivery boundary
    /// AFTER <see cref="WorldScreenBinder.ReconcileScreens"/> (the chiasmus ordering: speakers consume screen slots)
    /// and after the animator's own reconcile (placement anchors read its registrations).</summary>
    /// <param name="definition">The delivered definition.</param>
    public void ReconcileSpeakers(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        m_definition = definition;

        var audio = definition.Audio;

        MasterGainQ16 = GainQ16(gain: audio.MasterGain);
        m_plan.Clear();
        m_patchSet.Clear();

        foreach (var patch in definition.Patches) {
            m_patchSet.Add(item: (Id: patch.Id, Patch: WorldVoicePatch.FromDocument(document: patch.Document)));
        }

        DeriveSpeakers(definition: definition, audio: audio);
        DeriveEmissionFacets(definition: definition, audio: audio);
        DeriveCreationSounds(definition: definition, audio: audio);
        RetireDepartedKeys();

        if (m_plan.Count > WorldAudioSnapshot.DefaultMaxEmitters) {
            Console.Error.WriteLine(value: $"[world.audio: {m_plan.Count} derived emitters exceed the {WorldAudioSnapshot.DefaultMaxEmitters}-row snapshot table — the overflow renders silent]");
        }

        ApplyMixerBindings();
    }

    /// <summary>Resolves this frame's listener and emitter poses and publishes one snapshot from the slab rotation.
    /// Returns the published snapshot (the offline driver mixes it directly).</summary>
    /// <param name="transforms">The frame's packed dynamic transforms (empty headless — leaf anchors then resolve
    /// absent).</param>
    /// <param name="seats">The per-slot resolved view-camera poses (the listener policy's candidates).</param>
    public WorldAudioSnapshot Publish(ReadOnlySpan<DynamicTransform> transforms, ReadOnlySpan<WorldSeatCameraPose> seats) {
        var slab = m_slabs[m_slabIndex];

        m_slabIndex = ((m_slabIndex + 1) % SnapshotRotation);
        slab.Reset(listener: ResolveListener(seats: seats, transforms: transforms));

        foreach (var plan in m_plan) {
            if (!TryResolvePosition(plan: plan, transforms: transforms, position: out var position)) {
                continue; // An unresolvable anchor is an absent emitter — honest silence, zero special cases.
            }

            _ = slab.TryAddEmitter(emitter: new WorldAudioEmitter(
                Id: plan.Id,
                Kind: plan.Kind,
                Position: ToFixed(value: position),
                MinRadius: plan.MinRadius,
                MaxRadius: plan.MaxRadius,
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

        m_pendingTriggers.RemoveRange(index: write, count: (m_pendingTriggers.Count - write));

        m_buffer.Publish(frame: slab);

        return slab;
    }

    /// <summary>The deterministic <c>audio.emitters</c> listing: one segment per derived emitter — id, key, kind,
    /// source token, channel, gain, and radii — the document-derived STABLE facts (never live poses), so a piped
    /// proof asserts the derivation byte-for-byte.</summary>
    public string DescribeEmitters() {
        if (m_plan.Count == 0) {
            return "[audio.emitters: none derived]";
        }

        var builder = new StringBuilder(value: "[audio.emitters:");

        for (var index = 0; (index < m_plan.Count); index++) {
            var plan = m_plan[index];

            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"{((index == 0) ? " " : " | ")}{plan.Id} {plan.Key} {((plan.Kind == WorldAudioEmitterKind.Bed) ? "bed" : "point")} {SourceToken(source: plan.Source)} {ChannelToken(channel: plan.Channel)} gain={((double)plan.GainQ16 / 65536.0):0.###} min={((double)plan.MinRadius):0.###} max={((double)plan.MaxRadius):0.###}");
        }

        return builder.Append(value: ']').ToString();
    }

    // ---- derivation ------------------------------------------------------------------------------------------------

    private void DeriveSpeakers(WorldDefinition definition, WorldAudioDefaults audio) {
        foreach (var speaker in definition.Speakers) {
            var key = $"speaker:{speaker.Name}";
            var source = SourceKey(source: speaker.Feed.Source);
            var gain = GainQ16(gain: speaker.Feed.Gain);
            var channel = (speaker.Feed.Channel switch {
                WorldSpeakerFeed.ChannelLeft => WorldAudioChannel.Left,
                WorldSpeakerFeed.ChannelRight => WorldAudioChannel.Right,
                _ => WorldAudioChannel.Mix,
            });

            switch (speaker) {
                case WorldSpeaker.Bed bed:
                    Admit(plan: new EmitterPlan {
                        Key = key,
                        Kind = WorldAudioEmitterKind.Bed,
                        Anchor = EmitterAnchor.FixedPoint(position: bed.Center),
                        MinRadius = FixedQ4816.FromDouble(value: (bed.InnerRadius ?? 0f)),
                        MaxRadius = FixedQ4816.FromDouble(value: bed.Radius),
                        FadeFrames = FadeFrames(seconds: (bed.FadeSeconds ?? audio.DefaultBedFadeSeconds)),
                        GainQ16 = gain,
                        Channel = channel,
                        Source = source,
                    }, signatureToken: $"bed|{SourceSignature(source: speaker.Feed.Source, definition: definition)}");

                    break;
                case WorldSpeaker.Fixed fixedSpeaker:
                    Admit(plan: PointPlan(key: key, anchor: EmitterAnchor.FixedPoint(position: fixedSpeaker.Position), attenuation: speaker.Attenuation, audio: audio, gain: gain, channel: channel, source: source),
                        signatureToken: $"fixed|{SourceSignature(source: speaker.Feed.Source, definition: definition)}");

                    break;
                case WorldSpeaker.Anchored anchored:
                    Admit(plan: PointPlan(key: key, anchor: AnchorOf(anchor: anchored.Anchor, offset: anchored.Offset), attenuation: speaker.Attenuation, audio: audio, gain: gain, channel: channel, source: source),
                        signatureToken: $"anchored|{AnchorKindToken(anchor: anchored.Anchor)}|{SourceSignature(source: speaker.Feed.Source, definition: definition)}");

                    break;
            }
        }
    }

    private void DeriveEmissionFacets(WorldDefinition definition, WorldAudioDefaults audio) {
        foreach (var row in definition.Scene.Rows) {
            if (row.Emission is { } emission) {
                AdmitEmission(key: $"scene:{row.Id}", anchor: EmitterAnchor.FixedPoint(position: row.Center), emission: emission, audio: audio, definition: definition);
            }
        }

        foreach (var placement in definition.Placements) {
            if (placement.Emission is { } emission) {
                // Root-only under Repeat (documented on WorldPlacement): the emission binds the placement root.
                AdmitEmission(key: $"placement:{placement.Id}", anchor: EmitterAnchor.PlacementPoint(placementId: placement.Id, shapeId: null, staticPosition: placement.Position), emission: emission, audio: audio, definition: definition);
            }
        }
    }

    private void DeriveCreationSounds(WorldDefinition definition, WorldAudioDefaults audio) {
        foreach (var placement in definition.Placements) {
            if ((WorldPlacementStamper.FindCreation(creations: definition.Creations, id: placement.CreationId) is not { } creation) ||
                (creation.Document.Behavior?.Sounds is not { Count: > 0 } sounds)) {
                continue;
            }

            foreach (var sound in sounds) {
                // The inline patch registers under the emitter key itself — a per-emitter voice identity that can
                // never collide with a world patch row's id (the "sound:" prefix is not a legal row reference).
                var key = $"sound:{placement.Id}:{sound.Name}";

                m_patchSet.Add(item: (Id: key, Patch: WorldVoicePatch.FromDocument(document: sound.Patch)));
                Admit(plan: new EmitterPlan {
                    Key = key,
                    Kind = WorldAudioEmitterKind.Point,
                    Anchor = EmitterAnchor.PlacementPoint(placementId: placement.Id, shapeId: sound.ShapeId, staticPosition: StaticShapePosition(placement: placement, creation: creation, shapeId: sound.ShapeId)),
                    MinRadius = FixedQ4816.Zero,
                    MaxRadius = FixedQ4816.FromDouble(value: (sound.Radius ?? audio.DefaultSpeakerRadius)),
                    FadeFrames = 0,
                    GainQ16 = GainQ16(gain: (sound.Level ?? 1f)),
                    Channel = WorldAudioChannel.Mix,
                    Source = WorldAudioSourceKey.Synth(patchId: key),
                }, signatureToken: $"sound|{creation.Hash}");
            }
        }
    }

    private EmitterPlan PointPlan(string key, EmitterAnchor anchor, WorldSpeakerAttenuation? attenuation, WorldAudioDefaults audio, int gain, WorldAudioChannel channel, WorldAudioSourceKey source) => new() {
        Key = key,
        Kind = WorldAudioEmitterKind.Point,
        Anchor = anchor,
        // Points shoulder from their center (min 0); the attenuation radius (row or audio-defaults) is the finite
        // support edge — a full-gain inner band is a bed concept.
        MinRadius = FixedQ4816.Zero,
        MaxRadius = FixedQ4816.FromDouble(value: (attenuation?.Radius ?? audio.DefaultSpeakerRadius)),
        FadeFrames = 0,
        GainQ16 = gain,
        Channel = channel,
        Source = source,
    };

    private void AdmitEmission(string key, EmitterAnchor anchor, WorldEmission emission, WorldAudioDefaults audio, WorldDefinition definition) {
        Admit(plan: new EmitterPlan {
            Key = key,
            Kind = WorldAudioEmitterKind.Point,
            Anchor = anchor,
            MinRadius = FixedQ4816.Zero,
            MaxRadius = FixedQ4816.FromDouble(value: (emission.Radius ?? audio.DefaultSpeakerRadius)),
            FadeFrames = 0,
            GainQ16 = GainQ16(gain: emission.Level),
            Channel = WorldAudioChannel.Mix,
            Source = WorldAudioSourceKey.Synth(patchId: emission.PatchId),
        }, signatureToken: $"emission|{PatchHash(definition: definition, patchId: emission.PatchId)}");
    }

    // Admit one plan row: resolve its stable id against the registry (keep on identical signature; retire + reissue
    // on an identity change — the fresh id re-enters the mixer from silence) and fire the arrival trigger for
    // synth-fed rows (the v1 trigger policy in the type remarks).
    private void Admit(EmitterPlan plan, string signatureToken) {
        var signature = Fnv64(text: signatureToken);
        var arrived = true;

        if (m_registry.TryGetValue(key: plan.Key, value: out var existing)) {
            if (existing.Signature == signature) {
                plan.Id = existing.Id;
                arrived = false;
            } else {
                plan.Id = m_nextEmitterId++;
                m_registry[plan.Key] = new EmitterIdentity(Id: plan.Id, Signature: signature);
            }
        } else {
            plan.Id = m_nextEmitterId++;
            m_registry[plan.Key] = new EmitterIdentity(Id: plan.Id, Signature: signature);
        }

        if (arrived && (plan.Source.Kind == WorldAudioSourceKind.Synth) && (plan.Source.Id is { } patchId)) {
            // The seed folds the key and the identity signature: the same authored content reproduces the voice bit
            // for bit; a content change re-seeds with the new identity. Gain stays unity — the emitter's own gain
            // spatializes; a voice gain here would double-scale.
            SubmitTrigger(patchId: patchId, seed: (Fnv64(text: plan.Key) ^ signature), gainQ16: 65536, emitterId: plan.Id);
        }

        m_plan.Add(item: plan);
    }

    /// <summary>Submits one seeded synth trigger request — THE one trigger-production seam: stamps the
    /// strictly-increasing sequence and rides the pending ring onto the next published snapshots. Emitter-arrival
    /// policy is just this seam's first caller; AP4's cue producers (world-event cues, footstep derivation, screen
    /// lifecycle) feed the same sequence-stamped path.</summary>
    /// <param name="patchId">The registered patch the voice plays.</param>
    /// <param name="seed">The noise seed — the same seed reproduces the voice bit for bit.</param>
    /// <param name="gainQ16">The voice gain, Q16 (65536 = unity).</param>
    /// <param name="emitterId">The emitter the voice spatializes through.</param>
    public void SubmitTrigger(string patchId, ulong seed, int gainQ16, int emitterId) {
        m_pendingTriggers.Add(item: new PendingTrigger {
            Trigger = new WorldSynthTrigger(
                Sequence: ++m_nextTriggerSequence,
                PatchId: patchId,
                Seed: seed,
                GainQ16: gainQ16,
                EmitterId: emitterId
            ),
            RemainingPublishes = TriggerPublishRetention,
        });
    }

    // Drop registry rows whose key left the derived plan, so a re-authored row later re-enters from silence with a
    // fresh id rather than inheriting a stale ramp.
    private void RetireDepartedKeys() {
        List<string>? departed = null;

        foreach (var key in m_registry.Keys) {
            var present = false;

            foreach (var plan in m_plan) {
                if (string.Equals(a: plan.Key, b: key, comparisonType: StringComparison.Ordinal)) {
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

    // ---- source hosting --------------------------------------------------------------------------------------------

    // Apply the derived bindings to the attached mixer: master gain, the patch set, and tune acquire/release with
    // the tune HASH as the restart discriminator. No mixer attached = derivation only (the AP2 live posture).
    private void ApplyMixerBindings() {
        if ((m_mixer is not { } mixer) || (m_definition is not { } definition)) {
            return;
        }

        mixer.MasterGainQ16 = MasterGainQ16;

        foreach (var (id, patch) in m_patchSet) {
            mixer.RegisterPatch(id: id, patch: in patch);
        }

        // The referenced-tune set: acquire a headless host per tune some plan row taps; release orphans; a hash
        // change restarts the host honestly (the placement animator's release+recreate precedent).
        List<string>? orphaned = null;

        foreach (var (tuneId, host) in m_tuneHosts) {
            if (FindReferencedTune(definition: definition, tuneId: tuneId) is not { } tune) {
                (orphaned ??= new List<string>()).Add(item: tuneId);
            } else if (!string.Equals(a: tune.Hash, b: host.Hash, comparisonType: StringComparison.Ordinal)) {
                host.Source.Dispose();
                m_tuneHosts[tuneId] = CreateTuneHost(tune: tune, mixer: mixer);
            }
        }

        foreach (var tuneId in (orphaned ?? [])) {
            mixer.RemoveSource(key: WorldAudioSourceKey.Tune(id: tuneId));
            m_tuneHosts[tuneId].Source.Dispose();
            _ = m_tuneHosts.Remove(key: tuneId);
        }

        foreach (var plan in m_plan) {
            if ((plan.Source.Kind == WorldAudioSourceKind.Tune) && (plan.Source.Id is { } tuneId) && !m_tuneHosts.ContainsKey(key: tuneId) &&
                (FindTune(definition: definition, tuneId: tuneId) is { } tune)) {
                m_tuneHosts[tuneId] = CreateTuneHost(tune: tune, mixer: mixer);
            }
        }
    }

    private WorldTune? FindReferencedTune(WorldDefinition definition, string tuneId) {
        foreach (var plan in m_plan) {
            if ((plan.Source.Kind == WorldAudioSourceKind.Tune) && string.Equals(a: plan.Source.Id, b: tuneId, comparisonType: StringComparison.Ordinal)) {
                return FindTune(definition: definition, tuneId: tuneId);
            }
        }

        return null;
    }

    private static TuneHost CreateTuneHost(WorldTune tune, WorldAudioMixer mixer) {
        var source = new TuneMachineSource(document: tune.Document);

        mixer.SetSource(key: WorldAudioSourceKey.Tune(id: tune.Id), source: source);

        return new TuneHost(TuneId: tune.Id, Hash: tune.Hash, Source: source);
    }

    // ---- pose resolution -------------------------------------------------------------------------------------------

    private bool TryResolvePosition(in EmitterPlan plan, ReadOnlySpan<DynamicTransform> transforms, out Vector3 position) {
        var anchor = plan.Anchor;

        switch (anchor.Kind) {
            case EmitterAnchorKind.Fixed:
                position = anchor.Position;

                return true;
            case EmitterAnchorKind.Entity: {
                if ((m_client is not { } client) || !client.IsActive(index: anchor.EntityIndex)) {
                    position = default;

                    return false;
                }

                position = (client.Position(index: anchor.EntityIndex) + Vector3.Transform(value: anchor.Offset, rotation: client.Orientation(index: anchor.EntityIndex)));

                return true;
            }
            case EmitterAnchorKind.EntityLeaf: {
                if ((m_client is not { } client) || !client.IsActive(index: anchor.EntityIndex) || (transforms.Length < WorldAvatarCatalog.DynamicTransformCapacity)) {
                    position = default;

                    return false;
                }

                // THE LEAF-POSE SWAP: the frame's real packed transforms carry the gait-swung leaf pose, so a
                // hand-anchored speaker follows the swing, not the rest offset.
                var (leafPosition, leafOrientation) = WorldAvatarCatalog.LeafPose(avatar: anchor.EntityIndex, role: anchor.Role, transforms: transforms);

                position = (leafPosition + Vector3.Transform(value: anchor.Offset, rotation: leafOrientation));

                return true;
            }
            case EmitterAnchorKind.Placement:
            default: {
                // Animated placements ride the animator's current frame; static ones the reconcile-time stamp math.
                if ((m_animator is { } animator) && (anchor.PlacementId is { } placementId) && animator.TryShapePosition(placementId: placementId, shapeId: anchor.ShapeId, position: out var animated)) {
                    position = (animated + anchor.Offset);

                    return true;
                }

                position = (anchor.Position + anchor.Offset);

                return true;
            }
        }
    }

    private WorldAudioListener ResolveListener(ReadOnlySpan<WorldSeatCameraPose> seats, ReadOnlySpan<DynamicTransform> transforms) {
        var (eye, forward) = ResolveListenerPose(seats: seats, transforms: transforms);
        // The yaw rotor maps listener-local (X = right, Y = forward) into world (X, Z); building it from the planar
        // RIGHT vector r = (−fz, fx) makes an emitter on the listener's geometric right pan right (front/back fold
        // to center in the mixer's pan law, so only the right axis carries meaning).
        var planar = new Vector2(x: forward.X, y: forward.Z);

        if (planar.LengthSquared() > 1e-6f) {
            planar = Vector2.Normalize(value: planar);
            m_lastListenerYaw = new FixedComplex(
                Real: FixedQ4816.FromDouble(value: -planar.Y),
                Imaginary: FixedQ4816.FromDouble(value: planar.X)
            ).Normalize();
        }

        return new WorldAudioListener(Position: ToFixed(value: eye), Yaw: m_lastListenerYaw);
    }

    // The listener policy (plan A5): focus = the first joined seat's resolved view camera (the editor rig when that
    // seat edits — the frame source resolves the SAME rig the seat renders through); seat:<n> pins that seat (falling
    // back to focus while it is unjoined); a camera name resolves the declared camera row. No candidate at all
    // (headless, no seats) listens from the origin facing -Z.
    private (Vector3 Eye, Vector3 Forward) ResolveListenerPose(ReadOnlySpan<WorldSeatCameraPose> seats, ReadOnlySpan<DynamicTransform> transforms) {
        var listener = (m_definition?.Audio.Listener ?? WorldAudioDefaults.ListenerFocus);

        if (listener.StartsWith(value: WorldAudioDefaults.ListenerSeatPrefix, comparisonType: StringComparison.Ordinal) &&
            int.TryParse(s: listener.AsSpan(start: WorldAudioDefaults.ListenerSeatPrefix.Length), result: out var seat) &&
            ((seat - 1) is var slot) && ((uint)slot < (uint)seats.Length) && seats[slot].Joined) {
            return (Eye: seats[slot].Eye, Forward: seats[slot].Forward);
        }

        if (!string.Equals(a: listener, b: WorldAudioDefaults.ListenerFocus, comparisonType: StringComparison.Ordinal) &&
            !listener.StartsWith(value: WorldAudioDefaults.ListenerSeatPrefix, comparisonType: StringComparison.Ordinal) &&
            (ResolveCameraListener(name: listener, transforms: transforms) is { } pinned)) {
            return pinned;
        }

        foreach (var candidate in seats) {
            if (candidate.Joined) {
                return (Eye: candidate.Eye, Forward: candidate.Forward);
            }
        }

        return (Eye: Vector3.Zero, Forward: new Vector3(x: 0f, y: 0f, z: -1f));
    }

    private (Vector3 Eye, Vector3 Forward)? ResolveCameraListener(string name, ReadOnlySpan<DynamicTransform> transforms) {
        if (m_definition is not { } definition) {
            return null;
        }

        foreach (var camera in definition.Cameras) {
            if (!string.Equals(a: camera.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            switch (camera) {
                case WorldCamera.Fixed fixedCamera:
                    return (Eye: fixedCamera.Position, Forward: (fixedCamera.LookAt - fixedCamera.Position));
                case WorldCamera.Anchored anchored: {
                    var plan = new EmitterPlan {
                        Anchor = AnchorOf(anchor: anchored.Anchor, offset: anchored.Offset),
                    };

                    if (TryResolvePosition(plan: in plan, transforms: transforms, position: out var eye) && (m_client is { } client) &&
                        (anchored.Anchor is WorldAnchor.Entity or WorldAnchor.EntityLeaf)) {
                        var index = ((anchored.Anchor is WorldAnchor.Entity entity) ? entity.Index : ((WorldAnchor.EntityLeaf)anchored.Anchor).Index);

                        // Avatar-local forward is -Z (the body convention every kit composition rides).
                        return (Eye: eye, Forward: Vector3.Transform(value: new Vector3(x: 0f, y: 0f, z: -1f), rotation: client.Orientation(index: index)));
                    }

                    return null;
                }
            }
        }

        return null;
    }

    // ---- small shared derivations ----------------------------------------------------------------------------------

    private EmitterAnchor AnchorOf(WorldAnchor anchor, Vector3 offset) => anchor switch {
        WorldAnchor.Entity entity => EmitterAnchor.EntityRoot(index: entity.Index, offset: offset),
        WorldAnchor.EntityLeaf leaf when WorldAvatarCatalog.TryHumanoidRole(token: leaf.Leaf, role: out var role) =>
            EmitterAnchor.EntityLeafRole(index: leaf.Index, role: role, offset: offset),
        WorldAnchor.Placement placement => EmitterAnchor.PlacementPoint(
            placementId: placement.PlacementId,
            shapeId: placement.ShapeId,
            staticPosition: StaticPlacementPosition(placementId: placement.PlacementId, shapeId: placement.ShapeId),
            offset: offset
        ),
        _ => EmitterAnchor.FixedPoint(position: offset),
    };

    // A static placement anchor's stamped position: root position, or root ∘ (scale · shape local) under the yaw
    // rotation — the stamp math WorldPlacementStamper bakes, reduced to the anchor point.
    private Vector3 StaticPlacementPosition(string placementId, int? shapeId) {
        if (m_definition is not { } definition) {
            return Vector3.Zero;
        }

        foreach (var placement in definition.Placements) {
            if (!string.Equals(a: placement.Id, b: placementId, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            var creation = WorldPlacementStamper.FindCreation(creations: definition.Creations, id: placement.CreationId);

            return ((creation is null) ? placement.Position : StaticShapePosition(placement: placement, creation: creation, shapeId: shapeId));
        }

        return Vector3.Zero;
    }

    private static Vector3 StaticShapePosition(WorldPlacement placement, WorldCreation creation, int? shapeId) {
        if (shapeId is not { } targetShapeId) {
            return placement.Position;
        }

        foreach (var shape in (creation.Document.Shapes ?? [])) {
            if (shape.Id == targetShapeId) {
                var rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (placement.YawDegrees * (MathF.PI / 180f)));

                return (placement.Position + Vector3.Transform(value: (shape.Position * placement.Scale), rotation: rotation));
            }
        }

        return placement.Position;
    }

    private static WorldAudioSourceKey SourceKey(WorldSpeakerSource source) => source switch {
        WorldSpeakerSource.Machine machine => WorldAudioSourceKey.Machine(slot: machine.ScreenIndex),
        WorldSpeakerSource.Tune tune => WorldAudioSourceKey.Tune(id: tune.TuneId),
        WorldSpeakerSource.Synth synth => WorldAudioSourceKey.Synth(patchId: synth.PatchId),
        _ => WorldAudioSourceKey.None,
    };

    // The source half of an emitter's identity signature: the source shape plus the referenced asset's content HASH
    // (the restart discriminator — a tune/patch content change re-keys the emitter; a gain edit does not).
    private static string SourceSignature(WorldSpeakerSource source, WorldDefinition definition) => source switch {
        WorldSpeakerSource.Machine machine => $"machine:{machine.ScreenIndex}",
        WorldSpeakerSource.Tune tune => $"tune:{tune.TuneId}:{FindTune(definition: definition, tuneId: tune.TuneId)?.Hash}",
        WorldSpeakerSource.Synth synth => $"synth:{synth.PatchId}:{PatchHash(definition: definition, patchId: synth.PatchId)}",
        _ => "none",
    };

    private static WorldTune? FindTune(WorldDefinition definition, string tuneId) {
        foreach (var tune in definition.Tunes) {
            if (string.Equals(a: tune.Id, b: tuneId, comparisonType: StringComparison.Ordinal)) {
                return tune;
            }
        }

        return null;
    }

    private static string PatchHash(WorldDefinition definition, string patchId) {
        foreach (var patch in definition.Patches) {
            if (string.Equals(a: patch.Id, b: patchId, comparisonType: StringComparison.Ordinal)) {
                return patch.Hash;
            }
        }

        return string.Empty;
    }

    private static string AnchorKindToken(WorldAnchor anchor) => anchor switch {
        WorldAnchor.Entity => "entity",
        WorldAnchor.EntityLeaf => "entityLeaf",
        _ => "placement",
    };

    private static string SourceToken(in WorldAudioSourceKey source) => source.Kind switch {
        WorldAudioSourceKind.Machine => $"machine:{source.Slot}",
        WorldAudioSourceKind.Tune => $"tune:{source.Id}",
        WorldAudioSourceKind.Synth => $"synth:{source.Id}",
        _ => "none",
    };

    private static string ChannelToken(WorldAudioChannel channel) => channel switch {
        WorldAudioChannel.Left => "left",
        WorldAudioChannel.Right => "right",
        _ => "mix",
    };

    private static int GainQ16(float gain) => ((int)MathF.Round(x: (gain * 65536f)));

    private static int FadeFrames(float seconds) => ((int)MathF.Round(x: (seconds * WorldAudioMixer.SampleRate)));

    private static FixedVector3 ToFixed(Vector3 value) => new(
        X: FixedQ4816.FromDouble(value: value.X),
        Y: FixedQ4816.FromDouble(value: value.Y),
        Z: FixedQ4816.FromDouble(value: value.Z)
    );

    private static ulong Fnv64(string text) {
        var hash = Fnv64OffsetBasis;

        foreach (var character in text) {
            hash = ((hash ^ character) * Fnv64Prime);
        }

        return hash;
    }

    private enum EmitterAnchorKind : byte {
        Fixed,
        Entity,
        EntityLeaf,
        Placement,
    }

    // WHERE one derived emitter rides — a fixed point, an entity root/leaf, or a placement (with the static stamp
    // position precomputed at reconcile so per-frame resolution allocates nothing).
    private readonly struct EmitterAnchor {
        public EmitterAnchorKind Kind { get; init; }
        public Vector3 Position { get; init; }
        public Vector3 Offset { get; init; }
        public int EntityIndex { get; init; }
        public int Role { get; init; }
        public string? PlacementId { get; init; }
        public int? ShapeId { get; init; }

        public static EmitterAnchor FixedPoint(Vector3 position) => new() { Kind = EmitterAnchorKind.Fixed, Position = position };
        public static EmitterAnchor EntityRoot(int index, Vector3 offset) => new() { Kind = EmitterAnchorKind.Entity, EntityIndex = index, Offset = offset };
        public static EmitterAnchor EntityLeafRole(int index, int role, Vector3 offset) => new() { Kind = EmitterAnchorKind.EntityLeaf, EntityIndex = index, Role = role, Offset = offset };
        public static EmitterAnchor PlacementPoint(string placementId, int? shapeId, Vector3 staticPosition, Vector3 offset = default) =>
            new() { Kind = EmitterAnchorKind.Placement, PlacementId = placementId, ShapeId = shapeId, Position = staticPosition, Offset = offset };
    }

    // One derived emitter row — the document-derived stable facts Publish resolves a pose for each frame.
    private struct EmitterPlan {
        public string Key;
        public int Id;
        public WorldAudioEmitterKind Kind;
        public EmitterAnchor Anchor;
        public FixedQ4816 MinRadius;
        public FixedQ4816 MaxRadius;
        public int FadeFrames;
        public int GainQ16;
        public WorldAudioChannel Channel;
        public WorldAudioSourceKey Source;
    }

    private readonly record struct EmitterIdentity(int Id, ulong Signature);

    private struct PendingTrigger {
        public WorldSynthTrigger Trigger;
        public int RemainingPublishes;
    }

    private readonly record struct TuneHost(string TuneId, string Hash, TuneMachineSource Source);
}
