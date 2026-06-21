using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The composition graph's root node, authored polymorphically: the <c>$type</c> string selects which producer the
/// run builds (the SDF showcase, the compute world, or the ray-query world), and <c>produce</c> selects the backend
/// it renders on. This is the BACKEND-NEUTRAL description; turning it into a concrete <c>IRenderNode</c> (resolving GPU
/// services, applying OS/feature gates) is the GraphBuilder's job in Puck.Demo. Adding a node kind is a new derived
/// record. Validation-gate node kinds (parity/export/compute/reverse) and per-slot child graphs arrive in later phases.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ShowcaseNode), typeDiscriminator: "showcase")]
[JsonDerivedType(typeof(WorldNode), typeDiscriminator: "world")]
[JsonDerivedType(typeof(RtNode), typeDiscriminator: "rt")]
[JsonDerivedType(typeof(MiniActionNode), typeDiscriminator: "mini-action")]
public abstract record NodeDocument {
    /// <summary>The backend this node renders on: <c>"vulkan"</c> or <c>"directx"</c>. When null the builder picks the
    /// default for the node kind (the same default the equivalent flag used).</summary>
    public string? Produce { get; init; }
    /// <summary>An optional PNG path to capture this node's first rendered frame to. The CLI <c>--capture</c> overrides
    /// it when both are present.</summary>
    public string? Capture { get; init; }

    /// <summary>Whether <see cref="Produce"/> names a backend this node kind can render on.</summary>
    /// <param name="backend">The resolved backend name, lower-cased; meaningful only when the return is true.</param>
    /// <returns><see langword="true"/> when <see cref="Produce"/> is null or a recognized backend.</returns>
    public bool TryResolveProduce(out string backend) {
        backend = (Produce ?? DefaultBackend).ToLowerInvariant();

        return (string.Equals(backend, "vulkan", StringComparison.Ordinal) || string.Equals(backend, "directx", StringComparison.Ordinal));
    }

    /// <summary>The backend this node kind renders on when the document does not say.</summary>
    private protected abstract string DefaultBackend { get; }

    internal virtual void Validate(string path, int viewportCount, ValidationErrors errors) {
        if (!TryResolveProduce(backend: out _)) {
            errors.Add(path: $"{path}.produce", message: $"'{Produce}' is not a recognized backend; expected \"vulkan\" or \"directx\"");
        }
    }
}

/// <summary>The cross-backend SDF graphics showcase (a Vulkan-hosted window presenting a Direct3D 12- or
/// Vulkan-rendered SDF). It does not consume the document's scene/viewports — it renders its own built-in showcase.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ShowcaseNode : NodeDocument {
    private protected override string DefaultBackend => "directx";
}

/// <summary>The generic compute SDF world compositor, driven by the document's scene + viewports.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldNode : NodeDocument {
    /// <summary>Whether the bottom-right viewport hosts an animated child surface instead of an SDF camera (requires a
    /// four-viewport split layout).</summary>
    public bool Child { get; init; }

    private protected override string DefaultBackend => "directx";

    internal override void Validate(string path, int viewportCount, ValidationErrors errors) {
        base.Validate(errors: errors, path: path, viewportCount: viewportCount);

        if (Child && (viewportCount != 4)) {
            errors.Add(path: $"{path}.child", message: $"a child viewport requires a four-viewport split layout, but the document declares {viewportCount} viewport(s)");
        }
    }
}

/// <summary>The MiniAction action-game prototype: a controller-driven player avatar that runs around a room, rendered
/// by the compute SDF world path with a per-frame dynamic-entity transform. It builds its own dynamic scene + chase
/// camera each frame, so it consumes no document scene/viewports (like the showcase). It renders on the host device, so
/// <c>produce</c> is meaningless and rejected.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MiniActionNode : NodeDocument {
    private protected override string DefaultBackend => "vulkan";

    internal override void Validate(string path, int viewportCount, ValidationErrors errors) {
        if (Produce is not null) {
            errors.Add(path: $"{path}.produce", message: "the 'mini-action' node ignores 'produce' (it renders on the host device); use host.backend");
        }
    }
}

/// <summary>The ray-query world: a per-frame TLAS over the scene's primitives, ray-traced by an inline RayQuery
/// kernel rendering a single full-frame camera. Driven by the document's scene + the one viewport. The host device —
/// Vulkan, or Direct3D 12 DXR via <c>host.backend:"directx"</c> — is chosen by the host section, not by <c>produce</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RtNode : NodeDocument {
    private protected override string DefaultBackend => "vulkan";

    internal override void Validate(string path, int viewportCount, ValidationErrors errors) {
        // The rt node has no cross-backend producer: the ray-query kernel runs on whichever device host.backend selects.
        // So produce is meaningless here — reject any explicit value rather than silently ignoring it (the base produce
        // check is deliberately not called).
        if (Produce is not null) {
            errors.Add(path: $"{path}.produce", message: "the 'rt' node ignores 'produce' (the ray-query kernel runs on the host device); use host.backend to choose Vulkan or Direct3D 12");
        }

        // The ray-query kernel packs a single full-frame camera, so it can faithfully render exactly one viewport.
        if (viewportCount != 1) {
            errors.Add(path: path, message: $"the 'rt' node renders a single full-frame camera, but the document declares {viewportCount} viewport(s)");
        }
    }
}
