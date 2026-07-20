using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Abstractions.Machines;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Audio;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>One seat's resolved view-camera pose for the listener policy — filled by the frame source from the SAME
/// rig resolution the seat renders through (the editor rig when the seat edits), so "focus" listens where the active
/// view looks.</summary>
/// <param name="Joined">Whether the seat is joined this frame.</param>
/// <param name="Eye">The resolved camera eye, world space.</param>
/// <param name="Forward">The resolved camera forward (eye → target), world space.</param>
internal readonly record struct WorldSeatCameraPose(bool Joined, Vector3 Eye, Vector3 Forward);

/// <summary>
/// The client-side audio director — the seam between the world DOCUMENT and the mixer's emitter vocabulary.
/// <see cref="ReconcileSpeakers"/> derives the emitter TABLE from the delivered definition (speaker rows by
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
/// is deferred. Seeds derive from the emitter key + content signature, so a voice reproduces bit for bit
/// across runs. A pending trigger rides the next <see cref="TriggerPublishRetention"/> published snapshots: the
/// publish buffer keeps only the latest, so retention ≥ two device quanta guarantees a consumer sees the event once,
/// and the mixer's high-water sequence makes repeats free.</para>
/// <para><b>Source hosting:</b> patch registration and headless tune hosting (acquire while referenced, release when
/// orphaned, the tune HASH as the restart discriminator) activate when a mixer is attached
/// (<see cref="AttachMixer"/> — the offline proof and the device pump); unattached, the director only derives and
/// publishes. Machine sources bind through <see cref="MachineSourceResolver"/>: each <see cref="Publish"/> diffs the
/// binder's LIVE machines by reference for every machine-fed plan row, so a boot/eject/live-swap rebinds the mixer
/// source and a machine booting late into a referenced slot self-heals — the keys
/// (<see cref="WorldAudioSourceKey.Machine"/> by slot) stay stable across swaps.</para>
/// <para><b>Threading:</b> derivation and publishing stay on the window-pump thread, and the resolver is only
/// ever invoked there (it reads the binder's pump-owned slot table). The device pump adds two cross-thread callers —
/// <see cref="AttachMixer"/>/<see cref="DetachMixer"/> from the render service's governor and
/// <see cref="TryMixBlock"/> from the endpoint's fill thread — so every member that touches the mixer or the derived
/// plan serializes on one reentrant gate. The gate is uncontended in steady state (reconciles are rare, a mix block
/// is microseconds), which is the deliberate trade: one honest lock instead of a lock-free mixer-mutation protocol.</para>
/// </remarks>
internal sealed class WorldAudioDirector {
    /// <summary>The slab-rotation depth: the consumer holds one snapshot for one ~5.33 ms block; the
    /// producer needs ≥33 ms to lap four slabs — safe by an order of magnitude.</summary>
    public const int SnapshotRotation = 4;
    /// <summary>How many published snapshots a pending trigger rides (see the type remarks).</summary>
    public const int TriggerPublishRetention = 8;
    /// <summary>The transient cue-emitter pool size — capacity STRUCTURE like the snapshot's emitter cap, an
    /// engine invariant rather than world data: these slots are RESERVED off <see cref="WorldAudioSnapshot.DefaultMaxEmitters"/>
    /// (the reconcile overflow warning charges them), so a full derived plan can never starve a cue. A cue arriving
    /// with the pool full evicts the transient nearest its own expiry (its voice releases with the departed emitter).</summary>
    public const int TransientCueCapacity = 4;
    /// <summary>The life cap for a cue voicing a LOOPING patch (no authored duration): 2 s of audio frames. A cue is
    /// a transient by definition — a finite patch's life derives from its own envelope (data); only the loop cap is
    /// an invariant.</summary>
    public const long LoopingCueLifeFrames = (2L * WorldAudioMixer.SampleRate);
    /// <summary>The default per-publish clock advance for cue aging: one 240 Hz sim step (the offline drivers'
    /// cadence — one publish per mixed 200-frame block). The live frame source passes its real presentation delta.</summary>
    public const float DefaultPublishDeltaSeconds = (1f / 240f);

    private const ulong Fnv64OffsetBasis = 14695981039346656037UL;
    private const ulong Fnv64Prime = 1099511628211UL;

