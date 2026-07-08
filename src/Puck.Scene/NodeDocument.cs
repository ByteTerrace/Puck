using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The composition graph's root node, authored polymorphically: the <c>$type</c> string selects which producer the
/// run builds (the compute world compositor, or the overworld), and <c>produce</c> selects the backend
/// it renders on. This is the BACKEND-NEUTRAL description; turning it into a concrete <c>IRenderNode</c> (resolving GPU
/// services, applying OS/feature gates) is the GraphBuilder's job in Puck.Demo. Adding a node kind is a new derived
/// record. (The retired <c>showcase</c>/<c>rt</c>/<c>camera</c> kinds were removed with the demo's slim-down to the
/// four-quad world: a live camera is now a per-viewport <see cref="LiveCameraSource"/>, and the ray-query path's
/// coverage lives in Puck.Post's RT stage.)
/// </summary>
[JsonDerivedType(typeof(WorldNode), typeDiscriminator: "world")]
[JsonDerivedType(typeof(OverworldNode), typeDiscriminator: "overworld")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
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

/// <summary>The generic compute SDF world compositor, driven by the document's scene + viewports.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldNode : NodeDocument {
    /// <summary>RETIRED compatibility shim (the pre-purification animated test pane): still parses so old documents
    /// stay loadable, but the VALIDATOR rejects <see langword="true"/> with a pointer at the replacement — a child
    /// pane is a per-viewport SOURCE now (<see cref="GamingBrickSource"/>/<see cref="LiveCameraSource"/>), not a node
    /// boolean. Retired-shape rejection is validation's job (host-independent, never valid); the builder pre-flight
    /// owns only capability gaps (deferred/host-dependent paths).</summary>
    public bool Child { get; init; }

    private protected override string DefaultBackend => "directx";

    internal override void Validate(string path, int viewportCount, ValidationErrors errors) {
        base.Validate(errors: errors, path: path, viewportCount: viewportCount);

        if (Child) {
            errors.Add(path: $"{path}.child", message: "child is a retired affordance (the pre-purification animated test pane); declare the pane as a viewport source instead (e.g. \"source\": { \"$type\": \"gaming-brick\", ... })");
        }
    }
}

