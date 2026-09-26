using System.Numerics;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>
/// The colors a signed-distance program or a screen decal bakes when it is built — a creation palette's surface,
/// bounce, weathering and inset colors, a height field's color, a text screen's ink — read through the state mirror,
/// the one path presentation reads state through. A <c>#RRGGBB</c> literal parses directly; a state binding reads the
/// color slot the document's presentation manifest registered for it (<see cref="WorldPresentationManifest"/>), so a
/// bound color resolves from the mirror's sample rather than from a second read of the document.
/// <para>
/// A builder calls <see cref="Begin"/> when it starts a build, resolves every color through <see cref="Resolve"/>,
/// and asks <see cref="TryTakeMove"/> each frame whether a bound color it baked has moved since, which it answers once
/// per move. Bound colors are Text cells, which never ease, so a move is a write, and the builder rebuilds to follow
/// it. A check with no color slot moved compares two counters and allocates nothing.
/// </para>
/// </summary>
public sealed class WorldBakedColors {
    // The document a mirror is installed over on the first bound color, for colors read on their own (Of).
    private readonly WorldDefinition? m_definition;

    private int m_checkedColorRevision;
    private int m_checkedGeneration;
    private int m_count;
    private WorldStateMirror? m_mirror;
    private string[] m_tokens = [];
    private int[] m_changed = [];

    /// <summary>Initializes a new instance of the <see cref="WorldBakedColors"/> class over a state mirror.</summary>
    /// <param name="mirror">The mirror every bound color reads through; its installed document's manifest registers the
    /// colors.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> is <see langword="null"/>.</exception>
    public WorldBakedColors(WorldStateMirror mirror) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        m_mirror = mirror;
    }

    private WorldBakedColors(WorldDefinition definition) => m_definition = definition;

    /// <summary>Gets whether a mirror backs these colors: always for colors over a client's mirror, and for a
    /// document's own colors (<see cref="Of"/>) only once a bound color has been resolved.</summary>
    public bool IsMirrored => (m_mirror is not null);

    /// <summary>Returns the colors of a document read on its own: what a tool, a law, or a build from a document with no
    /// client of its own resolves its colors through. A literal parses directly; the first bound color installs a
    /// mirror over the document, compiling its manifest and reading every slot once, so a build that bakes only
    /// literals builds no mirror.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The colors, read through a mirror of <paramref name="definition"/> once one is needed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldBakedColors Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new WorldBakedColors(definition: definition);
    }
    /// <summary>Starts a build: forgets the colors the previous build baked.</summary>
    public void Begin() {
        m_count = 0;
        m_checkedColorRevision = (m_mirror?.ColorRevision ?? 0);
        m_checkedGeneration = (m_mirror?.Generation ?? 0);
    }
    /// <summary>Resolves one authored color for the build under way and remembers a bound one.</summary>
    /// <param name="value">The authored color: a <c>#RRGGBB</c> literal, a <c>state.&lt;row&gt;[.&lt;key&gt;]</c>
    /// binding naming a Text cell, or <see langword="null"/>.</param>
    /// <param name="fallback">The color when <paramref name="value"/> is neither, the manifest registers no slot for the
    /// binding, or the cell holds no color.</param>
    /// <returns>The color, alpha dropped.</returns>
    public Vector3 Resolve(string? value, Vector3 fallback) {
        if (!StateBinding.TryParse(
            binding: out _,
            token: value
        )) {
            return HexColor.Parse(
                fallback: fallback,
                value: value
            );
        }

        var mirror = Mirror();
        var slot = mirror.SlotOf(
            conversion: WorldStateConversion.Color,
            token: value
        );

        Remember(
            changed: mirror.Changed(slot: slot),
            token: value!
        );

        return (mirror.TryColor(
            slot: slot,
            value: out var color
        )
            ? new Vector3(
                x: color.X,
                y: color.Y,
                z: color.Z
            )
            : fallback
        );
    }
    /// <summary>Returns whether a bound color the last build baked has moved since it was resolved, or has lost or
    /// gained its slot, answering each move once.</summary>
    /// <returns><see langword="true"/> when the builder must rebuild to follow a bound color.</returns>
    public bool TryTakeMove() {
        if (
            (m_count == 0) ||
            (m_mirror is not { } mirror) ||
            ((mirror.ColorRevision == m_checkedColorRevision) && (mirror.Generation == m_checkedGeneration))
        ) {
            return false;
        }

        m_checkedColorRevision = mirror.ColorRevision;
        m_checkedGeneration = mirror.Generation;

        var moved = false;

        for (var index = 0; (index < m_count); index++) {
            var changed = mirror.Changed(slot: mirror.SlotOf(
                conversion: WorldStateConversion.Color,
                token: m_tokens[index]
            ));

            if (changed != m_changed[index]) {
                m_changed[index] = changed;
                moved = true;
            }
        }

        return moved;
    }

    // The mirror a bound color reads through, installed over the document on first need for colors read on their own.
    private WorldStateMirror Mirror() {
        if (m_mirror is { } mirror) {
            return mirror;
        }

        var definition = m_definition!;

        mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        m_mirror = mirror;
        m_checkedColorRevision = mirror.ColorRevision;
        m_checkedGeneration = mirror.Generation;

        return mirror;
    }
    private void Remember(string token, int changed) {
        if (m_count == m_tokens.Length) {
            var capacity = Math.Max(
                val1: 8,
                val2: (m_tokens.Length * 2)
            );

            Array.Resize(
                array: ref m_tokens,
                newSize: capacity
            );
            Array.Resize(
                array: ref m_changed,
                newSize: capacity
            );
        }

        m_tokens[m_count] = token;
        m_changed[m_count] = changed;
        m_count++;
    }
}