    private readonly WorldClient? m_client;
    private readonly WorldStampPool? m_animator;
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
    // The live machine bindings by screen slot: which IAudioMachine each Machine-source key currently drains.
    // Gate-guarded (Publish syncs it, DetachMixer clears it); the RESOLVER is only invoked from Publish.
    private readonly Dictionary<int, MachineBinding> m_machineBindings = new();
    private readonly List<int> m_machineBindingScratch = new();
    // Set on attach: the next pump-thread sync re-applies every cached binding into the (new) mixer.
    private bool m_machineBindingsDirty;
    // THE serialization gate (see the type remarks): reentrant, so Admit's SubmitTrigger nests under ReconcileSpeakers.
    private readonly Lock m_gate = new();
    // THE CUE TABLE, derived at reconcile: event token → its cue rows (gain in Q16, placement resolved to a
    // kind + optional speaker name). Cue patches are world patch rows, so the ordinary patch-set registration covers them.
    private readonly Dictionary<string, List<CueRow>> m_cueRows = new(comparer: StringComparer.Ordinal);
    // The live transient cue emitters (bounded by TransientCueCapacity; aged by the publish clock).
    private readonly List<TransientCue> m_transients = new(capacity: TransientCueCapacity);
    private WorldDefinition? m_definition;
    private WorldAudioMixer? m_mixer;
    private int m_nextEmitterId = 1;
    private ulong m_nextTriggerSequence;
    private ulong m_cueOrdinal;
    private int m_slabIndex;
    private FixedQ4816 m_defaultCueRadius = FixedQ4816.FromInteger(value: 8L);
    // The world.volume session lever (the render-levers asymmetry): null until touched — the document's
    // MasterGain then owns the live gain (reconcile follows it; the offline drivers stay purely document-driven);
    // once set, the lever owns "now" for the rest of the session and world.save folds it back into the document.
    private float? m_sessionMasterVolume;
    private FixedComplex m_lastListenerYaw = FixedComplex.MultiplicativeIdentity;

    /// <summary>Initializes the director over the client view and the animated-placement pool. Both are nullable so
    /// the OFFLINE driver (the audio-mix proof) runs the same derivation headlessly: without a client, entity-anchored
    /// emitters resolve absent (honest silence); without an animator, placements resolve through the static stamp math.</summary>
    /// <param name="client">The snapshot-fed entity view, or <see langword="null"/> headless.</param>
    /// <param name="animator">The animated-placement replay pool, or <see langword="null"/> headless.</param>
    public WorldAudioDirector(WorldClient? client, WorldStampPool? animator) {
        m_client = client;
        m_animator = animator;
        m_slabs = new WorldAudioSnapshot[SnapshotRotation];

        for (var index = 0; (index < SnapshotRotation); index++) {
            m_slabs[index] = new WorldAudioSnapshot();
        }
    }

    /// <summary>The LIVE master gain in Q16 — the value an attached mixer's <c>MasterGainQ16</c> follows: the
    /// document master gain until the <c>world.volume</c> session lever engages, the lever thereafter (see
    /// <see cref="SetMasterVolume"/>).</summary>
    public int MasterGainQ16 { get; private set; } = 65536;

    /// <summary>The derived emitter count (the plan's row count, before any capacity refusal).</summary>
    public int EmitterCount => m_plan.Count;

    /// <summary>Whether a mixer is currently attached (the device pump's live/silent echo).</summary>
    public bool MixerAttached => (m_mixer is not null);

    /// <summary>The machine-source resolver: screen slot → the live <see cref="IAudioMachine"/>, or
    /// <see langword="null"/> for an empty (or capability-less) slot. Wired once by the frame source to
    /// <see cref="WorldScreenBinder.AudioMachine"/>; invoked ONLY from <see cref="Publish"/> (the pump thread) —
    /// it reads pump-owned binder state. Null headless: machine-fed emitters then render honest silence.</summary>
    public Func<int, IAudioMachine?>? MachineSourceResolver { get; set; }

    /// <summary>Copies the latest published snapshot, when one exists (the raw consumer seam; the device pump uses
    /// <see cref="TryMixBlock"/>, which folds the snapshot read and the mix under the gate).</summary>
    /// <param name="snapshot">The latest snapshot.</param>
    public bool TrySnapshot(out WorldAudioSnapshot snapshot) => m_buffer.TrySnapshot(frame: out snapshot);

