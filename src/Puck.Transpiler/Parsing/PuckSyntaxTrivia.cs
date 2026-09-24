using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Parsing;

/// <summary>Hangs a source's comments, blank lines, and line breaks on the syntax nodes that own them.</summary>
/// <remarks>Reads the gaps between the spans the parser already recorded, so the grammar itself carries no trivia
/// bookkeeping. A node's recorded length may run past its last content character, because an expression reader
/// stops only after skipping whatever follows it; the walk therefore trims each span forward to its last content
/// character rather than trusting the length.</remarks>
public static class PuckSyntaxTrivia {
    /// <summary>Returns <paramref name="document"/> with the trivia of <paramref name="source"/> attached to it.</summary>
    /// <param name="document">The parsed document.</param>
    /// <param name="source">The source text the document was parsed from.</param>
    /// <returns>The document, with every node carrying the trivia written around it.</returns>
    public static DocumentNode Attach(DocumentNode document, string source) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: source);

        return new Walk(source: source).Document(document: document);
    }

    private sealed class Walk {
        private readonly string m_source;

        // What the most recent `List` found on its container's opening-delimiter line, read right after that call.
        private string? m_opening;

        public Walk(string source) {
            m_source = source;
        }

        private int ContentEnd(SyntaxNode node) {
            var start = Math.Clamp(
                max: m_source.Length,
                min: 0,
                value: node.Offset
            );
            var end = Math.Clamp(
                max: m_source.Length,
                min: start,
                value: (node.Offset + node.Length)
            );
            var last = start;
            var index = start;

            while (index < end) {
                var lexeme = SourceLexemes.End(
                    offset: index,
                    source: m_source
                );

                if (lexeme > index) {
                    if (m_source[index] is '"' or '`' or '$') {
                        last = Math.Min(
                            val1: lexeme,
                            val2: end
                        );
                    }
                    index = lexeme;

                    continue;
                }
                if (!char.IsWhiteSpace(c: m_source[index])) { last = (index + 1); }
                index++;
            }

            return last;
        }
        private int FindNext(int from, char target, int limit = -1) {
            var index = Math.Max(
                val1: 0,
                val2: from
            );
            var end = ((limit < 0)
                ? m_source.Length
                : Math.Min(
                    val1: limit,
                    val2: m_source.Length
                )
            );

            while (index < end) {
                var lexeme = SourceLexemes.End(
                    offset: index,
                    source: m_source
                );

                if (lexeme > index) { index = lexeme; continue; }
                if (m_source[index] == target) { return index; }
                index++;
            }

            return -1;
        }
        private int MatchDelimiter(int open) {
            if ((open < 0) || (open >= m_source.Length)) {
                return -1;
            }

            var opener = m_source[open];
            var closer = ((opener == '[')
                ? ']'
                : '}'
            );
            var depth = 0;
            var index = open;

            while (index < m_source.Length) {
                var lexeme = SourceLexemes.End(
                    offset: index,
                    source: m_source
                );

                if (lexeme > index) { index = lexeme; continue; }
                if (m_source[index] == opener) { depth++; } else if (m_source[index] == closer) {
                    depth--;
                    if (depth == 0) { return index; }
                }
                index++;
            }

            return -1;
        }
        private static void AddBlankLines(List<TriviaPiece> pieces, int count) {
            for (var index = 0; (index < count); index++) {
                pieces.Add(item: new TriviaPiece(Kind: TriviaKind.BlankLine, Text: string.Empty));
            }
        }
        // The comments and blank lines between two spans. A comment standing on the same line as the span that ended
        // just before it belongs to that span, so it prints where it was written rather than on a line of its own.
        private (string? SameLine, List<TriviaPiece> Pieces, bool Broke, bool Separated) ScanGap(int from, int to, bool allowSameLine) {
            var pieces = new List<TriviaPiece>();
            string? sameLine = null;
            var newlines = 0;
            var broke = false;
            var separated = false;
            var content = false;
            var index = Math.Max(
                val1: 0,
                val2: from
            );
            var end = Math.Min(
                val1: to,
                val2: m_source.Length
            );

            while (index < end) {
                var character = m_source[index];

                if (character == '\n') { broke = true; newlines++; index++; continue; }
                if (char.IsWhiteSpace(c: character)) { index++; continue; }

                var lexeme = SourceLexemes.End(
                    offset: index,
                    source: m_source
                );

                if (lexeme <= index) {
                    separated = (separated || (character is ',' or ';'));
                    content = true;
                    newlines = 0;
                    index++;

                    continue;
                }
                if (character != '/') { content = true; newlines = 0; index = lexeme; continue; }

                var text = m_source[index..Math.Min(
                    val1: lexeme,
                    val2: end
                )].TrimEnd();

                if (
                    allowSameLine &&
                    (newlines == 0) &&
                    !content &&
                    (sameLine is null) &&
                    (pieces.Count == 0)
                ) {
                    sameLine = text;
                } else {
                    AddBlankLines(
                        count: (newlines - 1),
                        pieces: pieces
                    );
                    pieces.Add(item: new TriviaPiece(Kind: TriviaKind.Comment, Text: text));
                }
                newlines = 0;
                index = lexeme;
            }
            AddBlankLines(
                count: (newlines - 1),
                pieces: pieces
            );

            return (sameLine, pieces, broke, separated);
        }
        private (IReadOnlyList<T> Items, IReadOnlyList<TriviaPiece> Inner) List<T>(IReadOnlyList<T> items, int bodyStart, int bodyEnd, Func<T, T> visit) where T : SyntaxNode {
            var result = new List<T>(capacity: items.Count);
            var cursor = bodyStart;

            m_opening = null;

            string? opening = null;

            foreach (var item in items) {
                var gap = ScanGap(
                    allowSameLine: true,
                    from: cursor,
                    to: Math.Max(
                        val1: cursor,
                        val2: item.Offset
                    )
                );

                if (gap.SameLine is not null) {
                    if (result.Count > 0) {
                        result[^1] = ((T)result[^1].WithTrivia(trivia: (result[^1].Trivia with { Trailing = gap.SameLine })));
                    } else { opening = gap.SameLine; }
                }
                if (result.Count > 0) {
                    result[^1] = ((T)result[^1].WithTrivia(trivia: (result[^1].Trivia with { Separated = gap.Separated })));
                }

                var visited = visit(arg: item);

                result.Add(item: ((T)visited.WithTrivia(trivia: (visited.Trivia with { Leading = gap.Pieces, OnNewLine = gap.Broke }))));
                cursor = Math.Max(
                    val1: cursor,
                    val2: ContentEnd(node: item)
                );
            }

            var tail = ScanGap(
                allowSameLine: (result.Count > 0),
                from: cursor,
                to: Math.Max(
                    val1: cursor,
                    val2: bodyEnd
                )
            );

            if (tail.SameLine is not null) {
                if (result.Count > 0) {
                    result[^1] = ((T)result[^1].WithTrivia(trivia: (result[^1].Trivia with { Trailing = tail.SameLine })));
                } else { opening = tail.SameLine; }
            }
            if (result.Count > 0) {
                result[^1] = ((T)result[^1].WithTrivia(trivia: (result[^1].Trivia with { Separated = tail.Separated })));
            }
            m_opening = opening;

            return (result, tail.Pieces);
        }
        // One of a test's three blocks: its leading trivia read from the gap since the previous block, its items'
        // own, and the trivia standing before its closing brace. `cursor` advances past the block's close so the
        // next block's leading trivia starts where this one ended.
        private TBlock? TestBlock<TBlock, TItem>(TBlock? block, IReadOnlyList<TItem>? items, ref int cursor, Func<TBlock, IReadOnlyList<TItem>, IReadOnlyList<TriviaPiece>, TBlock> rebuild)
            where TBlock : SyntaxNode
            where TItem : SyntaxNode {
            if ((block is null) || (items is null)) {
                return block;
            }

            var leading = ScanGap(
                allowSameLine: false,
                from: cursor,
                to: Math.Max(
                val1: cursor,
                val2: block.Offset
            )
            );
            var open = FindNext(
                from: block.Offset,
                limit: ContentEnd(node: block),
                target: '{'
            );
            var close = MatchDelimiter(open: open);

            if ((open < 0) || (close < 0)) {
                return block;
            }

            var (walked, inner) = List(
                bodyEnd: close,
                bodyStart: (open + 1),
                items: items,
                visit: TestItem
            );

            cursor = (close + 1);

            return ((TBlock)rebuild(
                arg1: block,
                arg2: walked,
                arg3: inner
            ).WithTrivia(trivia: (block.Trivia with { Leading = leading.Pieces, Opening = m_opening })));
        }
        // A world block standing among a test block's own lines: its inner lines are walked here, so a comment or a
        // blank-line run inside one keeps its place the way one outside it does.
        private TItem TestItem<TItem>(TItem item) where TItem : SyntaxNode => (item switch {
            TestGivenWorldNode addressed => ((TItem)((SyntaxNode)TestWorldBlock(
                block: addressed,
                items: addressed.Cells,
                rebuild: static (block, cells, inner) => (block with { Cells = cells, Trivia = (block.Trivia with { Inner = inner }) })
            ))),
            TestWhenWorldNode addressed => ((TItem)((SyntaxNode)TestWorldBlock(
                block: addressed,
                items: addressed.Steps,
                rebuild: static (block, steps, inner) => (block with { Steps = steps, Trivia = (block.Trivia with { Inner = inner }) })
            ))),
            TestExpectWorldNode addressed => ((TItem)((SyntaxNode)TestWorldBlock(
                block: addressed,
                items: addressed.Expectations,
                rebuild: static (block, expectations, inner) => (block with { Expectations = expectations, Trivia = (block.Trivia with { Inner = inner }) })
            ))),
            _ => item,
        });
        private TBlock TestWorldBlock<TBlock, TItem>(TBlock block, IReadOnlyList<TItem> items, Func<TBlock, IReadOnlyList<TItem>, IReadOnlyList<TriviaPiece>, TBlock> rebuild)
            where TBlock : SyntaxNode
            where TItem : SyntaxNode {
            var open = FindNext(
                from: block.Offset,
                limit: ContentEnd(node: block),
                target: '{'
            );
            var close = MatchDelimiter(open: open);

            if ((open < 0) || (close < 0)) {
                return block;
            }

            var (walked, inner) = List(
                bodyEnd: close,
                bodyStart: (open + 1),
                items: items,
                visit: static item => item
            );
            // The enclosing List reads m_opening only after its own last visit, so the nested read has to happen
            // here, while it still holds this block's own opening comment.
            var opening = m_opening;

            return ((TBlock)rebuild(
                arg1: block,
                arg2: walked,
                arg3: inner
            ).WithTrivia(trivia: (block.Trivia with { Opening = opening })));
        }
        // The comments standing inside a construct's own header, between its first token and its opening brace.
        private IReadOnlyList<TriviaPiece> Header(int from, int open) => ((open <= from)
            ? []
            : ScanGap(
                allowSameLine: false,
                from: from,
                to: open
            ).Pieces
        );
        // The body of a braced construct, located by the opener that follows `openFrom`.
        private (IReadOnlyList<StatementNode> Items, IReadOnlyList<TriviaPiece> Inner, IReadOnlyList<TriviaPiece> Header) Braced(IReadOnlyList<StatementNode> statements, int openFrom, char opener, int headerFrom = -1, int limit = -1) {
            var open = FindNext(
                from: openFrom,
                limit: limit,
                target: opener
            );
            var close = MatchDelimiter(open: open);

            if ((open < 0) || (close < 0)) {
                return (
                    [.. statements.Select(selector: VisitStatement)],
                    [],
                    []
                );
            }

            var (items, inner) = List(
                bodyEnd: close,
                bodyStart: (open + 1),
                items: statements,
                visit: VisitStatement
            );

            return (items, inner, Header(
                from: ((headerFrom < 0)
                ? open
                : headerFrom),
                open: open
            ));
        }

        public DocumentNode Document(DocumentNode document) {
            var headerEnd = 0;
            var headerStart = m_source.Length;
            var header = false;

            if (
                (document.Schema is not null) &&
                (document.SchemaSpan.Length > 0)
            ) {
                headerStart = Math.Min(
                    val1: headerStart,
                    val2: document.SchemaSpan.Offset
                );
                headerEnd = Math.Max(
                    val1: headerEnd,
                    val2: (document.SchemaSpan.Offset + document.SchemaSpan.Length)
                );
                header = true;
            }
            if (
                (document.Basis is not null) &&
                (document.BasisSpan.Length > 0)
            ) {
                headerStart = Math.Min(
                    val1: headerStart,
                    val2: document.BasisSpan.Offset
                );
                headerEnd = Math.Max(
                    val1: headerEnd,
                    val2: (document.BasisSpan.Offset + document.BasisSpan.Length)
                );
                header = true;
            }
            if (document.Statements.Count > 0) {
                headerStart = Math.Min(
                    val1: headerStart,
                    val2: document.Statements[0].Offset
                );
            }

            var leading = ScanGap(
                allowSameLine: false,
                from: 0,
                to: headerStart
            );

            var (statements, inner) = List(
                bodyEnd: m_source.Length,
                bodyStart: (header
                ? headerEnd
                : headerStart),
                items: document.Statements,
                visit: VisitStatement
            );

            return (document with {
                Statements = statements,
                Trivia = new SyntaxTrivia {
                    Inner = [.. inner.Where(predicate: static piece => (piece.Kind == TriviaKind.Comment))],
                    Leading = leading.Pieces,
                },
            });
        }
        public StatementNode VisitStatement(StatementNode statement) {
            switch (statement) {
                case WorldDeclarationNode world: {
                        return (world with {
                            Module = ((CallExpressionNode)VisitExpression(expression: world.Module)),
                            Name = VisitExpression(expression: world.Name),
                        });
                    }
                case WorldLinkNode link when (link.Kind == "border"): {
                        var (properties, inner, header) = Braced(
                            headerFrom: link.Offset,
                            limit: ContentEnd(node: link),
                            openFrom: ContentEnd(node: link.Right),
                            opener: '{',
                            statements: link.Properties
                        );

                        return (link with {
                            Left = VisitExpression(expression: link.Left),
                            Properties = properties,
                            Right = VisitExpression(expression: link.Right),
                            Trivia = (link.Trivia with { Header = header, Inner = inner, Opening = m_opening }),
                        });
                    }
                case WorldLinkNode link: {
                        return (link with {
                            Left = VisitExpression(expression: link.Left),
                            Right = VisitExpression(expression: link.Right),
                        });
                    }
                case BlockNode block: {
                        var (statements, inner, header) = Braced(
                            headerFrom: block.Offset,
                            limit: ContentEnd(node: block),
                            openFrom: block.Offset,
                            opener: '{',
                            statements: block.Statements
                        );

                        return (block with { Statements = statements, Trivia = (block.Trivia with { Header = header, Inner = inner, Opening = m_opening }) });
                    }
                case TemplateNode template: {
                        return (template with {
                            Body = ((BlockNode)VisitStatement(statement: template.Body)),
                            Parameters = [.. template.Parameters.Select(selector: parameter => (parameter with { DefaultValue = VisitOptional(expression: parameter.DefaultValue) }))],
                        });
                    }
                case ForStatementNode loop: {
                        var (statements, inner, header) = Braced(
                            openFrom: ContentEnd(node: loop.Sequence),
                            opener: '{',
                            statements: loop.Body
                        );

                        return (loop with {
                            Body = statements,
                            Sequence = VisitExpression(expression: loop.Sequence),
                            Trivia = (loop.Trivia with { Header = header, Inner = inner, Opening = m_opening }),
                        });
                    }
                case RuleScopeNode scope: {
                        var (statements, inner, header) = Braced(
                            headerFrom: scope.Offset,
                            limit: ContentEnd(node: scope),
                            openFrom: scope.Offset,
                            opener: '{',
                            statements: scope.Statements
                        );

                        return (scope with { Statements = statements, Trivia = (scope.Trivia with { Header = header, Inner = inner, Opening = m_opening }) });
                    }
                case StabilizeGroupNode group: {
                        var (statements, inner, header) = Braced(
                            headerFrom: group.Offset,
                            limit: ContentEnd(node: group),
                            openFrom: Math.Max(val1: group.Offset, val2: ((group.Undo is { } undo) ? (undo.Offset + undo.Length) : group.Offset)),
                            opener: '{',
                            statements: group.Statements
                        );

                        return (group with { Statements = statements, Trivia = (group.Trivia with { Header = header, Inner = inner, Opening = m_opening }) });
                    }
                case WorkflowNode workflow: {
                        var open = FindNext(
                            from: ((workflow.Undo is { } undo) ? (undo.Offset + undo.Length) : workflow.Offset),
                            limit: ContentEnd(node: workflow),
                            target: '{'
                        );
                        var close = MatchDelimiter(open: open);

                        var (steps, inner) = List(
                            bodyEnd: ((close < 0)
                            ? workflow.Offset
                            : close),
                            bodyStart: ((open < 0)
                            ? workflow.Offset
                            : (open + 1)),
                            items: workflow.Steps,
                            visit: static step => step
                        );

                        return (workflow with {
                            Steps = [.. steps.Select(selector: step => ((WorkflowStepNode)VisitStatement(statement: step)))],
                            Trivia = (workflow.Trivia with {
                                Header = Header(
                                    from: workflow.Offset,
                                    open: open
                                ),
                                Inner = inner,
                            }),
                        });
                    }
                case WorkflowStepNode step: {
                        var (statements, inner, header) = Braced(
                            headerFrom: step.Offset,
                            limit: ContentEnd(node: step),
                            openFrom: step.Offset,
                            opener: '{',
                            statements: step.Statements
                        );

                        return (step with {
                            ForEachCollection = VisitOptional(expression: step.ForEachCollection),
                            Statements = statements,
                            Trivia = (step.Trivia with { Header = header, Inner = inner, Opening = m_opening }),
                        });
                    }
                case RuleBlockNode rule: {
                        var (statements, inner, header) = Braced(
                            headerFrom: rule.Offset,
                            limit: ContentEnd(node: rule),
                            openFrom: rule.Offset,
                            opener: '{',
                            statements: rule.Statements
                        );

                        return (rule with { Statements = statements, Trivia = (rule.Trivia with { Header = header, Inner = inner, Opening = m_opening }) });
                    }
                case DecisionBlockNode decision: {
                        var (statements, inner, header) = Braced(
                            headerFrom: decision.Offset,
                            limit: ContentEnd(node: decision),
                            openFrom: decision.Offset,
                            opener: '{',
                            statements: decision.Statements
                        );

                        return (decision with { Statements = statements, Trivia = (decision.Trivia with { Header = header, Inner = inner, Opening = m_opening }) });
                    }
                case OptionBlockNode option: {
                        var (statements, inner, header) = Braced(
                            headerFrom: option.Offset,
                            limit: ContentEnd(node: option),
                            openFrom: option.Offset,
                            opener: '{',
                            statements: option.Statements
                        );

                        return (option with { Statements = statements, Trivia = (option.Trivia with { Header = header, Inner = inner, Opening = m_opening }) });
                    }
                case OnNoChoiceBlockNode fallback: {
                        var (statements, inner, header) = Braced(
                            headerFrom: fallback.Offset,
                            limit: ContentEnd(node: fallback),
                            openFrom: fallback.Offset,
                            opener: '{',
                            statements: fallback.Effects
                        );

                        return (fallback with { Effects = statements, Trivia = (fallback.Trivia with { Header = header, Inner = inner, Opening = m_opening }) });
                    }
                case TransactionStatementNode transaction: {
                        var open = FindNext(
                            from: transaction.Offset,
                            limit: ContentEnd(node: transaction),
                            target: '{'
                        );
                        var close = MatchDelimiter(open: open);

                        var (main, inner) = (((open < 0) || (close < 0))
                            ? (((IReadOnlyList<StatementNode>)[.. transaction.MainEffects.Select(selector: VisitStatement)]), ((IReadOnlyList<TriviaPiece>)[]))
                            : List(
                                bodyEnd: close,
                                bodyStart: (open + 1),
                                items: transaction.MainEffects,
                                visit: VisitStatement
                            ));

                        var opening = m_opening;

                        if (transaction.OnFailureEffects is null) {
                            return (transaction with { MainEffects = main, Trivia = (transaction.Trivia with { Header = Header(from: transaction.Offset, open: open), Inner = inner, Opening = opening }) });
                        }

                        var (failure, failureInner, _) = Braced(
                            limit: ContentEnd(node: transaction),
                            openFrom: (close + 1),
                            opener: '{',
                            statements: transaction.OnFailureEffects
                        );

                        return (transaction with {
                            MainEffects = main,
                            OnFailureEffects = failure,
                            Trivia = (transaction.Trivia with {
                                Alternate = failureInner,
                                Header = Header(from: transaction.Offset, open: open),
                                Inner = inner,
                                Opening = opening,
                            }),
                        });
                    }
                case IfStatementNode branch: {
                        var open = FindNext(
                            from: branch.Offset,
                            limit: ContentEnd(node: branch),
                            target: '{'
                        );
                        var close = MatchDelimiter(open: open);

                        var (then, inner) = (((open < 0) || (close < 0))
                            ? (((IReadOnlyList<StatementNode>)[.. branch.Then.Select(selector: VisitStatement)]), ((IReadOnlyList<TriviaPiece>)[]))
                            : List(
                                bodyEnd: close,
                                bodyStart: (open + 1),
                                items: branch.Then,
                                visit: VisitStatement
                            ));

                        var opening = m_opening;

                        if (branch.Else is null) {
                            return (branch with { Then = then, Trivia = (branch.Trivia with { Header = Header(from: branch.Offset, open: open), Inner = inner, Opening = opening }) });
                        }

                        // `else if` chains as one nested branch and opens no body of its own.
                        if (
                            (branch.Else.Count == 1) &&
                            (branch.Else[0] is IfStatementNode nested)
                        ) {
                            return (branch with {
                                Else = [VisitStatement(statement: nested)],
                                Then = then,
                                Trivia = (branch.Trivia with { Header = Header(from: branch.Offset, open: open), Inner = inner, Opening = opening }),
                            });
                        }

                        var (otherwise, otherwiseInner, _) = Braced(
                            limit: ContentEnd(node: branch),
                            openFrom: (close + 1),
                            opener: '{',
                            statements: branch.Else
                        );

                        return (branch with {
                            Else = otherwise,
                            Then = then,
                            Trivia = (branch.Trivia with {
                                Alternate = otherwiseInner,
                                Header = Header(from: branch.Offset, open: open),
                                Inner = inner,
                                Opening = opening,
                            }),
                        });
                    }
                case RepeatStatementNode loop: {
                        var (statements, inner, header) = Braced(
                            headerFrom: loop.Offset,
                            limit: ContentEnd(node: loop),
                            openFrom: loop.Offset,
                            opener: '{',
                            statements: loop.Body
                        );

                        return (loop with {
                            Body = statements,
                            Count = VisitExpression(expression: loop.Count),
                            Trivia = (loop.Trivia with { Header = header, Inner = inner, Opening = m_opening }),
                        });
                    }
                case ClaimStatementNode claim: {
                        var (statements, inner, header) = Braced(headerFrom: claim.Offset, limit: ContentEnd(node: claim), openFrom: claim.Offset, opener: '{', statements: claim.Body);
                        return (claim with { Body = statements, Trivia = (claim.Trivia with { Header = header, Inner = inner, Opening = m_opening }) });
                    }
                case ClaimPairStatementNode claim: {
                        var (statements, inner, header) = Braced(headerFrom: claim.Offset, limit: ContentEnd(node: claim), openFrom: claim.Offset, opener: '{', statements: claim.Body);
                        return (claim with { Body = statements, Trivia = (claim.Trivia with { Header = header, Inner = inner, Opening = m_opening }) });
                    }
                case PoolForEachStatementNode each: {
                        var (statements, inner, header) = Braced(headerFrom: each.Offset, limit: ContentEnd(node: each), openFrom: each.Offset, opener: '{', statements: each.Body);
                        return (each with { Body = statements, Trivia = (each.Trivia with { Header = header, Inner = inner, Opening = m_opening }) });
                    }
                case PropertyNode property: {
                        return (property with { Value = VisitExpression(expression: property.Value) });
                    }
                case LetNode declaration: {
                        return (declaration with { Value = VisitExpression(expression: declaration.Value) });
                    }
                case DerivedStateNode derived: {
                        return (derived with { Expression = ((OperandExpressionNode)VisitExpression(expression: derived.Expression)) });
                    }
                case ExpressionStatementNode expression: {
                        return (expression with { Expression = VisitExpression(expression: expression.Expression) });
                    }
                case TransformStatementNode transform: {
                        return (transform with { Transform = ((CallExpressionNode)VisitExpression(expression: transform.Transform)) });
                    }
                case EnumDeclarationNode declaration: {
                        var open = FindNext(
                            from: declaration.Offset,
                            limit: ContentEnd(node: declaration),
                            target: '{'
                        );
                        var close = MatchDelimiter(open: open);

                        if ((open < 0) || (close < 0)) {
                            return declaration;
                        }

                        var (members, inner) = List(
                            bodyEnd: close,
                            bodyStart: (open + 1),
                            items: declaration.Members,
                            visit: static member => member
                        );

                        return (declaration with { Members = members, Trivia = (declaration.Trivia with { Inner = inner, Opening = m_opening }) });
                    }
                case TestDeclarationNode declaration: {
                        var open = FindNext(
                            from: declaration.Offset,
                            limit: ContentEnd(node: declaration),
                            target: '{'
                        );
                        var close = MatchDelimiter(open: open);

                        if ((open < 0) || (close < 0)) {
                            return declaration;
                        }

                        var cursor = (open + 1);
                        var given = TestBlock(
                            block: declaration.Given,
                            cursor: ref cursor,
                            items: declaration.Given?.Cells,
                            rebuild: static (block, cells, inner) => (block with {
                                Cells = cells,
                                Trivia = (block.Trivia with { Inner = inner }),
                            })
                        );
                        var when = TestBlock(
                            block: declaration.When,
                            cursor: ref cursor,
                            items: declaration.When?.Steps,
                            rebuild: static (block, steps, inner) => (block with {
                                Steps = steps,
                                Trivia = (block.Trivia with { Inner = inner }),
                            })
                        );
                        var expect = TestBlock(
                            block: declaration.Expect,
                            cursor: ref cursor,
                            items: declaration.Expect?.Expectations,
                            rebuild: static (block, expectations, inner) => (block with {
                                Expectations = expectations,
                                Trivia = (block.Trivia with { Inner = inner }),
                            })
                        );

                        return (declaration with {
                            Expect = expect,
                            Given = given,
                            Trivia = (declaration.Trivia with {
                                Header = Header(
                                from: (declaration.Offset + "test".Length),
                                open: open
                            ),
                                Inner = ScanGap(
                                allowSameLine: false,
                                from: cursor,
                                to: Math.Max(
                                val1: cursor,
                                val2: close
                            )
                            ).Pieces,
                            }),
                            When = when,
                        });
                    }
                case RecordDeclarationNode declaration: {
                        var open = FindNext(
                            from: declaration.Offset,
                            limit: ContentEnd(node: declaration),
                            target: '{'
                        );
                        var close = MatchDelimiter(open: open);

                        if ((open < 0) || (close < 0)) {
                            return declaration;
                        }

                        var (fields, inner) = List(
                            bodyEnd: close,
                            bodyStart: (open + 1),
                            items: declaration.Fields,
                            visit: field => (field with {
                                Default = VisitOptional(expression: field.Default),
                                Modifiers = VisitModifiers(modifiers: field.Modifiers),
                            })
                        );

                        return (declaration with { Fields = fields, Trivia = (declaration.Trivia with { Inner = inner, Opening = m_opening }) });
                    }
                case StatePoolDeclarationNode pool:
                    return (pool with {
                        Capacity = VisitOptional(expression: pool.Capacity),
                        Initializer = VisitOptional(expression: pool.Initializer),
                    });
                case StatePairPoolDeclarationNode pool:
                    return pool;
                case StateTableDeclarationNode table: {
                        var (cells, inner, header) = Cells(
                            cells: table.Cells,
                            limit: ContentEnd(node: table),
                            openFrom: table.Offset
                        );

                        return (table with {
                            Cells = cells,
                            FamilySize = VisitOptional(expression: table.FamilySize),
                            Initializer = VisitOptional(expression: table.Initializer),
                            Modifiers = VisitModifiers(modifiers: table.Modifiers),
                            Trivia = (table.Trivia with { Header = header, Inner = inner, Opening = m_opening }),
                        });
                    }
                case StateGridDeclarationNode grid: {
                        var (cells, inner, header) = (grid.HasBody
                            ? Cells(
                                cells: grid.Cells,
                                limit: ContentEnd(node: grid),
                                openFrom: grid.Offset
                            )
                            : (((IReadOnlyList<StateCellEntryNode>)grid.Cells), ((IReadOnlyList<TriviaPiece>)[]), ((IReadOnlyList<TriviaPiece>)[])));

                        return (grid with {
                            Cells = cells,
                            FamilySize = VisitOptional(expression: grid.FamilySize),
                            Modifiers = VisitModifiers(modifiers: grid.Modifiers),
                            Trivia = (grid.Trivia with { Header = header, Inner = inner, Opening = m_opening }),
                        });
                    }
                case StateSlotDeclarationNode slot: {
                        return (slot with {
                            FamilySize = VisitOptional(expression: slot.FamilySize),
                            Modifiers = VisitModifiers(modifiers: slot.Modifiers),
                            Value = VisitOptional(expression: slot.Value),
                        });
                    }
                case StatePileDeclarationNode pile: {
                        return (pile with {
                            FamilySize = VisitOptional(expression: pile.FamilySize),
                            Initializer = VisitOptional(expression: pile.Initializer),
                            Modifiers = VisitModifiers(modifiers: pile.Modifiers),
                        });
                    }
                default: {
                        return statement;
                    }
            }
        }

        private (IReadOnlyList<StateCellEntryNode> Cells, IReadOnlyList<TriviaPiece> Inner, IReadOnlyList<TriviaPiece> Header) Cells(IReadOnlyList<StateCellEntryNode> cells, int openFrom, int limit) {
            var open = FindNext(
                from: openFrom,
                limit: limit,
                target: '{'
            );
            var close = MatchDelimiter(open: open);

            if ((open < 0) || (close < 0)) {
                return (cells, [], []);
            }

            var (entries, inner) = List(
                bodyEnd: close,
                bodyStart: (open + 1),
                items: cells,
                visit: cell => (cell with {
                    Modifiers = VisitModifiers(modifiers: cell.Modifiers),
                    Value = VisitExpression(expression: cell.Value),
                })
            );

            return (entries, inner, Header(
                from: openFrom,
                open: open
            ));
        }
        private IReadOnlyList<StateModifierNode> VisitModifiers(IReadOnlyList<StateModifierNode> modifiers) =>
            [.. modifiers.Select(selector: modifier => (modifier with { Arguments = VisitArguments(arguments: modifier.Arguments, limit: ContentEnd(node: modifier), openFrom: modifier.Offset) }))];
        private IReadOnlyList<ArgumentNode> VisitArguments(IReadOnlyList<ArgumentNode> arguments, int openFrom, int limit) {
            var open = FindNext(
                from: openFrom,
                limit: limit,
                target: '('
            );
            var close = MatchParenthesis(open: open);

            if (
                (arguments.Count == 0) ||
                (open < 0) ||
                (close < 0)
            ) {
                return [.. arguments.Select(selector: argument => (argument with { Value = VisitExpression(expression: argument.Value) }))];
            }

            var (visited, _) = List(
                bodyEnd: close,
                bodyStart: (open + 1),
                items: arguments,
                visit: argument => (argument with { Value = VisitExpression(expression: argument.Value) })
            );

            return visited;
        }
        private int MatchParenthesis(int open) {
            if ((open < 0) || (open >= m_source.Length)) {
                return -1;
            }

            var depth = 0;
            var index = open;

            while (index < m_source.Length) {
                var lexeme = SourceLexemes.End(
                    offset: index,
                    source: m_source
                );

                if (lexeme > index) { index = lexeme; continue; }
                if (m_source[index] == '(') { depth++; } else if (m_source[index] == ')') {
                    depth--;
                    if (depth == 0) { return index; }
                }
                index++;
            }

            return -1;
        }
        private ExpressionNode? VisitOptional(ExpressionNode? expression) => ((expression is null)
            ? null
            : VisitExpression(expression: expression)
        );

        public ExpressionNode VisitExpression(ExpressionNode expression) {
            switch (expression) {
                case ArrayExpressionNode array: {
                        var close = MatchDelimiter(open: array.Offset);

                        var (elements, inner) = ((close < 0)
                            ? (((IReadOnlyList<ExpressionNode>)[.. array.Elements.Select(selector: VisitExpression)]), ((IReadOnlyList<TriviaPiece>)[]))
                            : List(
                                bodyEnd: close,
                                bodyStart: (array.Offset + 1),
                                items: array.Elements,
                                visit: VisitExpression
                            )
                        );

                        return (array with {
                            Elements = elements,
                            Trivia = (array.Trivia with { Inner = inner, MultiLine = SpansLines(node: array), Opening = m_opening }),
                        });
                    }
                case ObjectExpressionNode @object: {
                        var close = MatchDelimiter(open: @object.Offset);

                        var (properties, inner) = ((close < 0)
                            ? (((IReadOnlyList<PropertyNode>)[.. @object.Properties.Select(selector: property => ((PropertyNode)VisitStatement(statement: property)))]), ((IReadOnlyList<TriviaPiece>)[]))
                            : List(
                                bodyEnd: close,
                                bodyStart: (@object.Offset + 1),
                                items: @object.Properties,
                                visit: property => ((PropertyNode)VisitStatement(statement: property))
                            )
                        );

                        return (@object with {
                            Properties = properties,
                            Trivia = (@object.Trivia with { Inner = inner, MultiLine = SpansLines(node: @object), Opening = m_opening }),
                        });
                    }
                case CallExpressionNode call: {
                        return (call with {
                            Arguments = VisitArguments(
                            arguments: call.Arguments,
                            limit: ContentEnd(node: call),
                            openFrom: call.Offset
                        ),
                            Trivia = (call.Trivia with { MultiLine = SpansLines(node: call) }),
                        });
                    }
                case BinaryExpressionNode binary: {
                        return (binary with {
                            Left = VisitExpression(expression: binary.Left),
                            Right = VisitExpression(expression: binary.Right),
                        });
                    }
                case ConditionalExpressionNode conditional: {
                        return (conditional with {
                            Condition = VisitExpression(expression: conditional.Condition),
                            WhenFalse = VisitExpression(expression: conditional.WhenFalse),
                            WhenTrue = VisitExpression(expression: conditional.WhenTrue),
                        });
                    }
                case UnaryExpressionNode unary: {
                        return (unary with { Operand = VisitExpression(expression: unary.Operand) });
                    }
                case IndexExpressionNode index: {
                        return (index with {
                            Index = VisitExpression(expression: index.Index),
                            Target = VisitExpression(expression: index.Target),
                        });
                    }
                case RangeExpressionNode range: {
                        return (range with {
                            End = ((range.End is { } end) ? VisitExpression(expression: end) : null),
                            Start = ((range.Start is { } start) ? VisitExpression(expression: start) : null),
                        });
                    }
                case MemberAccessExpressionNode member: {
                        return (member with { Target = VisitExpression(expression: member.Target) });
                    }
                case LambdaExpressionNode lambda: {
                        return (lambda with { Body = VisitExpression(expression: lambda.Body) });
                    }
                default: {
                        return expression;
                    }
            }
        }

        private bool SpansLines(SyntaxNode node) {
            var start = Math.Clamp(
                max: m_source.Length,
                min: 0,
                value: node.Offset
            );
            var end = Math.Clamp(
                max: m_source.Length,
                min: start,
                value: ContentEnd(node: node)
            );

            return (m_source.AsSpan(
                length: (end - start),
                start: start
            ).IndexOf(value: '\n') >= 0);
        }
    }
}
