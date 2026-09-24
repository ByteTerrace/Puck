namespace Puck.Transpiler.Ast;

/// <summary>The one enumeration of a syntax node's children, and the one search for the nodes a source position
/// stands in. Every reader that walks a parsed tree without rebuilding it — hover, the linter's reference scan, the
/// embedding and SQL editor services — walks it through here, so a node kind gaining a child is reached by all of
/// them at once.</summary>
/// <remarks>A rewrite rebuilds each node it descends through, so it keeps its own typed descent
/// (<see cref="Rewriting.PuckSyntaxRewriter"/>). A member carrying a sub-grammar as text rather than as a node — an
/// embedded block's body, a pattern's match, an operand's parsed expression tree — has no children here.</remarks>
public static class SyntaxWalk {
    /// <summary>Returns every syntax node <paramref name="node"/> holds directly, in the order its source writes
    /// them.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The node's children; empty for a leaf.</returns>
    /// <remarks>An operand's atoms and a cell key's atom are children, since a reference walk has to reach their
    /// holes, but they were parsed from their own text and carry offsets into it, so <see cref="PathAt"/> never
    /// stands in one.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SyntaxNode> Children(SyntaxNode node) {
        ArgumentNullException.ThrowIfNull(argument: node);

        return node switch {
            DocumentNode document => document.Statements,
            ExpressionStatementNode statement => [statement.Expression],
            LetNode constant => [constant.Value],
            TemplateNode template => [.. template.Parameters, template.Body],
            TemplateParameterNode parameter => Present(parameter.DefaultValue),
            BlockNode block => [.. Present(block.NameExpression), .. block.Statements],
            PropertyNode property => [property.Value],
            MemberAccessExpressionNode member => [member.Target],
            CallExpressionNode call => call.Arguments,
            ArgumentNode argument => [argument.Value],
            BinaryExpressionNode binary => [binary.Left, binary.Right],
            ConditionalExpressionNode conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            UnaryExpressionNode unary => [unary.Operand],
            ForStatementNode loop => [loop.Sequence, .. loop.Body],
            IndexExpressionNode index => [index.Target, index.Index],
            LambdaExpressionNode lambda => [lambda.Body],
            ArrayExpressionNode array => array.Elements,
            RangeExpressionNode range => Present(range.Start, range.End),
            ObjectExpressionNode obj => obj.Properties,
            OperandExpressionNode operand => [.. operand.Atoms.Select(selector: static atom => atom.Value)],
            InterpolatedStringNode interpolated => [.. interpolated.Segments.OfType<InterpolationSegment.Hole>().Select(selector: static hole => hole.Expression)],
            WorldDeclarationNode world => [world.Name, world.Module],
            WorldLinkNode link => [link.Left, link.Right, .. link.Properties],
            RowRefNode row => Present(row.KeyAtom),
            RhsOperandNode rhs => [rhs.Expression],
            SetCellStatementNode set => [set.Target, set.Rhs],
            AddCellStatementNode add => [add.Target, add.Rhs],
            CompoundAssignStatementNode compound => [compound.Target, compound.Rhs],
            PushStatementNode push => [push.Rhs],
            RemoveCellStatementNode remove => [remove.Target],
            ScheduleStatementNode schedule => [schedule.Target],
            TransformStatementNode transform => [transform.Transform],
            TransactionStatementNode transaction => [.. transaction.MainEffects, .. (transaction.OnFailureEffects ?? [])],
            IfStatementNode branch => [branch.Condition, .. branch.Then, .. (branch.Else ?? [])],
            RepeatStatementNode repeat => [repeat.Count, .. repeat.Body],
            ClaimStatementNode claim => claim.Body,
            ClaimPairStatementNode claimPair => claimPair.Body,
            PoolForEachStatementNode forEach => forEach.Body,
            PatternDeclarationNode pattern => [.. pattern.Symbols, .. Present(pattern.Value)],
            ComparisonPredicateNode comparison => [comparison.Left, comparison.Right],
            AndPredicateNode conjunction => conjunction.Operands,
            OrPredicateNode disjunction => disjunction.Operands,
            NotPredicateNode negation => [negation.Operand],
            WhenStatementNode whenClause => [whenClause.Predicate],
            CallPredicateNode gate => [gate.Call],
            RuleScopeNode scope => [.. Present(scope.HeaderWhen), .. scope.Statements],
            StabilizeGroupNode group => [.. Present(group.MaxPasses, group.UntilCondition, group.Undo), .. group.Statements],
            WorkflowStepNode step => [.. Present(step.UntilCondition, step.ForEachCollection), .. step.Statements],
            WorkflowNode workflow => [.. Present(workflow.Undo), .. workflow.Steps],
            RuleBlockNode rule => [.. Present(rule.NameExpression), .. rule.Statements],
            LocalStatementNode local => [.. Present(local.NameExpression), local.Expression],
            DecisionBlockNode decision => decision.Statements,
            OptionBlockNode option => option.Statements,
            ScoreStatementNode score => [score.Expression],
            InterruptStatementNode interrupt => [interrupt.Predicate],
            OnNoChoiceBlockNode fallback => fallback.Effects,
            FamilyMemberNode member => Present(member.First, member.Last),
            StateModifierNode modifier => modifier.Arguments,
            StateCellEntryNode cell => [cell.Value, .. cell.Modifiers],
            StateTableDeclarationNode table => [.. Family(size: table.FamilySize, members: table.FamilyMembers), .. table.Modifiers, .. Present(table.Initializer), .. table.Cells],
            StateSlotDeclarationNode slot => [.. Family(size: slot.FamilySize, members: slot.FamilyMembers), .. slot.Modifiers, .. Present(slot.Value)],
            StatePileDeclarationNode pile => [.. Family(size: pile.FamilySize, members: pile.FamilyMembers), .. pile.Modifiers, .. Present(pile.Initializer), .. pile.Tokens],
            StateGridDeclarationNode grid => [.. Family(size: grid.FamilySize, members: grid.FamilyMembers), .. grid.Modifiers, .. grid.Cells],
            EnumDeclarationNode enumeration => enumeration.Members,
            RecordFieldNode field => [.. field.Modifiers, .. Present(field.Default)],
            RecordDeclarationNode record => record.Fields,
            StatePoolDeclarationNode pool => Present(pool.Capacity, pool.Initializer),
            StatePairPoolDeclarationNode pairPool => [pairPool.MaxLive],
            DerivedStateNode derived => [derived.Expression],
            TestGivenNode given => [given.Target, given.Value],
            TestGivenWorldNode givenWorld => givenWorld.Cells,
            TestWhenWorldNode whenWorld => whenWorld.Steps,
            TestExpectationNode expectation => [expectation.Predicate],
            TestExpectWorldNode expectWorld => expectWorld.Expectations,
            TestGivenBlockNode givenBlock => givenBlock.Cells,
            TestWhenBlockNode whenBlock => whenBlock.Steps,
            TestExpectBlockNode expectBlock => expectBlock.Expectations,
            TestDeclarationNode test => Present(test.Subject, test.Given, test.When, test.Expect),
            _ => [],
        };
    }
    /// <summary>Returns the nodes the source position <paramref name="offset"/> stands in, from
    /// <paramref name="root"/> down to the innermost.</summary>
    /// <param name="root">The node to search from, usually a document.</param>
    /// <param name="offset">The 0-based source offset.</param>
    /// <returns>The path, outermost first; empty when <paramref name="root"/> does not span
    /// <paramref name="offset"/>.</returns>
    /// <remarks>A node spans the offsets from its first character up to, but not including, the one after its last.
    /// The search descends into the first child, in <see cref="Children"/> order, that spans the offset and does not
    /// start before its parent; a child that does was parsed from its own text rather than placed in the source, so it
    /// is never on a path.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SyntaxNode> PathAt(SyntaxNode root, int offset) {
        ArgumentNullException.ThrowIfNull(argument: root);

        var path = new List<SyntaxNode>();

        if (!Spans(node: root, offset: offset)) {
            return path;
        }

        for (var node = root; (node is not null);) {
            path.Add(item: node);

            var parent = node;

            node = Children(node: parent).FirstOrDefault(predicate: child => ((child.Offset >= parent.Offset) && Spans(node: child, offset: offset)));
        }

        return path;
    }

    private static bool Spans(SyntaxNode node, int offset) => ((offset >= node.Offset) && (offset < (node.Offset + node.Length)));
    private static SyntaxNode[] Present(params SyntaxNode?[] nodes) => [.. nodes.OfType<SyntaxNode>()];
    private static IEnumerable<SyntaxNode> Family(ExpressionNode? size, IReadOnlyList<FamilyMemberNode>? members) => [.. Present(size), .. (members ?? [])];
}