    /// <summary>Mixes one block from the latest published snapshot into the attached mixer — the device pump's
    /// per-quantum entry, callable from any thread. Returns <see langword="false"/> (leaving the span UNTOUCHED —
    /// the caller writes silence) while no mixer is attached or nothing has been published yet.</summary>
    /// <param name="stereoInterleaved">The output block; fully overwritten on <see langword="true"/>.</param>
    public bool TryMixBlock(Span<short> stereoInterleaved) {
        lock (m_gate) {
            if ((m_mixer is not { } mixer) || !m_buffer.TrySnapshot(frame: out var snapshot)) {
                return false;
            }

            mixer.MixBlock(snapshot: snapshot, stereoInterleaved: stereoInterleaved);

            return true;
        }
    }

    /// <summary>Attaches a mixer: registers the current patch set, sets its master gain, and activates tune
    /// acquire/release hosting (sources bind now and follow every reconcile until detached). Machine sources apply
    /// on the NEXT pump-thread publish (their resolver reads pump-owned binder state) — at most one frame of
    /// machine silence after an attach.</summary>
    /// <param name="mixer">The mixer to bind sources into.</param>
    public void AttachMixer(WorldAudioMixer mixer) {
        ArgumentNullException.ThrowIfNull(argument: mixer);

        lock (m_gate) {
            m_mixer = mixer;
            m_machineBindingsDirty = true;
            ApplyMixerBindings();
        }
    }

    /// <summary>Releases every hosted tune source, unbinds every machine source, and detaches the mixer.</summary>
    public void DetachMixer() {
        lock (m_gate) {
            foreach (var host in m_tuneHosts.Values) {
                m_mixer?.RemoveSource(key: WorldAudioSourceKey.Tune(id: host.TuneId));
                host.Source.Dispose();
            }

            m_tuneHosts.Clear();

            foreach (var slot in m_machineBindings.Keys) {
                m_mixer?.RemoveSource(key: WorldAudioSourceKey.Machine(slot: slot));
            }

            m_machineBindings.Clear();
            m_mixer = null;
        }
    }

