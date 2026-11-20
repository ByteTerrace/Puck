using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The identity a keyed document section's rows are replaced and removed by: the key composition matches a
/// submitted row or a removal against, and the state reference that key reads when an author bound it.</summary>
/// <remarks>Composition (<c>WorldDocument.TryComposeCore</c>) keys every upsert and removal through one of these, and
/// the write door (<c>WorldRecipientMutations</c>) finds the installed row a write replaces through the same one, so
/// the two cannot key a section differently. A key bound to a state row (a creation's id, a look's name) is its live
/// value, which composition compares and the door never reads: it asks <see cref="ReferenceOf"/> instead, and treats
/// a row with a bound key as one any write to the section may target.</remarks>
/// <typeparam name="T">The row type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
/// <param name="keyOf">Reads a row's key: its resolved or live value.</param>
/// <param name="referenceOf">Reads the state reference a bound key reads, or <see langword="null"/> for a literal
/// key; omitted for a section whose keys are always literal.</param>
public sealed class WorldRowKey<T, TKey>(Func<T, TKey> keyOf, Func<T, string?>? referenceOf = null) {
    /// <summary>Reads a row's key as composition compares it.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The key.</returns>
    /// <exception cref="InvalidOperationException">The key is bound to a state cell that holds no value, or is an
    /// unresolved reference.</exception>
    public TKey Of(T row) => keyOf(row);
    /// <summary>Returns the state reference a row's key reads, or <see langword="null"/> when the key is literal.</summary>
    /// <param name="row">The row.</param>
    public string? ReferenceOf(T row) => referenceOf?.Invoke(row);
    /// <summary>Returns whether two keys are the same key, as composition compares them.</summary>
    /// <param name="left">One key.</param>
    /// <param name="right">The other.</param>
    public static bool Same(TKey left, TKey right) => EqualityComparer<TKey>.Default.Equals(left, right);
    /// <summary>Finds the first row under a key, as composition's upsert and removal match it.</summary>
    /// <param name="rows">The section's rows, or <see langword="null"/>.</param>
    /// <param name="key">The key.</param>
    /// <returns>The row, or the default value (<see langword="null"/> for a reference row) when none matches.</returns>
    /// <exception cref="InvalidOperationException">A row's key is bound to a state cell that holds no value, or is an
    /// unresolved reference.</exception>
    public T? Find(IReadOnlyList<T>? rows, TKey key) {
        if (rows is null) { return default; }
        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];
            if ((row is not null) && Same(keyOf(row), key)) { return row; }
        }
        return default;
    }
}

/// <summary>The one table of keyed document sections: each section's <see cref="WorldRowKey{T, TKey}"/>, which
/// composition and the write door both read.</summary>
public static class WorldRowKeys {
    /// <summary>Kits, by name.</summary>
    public static WorldRowKey<WorldKit, string> Kits { get; } = new(static kit => kit.Name);
    /// <summary>Machines, by name.</summary>
    public static WorldRowKey<WorldMachine, string> Machines { get; } = new(static machine => machine.Name);
    /// <summary>Screens, by their engine index: a screen has no name, and its index is not its list position.</summary>
    public static WorldRowKey<WorldScreen, int> Screens { get; } = new(static screen => screen.Index);
    /// <summary>Cameras, by name.</summary>
    public static WorldRowKey<WorldCamera, string> Cameras { get; } = new(static camera => camera.Name);
    /// <summary>Mounted addons, by name.</summary>
    public static WorldRowKey<WorldAddonRow, string> Addons { get; } = new(static addon => addon.Name);
    /// <summary>Creations, by the id's value, which a bound id reads from its state cell.</summary>
    public static WorldRowKey<WorldPrototype, string> Creations { get; } = new(static creation => creation.Id?.Value!, static creation => creation.Id?.Reference);
    /// <summary>Placements, by id.</summary>
    public static WorldRowKey<WorldPlacement, string> Placements { get; } = new(static placement => placement.Id);
    /// <summary>Speakers, by name.</summary>
    public static WorldRowKey<WorldSpeaker, string> Speakers { get; } = new(static speaker => speaker.Name);
    /// <summary>Tunes, by name.</summary>
    public static WorldRowKey<WorldTune, string> Tunes { get; } = new(static tune => tune.Name);
    /// <summary>Synth patches, by name.</summary>
    public static WorldRowKey<WorldPatch, string> Patches { get; } = new(static patch => patch.Name);
    /// <summary>Binding overlays, by id.</summary>
    public static WorldRowKey<WorldBindingOverlay, string> BindingOverlays { get; } = new(static overlay => overlay.Id);
    /// <summary>Looks, by the name's value, which a bound name reads from its state cell.</summary>
    public static WorldRowKey<WorldLook, string> Looks { get; } = new(static look => look.Name?.Value!, static look => look.Name?.Reference);
    /// <summary>Dynamics rows, by name.</summary>
    public static WorldRowKey<DynamicsRow, string> Dynamics { get; } = new(static row => row.Name);
    /// <summary>Curve rows, by name.</summary>
    public static WorldRowKey<WorldCurveRow, string> Curves { get; } = new(static row => row.Name);
    /// <summary>View layouts, by name.</summary>
    public static WorldRowKey<WorldViewLayout, string> ViewLayouts { get; } = new(static layout => layout.Name);
    /// <summary>View graph instances, by name.</summary>
    public static WorldRowKey<WorldViewGraph, string> ViewGraphs { get; } = new(static graph => graph.Name);
    /// <summary>HUD panels, by id.</summary>
    public static WorldRowKey<WorldHudPanel, string> HudPanels { get; } = new(static panel => panel.Id);
    /// <summary>HUD elements within their panel, by id.</summary>
    public static WorldRowKey<WorldHudElement, string> HudElements { get; } = new(static element => element.Id);
    /// <summary>State rows, by name.</summary>
    public static WorldRowKey<WorldStateRow, string> StateRows { get; } = new(static row => row.Name.Value);
    /// <summary>World rules, by name.</summary>
    public static WorldRowKey<WorldRule, string> Rules { get; } = new(static rule => rule.Name);
    /// <summary>Interactions, by name.</summary>
    public static WorldRowKey<WorldInteraction, string> Interactions { get; } = new(static row => row.Name);
    /// <summary>Grants, by grantee, capability and subject.</summary>
    public static WorldRowKey<WorldGrant, (Grantee Grantee, WorldCapability Capability, GrantSubject Subject)> Grants { get; } =
        new(static grant => (grant.Grantee, grant.Capability, grant.Subject));
    /// <summary>Group kinds, by name.</summary>
    public static WorldRowKey<WorldGroupKind, string> GroupKinds { get; } = new(static kind => kind.Name);
    /// <summary>Groups, by id.</summary>
    public static WorldRowKey<WorldGroup, string> Groups { get; } = new(static group => group.Id);
    /// <summary>Ownership rows, by subject.</summary>
    public static WorldRowKey<WorldOwnership, OwnershipSubject> Ownership { get; } = new(static row => row.Subject);
    /// <summary>Registered property names, by themselves.</summary>
    public static WorldRowKey<string, string> PropertyNames { get; } = new(static name => name);
}
