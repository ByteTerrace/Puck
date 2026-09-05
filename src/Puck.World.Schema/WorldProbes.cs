using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>One <c>probes</c> row: a registered probe kind reading a declared SOCKET set at a bounded rate, and the
/// bindings that route its channels. A socket is a named input slot the kind's manifest declares (e.g. <c>color</c>,
/// <c>strobe</c>); a row plugs one <see cref="WorldFrameSource"/> into each socket name it binds. Exactly one of
/// <see cref="Inputs"/>/<see cref="Track"/> is present — the live-hardware leg (named sockets) or the recorded-track
/// leg (one <c>puck.probe-track.v1</c> document standing in for every socket at once), never both, never neither. A
/// world declares no <c>probes</c> rows when it wants no camera-derived reading at all. Boot-authored only: no
/// <see cref="Protocol.WorldSection"/> dispatch axis carries a live mutation kind for it yet (see
/// <see cref="Protocol.WorldSection.Probes"/>), the same standing <c>music</c> carries.</summary>
/// <param name="Id">The probe's own name — <see cref="WorldSafeName"/>-shaped, unique among the rows; <c>probe.status</c>
/// and <c>probe.record</c> address it.</param>
/// <param name="Kind">The registered probe kind id (a <c>puck.probe.v1</c> manifest's file stem) — checked
/// against the shipped vocabulary at document load
/// (<see cref="WorldProbeVocabularyHook.IsRegisteredProbeKind"/>), never interpreted here. The document never
/// states where the kind runs; the kind's own registration decides kernel-on-device versus out-of-process
/// model.</param>
/// <param name="RateHz">The probe's cadence ceiling, 1..240 Hz — a rate its host may run slower than
/// (latest-wins throughout the pipeline), never faster.</param>
/// <param name="Inputs">The kind's sockets, by name, each bound to the frame source that fills it — the live-hardware
/// leg. The socket vocabulary itself lives behind the kind's manifest, unchecked here; the host checks a bound name
/// against it by name at boot, the same shallow-then-deep split every kind-vocabulary field follows. Mutually
/// exclusive with <see cref="Track"/>. Omitted from the wire when null.</param>
/// <param name="Track">A recorded <c>puck.probe-track.v1</c> document path, resolved against the world document's own
/// directory, played back in place of every socket at once — <c>probe.record</c>'s own output shape, the
/// hardware-free leg every probe admits. Mutually exclusive with <see cref="Inputs"/>. Omitted from the wire when
/// null.</param>
/// <param name="Config">The kind's config values, or <see langword="null"/> when the kind declares none or every
/// field has a default. Not validated at document load — the kind's own manifest config schema validates it at
/// boot, matching <see cref="WorldRenderExtensionEntry.Config"/>'s shallow-then-deep precedent.</param>
/// <param name="Bindings">What this probe's channels drive, or <see langword="null"/> for a probe that is only read back.</param>
public sealed record WorldProbe(
    string Id,
    string Kind,
    uint RateHz,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, WorldFrameSource>? Inputs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Track = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Config = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldProbeBinding>? Bindings = null
);
/// <summary>The presentation target a <see cref="WorldProbeBinding.Parameter"/> row writes. The <c>$type</c> string
/// is the JSON discriminator; a future target (a view-rig op, an overlay layer transform) widens this union with
/// another arm rather than adding parallel optional fields to the binding row.</summary>
[JsonDerivedType(typeof(WorldProbeParameterTarget.Extension), typeDiscriminator: "extension")]
[JsonDerivedType(typeof(WorldProbeParameterTarget.Probe), typeDiscriminator: "probe")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldProbeParameterTarget {
    private WorldProbeParameterTarget() {
    }

    /// <summary>Writes a shader-extension config field on a composed <c>render.extensions</c> entry.</summary>
    /// <param name="Id">The <c>render.extensions[].id</c> entry this targets — must name an entry the document
    /// itself composes.</param>
    /// <param name="Field">The extension's config field name — checked against its manifest at boot, never here
    /// (the same shallow-then-deep precedent every kind-vocabulary field follows).</param>
    public sealed record Extension(string Id, string Field) : WorldProbeParameterTarget;
    /// <summary>Writes a config field of another declared probe's kind, live, into its running kernel — one probe's
    /// reading steering another's computation (a tracked centroid placing a relighting kind's light).</summary>
    /// <param name="Id">The <c>probes[].id</c> this targets — a row of this document other than the binding's own.</param>
    /// <param name="Field">That probe's kind config field name — checked against its manifest at boot, never here.</param>
    public sealed record Probe(string Id, string Field) : WorldProbeParameterTarget;
}
/// <summary>One <c>probes[].bindings</c> row: routes one of the enclosing probe's channels to a command axis, a presentation
/// parameter, or a camera control. The <c>$type</c> string is the JSON discriminator.</summary>
[JsonDerivedType(typeof(WorldProbeBinding.Axis), typeDiscriminator: "axis")]
[JsonDerivedType(typeof(WorldProbeBinding.Parameter), typeDiscriminator: "parameter")]
[JsonDerivedType(typeof(WorldProbeBinding.Control), typeDiscriminator: "control")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldProbeBinding {
    private WorldProbeBinding() {
    }

    /// <summary>Conditions a channel into a per-tick command axis, sampled through <c>Puck.Commands</c> exactly like
    /// a stick axis: deadband with hysteresis, optional fixed-point EMA, quantization, and a declared
    /// <paramref name="MaxAgeSeconds"/> after which the axis returns to neutral and confidence to zero.</summary>
    /// <param name="Channel">The probe channel name — checked against the kind's manifest at boot, never here.</param>
    /// <param name="Source">The axis' own name — kebab-case, 1..64 characters, unique among every probe's axis
    /// bindings. Publishes the input source id <c>probe.&lt;Source&gt;</c>
    /// (<c>Puck.Input.InputSources.Probe.Axis</c>), an ordinary bindable Axis1D source any binding overlay may map
    /// like a stick.</param>
    /// <param name="Deadband">The deadband about the channel's neutral, in <c>[0, 1)</c> of the mapped
    /// <c>[-1, 1]</c> axis range.</param>
    /// <param name="Hysteresis">The hysteresis band width, in <c>[0, 1)</c>, applied at the deadband edge.</param>
    /// <param name="Smoothing">The fixed-point EMA smoothing factor, in <c>[0, 1]</c> — 0 disables smoothing.</param>
    /// <param name="QuantizeBits">The output quantization width, 1..16 bits.</param>
    /// <param name="MaxAgeSeconds">How long a reading stays live before the axis returns to neutral and
    /// confidence to zero. Must be finite and positive.</param>
    /// <param name="Seat">The 1-based local seat this axis is captured for. Forbidden (refused at load) on a
    /// seat-relative probe — a row instanced once per occupied seat because at least one of its camera sockets
    /// carries no <c>seat</c> of its own — whose axis bindings always take their own instance's seat; required
    /// (defaulting to seat 1) on a single-instance probe, exactly as before. <see langword="null"/> is the wire
    /// default. Omitted from the wire when null.</param>
    public sealed record Axis(
        string Channel,
        string Source,
        float Deadband = 0f,
        float Hysteresis = 0f,
        float Smoothing = 0f,
        int QuantizeBits = 8,
        float MaxAgeSeconds = 0.25f,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Seat = null
    ) : WorldProbeBinding;
    /// <summary>Writes a channel into a presentation-float shader-extension config field, lerped over
    /// <paramref name="Range"/>. Presentation-only — the destination never feeds simulation state.</summary>
    /// <param name="Channel">The probe channel name — checked against the kind's manifest at boot, never here.</param>
    /// <param name="Target">The presentation destination.</param>
    /// <param name="Range">The <c>[min, max]</c> presentation range a channel's mapped <c>[-1, 1]</c> axis value
    /// lerps across. Must be finite with <c>X &lt; Y</c>.</param>
    /// <param name="MaxAgeSeconds">How long a reading stays live before this binding stops writing. Must be
    /// finite and positive.</param>
    public sealed record Parameter(
        string Channel,
        WorldProbeParameterTarget Target,
        DocumentVector2 Range,
        float MaxAgeSeconds = 0.5f
    ) : WorldProbeBinding;
    /// <summary>Writes a channel onto the existing <see cref="WorldCameraControls"/> surface, mapped over
    /// <c>[Minimum, Maximum]</c>.</summary>
    /// <param name="Channel">The probe channel name — checked against the kind's manifest at boot, never here.</param>
    /// <param name="ControlName">The <see cref="WorldCameraControls"/> member this binding writes — one of
    /// <c>pan</c>, <c>tilt</c>, <c>zoom</c>, <c>exposure</c>, <c>focus</c>, <c>brightness</c>, <c>contrast</c>,
    /// <c>saturation</c>, <c>sharpness</c>, <c>gain</c>, <c>whiteBalance</c>, <c>backlightCompensation</c>, or
    /// <c>fieldOfView</c>.</param>
    /// <param name="Minimum">The control value a channel's mapped <c>-1</c> resolves to.</param>
    /// <param name="Maximum">The control value a channel's mapped <c>1</c> resolves to. Must exceed
    /// <paramref name="Minimum"/>.</param>
    /// <param name="MaxAgeSeconds">How long a reading stays live before this binding stops writing. Must be
    /// finite and positive.</param>
    public sealed record Control(
        string Channel,
        [property: JsonPropertyName("control")] string ControlName,
        int Minimum,
        int Maximum,
        float MaxAgeSeconds = 0.5f
    ) : WorldProbeBinding;
}