    /// <summary>Reconciles the derived emitter table against a delivered definition — call at the delivery boundary
    /// AFTER <see cref="WorldScreenBinder.ReconcileScreens"/> (the chiasmus ordering: speakers consume screen slots)
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
                m_patchSet.Add(item: (Id: patch.Id, Patch: WorldVoicePatch.FromDocument(document: patch.Document)));
            }

            DeriveSpeakers(definition: definition, audio: audio);
            DeriveEmissionFacets(definition: definition, audio: audio);
            DeriveCreationSounds(definition: definition, audio: audio);
            RetireDepartedKeys();

            // The reserved transient pool is charged against the snapshot cap: the plan may only fill what cues never need.
            if (m_plan.Count > (WorldAudioSnapshot.DefaultMaxEmitters - TransientCueCapacity)) {
                Console.Error.WriteLine(value: $"[world.audio: {m_plan.Count} derived emitters exceed the {(WorldAudioSnapshot.DefaultMaxEmitters - TransientCueCapacity)}-row plan budget ({WorldAudioSnapshot.DefaultMaxEmitters}-row snapshot table minus the {TransientCueCapacity} reserved cue transients) — the overflow renders silent]");
            }

            // largechange-01: validate the WHOLE derived plan against the mixer's bounded registries at the compose
            // boundary — the patch set (per-emitter synth voices) and the distinct external-source identities the plan
            // taps — so an overfull registry is a loud, contained warn here rather than a silent drop the mixer only
            // discovers row-by-row at bind time.
            if (m_patchSet.Count > WorldAudioMixer.MaxPatches) {
                Console.Error.WriteLine(value: $"[world.audio: {m_patchSet.Count} derived synth patches exceed the {WorldAudioMixer.MaxPatches}-slot mixer patch table — the overflow renders silent]");
            }

            var distinctSources = CountDistinctExternalSources();

            if (distinctSources > WorldAudioMixer.MaxSources) {
                Console.Error.WriteLine(value: $"[world.audio: {distinctSources} derived machine/tune sources exceed the {WorldAudioMixer.MaxSources}-slot mixer source table — the overflow renders silent]");
            }

            ApplyMixerBindings();
        }
    }

    /// <summary>Resolves this frame's listener and emitter poses and publishes one snapshot from the slab rotation.
    /// Returns the published snapshot (the offline driver mixes it directly).</summary>
    /// <param name="transforms">The frame's packed dynamic transforms (empty headless — leaf anchors then resolve
    /// absent).</param>
    /// <param name="seats">The per-slot resolved view-camera poses (the listener policy's candidates).</param>
    /// <param name="deltaSeconds">The clock advance since the previous publish — ages the transient cue pool. The
    /// default is one sim step (the offline drivers publish once per mixed block); the live frame source passes its
    /// clamped presentation delta.</param>
    public WorldAudioSnapshot Publish(ReadOnlySpan<DynamicTransform> transforms, ReadOnlySpan<WorldSeatCameraPose> seats, float deltaSeconds = DefaultPublishDeltaSeconds) {
        lock (m_gate) {
            SyncMachineSources();

            var slab = m_slabs[m_slabIndex];

            m_slabIndex = ((m_slabIndex + 1) % SnapshotRotation);
            slab.Reset(listener: ResolveListener(seats: seats, transforms: transforms));
            // Transients FIRST — the reserved pool must land even when the derived plan overfills the table.
            PublishTransients(slab: slab, transforms: transforms, deltaSeconds: deltaSeconds);

            // The listener eye as float, for the per-row support check (a presentation ECHO fact, never mix math).
            var listenerEye = new Vector3(
                x: (slab.Listener.Position.X.Value / 65536f),
                y: (slab.Listener.Position.Y.Value / 65536f),
                z: (slab.Listener.Position.Z.Value / 65536f)
            );

            for (var index = 0; (index < m_plan.Count); index++) {
                var plan = m_plan[index];

                if (!TryResolvePosition(plan: plan, transforms: transforms, position: out var position)) {
                    // An unresolvable anchor is an absent emitter — honest silence, zero special cases.
                    plan.LastResolved = false;
                    plan.LastInSupport = false;
                    m_plan[index] = plan;

                    continue;
                }

                plan.LastResolved = true;
                plan.LastPosition = position;

                var maxRadius = (plan.MaxRadius.Value / 65536f);

                plan.LastInSupport = (Vector3.DistanceSquared(value1: position, value2: listenerEye) < (maxRadius * maxRadius));
                m_plan[index] = plan;
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
    }

    // Diff the binder's live machines against the cached bindings for every machine-fed plan row — the per-frame
    // reconcile/self-heal (called under the gate from Publish, the only resolver call site). Reference compares only
    // in steady state; a change rebinds the STABLE Machine(slot) key so the mixer's emitter ramps never notice a
    // swap. An attach marks the set dirty and this re-applies every cached binding into the new mixer.
    private void SyncMachineSources() {
        if ((MachineSourceResolver is not { } resolver) || (m_mixer is not { } mixer)) {
            return;
        }

        foreach (var plan in m_plan) {
            if (plan.Source.Kind != WorldAudioSourceKind.Machine) {
                continue;
            }

            var slot = plan.Source.Slot;
            var live = resolver(arg: slot);
            var bound = m_machineBindings.TryGetValue(key: slot, value: out var binding);

            if (live is null) {
                if (bound) {
                    mixer.RemoveSource(key: WorldAudioSourceKey.Machine(slot: slot));
                    _ = m_machineBindings.Remove(key: slot);
                }
            } else if (!bound || !ReferenceEquals(objA: binding.Machine, objB: live)) {
                var source = new MachineBlockSource(machine: live);

                m_machineBindings[slot] = new MachineBinding(Machine: live, Source: source);
                mixer.SetSource(key: WorldAudioSourceKey.Machine(slot: slot), source: source);
            } else if (m_machineBindingsDirty) {
                mixer.SetSource(key: WorldAudioSourceKey.Machine(slot: slot), source: binding.Source);
            }
        }

        m_machineBindingsDirty = false;

        // Retire bindings whose slot no longer feeds any plan row (an eject, or the speaker rows departed).
        foreach (var slot in m_machineBindings.Keys) {
            var referenced = false;

            foreach (var plan in m_plan) {
                if ((plan.Source.Kind == WorldAudioSourceKind.Machine) && (plan.Source.Slot == slot)) {
                    referenced = true;

                    break;
                }
            }

            if (!referenced) {
                m_machineBindingScratch.Add(item: slot);
            }
        }

        foreach (var slot in m_machineBindingScratch) {
            mixer.RemoveSource(key: WorldAudioSourceKey.Machine(slot: slot));
            _ = m_machineBindings.Remove(key: slot);
        }

        m_machineBindingScratch.Clear();
    }

    /// <summary>The deterministic <c>audio.emitters</c> listing: one segment per derived emitter — id, key, kind,
    /// source token, channel, gain, and radii — the document-derived STABLE facts (never live poses), so a piped
    /// proof asserts the derivation byte-for-byte.</summary>
    public string DescribeEmitters() {
        lock (m_gate) {
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
    }

    /// <summary>The <c>speaker.state</c> echo — the LIVE per-row status joining <c>audio.state</c>'s device facts:
    /// for every derived SPEAKER row its kind, source token, binding status (bound / silent-with-reason / faulted),
    /// the last published resolved position (or <c>unresolved</c> for an absent anchor), and whether the listener
    /// currently sits inside its finite support (<c>inMix</c>); then the live transient-cue tail (token + remaining
    /// life). Live facts move frame to frame — a proof asserts presence/shape, never exact poses.</summary>
    public string DescribeSpeakerState() {
        lock (m_gate) {
            var builder = new StringBuilder(value: "[speaker.state:");
            var wrote = false;

            foreach (var plan in m_plan) {
                if (!plan.Key.StartsWith(value: "speaker:", comparisonType: StringComparison.Ordinal)) {
                    continue;
                }

                var name = plan.Key["speaker:".Length..];
                var position = (plan.LastResolved
                    ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"({plan.LastPosition.X:0.0},{plan.LastPosition.Y:0.0},{plan.LastPosition.Z:0.0})")
                    : "unresolved");

                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"{(wrote ? " | " : " ")}{name} {((plan.Kind == WorldAudioEmitterKind.Bed) ? "bed" : "point")} {SourceToken(source: plan.Source)} {SourceStatus(source: plan.Source)} pos={position} inMix={((plan.LastResolved && plan.LastInSupport) ? "y" : "n")}");
                wrote = true;
            }

            if (!wrote) {
                _ = builder.Append(value: " none declared");
            }

            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" | cues {m_transients.Count}");

            foreach (var transient in m_transients) {
                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" cue:{transient.Token}={transient.PatchId}");
            }

            return builder.Append(value: ']').ToString();
        }
    }

    // One speaker row's live binding status: what its source identity resolves to RIGHT NOW (under the gate).
    private string SourceStatus(in WorldAudioSourceKey source) => source.Kind switch {
        WorldAudioSourceKind.Machine => (m_machineBindings.ContainsKey(key: source.Slot) ? "bound" : "silent(no-machine)"),
        WorldAudioSourceKind.Tune => ((source.Id is { } tuneId && m_tuneHosts.ContainsKey(key: tuneId))
            ? "bound"
            : ((m_mixer is null) ? "silent(no-device)" : "silent(no-tune)")),
        WorldAudioSourceKind.Synth => (((source.Id is { } patchId) && HasPatch(patchId: patchId)) ? "bound" : "faulted(no-patch)"),
        _ => "silent(no-source)",
    };

    private bool HasPatch(string patchId) {
        foreach (var (id, _) in m_patchSet) {
            if (string.Equals(a: id, b: patchId, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
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
                        MinRadius = FixedQ4816.FromDouble(value: bed.InnerRadius),
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
                    Anchor = EmitterAnchor.PlacementPoint(placementId: placement.Id, shapeId: sound.ShapeId, staticPosition: WorldAnchorGeometry.StaticShapePosition(placement: placement, creation: creation, shapeId: sound.ShapeId)),
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
    /// policy is just this seam's first caller; the cue producers (world-event cues, footstep derivation, screen
    /// lifecycle) feed the same sequence-stamped path.</summary>
    /// <param name="patchId">The registered patch the voice plays.</param>
    /// <param name="seed">The noise seed — the same seed reproduces the voice bit for bit.</param>
    /// <param name="gainQ16">The voice gain, Q16 (65536 = unity).</param>
    /// <param name="emitterId">The emitter the voice spatializes through.</param>
    public void SubmitTrigger(string patchId, ulong seed, int gainQ16, int emitterId) {
        lock (m_gate) {
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
    }

    // ---- the cue engine ----------------------------------------------------------------------------------------------

    /// <summary>Fires a world-event CUE — the producers' one entry (the edit-echo lane, the binder lifecycle, the
    /// gait derivation, the seat roster), gate-safe from any thread: every cue row bound to
    /// <paramref name="eventToken"/> allocates a short-lived transient point emitter (placed per the row) and one
    /// seeded trigger. The trigger and its transient land in the SAME next published snapshot, so the mixer's
    /// unbound-voice release can never race the voice's own emitter. An unknown or cue-less token is a no-op — cue
    /// coverage is world DATA, never engine policy.</summary>
    /// <param name="eventToken">The published event token (<see cref="WorldAudioCue.EventTokens"/>).</param>
    /// <param name="site">The event's world position, or <see langword="null"/> when none is derivable — an
    /// <c>at-site</c> row then falls back to the listener placement (documented on <see cref="WorldAudioCue"/>).</param>
    public void SubmitCue(string eventToken, Vector3? site) {
        lock (m_gate) {
            if (!m_cueRows.TryGetValue(key: eventToken, value: out var rows)) {
                return;
            }

            foreach (var row in rows) {
                var placement = (((row.Placement == CuePlacement.AtSite) && (site is null)) ? CuePlacement.Listener : row.Placement);
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
                SubmitTrigger(patchId: row.PatchId, seed: (Fnv64(text: eventToken) ^ ++m_cueOrdinal), gainQ16: 65536, emitterId: id);
            }
        }
    }

    /// <summary>The live transient-cue count (the <c>speaker.state</c> echo's cue meter).</summary>
    public int LiveCueCount {
        get {
            lock (m_gate) {
                return m_transients.Count;
            }
        }
    }

    /// <summary>The at-site position a mutation's cue can derive, or <see langword="null"/>: upserts carry their
    /// row's authored pose in the mutation payload; removals and section-wide edits have no single site (their cues
    /// fall back to the listener placement — honest, documented).</summary>
    /// <param name="mutation">The mutation the edit echo answered, or <see langword="null"/>.</param>
    public static Vector3? MutationSite(WorldMutation? mutation) => mutation switch {
        WorldMutation.UpsertSceneRow upsert => upsert.Row.Center,
        WorldMutation.UpsertScreen upsert => upsert.Screen.Origin,
        WorldMutation.UpsertPlacement upsert => upsert.Placement.Position,
        WorldMutation.UpsertSpeaker { Speaker: WorldSpeaker.Fixed fixedSpeaker } => fixedSpeaker.Position,
        WorldMutation.UpsertSpeaker { Speaker: WorldSpeaker.Bed bed } => bed.Center,
        WorldMutation.UpsertCamera upsert when (upsert.Camera.Anchor is null) => upsert.Camera.Offset,
        _ => null,
    };

    // Rebuild the event → cue-row table from the delivered Audio section (called under the gate from reconcile).
    private void BuildCueTable(WorldAudioDefaults audio) {
        m_cueRows.Clear();

        foreach (var cue in audio.Cues) {
            CuePlacement placement;
            string? speakerName = null;

            if (string.Equals(a: cue.Placement, b: WorldAudioCue.PlacementListener, comparisonType: StringComparison.Ordinal)) {
                placement = CuePlacement.Listener;
            } else if (cue.Placement.StartsWith(value: WorldAudioCue.PlacementEmitterPrefix, comparisonType: StringComparison.Ordinal)) {
                placement = CuePlacement.Emitter;
                speakerName = cue.Placement[WorldAudioCue.PlacementEmitterPrefix.Length..];
            } else {
                placement = CuePlacement.AtSite;
            }

            if (!m_cueRows.TryGetValue(key: cue.Event, value: out var rows)) {
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

    // A cue's life derives from its own patch envelope (data): a finite patch lives its duration + release plus one
    // sim step of slack; a looping patch takes the invariant cap (a cue is a transient by definition). A patch the
    // table no longer carries gets one step (its trigger would drop in the mixer anyway).
    private long CueLifeFrames(string patchId) {
        foreach (var (id, patch) in m_patchSet) {
            if (string.Equals(a: id, b: patchId, comparisonType: StringComparison.Ordinal)) {
                return ((patch.DurationFrames > 0)
                    ? (((long)patch.DurationFrames + patch.ReleaseFrames) + WorldAudioMixer.FramesPerSimStep)
                    : LoopingCueLifeFrames);
            }
        }

        return WorldAudioMixer.FramesPerSimStep;
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

    // Emit the live transient cue emitters into the slab (FIRST — the reserved pool always lands) and age them by
    // this publish's clock advance. Placement resolution per kind: at-site holds the event position; listener rides
    // the slab's already-resolved listener (distance 0 = full gain, and the mixer's on-top-of-listener pan hold
    // centers it); emitter follows the named speaker's live plan pose and support radius (falling back to the
    // listener while the speaker is absent).
    private void PublishTransients(WorldAudioSnapshot slab, ReadOnlySpan<DynamicTransform> transforms, float deltaSeconds) {
        if (m_transients.Count == 0) {
            return;
        }

        var elapsedFrames = ((long)MathF.Round(x: (MathF.Max(x: deltaSeconds, y: 0f) * WorldAudioMixer.SampleRate)));

        for (var index = (m_transients.Count - 1); index >= 0; index--) {
            var transient = m_transients[index];
            var position = slab.Listener.Position;
            var minRadius = FixedQ4816.Zero;
            var maxRadius = m_defaultCueRadius;

            switch (transient.Placement) {
                case CuePlacement.AtSite:
                    position = ToFixed(value: transient.Site);

                    break;
                case CuePlacement.Emitter:
                    if (TryFindSpeakerPlan(name: transient.SpeakerName, plan: out var speakerPlan) &&
                        TryResolvePosition(plan: in speakerPlan, transforms: transforms, position: out var resolved)) {
                        position = ToFixed(value: resolved);
                        minRadius = speakerPlan.MinRadius;
                        maxRadius = speakerPlan.MaxRadius;
                    }

                    break;
                case CuePlacement.Listener:
                default:
                    break;
            }

            _ = slab.TryAddEmitter(emitter: new WorldAudioEmitter(
                Id: transient.Id,
                Kind: WorldAudioEmitterKind.Point,
                Position: position,
                MinRadius: minRadius,
                MaxRadius: maxRadius,
                FadeFrames: 0,
                GainQ16: transient.GainQ16,
                Channel: WorldAudioChannel.Mix,
                Source: WorldAudioSourceKey.Synth(patchId: transient.PatchId)
            ));

            transient.RemainingFrames -= elapsedFrames;

            if (transient.RemainingFrames <= 0) {
                m_transients.RemoveAt(index: index);
            } else {
                m_transients[index] = transient;
            }
        }
    }

    private bool TryFindSpeakerPlan(string? name, out EmitterPlan plan) {
        if (name is not null) {
            var key = $"speaker:{name}";

            foreach (var candidate in m_plan) {
                if (string.Equals(a: candidate.Key, b: key, comparisonType: StringComparison.Ordinal)) {
                    plan = candidate;

                    return true;
                }
            }
        }

        plan = default;

        return false;
    }

    /// <summary>Resolves a speaker row's LIVE gizmo pose — Fixed/Bed directly, an anchored row through the same
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
                        Anchor = AnchorOf(anchor: anchored.Anchor, offset: anchored.Offset),
                    };

                    return TryResolvePosition(plan: in plan, transforms: transforms, position: out position);
                }
            }
            default:
                position = default;

                return false;
        }
    }

    // ---- the master-volume session lever --------------------------------------------------------------------------

    /// <summary>Engages the <c>world.volume</c> session lever: the live mix gain applies NOW and owns every later
    /// reconcile; the document's <see cref="WorldAudioDefaults.MasterGain"/> keeps owning boot, and
    /// <c>world.save</c> folds the lever back into it (the render-levers asymmetry). Until first engaged, the
    /// document value flows live (so the offline document-driven proofs and <c>world.audio.set</c>'s live master
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

    /// <summary>The live master volume — the session lever when engaged, else the document master gain. The
    /// <c>world.save</c> fold and the session-drift hint read this.</summary>
    public float EffectiveMasterVolume {
        get {
            lock (m_gate) {
                return (m_sessionMasterVolume ?? (m_definition?.Audio.MasterGain ?? 1f));
            }
        }
    }

    /// <summary>Whether the session lever has been engaged (the drift hint's cheap discriminator).</summary>
    public bool MasterVolumeLeverEngaged {
        get {
            lock (m_gate) {
                return m_sessionMasterVolume.HasValue;
            }
        }
    }

    // The distinct external (machine/tune) source identities the derived plan taps — the mixer binds one source slot
    // per identity, so this is the plan's real demand on the bounded source table (largechange-01 compose-boundary
    // validation). Synth-fed rows register a patch, not a source, so they do not count here.
    private int CountDistinctExternalSources() {
        var seen = new HashSet<WorldAudioSourceKey>();

        foreach (var plan in m_plan) {
            if (plan.Source.Kind is WorldAudioSourceKind.Machine or WorldAudioSourceKind.Tune) {
                _ = seen.Add(item: plan.Source);
            }
        }

        return seen.Count;
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
    // the tune HASH as the restart discriminator. No mixer attached = derivation only.
    private void ApplyMixerBindings() {
        if ((m_mixer is not { } mixer) || (m_definition is not { } definition)) {
            return;
        }

        mixer.MasterGainQ16 = MasterGainQ16;

        // largechange-01 reclaim: retire patch slots whose id left the derived plan BEFORE re-registering the live set,
        // so the bounded table is not filled by the carcasses of churned sound emitters across reconciles.
        var livePatchIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var (id, _) in m_patchSet) {
            _ = livePatchIds.Add(item: id);
        }

        mixer.RetirePatches(live: livePatchIds);

        foreach (var (id, patch) in m_patchSet) {
            mixer.RegisterPatch(id: id, patch: in patch);
        }

        // The referenced-tune set: acquire a headless host per tune some plan row taps; release orphans; a hash
        // change restarts the host honestly, the same release+recreate approach the placement animator uses.
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
                // Animated placements ride the stamp pool's current frame; an INHABITED placement rides its live body
                // pose (both through TryShapePosition); a static placement uses the reconcile-time stamp math.
                if ((m_animator is { } animator) && (m_client is { } client) && (anchor.PlacementId is { } placementId) && animator.TryShapePosition(placementId: placementId, shapeId: anchor.ShapeId, client: client, out var animated)) {
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

    // The listener policy: focus = the first joined seat's resolved view camera (the editor rig when that
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

            // An unanchored look-at camera listens from its world eye toward its target; other unanchored rigs have no
            // simple static listener pose (fall back to the listener placement).
            if (camera.Anchor is null) {
                return ((camera.Rig is WorldRig.LookAt look) ? (Eye: camera.Offset, Forward: (look.Target - camera.Offset)) : ((Vector3 Eye, Vector3 Forward)?)null);
            }

            var plan = new EmitterPlan {
                Anchor = AnchorOf(anchor: camera.Anchor, offset: camera.Offset),
            };

            if (TryResolvePosition(plan: in plan, transforms: transforms, position: out var eye) && (m_client is { } client) &&
                (camera.Anchor is WorldAnchor.Entity or WorldAnchor.EntityLeaf)) {
                var index = ((camera.Anchor is WorldAnchor.Entity entity) ? entity.Index : ((WorldAnchor.EntityLeaf)camera.Anchor).Index);

                // Avatar-local forward is -Z (the body convention every kit composition rides).
                return (Eye: eye, Forward: Vector3.Transform(value: new Vector3(x: 0f, y: 0f, z: -1f), rotation: client.Orientation(index: index)));
            }

            return null;
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

    // A static placement anchor's stamped position — the ONE shared resolver cameras and speakers both read (P9).
    private Vector3 StaticPlacementPosition(string placementId, int? shapeId) =>
        ((m_definition is { } definition) ? WorldAnchorGeometry.StaticPlacementPosition(definition: definition, placementId: placementId, shapeId: shapeId) : Vector3.Zero);

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

    // One derived emitter row — the document-derived stable facts Publish resolves a pose for each frame, plus the
    // last publish's LIVE status (the speaker.state echo: where the row resolved and whether the listener sits
    // inside its finite support).
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
        public Vector3 LastPosition;
        public bool LastResolved;
        public bool LastInSupport;
    }

    private readonly record struct EmitterIdentity(int Id, ulong Signature);

    private struct PendingTrigger {
        public WorldSynthTrigger Trigger;
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
