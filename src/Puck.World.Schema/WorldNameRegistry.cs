using Puck.Commands;
using Puck.Physics.Motion;
using Puck.Text;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The namespace a document name belongs to — which declaration a name-bearing field resolves against.</summary>
public enum WorldNameKind : byte {
    /// <summary>A <c>state.world</c> row.</summary>
    State,
    /// <summary>An ordered zone: a <c>state.world</c> row a rule's zone table, a transfer, or a search job names as a pile.</summary>
    Zone,
    /// <summary>A <c>rules</c> row.</summary>
    Rule,
    /// <summary>A <c>tables</c> row.</summary>
    Table,
    /// <summary>A <c>patterns</c> row.</summary>
    Pattern,
    /// <summary>A <c>state.lattices</c> topology.</summary>
    Topology,
    /// <summary>A <c>generators</c> row.</summary>
    Generator,
    /// <summary>A <c>fields</c> row.</summary>
    Field,
    /// <summary>A <c>dynamics</c> row.</summary>
    Dynamics,
    /// <summary>Any declaration the document makes, whatever its namespace: an entry of a module's export lists.</summary>
    Any,
}
/// <summary>How a document field carries a name.</summary>
public enum WorldNameRole : byte {
    /// <summary>The field mints the name: the row's own <c>name</c>.</summary>
    Declares,
    /// <summary>The field holds one name of its kind, or a reserved <c>$</c> channel whose colon-separated segments
    /// hold names; a list-typed field holds one per element.</summary>
    Names,
    /// <summary>The field is a cell key in the dynamic-key grammar: a literal key, or a <c>$cell:</c>/<c>$zone:</c>/
    /// <c>$expr:</c>/<c>cell:</c> spelling whose segments or expression hold names.</summary>
    Key,
    /// <summary>The field is a value expression, infix text or postfix tokens, whose state reads and topology
    /// arguments are names.</summary>
    Expression,
    /// <summary>The field is a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> binding token.</summary>
    Binding,
    /// <summary>The field is a HUD template whose <c>{…}</c> placeholders are binding tokens.</summary>
    Template,
}
/// <summary>One registered name-bearing member of the document model.</summary>
/// <param name="Owner">The type declaring the member; a derived record inheriting it matches too.</param>
/// <param name="Member">The member's C# name.</param>
/// <param name="Kind">The namespace the name resolves against.</param>
/// <param name="Role">How the member carries the name.</param>
/// <param name="Facet">The export list a host reference through this member is admitted by when the name belongs
/// to an imported module (<see cref="WorldModuleExports"/>); meaningless for a <see cref="WorldNameRole.Declares"/>
/// member.</param>
public sealed record WorldNameField(Type Owner, string Member, WorldNameKind Kind, WorldNameRole Role, WorldExportFacet Facet = WorldExportFacet.Read);
/// <summary>One name-shaped member of the document model that carries no module-scoped name, with the reason.</summary>
/// <param name="Owner">The type declaring the member.</param>
/// <param name="Member">The member's C# name.</param>
/// <param name="Reason">Why the member is outside every <see cref="WorldNameKind"/>.</param>
public sealed record WorldNameExclusion(Type Owner, string Member, string Reason);

/// <summary>
/// The name registry: every document member that carries a state, zone, rule, table, pattern, topology, generator,
/// field, or dynamics name, keyed by the C# member so the JSON paths it reaches are derived from
/// <see cref="WorldJsonContext"/> rather than listed by hand (<see cref="Sites"/>). Two consumers read it: the
/// <c>puck registry</c> verb renders it to <c>docs/world-name-registry.md</c> and checks the rendering against the
/// model (<see cref="Render"/>, <see cref="Uncovered"/>), and <see cref="WorldModuleNamespace"/> prefixes an aliased
/// import's names at compose time. A member typed <see cref="CellName"/>, <see cref="ValueExpression"/>,
/// <see cref="BindableScalar"/>, <see cref="BindableColor"/>, or <see cref="WorldLatticeScalar"/>, or a string
/// member whose C# name reads like a name position, must be registered or excluded with a reason; the check names
/// every member that is neither.
/// </summary>
public static partial class WorldNameRegistry {
    /// <summary>The separator between an import alias and the name it prefixes: the one character a
    /// <see cref="CellName"/> admits that also lexes inside a bare expression name and is not the reserved
    /// <c>$</c>.</summary>
    public const char AliasSeparator = '_';

