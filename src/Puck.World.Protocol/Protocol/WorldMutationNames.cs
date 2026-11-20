using System.Reflection;
using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>Reads every name a mutation spells, wherever it spells it: a member of the document model (a placement,
/// a row declaration, a transform, a HUD element) is read through the name registry's grammar
/// (<see cref="WorldSpelledNames.Collect"/>), and a member of the mutation vocabulary itself (a cell write's row, a
/// removal's id, a guard's rows) through its entry in <see cref="Fields"/>. The write door's footprint is these names,
/// so a name position the registry or this table learns reaches the door without a list of its own.</summary>
/// <remarks>Every member of every mutation kind, and of the guard and expectation records a batch carries, is a
/// document model member, a nested mutation, a record walked member by member, a value that carries no name, an
/// entry of <see cref="Fields"/> or an entry of <see cref="Exclusions"/>. <see cref="Uncovered"/> names any string or
/// <see cref="CellName"/> member that is none of these, which the write door's laws refuse.</remarks>
public static class WorldMutationNames {
    /// <summary>The mutation vocabulary's own name-bearing members, each with the namespace it names and how it
    /// carries the name.</summary>
    public static IReadOnlyList<WorldNameField> Fields { get; } = [
        new(typeof(WorldMutation.UpsertStateCell), nameof(WorldMutation.UpsertStateCell.Row), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldMutation.RemoveStateCell), nameof(WorldMutation.RemoveStateCell.Row), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldMutation.RemoveStateRow), nameof(WorldMutation.RemoveStateRow.Name), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldMutation.Generate), nameof(WorldMutation.Generate.Row), WorldNameKind.State, WorldNameRole.Names),
        // A property's per-carrier tags are stored in the keyed state row of its own name, which validation reads.
        new(typeof(WorldMutation.SetProperty), nameof(WorldMutation.SetProperty.Name), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldMutation.RemoveMachine), nameof(WorldMutation.RemoveMachine.Name), WorldNameKind.Machine, WorldNameRole.Names),
        new(typeof(WorldMutation.RemovePlacement), nameof(WorldMutation.RemovePlacement.Id), WorldNameKind.Placement, WorldNameRole.Names),
        new(typeof(WorldMutation.RemoveCreation), nameof(WorldMutation.RemoveCreation.Id), WorldNameKind.Prototype, WorldNameRole.Names),
        new(typeof(WorldMutation.RemoveWorldRule), nameof(WorldMutation.RemoveWorldRule.Name), WorldNameKind.Rule, WorldNameRole.Names),
        new(typeof(WorldMutation.RemoveDynamics), nameof(WorldMutation.RemoveDynamics.Name), WorldNameKind.Dynamics, WorldNameRole.Names),
        new(typeof(WorldMutation.Batch), nameof(WorldMutation.Batch.ExpectedStateRows), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldStateExpectation), nameof(WorldStateExpectation.Row), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldDefinitionReadDependency), nameof(WorldDefinitionReadDependency.StateRows), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldDefinitionReadDependency), nameof(WorldDefinitionReadDependency.PlacementIds), WorldNameKind.Placement, WorldNameRole.Names),
        new(typeof(PhaseGuard), nameof(PhaseGuard.Row), WorldNameKind.State, WorldNameRole.Names),
    ];

    /// <summary>The mutation vocabulary's string members that carry no name a declaration of the document resolves,
    /// each with the reason.</summary>
    public static IReadOnlyList<WorldNameExclusion> Exclusions { get; } = [
        new(typeof(WorldMutation.UpsertStateCell), nameof(WorldMutation.UpsertStateCell.Key), "a cell key inside the row the write names"),
        new(typeof(WorldMutation.UpsertStateCell), nameof(WorldMutation.UpsertStateCell.Text), "a cell value"),
        new(typeof(WorldMutation.UpsertStateCell), nameof(WorldMutation.UpsertStateCell.RawToken), "a cell value as typed"),
        new(typeof(WorldMutation.UpsertStateCell), nameof(WorldMutation.UpsertStateCell.CycleTokens), "the values a cycling write steps through"),
        new(typeof(WorldMutation.UpsertStateCell), nameof(WorldMutation.UpsertStateCell.CommandTail), "the raw operand compose reads against the row the write names"),
        new(typeof(WorldMutation.RemoveStateCell), nameof(WorldMutation.RemoveStateCell.Key), "a cell key inside the row the removal names"),
        new(typeof(WorldMutation.Generate), nameof(WorldMutation.Generate.Keys), "cell keys inside the row the draw names"),
        new(typeof(WorldMutation.Batch), nameof(WorldMutation.Batch.ExpectedDefinition), "a document fingerprint"),
        new(typeof(WorldStateExpectation), nameof(WorldStateExpectation.Key), "a cell key inside the row the guard names"),
        new(typeof(WorldStateExpectation), nameof(WorldStateExpectation.Text), "a cell value"),
        new(typeof(WorldDefinitionReadDependency), nameof(WorldDefinitionReadDependency.Fingerprint), "a document fingerprint"),
        new(typeof(WorldSpatialReadDependency), nameof(WorldSpatialReadDependency.Fingerprint), "a census fingerprint"),
        new(typeof(PhaseGuard), nameof(PhaseGuard.Participant), "a principal token"),
        new(typeof(WorldMutation.RemoveKit), nameof(WorldMutation.RemoveKit.Name), "a kit's name, which no registered name kind holds; the write door reads the installed kit"),
        new(typeof(WorldMutation.SetDefaultSeatKit), nameof(WorldMutation.SetDefaultSeatKit.Name), "a kit's name; the kit's actions run as the world"),
        new(typeof(WorldMutation.RemoveCamera), nameof(WorldMutation.RemoveCamera.Name), "a camera's name; the write door reads the installed camera"),
        new(typeof(WorldMutation.RemoveAddon), nameof(WorldMutation.RemoveAddon.Name), "an addon's name; the write door reads the installed addon"),
        new(typeof(WorldMutation.RemoveBindingOverlay), nameof(WorldMutation.RemoveBindingOverlay.Id), "an overlay's id; the write door reads the installed overlay"),
        new(typeof(WorldMutation.RemoveSpeaker), nameof(WorldMutation.RemoveSpeaker.Name), "a speaker's name; the write door reads the installed speaker"),
        new(typeof(WorldMutation.RemoveTune), nameof(WorldMutation.RemoveTune.Name), "a tune's name; the write door reads the installed tune"),
        new(typeof(WorldMutation.RemovePatch), nameof(WorldMutation.RemovePatch.Name), "a patch's name; the write door reads the installed patch"),
        new(typeof(WorldMutation.RemoveViewLayout), nameof(WorldMutation.RemoveViewLayout.Name), "a view layout's name; the write door reads the installed layout"),
        new(typeof(WorldMutation.RemoveLook), nameof(WorldMutation.RemoveLook.Name), "a look's name; the write door reads the installed look"),
        new(typeof(WorldMutation.RemoveHudPanel), nameof(WorldMutation.RemoveHudPanel.Id), "a HUD panel's id; the write door reads the installed panel"),
        new(typeof(WorldMutation.UpsertHudElement), nameof(WorldMutation.UpsertHudElement.PanelId), "a HUD panel's id; the write door reads the installed element"),
        new(typeof(WorldMutation.RemoveHudElement), nameof(WorldMutation.RemoveHudElement.PanelId), "a HUD panel's id; the write door reads the installed element"),
        new(typeof(WorldMutation.RemoveHudElement), nameof(WorldMutation.RemoveHudElement.ElementId), "a HUD element's id; the write door reads the installed element"),
        new(typeof(WorldMutation.RemoveInteraction), nameof(WorldMutation.RemoveInteraction.Name), "an interaction's name; a recipient's program edit is refused by its section"),
        new(typeof(WorldMutation.RemoveGroupKind), nameof(WorldMutation.RemoveGroupKind.Name), "a group kind's name; group changes are the grants' transition"),
        new(typeof(WorldMutation.FormGroup), nameof(WorldMutation.FormGroup.Id), "a group id; group changes are the grants' transition"),
        new(typeof(WorldMutation.FormGroup), nameof(WorldMutation.FormGroup.KindName), "a group kind's name; group changes are the grants' transition"),
        new(typeof(WorldMutation.JoinGroup), nameof(WorldMutation.JoinGroup.GroupId), "a group id; group changes are the grants' transition"),
        new(typeof(WorldMutation.LeaveGroup), nameof(WorldMutation.LeaveGroup.GroupId), "a group id; group changes are the grants' transition"),
        new(typeof(WorldMutation.KickMember), nameof(WorldMutation.KickMember.GroupId), "a group id; group changes are the grants' transition"),
        new(typeof(WorldMutation.RemoveCurve), nameof(WorldMutation.RemoveCurve.Name), "a curve's name; the write door reads the installed curve"),
        new(typeof(WorldMutation.RemoveViewGraph), nameof(WorldMutation.RemoveViewGraph.Name), "a graph instance's name; the write door reads the installed graph"),
        new(typeof(WorldMutation.CommitViewGraph), nameof(WorldMutation.CommitViewGraph.Name), "a graph instance's name; the write door reads the installed graph"),
        new(typeof(WorldMutation.CommitViewGraph), nameof(WorldMutation.CommitViewGraph.Revision), "a graph row fingerprint"),
        new(typeof(WorldMutation.CommitViewGraph), nameof(WorldMutation.CommitViewGraph.SourceIdentity), "a source content identity"),
        new(typeof(WorldMutation.CommitViewGraph), nameof(WorldMutation.CommitViewGraph.ConfigIdentity), "a parameter schema identity"),
        new(typeof(WorldMutation.CommitViewGraph), nameof(WorldMutation.CommitViewGraph.Output), "a graph's image version"),
    ];

    private enum MemberRole : byte { Site, Model, Mutations, Record, Ignored, Uncovered }
    private sealed record MemberPlan(PropertyInfo Property, MemberRole Role, WorldNameField? Field, Type? Element);
    private static readonly Puck.Abstractions.CodeDerivedTable<Type, MemberPlan[]> Plans = new();

    /// <summary>Reports every name a mutation spells, in member order, nested batch members included.</summary>
    /// <param name="mutation">The mutation.</param>
    /// <param name="found">Receives each name position's registration, namespace and name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> or <paramref name="found"/> is
    /// <see langword="null"/>.</exception>
    public static void Collect(WorldMutation mutation, Action<WorldNameField, WorldNameKind?, string> found) {
        ArgumentNullException.ThrowIfNull(argument: mutation);
        ArgumentNullException.ThrowIfNull(argument: found);

        Walk(value: mutation, type: mutation.GetType(), found: found);
    }

    /// <summary>Gets every string or <see cref="CellName"/> member of a mutation kind, or of a record one carries, that
    /// is neither a registered name position, a document model member, nor excluded with a reason, each as
    /// <c>Owner.Member</c>; empty when the vocabulary is covered.</summary>
    public static IReadOnlyList<string> Uncovered {
        get {
            var uncovered = new List<string>();
            var seen = new HashSet<Type>();
            var pending = new Queue<Type>(collection: typeof(WorldMutation).GetNestedTypes(bindingAttr: BindingFlags.Public)
                .Where(predicate: static type => typeof(WorldMutation).IsAssignableFrom(c: type) && !type.IsAbstract));

            while (pending.TryDequeue(result: out var type)) {
                if (!seen.Add(item: type)) { continue; }
                foreach (var plan in PlanOf(type: type)) {
                    if (plan.Role == MemberRole.Uncovered) { uncovered.Add(item: $"{type.Name}.{plan.Property.Name}"); }
                    if ((plan.Role == MemberRole.Record) && (plan.Element ?? plan.Property.PropertyType) is var record) { pending.Enqueue(item: record); }
                }
            }
            return uncovered;
        }
    }

    private static void Walk(object value, Type type, Action<WorldNameField, WorldNameKind?, string> found) {
        foreach (var plan in PlanOf(type: type)) {
            if ((plan.Role is MemberRole.Ignored or MemberRole.Uncovered) || (plan.Property.GetValue(obj: value) is not { } member)) { continue; }
            switch (plan.Role) {
                case MemberRole.Site:
                    foreach (var text in Texts(value: member)) {
                        // A plain name at a name position is the one name the grammar reads there: no reserved
                        // channel to parse and no dotted key to split, so a cell write reads it without a reader.
                        if ((plan.Field!.Role == WorldNameRole.Names) && !text.StartsWith('$') && !text.Contains('.')) {
                            if (text.Length > 0) { found(plan.Field, plan.Field.Kind, text); }
                            continue;
                        }
                        _ = WorldModuleNamespace.Respell(text: text, site: plan.Field, respell: (site, kind, name, _) => {
                            found(site, kind, name);
                            return null;
                        });
                    }
                    break;
                case MemberRole.Model:
                    WorldSpelledNames.Collect(value: member, type: plan.Property.PropertyType, found: found);
                    break;
                case MemberRole.Mutations:
                    if (member is WorldMutation single) { Walk(value: single, type: single.GetType(), found: found); break; }
                    foreach (var nested in (IEnumerable<WorldMutation>)member) {
                        if (nested is not null) { Walk(value: nested, type: nested.GetType(), found: found); }
                    }
                    break;
                case MemberRole.Record:
                    if (plan.Element is { } element) {
                        foreach (var item in (System.Collections.IEnumerable)member) {
                            if (item is not null) { Walk(value: item, type: element, found: found); }
                        }
                    } else {
                        Walk(value: member, type: plan.Property.PropertyType, found: found);
                    }
                    break;
            }
        }
    }
    private static IEnumerable<string> Texts(object value) => value switch {
        string text => [text],
        CellName name => [name.Value],
        IEnumerable<string> texts => texts,
        IEnumerable<CellName> names => names.Select(selector: static name => name.Value),
        _ => [],
    };

    private static MemberPlan[] PlanOf(Type type) => Plans.GetValue(key: type, derive: static type => [..
        type.GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Instance)
            .Where(predicate: static property => property.GetIndexParameters().Length == 0)
            .Select(selector: property => Plan(owner: type, property: property))]);
    private static MemberPlan Plan(Type owner, PropertyInfo property) {
        var type = Nullable.GetUnderlyingType(nullableType: property.PropertyType) ?? property.PropertyType;
        var element = ElementOf(type: type);

        if (Fields.FirstOrDefault(predicate: field => field.Owner.IsAssignableFrom(c: owner) && (field.Member == property.Name)) is { } registered) {
            return new(Element: null, Field: registered, Property: property, Role: MemberRole.Site);
        }
        if (Exclusions.Any(predicate: exclusion => exclusion.Owner.IsAssignableFrom(c: owner) && (exclusion.Member == property.Name))) {
            return new(Element: null, Field: null, Property: property, Role: MemberRole.Ignored);
        }
        if (typeof(WorldMutation).IsAssignableFrom(c: type) || ((element is not null) && typeof(WorldMutation).IsAssignableFrom(c: element))) {
            return new(Element: null, Field: null, Property: property, Role: MemberRole.Mutations);
        }
        if (WorldModelShape.Of(type: type) is not null) {
            return new(Element: null, Field: null, Property: property, Role: MemberRole.Model);
        }
        if (CarriesText(type: type) || ((element is not null) && CarriesText(type: element))) {
            return new(Element: null, Field: null, Property: property, Role: MemberRole.Uncovered);
        }
        if (CarriesNoName(type: type)) {
            return new(Element: null, Field: null, Property: property, Role: MemberRole.Ignored);
        }
        return new(Element: element, Field: null, Property: property, Role: MemberRole.Record);
    }
    private static bool CarriesText(Type type) => (type == typeof(string)) || (type == typeof(CellName));
    // A value type, a principal and a collection of such carry no document name; a mutation's own principal is the
    // ingress-stamped actor.
    private static bool CarriesNoName(Type type) =>
        type.IsValueType || (type == typeof(Principal)) || (type == typeof(StateVector)) || (type == typeof(Type)) ||
        ((ElementOf(type: type) is { } element) && (element.IsValueType || (element == typeof(Principal))));
    private static Type? ElementOf(Type type) => (type == typeof(string)) ? null : type.GetInterfaces().Prepend(element: type)
        .FirstOrDefault(predicate: static candidate => candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        ?.GetGenericArguments()[0];
}