/// <summary>The overworld: a controller-driven player avatar that runs around a room, rendered by the compute SDF world
/// path with a per-frame dynamic-entity transform, with up to four console stands the player boots — each boot lights
/// a GamingBrick pane and the screen layout walks its staged transition. It builds its own dynamic scene + views each
/// frame, so it consumes no document scene/viewports (the console list here replaces per-viewport brick sources). It
/// renders on the host device, so <c>produce</c> is meaningless and rejected.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OverworldNode : NodeDocument {
    /// <summary>The most console stands the room seats — the compositor's five view slots minus the room view.</summary>
    public const int MaxConsoles = 4;

    /// <summary>The bootable consoles, in stand order (left to right along the far wall). Each entry reuses the
    /// gaming-brick source shape (console costume + fit + an OPTIONAL pre-inserted ROM path); a console with a ROM
    /// path assembles its machine at build time, while a console with a null ROM path starts as an
    /// EMPTY stand — its machine assembles once the player inserts a cartridge from <see cref="Library"/>. Empty (the
    /// default) is the bare room. A later revision grows an entry with a peripheral field (e.g. the PC camera as a
    /// GB-camera cartridge feed).</summary>
    public IReadOnlyList<GamingBrickSource> Consoles { get; init; } = [];
    // NOTE: nullable-optional-field pattern (see the note in GamingBrickSource) — a polymorphic-derived record
    // deserialized through the run-document parse path skips property initializers, so an omitted member arrives
    // NULL regardless of the initializer below.
    /// <summary>The cartridge shelf: games the player can carry to an empty stand and insert. Null (the default,
    /// matching every document authored before this field existed) is no shelf at all — every console must then be
    /// pre-inserted (<see cref="GamingBrickSource.RomPath"/> set). A document with ANY empty stand must declare a
    /// non-null, non-empty library, or that stand is unreachable dead weight.</summary>
    public IReadOnlyList<CartridgeSource>? Library { get; init; }
    /// <summary>IMMERSED start (the fourth-wall boot, null = false): the run opens INSIDE the machines — each
    /// connecting player is seated at (boots and takes over) their own stand and sees only the game panes; the room
    /// is revealed when a console's <see cref="GamingBrickSource.Exit"/> condition fires (all active players then
    /// stand at their stands, the games continuing on the diegetic screens). Requires every console pre-inserted
    /// (there is no walkable room to fetch a cartridge from until the reveal).</summary>
    public bool? Immersed { get; init; }
    // NOTE: nullable-optional-field pattern (see the note in GamingBrickSource) — a polymorphic-derived record
    // deserialized through the run-document parse path skips property initializers, so an omitted member arrives
    // NULL regardless of any initializer; validate/normalize only when present.
    /// <summary>A saved <c>puck.world.v1</c> world handle (resolved under <c>./worlds/</c>, or a direct path, plus the
    /// CAS store) the overworld LOADS and COMMITS at boot, so the room the player stands in — and the room the
    /// immersed fourth-wall reveal eases them out INTO — is that sculpted world (e.g. the town, <c>"puckton"</c>)
    /// rather than the bare default room. Null (the default, and every document authored before this field existed)
    /// leaves the plain room, so the default demo is byte-unchanged. Loading is GRACEFUL: an unreadable handle or a
    /// world whose creations are missing from the store narrates to stderr and leaves the plain room rather than
    /// crashing the boot (parse validation never checks file existence — the boot-load does). This is the run-document
    /// home of the former <c>PUCK_OVERWORLD_WORLD</c> env var; the live mid-session path is the <c>world.load</c>
    /// console verb.</summary>
    public string? World { get; init; }
    // NOTE: nullable-optional-field pattern (see the note in GamingBrickSource) — a polymorphic-derived record
    // deserialized through the run-document parse path skips property initializers, so an omitted member arrives
    // NULL regardless of any initializer; ANY long value is a valid cell, so this is validated only for PRESENCE (there
    // is nothing to range-check), and normalized at consumption (null → cell 0, the origin).
    /// <summary>The FAR spawn cell (applied to BOTH the X and Z axes) the whole room is placed at — the planet-scale
    /// coordinate-stability demonstration: the simulation is cell-agnostic (identical per-tick local motion at any cell)
    /// and the floating-origin render seam keeps it crisp arbitrarily far from the origin. Null (the default, and every
    /// document authored before this field existed) is the origin cell (0), so the default demo is byte-unchanged. This
    /// is the run-document home of the former <c>PUCK_OVERWORLD_CELL</c> env var.</summary>
    public long? Cell { get; init; }

    private protected override string DefaultBackend => "vulkan";

    internal override void Validate(string path, int viewportCount, ValidationErrors errors) {
        // The nullable-optional-field pattern (see the Library note): an omitted `consoles` arrives NULL through the
        // polymorphic parse path despite the initializer, so validation normalizes before touching it.
        var consoles = (Consoles ?? []);

        if (Produce is not null) {
            errors.Add(path: $"{path}.produce", message: "the 'overworld' node ignores 'produce' (it renders on the host device); use host.backend");
        }

        if (consoles.Count > MaxConsoles) {
            errors.Add(path: $"{path}.consoles", message: $"the overworld room seats at most {MaxConsoles} consoles, but the document declares {consoles.Count}");
        }

        for (var index = 0; (index < consoles.Count); index++) {
            consoles[index].Validate(path: $"{path}.consoles[{index}]", errors: errors);
        }

        ValidateMetaVictoryGroups(consoles: consoles, errors: errors, path: path);

        if (Library is not null) {
            if (Library.Count > CartridgeSource.MaxEntries) {
                errors.Add(path: $"{path}.library", message: $"the shelf holds at most {CartridgeSource.MaxEntries} cartridges, but the document declares {Library.Count}");
            }

            for (var index = 0; (index < Library.Count); index++) {
                Library[index].Validate(path: $"{path}.library[{index}]", errors: errors);
            }
        }

        // NOTE: an empty stand is fed by the cabinet's cart-cycle (Right bumper among the built-in cart types:
        // world-lens, camera, showcase) or by a built-in immersed cart, NOT by a carried library cartridge. An empty
        // stand needs no library entry.

        if ((Immersed == true) && (consoles.Count == 0)) {
            errors.Add(path: $"{path}.immersed", message: "an immersed start needs at least one console to seat the first player at");
        }
        // An immersed console may now start EMPTY: a built-in immersed cart (the world-lens) or the cabinet's own
        // cart-cycle feeds it, so no pre-inserted romPath is required.

        // The world handle is validated only when PRESENT (the nullable-optional-field pattern): an empty/whitespace
        // string is a malformed handle. A non-empty handle is NOT checked for existence here — parse validation never
        // touches the filesystem; the boot-load resolves it under ./worlds/ + the CAS store and narrates-and-skips a
        // bad handle rather than crashing (see OverworldFrameSource.LoadBootWorld).
        if ((World is not null) && string.IsNullOrWhiteSpace(value: World)) {
            errors.Add(path: $"{path}.world", message: "the world handle is empty; omit the field to load no world, or name a saved world (resolved under ./worlds/ or a direct path)");
        }
    }

    // Cross-cabinet META validation: cabinets sharing a group must (a) agree on the target, (b) number at least two, and
    // (c) have shares whose XOR equals that target — the structural "no cabinet wins alone" guarantee. Checked here,
    // where the whole console set is visible; a single source's own Validate can never see its peers. Solo victories and
    // per-record shape (mode/target/share well-formedness) are already gated by GamingBrickSource.Validate.
    private static void ValidateMetaVictoryGroups(IReadOnlyList<GamingBrickSource> consoles, string path, ValidationErrors errors) {
        var groups = new Dictionary<string, List<(int Index, BrickVictoryCondition Victory)>>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < consoles.Count); index++) {
            if (consoles[index].Victory is { IsMeta: true } victory) {
                var key = (victory.Group ?? "");

                if (!groups.TryGetValue(key: key, value: out var members)) {
                    members = [];
                    groups[key] = members;
                }

                members.Add(item: (index, victory));
            }
        }

        foreach (var (group, members) in groups) {
            var groupLabel = ((group.Length == 0) ? "(default)" : group);
            var target = members[0].Victory.Target;

            if (members.Count < 2) {
                errors.Add(path: $"{path}.consoles[{members[0].Index}].victory", message: $"meta group '{groupLabel}' has only one cabinet; a meta victory needs at least two so no cabinet can win alone");

                continue;
            }

            var accumulator = new byte[VictoryGate.RegionByteCount];
            var allSharesParsed = true;

            foreach (var (memberIndex, victory) in members) {
                if (!string.Equals(a: victory.Target, b: target, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    errors.Add(path: $"{path}.consoles[{memberIndex}].victory.target", message: $"meta group '{groupLabel}' cabinets must all name the same target; found '{victory.Target}' and '{target}'");
                }

                var share = new byte[VictoryGate.RegionByteCount];

                if (victory.TryParseShare(destination: share)) {
                    VictoryGate.Xor(accumulator: accumulator, operand: share);
                }
                else {
                    allSharesParsed = false;
                }
            }

            var targetBytes = new byte[VictoryGate.RegionByteCount];

            if (allSharesParsed && VictoryGate.TryParseGuidBytes(text: target, destination: targetBytes) && !VictoryGate.RegionEquals(region: accumulator, target: targetBytes)) {
                errors.Add(path: $"{path}.consoles[{members[0].Index}].victory", message: $"meta group '{groupLabel}' shares do not XOR to the target — the cooperative win is unreachable (author the last share as the target XORed with the others)");
            }
        }
    }
}