    private static readonly WorldNameField[] Fields = [
        // Declarations.
        new(typeof(StateRow), nameof(StateRow.Name), WorldNameKind.State, WorldNameRole.Declares),
        new(typeof(Rule), nameof(Rule.Name), WorldNameKind.Rule, WorldNameRole.Declares),
        new(typeof(TableRow), nameof(TableRow.Name), WorldNameKind.Table, WorldNameRole.Declares),
        new(typeof(PatternRow), nameof(PatternRow.Name), WorldNameKind.Pattern, WorldNameRole.Declares),
        new(typeof(LatticeTopology), nameof(LatticeTopology.Name), WorldNameKind.Topology, WorldNameRole.Declares),
        new(typeof(GeneratorRow), nameof(GeneratorRow.Name), WorldNameKind.Generator, WorldNameRole.Declares),
        new(typeof(WorldFieldRow), nameof(WorldFieldRow.Name), WorldNameKind.Field, WorldNameRole.Declares),
        new(typeof(DynamicsRow), nameof(DynamicsRow.Name), WorldNameKind.Dynamics, WorldNameRole.Declares),
        // A module's export lists: each entry is one of the module's own declarations, prefixed by an alias.
        new(typeof(WorldExports), nameof(WorldExports.Reads), WorldNameKind.Any, WorldNameRole.Names),
        new(typeof(WorldExports), nameof(WorldExports.Actions), WorldNameKind.Any, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldExports), nameof(WorldExports.Bindings), WorldNameKind.Any, WorldNameRole.Names, WorldExportFacet.Binding),
        // State rows and their traits.
        new(typeof(StateRow), nameof(StateRow.ValuesFrom), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateRow), nameof(StateRow.PhaseOf), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateDomain.KeysOf), nameof(StateDomain.KeysOf.Row), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateDomain.CellsOf), nameof(StateDomain.CellsOf.Topology), WorldNameKind.Topology, WorldNameRole.Names),
        new(typeof(Draw), nameof(Draw.Source), WorldNameKind.Generator, WorldNameRole.Names),
        new(typeof(StateDynamics), nameof(StateDynamics.Row), WorldNameKind.Dynamics, WorldNameRole.Names),
        new(typeof(StateInverse), nameof(StateInverse.Tokens), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateInverse), nameof(StateInverse.Codes), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateKnowledge), nameof(StateKnowledge.Source), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateKnowledge), nameof(StateKnowledge.Mask), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateVisibility), nameof(StateVisibility.ReadersFrom), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldLatticeFill.Draw), nameof(WorldLatticeFill.Draw.Source), WorldNameKind.Generator, WorldNameRole.Names),
        // Rules.
        new(typeof(Rule), nameof(Rule.ForEach), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(Rule), nameof(Rule.Zones), WorldNameKind.Zone, WorldNameRole.Names),
        new(typeof(RuleBinding), nameof(RuleBinding.Expression), WorldNameKind.State, WorldNameRole.Expression),
        new(typeof(ActionPredicate.CompareState), nameof(ActionPredicate.CompareState.State), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(ActionPredicate.CompareState), nameof(ActionPredicate.CompareState.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ActionPredicate.CompareState), nameof(ActionPredicate.CompareState.ComparandState), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(ActionPredicate.CompareState), nameof(ActionPredicate.CompareState.ComparandKey), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ActionPredicate.CompareValue), nameof(ActionPredicate.CompareValue.Left), WorldNameKind.State, WorldNameRole.Expression),
        new(typeof(ActionPredicate.CompareValue), nameof(ActionPredicate.CompareValue.Right), WorldNameKind.State, WorldNameRole.Expression),
        new(typeof(ActionEffect.SetState), nameof(ActionEffect.SetState.State), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(ActionEffect.SetState), nameof(ActionEffect.SetState.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ActionEffect.SetState), nameof(ActionEffect.SetState.FromState), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(ActionEffect.SetState), nameof(ActionEffect.SetState.FromKey), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ActionEffect.SetState), nameof(ActionEffect.SetState.Expression), WorldNameKind.State, WorldNameRole.Expression),
        new(typeof(ActionEffect.AddState), nameof(ActionEffect.AddState.State), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(ActionEffect.AddState), nameof(ActionEffect.AddState.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ActionEffect.AddState), nameof(ActionEffect.AddState.FromState), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(ActionEffect.AddState), nameof(ActionEffect.AddState.FromKey), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ActionEffect.AddState), nameof(ActionEffect.AddState.Expression), WorldNameKind.State, WorldNameRole.Expression),
        new(typeof(ActionEffect.PushState), nameof(ActionEffect.PushState.State), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(ActionEffect.PushState), nameof(ActionEffect.PushState.FromState), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(ActionEffect.PushState), nameof(ActionEffect.PushState.FromKey), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ActionEffect.PushState), nameof(ActionEffect.PushState.Expression), WorldNameKind.State, WorldNameRole.Expression),
        new(typeof(ActionEffect.CountdownState), nameof(ActionEffect.CountdownState.State), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(ActionEffect.CountdownState), nameof(ActionEffect.CountdownState.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ActionEffect.RemoveStateCell), nameof(ActionEffect.RemoveStateCell.State), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(ActionEffect.RemoveStateCell), nameof(ActionEffect.RemoveStateCell.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ActionEffect.ScheduleState), nameof(ActionEffect.ScheduleState.State), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(ActionEffect.ScheduleState), nameof(ActionEffect.ScheduleState.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ActionEffect.Generate), nameof(ActionEffect.Generate.Row), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldEffect.StartTimer), nameof(WorldEffect.StartTimer.State), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldEffect.EmitCue), nameof(WorldEffect.EmitCue.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.SetBodyVerticalVelocity), nameof(WorldEffect.SetBodyVerticalVelocity.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.ScaleBodyVerticalVelocity), nameof(WorldEffect.ScaleBodyVerticalVelocity.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.ApplyBodyImpulse), nameof(WorldEffect.ApplyBodyImpulse.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.ApplyRigidImpulse), nameof(WorldEffect.ApplyRigidImpulse.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.ApplyRigidImpulse), nameof(WorldEffect.ApplyRigidImpulse.HeadingKey), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.ApplyRigidImpulse), nameof(WorldEffect.ApplyRigidImpulse.MagnitudeState), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldEffect.ApplyRigidImpulse), nameof(WorldEffect.ApplyRigidImpulse.MagnitudeKey), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.DesignateBody), nameof(WorldEffect.DesignateBody.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.DesignateBody), nameof(WorldEffect.DesignateBody.TargetKey), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.Pose), nameof(WorldEffect.Pose.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.SetIdentityFact), nameof(WorldEffect.SetIdentityFact.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldEffect.SetIdentityFact), nameof(WorldEffect.SetIdentityFact.Expression), WorldNameKind.State, WorldNameRole.Expression),
        new(typeof(WorldEffect.PaintField), nameof(WorldEffect.PaintField.Field), WorldNameKind.Field, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldDecisionOption), nameof(WorldDecisionOption.Score), WorldNameKind.State, WorldNameRole.Expression),
        // State transforms.
        new(typeof(StateTransform.Observe), nameof(StateTransform.Observe.Row), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.Transfer), nameof(StateTransform.Transfer.From), WorldNameKind.Zone, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.Transfer), nameof(StateTransform.Transfer.To), WorldNameKind.Zone, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.Transfer), nameof(StateTransform.Transfer.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(StateTransform.Transfer), nameof(StateTransform.Transfer.Draw), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateTransform.SetRay), nameof(StateTransform.SetRay.Row), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.SetRay), nameof(StateTransform.SetRay.From), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(StateTransform.SetRay), nameof(StateTransform.SetRay.Pattern), WorldNameKind.Pattern, WorldNameRole.Names),
        new(typeof(StateTransform.Shuffle), nameof(StateTransform.Shuffle.Row), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.Shuffle), nameof(StateTransform.Shuffle.Draw), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateTransform.SortZone), nameof(StateTransform.SortZone.Row), WorldNameKind.Zone, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(SortKey), nameof(SortKey.Row), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateTransform.SortKeyed), nameof(StateTransform.SortKeyed.Row), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.WriteSet), nameof(StateTransform.WriteSet.Row), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.WriteSet), nameof(StateTransform.WriteSet.Set), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateTransform.WriteSet), nameof(StateTransform.WriteSet.SetKey), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(StateTransform.BoardCombine), nameof(StateTransform.BoardCombine.Row), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.BoardCombine), nameof(StateTransform.BoardCombine.Left), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateTransform.BoardCombine), nameof(StateTransform.BoardCombine.Right), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateTransform.Arrange), nameof(StateTransform.Arrange.Row), WorldNameKind.Zone, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.Arrange), nameof(StateTransform.Arrange.From), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(StateTransform.Arrange), nameof(StateTransform.Arrange.FromKey), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(StateTransform.Push), nameof(StateTransform.Push.Row), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.ClearEnclosed), nameof(StateTransform.ClearEnclosed.Row), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(StateTransform.ClearEnclosed), nameof(StateTransform.ClearEnclosed.From), WorldNameKind.State, WorldNameRole.Key),
        // Expression tokens (the postfix spelling).
        new(typeof(ValueToken.State), nameof(ValueToken.State.Name), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(ValueToken.State), nameof(ValueToken.State.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(ValueToken.BoardShift), nameof(ValueToken.BoardShift.Topology), WorldNameKind.Topology, WorldNameRole.Names),
        new(typeof(ValueToken.BoardFill), nameof(ValueToken.BoardFill.Topology), WorldNameKind.Topology, WorldNameRole.Names),
        new(typeof(ValueToken.BoardImage), nameof(ValueToken.BoardImage.Topology), WorldNameKind.Topology, WorldNameRole.Names),
        // Patterns.
        new(typeof(PatternRow), nameof(PatternRow.Attribute), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(PatternRow), nameof(PatternRow.Value), WorldNameKind.State, WorldNameRole.Expression),
        // Search.
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Tokens), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Board), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Zones), WorldNameKind.Zone, WorldNameRole.Names),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Turn), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Verdict), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Legal), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Reach), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Held), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Counts), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Score), WorldNameKind.State, WorldNameRole.Expression),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Scores), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Best), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldSearchShape.Promote), nameof(WorldSearchShape.Promote.Codes), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldSearchChance), nameof(WorldSearchChance.Row), WorldNameKind.State, WorldNameRole.Names),
        // Placements, bodies, targeting.
        new(typeof(WorldPlacementBoard), nameof(WorldPlacementBoard.Topology), WorldNameKind.Topology, WorldNameRole.Names),
        new(typeof(WorldPlacementBoard), nameof(WorldPlacementBoard.Occupancy), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldPlacementBoard), nameof(WorldPlacementBoard.Turn), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldPlacementBoard), nameof(WorldPlacementBoard.Verdict), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldPlacementBoard), nameof(WorldPlacementBoard.Move), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldPlacementBoard), nameof(WorldPlacementBoard.Plan), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldPlacementInhabitCount), nameof(WorldPlacementInhabitCount.Row), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldPlacementInhabitCount), nameof(WorldPlacementInhabitCount.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldPlacementDeal), nameof(WorldPlacementDeal.Row), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldPlacementReflow), nameof(WorldPlacementReflow.CostRow), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldPlacementDealVariants), nameof(WorldPlacementDealVariants.Row), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldInteraction), nameof(WorldInteraction.Left), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldInteraction), nameof(WorldInteraction.Right), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldBodiesDefaults), nameof(WorldBodiesDefaults.CapacityRow), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldBodiesDefaults), nameof(WorldBodiesDefaults.ScaleRow), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldTargetRegister), nameof(WorldTargetRegister.RangeState), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldTargetRegister), nameof(WorldTargetRegister.HalfAngleState), WorldNameKind.State, WorldNameRole.Names),
        // Fields and reactions.
        new(typeof(WorldFieldCondition), nameof(WorldFieldCondition.Field), WorldNameKind.Field, WorldNameRole.Names),
        new(typeof(WorldFieldWrite), nameof(WorldFieldWrite.Field), WorldNameKind.Field, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldReaction.Diffuse), nameof(WorldReaction.Diffuse.Field), WorldNameKind.Field, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldReaction.Decay), nameof(WorldReaction.Decay.Field), WorldNameKind.Field, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldReaction.Emit), nameof(WorldReaction.Emit.Field), WorldNameKind.Field, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldReaction.Expose), nameof(WorldReaction.Expose.Field), WorldNameKind.Field, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldReaction.Expose), nameof(WorldReaction.Expose.Row), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldReaction.Flow), nameof(WorldReaction.Flow.Field), WorldNameKind.Field, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldReaction.Flow), nameof(WorldReaction.Flow.Over), WorldNameKind.Field, WorldNameRole.Names),
        new(typeof(WorldReaction.Flow), nameof(WorldReaction.Flow.SpillRow), WorldNameKind.State, WorldNameRole.Names, WorldExportFacet.Action),
        new(typeof(WorldLatticeScalar), nameof(WorldLatticeScalar.Row), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldPlacementResponseCondition.FieldCondition), nameof(WorldPlacementResponseCondition.FieldCondition.Field), WorldNameKind.Field, WorldNameRole.Names),
        new(typeof(WorldPlacementResponseCondition.StateCondition), nameof(WorldPlacementResponseCondition.StateCondition.State), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldPlacementResponseCondition.StateCondition), nameof(WorldPlacementResponseCondition.StateCondition.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldPlacementResponseCondition.StateCondition), nameof(WorldPlacementResponseCondition.StateCondition.ComparandState), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldPlacementResponseCondition.StateCondition), nameof(WorldPlacementResponseCondition.StateCondition.ComparandKey), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldScreenMemory), nameof(WorldScreenMemory.Row), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldScreenMemory), nameof(WorldScreenMemory.Key), WorldNameKind.State, WorldNameRole.Key),
        new(typeof(WorldNavigationDomain), nameof(WorldNavigationDomain.Medium), WorldNameKind.Field, WorldNameRole.Names),
        new(typeof(WorldMarkerRing), nameof(WorldMarkerRing.Field), WorldNameKind.Field, WorldNameRole.Names),
        // Bindings: HUD, overlays, cameras, theme.
        new(typeof(WorldHudElement), nameof(WorldHudElement.Binding), WorldNameKind.State, WorldNameRole.Binding, WorldExportFacet.Binding),
        new(typeof(WorldHudElement), nameof(WorldHudElement.Template), WorldNameKind.State, WorldNameRole.Template, WorldExportFacet.Binding),
        new(typeof(OverlayPredicate.State), nameof(OverlayPredicate.State.Binding), WorldNameKind.State, WorldNameRole.Binding, WorldExportFacet.Binding),
        new(typeof(WorldCameraProgramOp.Dynamics), nameof(WorldCameraProgramOp.Dynamics.Row), WorldNameKind.Dynamics, WorldNameRole.Names, WorldExportFacet.Binding),
        new(typeof(WorldRenderCycle), nameof(WorldRenderCycle.State), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(WorldFlockProfile), nameof(WorldFlockProfile.CohesionAffinity), WorldNameKind.State, WorldNameRole.Expression),
        new(typeof(WorldFlockProfile), nameof(WorldFlockProfile.AlignmentAffinity), WorldNameKind.State, WorldNameRole.Expression),
        new(typeof(WorldHostDefaults), nameof(WorldHostDefaults.BackendRow), WorldNameKind.State, WorldNameRole.Names),
        new(typeof(BindingWheelDefinition), nameof(BindingWheelDefinition.LabelRow), WorldNameKind.State, WorldNameRole.Binding, WorldExportFacet.Binding),
        new(typeof(BindingWheelDefinition), nameof(BindingWheelDefinition.IconRow), WorldNameKind.State, WorldNameRole.Binding, WorldExportFacet.Binding),
        new(typeof(WorldBindingBarAuthoring), nameof(WorldBindingBarAuthoring.IconRow), WorldNameKind.State, WorldNameRole.Binding, WorldExportFacet.Binding),
    ];
    private static readonly WorldNameExclusion[] Exclusions = [
        new(typeof(StateCell), nameof(StateCell.Key), "a cell key is local to its row"),
        new(typeof(RuleBinding), nameof(RuleBinding.Name), "a binding name is local to its rule, read as $bind:<name>"),
        new(typeof(WorldDecisionOption), nameof(WorldDecisionOption.Name), "an option name is local to its decision"),
        new(typeof(PatternSymbol), nameof(PatternSymbol.Name), "a symbol name is local to its pattern"),
        new(typeof(PatternNode.Symbol), nameof(PatternNode.Symbol.Name), "a symbol name is local to its pattern"),
        new(typeof(PatternNode.Except), nameof(PatternNode.Except.Name), "a symbol name is local to its pattern"),
        new(typeof(GeneratorAlternative), nameof(GeneratorAlternative.Next), "a context key is local to its generator"),
        new(typeof(GeneratorContext), nameof(GeneratorContext.Key), "a context key is local to its generator"),
        new(typeof(StateGenerator), nameof(StateGenerator.Start), "a context key is local to its generator"),
        new(typeof(TableRow), nameof(TableRow.Source), "a table's source is a document path"),
        new(typeof(TopologyDirection), nameof(TopologyDirection.Name), "a direction name is local to its topology"),
        new(typeof(TopologyElementAlias), nameof(TopologyElementAlias.Name), "an element alias is local to its topology"),
        new(typeof(GraphDirection), nameof(GraphDirection.Name), "a direction name is local to its topology"),
        new(typeof(GraphEdge), nameof(GraphEdge.From), "a graph cell id is local to its topology"),
        new(typeof(GraphEdge), nameof(GraphEdge.To), "a graph cell id is local to its topology"),
        new(typeof(StateTransform.Transfer), nameof(StateTransform.Transfer.Key), "a literal key names a cell of the source zone; a $ spelling is rewritten"),
        new(typeof(WorldSearchRow), nameof(WorldSearchRow.Name), "a search job name is the search section's own namespace"),
        new(typeof(WorldSearchShape.Paired), nameof(WorldSearchShape.Paired.With), "a cell key of the job's token row"),
        new(typeof(WorldSearchShape.Jump), nameof(WorldSearchShape.Jump.Over), "direction names are local to the board's topology"),
        new(typeof(WorldCaptureRow), nameof(WorldCaptureRow.Station), "a capture station is the captures section's own namespace"),
        new(typeof(WorldInteraction), nameof(WorldInteraction.Name), "an interaction name is the interactions section's own namespace"),
        new(typeof(WorldEffect.EmitCue), nameof(WorldEffect.EmitCue.Name), "a cue name"),
        new(typeof(WorldEffect.Designate), nameof(WorldEffect.Designate.Register), "a target register name"),
        new(typeof(WorldEffect.DesignateBody), nameof(WorldEffect.DesignateBody.Register), "a target register name"),
        new(typeof(WorldTargetRegister), nameof(WorldTargetRegister.Name), "a target register name"),
        new(typeof(ActionStateSlot), nameof(ActionStateSlot.Name), "a per-body slot name, the body-state lane's own namespace"),
        new(typeof(WorldStateSection), nameof(WorldStateSection.Body), "per-body slots, the body-state lane's own namespace"),
        new(typeof(WorldStateSection), nameof(WorldStateSection.Identity), "per-body slots, the identity lane's own namespace"),
        new(typeof(WorldMusicRow), nameof(WorldMusicRow.Name), "a music row name is the music section's own namespace"),
        new(typeof(WorldMusicRow), nameof(WorldMusicRow.Source), "a music row's source is a document path"),
        new(typeof(WorldPropertyRegistrySection), nameof(WorldPropertyRegistrySection.Names), "property names are the properties section's own namespace"),
        new(typeof(WorldGroupKind), nameof(WorldGroupKind.SharedStateScope), "a group scope token"),
        new(typeof(WorldCameraProgramOp.Blend), nameof(WorldCameraProgramOp.Blend.A), "a camera program name"),
        new(typeof(WorldCameraProgramOp.Blend), nameof(WorldCameraProgramOp.Blend.B), "a camera program name"),
        new(typeof(WorldCameraProgramOp.Select), nameof(WorldCameraProgramOp.Select.Default), "a camera program name"),
        new(typeof(WorldCameraSelectCase), nameof(WorldCameraSelectCase.Program), "a camera program name"),
        new(typeof(WorldCameraProgramOp.Path), nameof(WorldCameraProgramOp.Path.Curve), "a curve name"),
        new(typeof(WorldReaction.Emit), nameof(WorldReaction.Emit.Tag), "an emission tag"),
        new(typeof(WorldSequence), nameof(WorldSequence.Name), "a sequence name"),
        new(typeof(WorldKit), nameof(WorldKit.Name), "a kit name"),
        new(typeof(WorldKit), nameof(WorldKit.BodyMotionProgram), "a body motion program name"),
        new(typeof(WorldViewLayout), nameof(WorldViewLayout.Name), "a view layout name"),
        new(typeof(WorldViewStudy), nameof(WorldViewStudy.Name), "a view study name"),
        new(typeof(WorldViewStudy), nameof(WorldViewStudy.Source), "a view study's source is a document path"),
        new(typeof(WorldCameraProgram), nameof(WorldCameraProgram.Name), "a camera program name"),
        new(typeof(WorldGroupRole), nameof(WorldGroupRole.Name), "a group role name"),
        new(typeof(WorldGroupKind), nameof(WorldGroupKind.Name), "a group kind name"),
        new(typeof(WorldMarkerRow), nameof(WorldMarkerRow.Id), "a marker id"),
        new(typeof(WorldPlacement), nameof(WorldPlacement.Id), "a placement id"),
        new(typeof(WorldHudElement), nameof(WorldHudElement.Id), "a HUD element id"),
        new(typeof(WorldHudPanel), nameof(WorldHudPanel.Id), "a HUD panel id"),
        new(typeof(WorldEffect.RemoveHudPanel), nameof(WorldEffect.RemoveHudPanel.Id), "a HUD panel id"),
        new(typeof(WorldEffect.RemovePlacement), nameof(WorldEffect.RemovePlacement.Id), "a placement id"),
        new(typeof(WorldSpawnPoint), nameof(WorldSpawnPoint.Id), "a spawn point id"),
        new(typeof(WorldDistributionRegion.Points), nameof(WorldDistributionRegion.Points.Names), "spawn point ids"),
        new(typeof(WorldRenderExtensionEntry), nameof(WorldRenderExtensionEntry.Id), "a post-render extension id"),
        new(typeof(WorldMachineCable), nameof(WorldMachineCable.Name), "a link-cable name"),
        new(typeof(WorldScreenSource.Probe), nameof(WorldScreenSource.Probe.Id), "a probe id"),
        new(typeof(WorldCamera), nameof(WorldCamera.Name), "a camera name"),
        new(typeof(WorldIdentitySeed), nameof(WorldIdentitySeed.Name), "an identity display name"),
        new(typeof(WorldChannel), nameof(WorldChannel.Name), "an input channel name"),
        new(typeof(BodyMotionProgram), nameof(BodyMotionProgram.Name), "a body motion program name"),
        new(typeof(BodyTargetSource.Designated), nameof(BodyTargetSource.Designated.Register), "a target register name"),
        new(typeof(BodyTargetSource.Navigated), nameof(BodyTargetSource.Navigated.Register), "a target register name"),
        new(typeof(WorldPredicate.TimerElapsed), nameof(WorldPredicate.TimerElapsed.State), "a per-body slot name"),
        new(typeof(WorldHold), nameof(WorldHold.Name), "a hold name, local to its kit"),
        new(typeof(WorldHoldSpend), nameof(WorldHoldSpend.State), "a per-body slot name"),
        new(typeof(WorldTether), nameof(WorldTether.ModeState), "a per-body slot name"),
        new(typeof(WorldAddonRow), nameof(WorldAddonRow.Name), "an addon name"),
        new(typeof(WorldBindingOverlay), nameof(WorldBindingOverlay.Id), "a binding overlay id"),
        new(typeof(BindingModifierDefinition), nameof(BindingModifierDefinition.Id), "a binding modifier id"),
        new(typeof(BindingPageDefinition), nameof(BindingPageDefinition.Id), "a binding page id"),
        new(typeof(BindingPageEntryDefinition), nameof(BindingPageEntryDefinition.Id), "a binding entry id"),
        new(typeof(BindingChordDefinition), nameof(BindingChordDefinition.Held), "a chord source token"),
        new(typeof(BindingContextDefinition), nameof(BindingContextDefinition.State), "a binding family state token"),
        new(typeof(BindingWheelDefinition), nameof(BindingWheelDefinition.Id), "a binding wheel id"),
        new(typeof(WorldBindingBarBank), nameof(WorldBindingBarBank.Id), "a binding bar bank id"),
        new(typeof(WorldBindingBarSlotPlacement), nameof(WorldBindingBarSlotPlacement.Source), "an input source id"),
        new(typeof(WorldBindingBarPiece), nameof(WorldBindingBarPiece.Table), "a key of the layout's own tables"),
        new(typeof(WorldSpeaker), nameof(WorldSpeaker.Name), "a speaker name"),
        new(typeof(WorldTune), nameof(WorldTune.Name), "a tune name"),
        new(typeof(WorldTune), nameof(WorldTune.Source), "a tune's source is a document path"),
        new(typeof(WorldPatch), nameof(WorldPatch.Name), "a patch name"),
        new(typeof(WorldPatch), nameof(WorldPatch.Source), "a patch's source is a document path"),
        new(typeof(WorldIconRow), nameof(WorldIconRow.Name), "an icon name"),
        new(typeof(WorldIconBadgeRow), nameof(WorldIconBadgeRow.Source), "an input source id"),
        new(typeof(WorldLatticeFill), nameof(WorldLatticeFill.Field), "compile-stamped from the carrying row, never authored"),
        new(typeof(GraphCell), nameof(GraphCell.Id), "a graph cell id, local to its topology"),
        new(typeof(WorldIdentityDefinition), nameof(WorldIdentityDefinition.Name), "an identity display name"),
        new(typeof(WorldIdentityDefinition), nameof(WorldIdentityDefinition.MoveSpeedState), "an identity-lane slot name"),
        new(typeof(WorldIdentityDefinition), nameof(WorldIdentityDefinition.TurnSpeedState), "an identity-lane slot name"),
        new(typeof(WorldIdentityFacts), nameof(WorldIdentityFacts.State), "an identity-lane row name"),
        new(typeof(WorldEffect.SetIdentityFact), nameof(WorldEffect.SetIdentityFact.Fact), "a fact key on the identity's own row, outside every world namespace"),
        new(typeof(WorldControllerStateSlots), nameof(WorldControllerStateSlots.MachineState), "an identity-lane slot name"),
        new(typeof(WorldControllerStateSlots), nameof(WorldControllerStateSlots.DeviceState), "an identity-lane slot name"),
        new(typeof(OwnershipSubject), nameof(OwnershipSubject.Id), "an ownership subject id"),
        new(typeof(WorldReference), nameof(WorldReference.NeighbourKey), "a neighbour document key"),
        new(typeof(WorldAdmissionEntry), nameof(WorldAdmissionEntry.PublicKey), "a public key"),
        new(typeof(TextFontDefinition), nameof(TextFontDefinition.Name), "a font name"),
        new(typeof(TextFontDefinition), nameof(TextFontDefinition.Source), "a font's source is a file path"),
        new(typeof(WorldMetadataAuthor), nameof(WorldMetadataAuthor.Name), "an author's name"),
        new(typeof(WorldSeatModeFamily), nameof(WorldSeatModeFamily.Name), "a seat mode family name"),
        new(typeof(WorldSeatModeFamily), nameof(WorldSeatModeFamily.DefaultState), "a seat mode state token"),
        new(typeof(WorldSeatModeState), nameof(WorldSeatModeState.Name), "a seat mode state token"),
        new(typeof(WorldProbe), nameof(WorldProbe.Id), "a probe id"),
        new(typeof(WorldProbeBinding.Axis), nameof(WorldProbeBinding.Axis.Source), "an input source id"),
        new(typeof(WorldProbeParameterTarget.Extension), nameof(WorldProbeParameterTarget.Extension.Id), "a post-render extension id"),
        new(typeof(WorldProbeParameterTarget.Extension), nameof(WorldProbeParameterTarget.Extension.Field), "an extension config field"),
        new(typeof(WorldProbeParameterTarget.Probe), nameof(WorldProbeParameterTarget.Probe.Id), "a probe id"),
        new(typeof(WorldProbeParameterTarget.Probe), nameof(WorldProbeParameterTarget.Probe.Field), "a probe parameter field"),
        new(typeof(WorldCurveRow), nameof(WorldCurveRow.Name), "a curve name"),
        new(typeof(WorldNavigationDomain), nameof(WorldNavigationDomain.Name), "a navigation domain name"),
        new(typeof(WorldPlacementReflow), nameof(WorldPlacementReflow.CostKey), "a literal payer cell key"),
        new(typeof(WorldNavigationDomain), nameof(WorldNavigationDomain.Parent), "a placement frame id"),
        new(typeof(WorldPlacementSpatialVolume), nameof(WorldPlacementSpatialVolume.Name), "a placement-local spatial volume name"),
        new(typeof(WorldPlacementSpatialVolume), nameof(WorldPlacementSpatialVolume.Channel), "an author-defined spatial influence channel"),
    ];

    /// <summary>Gets every registered name-bearing member.</summary>
    public static IReadOnlyList<WorldNameField> RegisteredFields => Fields;
    /// <summary>Gets every name-shaped member excluded with a reason.</summary>
    public static IReadOnlyList<WorldNameExclusion> ExcludedMembers => Exclusions;

    /// <summary>Finds the registration for a member, matching the declaring type or any type it derives from.</summary>
    /// <param name="declaringType">The type the member was found on.</param>
    /// <param name="member">The member's C# name.</param>
    /// <param name="field">The registration, when one matches.</param>
    /// <returns><see langword="true"/> when the member is registered.</returns>
    public static bool TryFind(Type declaringType, string member, out WorldNameField field) =>
        TryFind(fields: Fields, declaringType: declaringType, member: member, field: out field);
    private static bool TryFind(IReadOnlyList<WorldNameField> fields, Type declaringType, string member, out WorldNameField field) {
        foreach (var candidate in fields) {
            if (candidate.Owner.IsAssignableFrom(c: declaringType) && string.Equals(a: candidate.Member, b: member, comparisonType: StringComparison.Ordinal)) {
                field = candidate;

                return true;
            }
        }

        field = null!;

        return false;
    }
    private static bool IsExcluded(Type declaringType, string member) {
        foreach (var exclusion in Exclusions) {
            if (exclusion.Owner.IsAssignableFrom(c: declaringType) && string.Equals(a: exclusion.Member, b: member, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
    private static string? ExclusionReason(Type declaringType, string member) {
        foreach (var exclusion in Exclusions) {
            if (exclusion.Owner.IsAssignableFrom(c: declaringType) && string.Equals(a: exclusion.Member, b: member, comparisonType: StringComparison.Ordinal)) {
                return exclusion.Reason;
            }
        }

        return null;
    }
}
