using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The single versioned document that describes an entire Puck run. The top-level <see cref="Version"/> is an
/// enforced discriminator gated before anything else binds; only the root carries <see cref="Extensions"/> (additive
/// forward-compat), while every leaf record is strict (an unknown field is a hard error). The document carries the
/// optional <see cref="Host"/> section (window/launcher/backend) plus the <see cref="Scene"/>, <see cref="Viewports"/>,
/// and composition <see cref="Graph"/>; validation/fuzzing sections arrive in later phases as additive fields.
/// </summary>
public sealed class PuckRunDocument {
    /// <summary>The only document version this build accepts.</summary>
    public const string CurrentVersion = "puck.run.v1";

    /// <summary>The document version; must equal <see cref="CurrentVersion"/>.</summary>
    public string Version { get; init; } = "";
    /// <summary>How the run is presented (window/launcher/backend); optional, defaults apply when omitted.</summary>
    public HostDocument? Host { get; init; }
    /// <summary>The scene: materials + placed objects, compiled to the GPU program.</summary>
    public SceneDocument Scene { get; init; } = new();
    /// <summary>The viewports: per-region cameras over the scene (1..5).</summary>
    public IReadOnlyList<Viewport> Viewports { get; init; } = [];
    /// <summary>The optional diegetic screen-source table: which provider feeds each sampled screen surface the
    /// scene's screen slabs declare (see <see cref="ScreenSlabObject.ScreenIndex"/>). Consumed by the world graph;
    /// a declared surface with no entry falls back to the flat/procedural screen material.</summary>
    public IReadOnlyList<ScreenSourceDocument>? ScreenSources { get; init; }
    /// <summary>The optional WASM addon table: content-addressed modules the sim-tick host instantiates and drives
    /// with a fixed-point snapshot each tick, folding their returned virtual-pad commands into a roster slot's
    /// input. Consumed only by a <see cref="Graph"/> run; meaningless (and rejected) under <see cref="Validation"/>
    /// or <see cref="Fuzzing"/>.</summary>
    public IReadOnlyList<AddonDocument>? Addons { get; init; }
    /// <summary>The composition graph's root node. Ignored (and optional) when <see cref="Validation"/> is present.</summary>
    public NodeDocument? Graph { get; init; }
    /// <summary>The optional input section: controller→consumer routing policy.</summary>
    public InputDocument? Input { get; init; }
    /// <summary>The optional validation gate: when present the run validates (installs a cross-backend gate and
    /// propagates its exit code) instead of presenting a live render.</summary>
    public ValidationDocument? Validation { get; init; }
    /// <summary>The optional fuzzing section: when present the run differential-fuzzes the SDF VM (a generated scene
    /// diffed across backends) instead of presenting a live render or validating the document's own scene.</summary>
    public FuzzingDocument? Fuzzing { get; init; }
    /// <summary>Unknown top-level members, captured for additive forward-compatibility (root only). A settable
    /// (not <c>init</c>) accessor is required: System.Text.Json appends to it during deserialization.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}
