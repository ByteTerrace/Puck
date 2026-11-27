using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Rewriting;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck migrate frame</c>: converts every <c>puck.creation.v1</c> creation, written in the retired author frame (a
/// half turn about +Y from the engine's, so the engine negated x and z of every position), into a
/// <c>puck.creation.v2</c> creation written in the engine's own frame (+Y up, −Z the front a creation faces, +X its right),
/// so each draws, collides and moves exactly as it did.</summary>
/// <remarks>
/// <para>The rewrite is mechanical and exact. Every creation-frame field the retired conversion turned is turned once in
/// the source: a position, a joint, a pivot, an axis, a probe direction or offset and a symmetry normal negate their x and
/// z (an array literal element by element, anything else as <c>v * [-1, 1, -1]</c>), and a rotation <c>q</c> becomes the
/// half turn followed by it, <c>compose(rotate([0, 1, 0], 180), q)</c>, which a literal or a yaw folds. A shape whose
/// field and frame readers cannot tell its own half turn apart (a box, a sphere and the other primitives symmetric under
/// it, with no facet, paint, face, run, camera, chain or frame that reads its local axes) takes the conjugate instead,
/// <c>q</c> with its x and z negated, so an unturned box stays unturned; whether a shape statement may drop its half turn
/// is decided from every shape it compiles to, in every document of the scan.</para>
/// <para>The schema version is the marker: a <c>v1</c> creation is converted and becomes <c>v2</c>; a <c>v2</c> creation is
/// left alone, so the migration is idempotent. A template, or a function or <c>let</c> whose value a creation reads as one
/// of its rows, is converted when a <c>v1</c> creation reaches it and no <c>v2</c> creation does; one both reach is refused
/// by name, to convert by hand.</para>
/// <para>The proof is the documents themselves: every source of the scan that compiles before the rewrite compiles after it
/// to the document it did before with every <c>v1</c> creation turned by the retired conversion, member for member
/// (<see cref="DocumentDifference"/>), the half turn a symmetric shape dropped excepted.</para>
/// <para>Retire this migration, with <c>puck.creation.v1</c>'s refusal, once no branch holds a <c>v1</c> creation.</para>
/// </remarks>
internal sealed class CreationFrameMigration : PuckMigration {
    private const string OldSchema = "puck.creation.v1";
    private const string NewSchema = "puck.creation.v2";
    // A half turn about +Y, the retired conversion's quaternion: (x, y, z, w).
    private static readonly Quaternion HalfTurn = new(x: 0f, y: 1f, z: 0f, w: 0f);
    private static readonly HashSet<string> SymmetricTypes = new(collection: [
        "Sphere", "Box", "Torus", "Cylinder", "Capsule", "Ellipsoid", "RoundCone", "Plane", "Cone", "Superellipsoid",
    ], comparer: StringComparer.Ordinal);
    // The shape members whose geometry reads the shape's own x or z axis one way: its local half turn would mirror them.
    private static readonly string[] AsymmetricShapeMembers = [
        "profile", "lift", "panel", "trims", "flare", "bumps", "shear", "curve", "erode", "cells", "warp", "displace",
        "domain", "lightsAs", "bend", "taper",
    ];
    // The palette members that read the frame of the slot that won a hit, which on a moving or body placement is the
    // shape's own.
    private static readonly string[] FramedPaletteMembers = ["paints", "inset", "weathering", "flow", "shade", "fill"];
    private static readonly string[] MotionMembers = ["frames", "chains", "drivers", "effectors", "clips", "parts"];

    private enum Kind {
        Vector,
        RotationExact,
        SwingList,
        SlideList,
        DomainList,
        TextRunList,
        VolumeList,
        EffectorList,
        CameraList,
        FrameList,
        ClipList,
        ShapeList,
        TransformList,
        KeyList,
        PoseList,
        TurnList,
        Swing,
        Slide,
        TextRun,
        Volume,
        Effector,
        EffectorTarget,
        Camera,
        Frame,
        Clip,
        Shape,
        Transform,
        Key,
        Pose,
        Turn,
        CreationBody,
    }

    private static readonly Dictionary<string, Kind> CreationLists = new(comparer: StringComparer.Ordinal) {
        ["textRuns"] = Kind.TextRunList,
        ["volumes"] = Kind.VolumeList,
        ["effectors"] = Kind.EffectorList,
        ["cameras"] = Kind.CameraList,
        ["frames"] = Kind.FrameList,
        ["clips"] = Kind.ClipList,
        ["shapes"] = Kind.ShapeList,
    };

    private sealed record Definition(SyntaxNode Node, string? Path);

    // What the analysis found: the nodes to convert and as what, keyed by identity.
    private readonly Dictionary<SyntaxNode, Kind> m_convert = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly HashSet<SyntaxNode> m_reachedByNew = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<string> m_refusals = [];
    // A shape statement may drop its half turn only when every shape it compiles to may: (source, offset) to verdict.
    private readonly List<(string Path, int Offset, string? Name, bool Verdict)> m_dropVerdicts = [];
    private readonly HashSet<string> m_slotFramedPrototypes = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, List<Definition>> m_topLevel = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<SyntaxNode, string> m_pathOf = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IReadOnlyList<StatementNode>> m_files = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, List<StatementNode>> m_statementsByPath = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<SyntaxNode, bool> m_shapeVerdicts = new(comparer: ReferenceEqualityComparer.Instance);
    private string? m_currentPath;

    /// <inheritdoc/>
    public override string Name => "frame";
    /// <inheritdoc/>
    public override IReadOnlyList<string> ReshapedMembers => [];
    /// <inheritdoc/>
    public override string Summary => "Writes every puck.creation.v1 creation in the engine's frame as puck.creation.v2, so it draws exactly as before.";
    /// <inheritdoc/>
    public override bool ProvesAcrossSources => true;
    /// <inheritdoc/>
    public override IReadOnlyList<string> Refusals => m_refusals;

    // ---------------------------------------------------------------------------------------------------------------
    // Analysis
    // ---------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void Prepare(IReadOnlyList<MigrationSource> sources) {
        ArgumentNullException.ThrowIfNull(argument: sources);
        m_convert.Clear();
        m_reachedByNew.Clear();
        m_refusals.Clear();
        m_dropVerdicts.Clear();
        m_slotFramedPrototypes.Clear();
        m_templates.Clear();
        m_templateLists.Clear();
        m_topLevel.Clear();
        m_pathOf.Clear();

        foreach (var source in sources) {
            if (source.Before is { } before) { CollectSlotFramed(document: before); }
        }
        foreach (var source in sources) {
            if ((source.Before is { } before) && (source.Map is { } map)) {
                CollectVerdicts(document: before, map: map, path: source.Path);
            }
        }
        m_files.Clear();
        m_statementsByPath.Clear();
        m_shapeVerdicts.Clear();
        foreach (var source in sources) {
            m_files[Path.GetFullPath(path: source.Path)] = source.Document.Statements;
            foreach (var statement in source.Document.Statements) {
                Index(path: Path.GetFullPath(path: source.Path), statement: statement, topLevel: true);
            }
        }
        PropagateVerdicts();

        var anyNew = false;

        foreach (var source in sources) {
            m_currentPath = source.Path;
            foreach (var (block, isNew) in CreationBlocks(statements: source.Document.Statements, standalone: source.Document.Schema)) {
                anyNew |= isNew;
                Analyze(block: block, isNew: isNew, scopes: [source.Document.Statements]);
            }
            if (IsCreationSchema(schema: source.Document.Schema)) {
                var isNew = (source.Document.Schema == NewSchema);

                anyNew |= isNew;
                Analyze(block: source.Document, isNew: isNew, scopes: [source.Document.Statements]);
            }
        }

        foreach (var node in m_reachedByNew) {
            if (m_convert.Remove(key: node)) {
                m_refusals.Add(item: $"{Describe(node: node)} is read by both a {OldSchema} and a {NewSchema} creation; convert its {OldSchema} callers by hand");
            }
        }

        // A template that builds shapes and that no creation reaches is converted only on a scan holding no converted
        // creation at all, the first run: on any later run it is already converted, or new and unreached, and is named.
        foreach (var source in sources) {
            foreach (var statement in source.Document.Statements) {
                if ((statement is TemplateNode template) && !m_convert.ContainsKey(key: template) && !m_reachedByNew.Contains(item: template) && BuildsShapes(statements: template.Body.Statements)) {
                    if (anyNew) {
                        m_refusals.Add(item: $"{Describe(node: template)} builds shapes no creation of the scan reaches; convert it by hand or delete it");
                    } else {
                        m_currentPath = source.Path;
                        m_convert[template] = Kind.CreationBody;
                        AnalyzeStatements(statements: template.Body.Statements, isNew: false, scopes: [template.Body.Statements, source.Document.Statements]);
                    }
                }
            }
        }
    }

    private static bool IsCreationSchema(string? schema) => (schema is OldSchema or NewSchema);

    private readonly Stack<TemplateNode> m_templates = new();
    private readonly Dictionary<TemplateNode, Dictionary<string, Kind>> m_templateLists = new(comparer: ReferenceEqualityComparer.Instance);

    // The arguments of a call that a template hands to a creation's lists, each with the list it fills.
    private IEnumerable<(ArgumentNode Argument, Kind List)> ListArguments(CallExpressionNode call, TemplateNode template) {
        if (!m_templateLists.TryGetValue(key: template, value: out var lists)) { yield break; }

        for (var index = 0; (index < call.Arguments.Count); index++) {
            var argument = call.Arguments[index];
            var name = (argument.Name ?? ((index < template.Parameters.Count) ? template.Parameters[index].Name : null));

            if ((name is not null) && lists.TryGetValue(key: name, value: out var list)) { yield return (argument, list); }
        }
    }

    private string Describe(SyntaxNode node) {
        var name = node switch {
            TemplateNode template => $"template '{template.Name}'",
            LetNode let => $"'{let.Name}'",
            _ => node.GetType().Name,
        };

        return $"{(m_pathOf.TryGetValue(key: node, value: out var path) ? path : "?")}({node.Line},{node.Column}): {name}";
    }

    private void Index(string path, StatementNode statement, bool topLevel) {
        if (topLevel && (statement is TemplateNode or LetNode)) {
            var name = (statement is TemplateNode template) ? template.Name : ((LetNode)statement).Name;

            if (!m_topLevel.TryGetValue(key: name, value: out var list)) {
                m_topLevel[name] = list = [];
            }

            list.Add(item: new Definition(Node: statement, Path: path));
        }

        m_pathOf[statement] = path;
        if (!m_statementsByPath.TryGetValue(key: path, value: out var statements)) {
            m_statementsByPath[path] = statements = [];
        }
        statements.Add(item: statement);
        foreach (var child in ChildStatements(statement: statement)) { Index(path: path, statement: child, topLevel: false); }
    }

    // A compiled shape is attributed to the statement its source map names: the shape statement itself, or, for a shape a
    // template built inside another template's expansion, the call or block that expanded it. Each verdict therefore
    // holds for every shape statement that statement can expand to and whose name can spell the shape's, so a shape that
    // must keep its half turn keeps it wherever the map placed it.
    private void PropagateVerdicts() {
        foreach (var (path, offset, name, verdict) in m_dropVerdicts) {
            if (Innermost(path: path, offset: offset) is not { } node) {
                if (!verdict) { m_refusals.Add(item: $"{path}: a shape that must keep its half turn was authored at offset {offset}, which no statement holds"); }
                continue;
            }

            foreach (var shape in Expansion(node: node, path: path, visited: new HashSet<SyntaxNode>(comparer: ReferenceEqualityComparer.Instance))) {
                if (!CanName(shape: shape, name: name)) { continue; }
                m_shapeVerdicts[shape] = (m_shapeVerdicts.TryGetValue(key: shape, value: out var held) ? (held && verdict) : verdict);
            }
        }
    }

    // Whether a shape statement can write a shape named `name` (null: unnamed). A header name read from a template's
    // parameter or written as an expression can spell anything.
    private static bool CanName(BlockNode shape, string? name) {
        if (shape.Target is null) { return (name is null); }
        if (shape.NameExpression is InterpolatedStringNode interpolated) {
            if (name is null) { return false; }

            var pattern = string.Concat(values: interpolated.Segments.Select(selector: static segment => ((segment is InterpolationSegment.Literal literal)
                ? System.Text.RegularExpressions.Regex.Escape(str: literal.Text)
                : ".*")));

            return System.Text.RegularExpressions.Regex.IsMatch(input: name, pattern: $"^{pattern}$", options: System.Text.RegularExpressions.RegexOptions.Singleline);
        }
        if (shape.NameExpression is not null) { return true; }
        if (shape.Name is null) { return (name is null); }

        return (!shape.NameQuoted || (shape.Name == name));
    }

    private StatementNode? Innermost(string path, int offset) {
        StatementNode? found = null;

        if (!m_statementsByPath.TryGetValue(key: path, value: out var statements)) { return null; }
        foreach (var statement in statements) {
            if ((statement.Offset <= offset) && (offset < (statement.Offset + Math.Max(val1: 1, val2: statement.Length))) && ((found is null) || (statement.Length <= found.Length))) {
                found = statement;
            }
        }

        return found;
    }

    private IEnumerable<BlockNode> Expansion(StatementNode node, string path, HashSet<SyntaxNode> visited) {
        if (!visited.Add(item: node)) { yield break; }
        if (node is BlockNode { Identifier: "shape" } shape) {
            yield return shape;
            yield break;
        }
        if (node is ExpressionStatementNode { Expression: CallExpressionNode call }) {
            var previous = m_currentPath;

            m_currentPath = path;
            var definition = Resolve(name: call.Name, scopes: [m_files.GetValueOrDefault(key: path) ?? []]);

            m_currentPath = previous;
            if (definition is { Node: TemplateNode template }) {
                foreach (var found in Expansion(node: template.Body, path: (definition.Path ?? path), visited: visited)) { yield return found; }
            }
            yield break;
        }
        foreach (var child in ChildStatements(statement: node)) {
            foreach (var found in Expansion(node: child, path: path, visited: visited)) { yield return found; }
        }
    }

    private static IEnumerable<StatementNode> ChildStatements(StatementNode statement) => statement switch {
        BlockNode block => block.Statements,
        TemplateNode template => [template.Body],
        ForStatementNode loop => loop.Body,
        ConditionalStatementNode conditional => [.. conditional.Then, .. (conditional.Else ?? [])],
        _ => [],
    };

    // Every creation document block, at any depth, with whether it is already converted.
    private IEnumerable<(BlockNode Block, bool IsNew)> CreationBlocks(IReadOnlyList<StatementNode> statements, string? standalone, bool inPrototype = false) {
        foreach (var statement in statements) {
            if ((statement is BlockNode { Identifier: "document" } block) && (CreationSchemaOf(document: block, inPrototype: inPrototype) is { } schema)) {
                yield return (block, (schema == NewSchema));
                continue;
            }
            foreach (var found in CreationBlocks(statements: ChildStatements(statement: statement).ToList(), standalone: standalone, inPrototype: (statement is BlockNode { Identifier: "prototype" }))) {
                yield return found;
            }
        }
    }

    // A document block's creation schema: its own literal, or, for a prototype's document, the literal of a template it
    // calls (a template that writes a creation's whole head), else the retired schema; null for a document that is not a
    // creation.
    private string? CreationSchemaOf(BlockNode document, bool inPrototype) {
        if (SchemaOf(statements: document.Statements) is { } literal) {
            return (IsCreationSchema(schema: literal) ? literal : null);
        }
        if (!inPrototype) { return null; }

        return (SchemaThroughTemplates(statements: document.Statements, visited: new HashSet<SyntaxNode>(comparer: ReferenceEqualityComparer.Instance)) ?? OldSchema);
    }

    private string? SchemaThroughTemplates(IReadOnlyList<StatementNode> statements, HashSet<SyntaxNode> visited) {
        foreach (var statement in statements) {
            if ((statement is ExpressionStatementNode { Expression: CallExpressionNode call }) && (Resolve(name: call.Name, scopes: [m_files.GetValueOrDefault(key: (m_currentPath ?? "")) ?? []]) is { Node: TemplateNode template }) && visited.Add(item: template)) {
                if ((SchemaOf(statements: template.Body.Statements) is { } literal) && IsCreationSchema(schema: literal)) { return literal; }
                if (SchemaThroughTemplates(statements: template.Body.Statements, visited: visited) is { } inner) { return inner; }
            }
        }

        return null;
    }

    private static string? SchemaOf(IReadOnlyList<StatementNode> statements) {
        foreach (var statement in statements) {
            if ((statement is PropertyNode { Name: "schema", Value: LiteralExpressionNode { Value: string text } })) {
                return text;
            }
        }

        return null;
    }

    // Whether statements write a creation's shapes themselves: shapes inside a prototype's document belong to that
    // document, which carries its own schema.
    private static bool BuildsShapes(IReadOnlyList<StatementNode> statements) {
        foreach (var statement in statements) {
            if (statement is BlockNode { Identifier: "shape" }) { return true; }
            if ((statement is not (TemplateNode or BlockNode { Identifier: "document" or "prototype" })) && BuildsShapes(statements: ChildStatements(statement: statement).ToList())) { return true; }
        }

        return false;
    }

    private void Analyze(SyntaxNode block, bool isNew, IReadOnlyList<IReadOnlyList<StatementNode>> scopes) {
        var statements = (block is BlockNode b) ? b.Statements : ((DocumentNode)block).Statements;

        AnalyzeStatements(statements: statements, isNew: isNew, scopes: [statements, .. scopes]);
    }

    private void AnalyzeStatements(IReadOnlyList<StatementNode> statements, bool isNew, IReadOnlyList<IReadOnlyList<StatementNode>> scopes) {
        foreach (var statement in statements) {
            switch (statement) {
                case ExpressionStatementNode { Expression: CallExpressionNode call }:
                    if (Resolve(name: call.Name, scopes: scopes) is { Node: TemplateNode template } definition) {
                        Mark(definition: definition, isNew: isNew, kind: Kind.CreationBody, scopes: scopes);
                        foreach (var (argument, list) in ListArguments(call: call, template: template)) {
                            AnalyzeElements(expression: argument.Value, isNew: isNew, kind: ElementOf(list: list), scopes: scopes);
                        }
                    }

                    break;
                case BlockNode { Identifier: "shape" } shape:
                    foreach (var property in shape.Statements.OfType<PropertyNode>()) {
                        if (property.Name is "swings") { AnalyzeElements(expression: property.Value, isNew: isNew, kind: Kind.Swing, scopes: scopes); }
                        if (property.Name is "slides") { AnalyzeElements(expression: property.Value, isNew: isNew, kind: Kind.Slide, scopes: scopes); }
                    }

                    break;
                case PropertyNode property when CreationLists.TryGetValue(key: property.Name, value: out var list):
                    // A template that hands a parameter to one of a creation's lists has its callers write those rows.
                    if ((property.Value is IdentifierExpressionNode parameter) && (m_templates.TryPeek(result: out var enclosing)) && enclosing.Parameters.Any(predicate: p => (p.Name == parameter.Name))) {
                        if (!m_templateLists.TryGetValue(key: enclosing, value: out var lists)) {
                            m_templateLists[enclosing] = lists = new Dictionary<string, Kind>(comparer: StringComparer.Ordinal);
                        }
                        lists[parameter.Name] = list;
                    }
                    AnalyzeElements(expression: property.Value, isNew: isNew, kind: ElementOf(list: list), scopes: scopes);

                    break;
                case ForStatementNode loop:
                    AnalyzeStatements(statements: loop.Body, isNew: isNew, scopes: [loop.Body, .. scopes]);

                    break;
                case ConditionalStatementNode conditional:
                    AnalyzeStatements(statements: conditional.Then, isNew: isNew, scopes: [conditional.Then, .. scopes]);
                    AnalyzeStatements(statements: (conditional.Else ?? []), isNew: isNew, scopes: [(conditional.Else ?? []), .. scopes]);

                    break;
            }
        }
    }

    private static Kind ElementOf(Kind list) => list switch {
        Kind.SwingList => Kind.Swing,
        Kind.SlideList => Kind.Slide,
        Kind.TextRunList => Kind.TextRun,
        Kind.VolumeList => Kind.Volume,
        Kind.EffectorList => Kind.Effector,
        Kind.CameraList => Kind.Camera,
        Kind.FrameList => Kind.Frame,
        Kind.ClipList => Kind.Clip,
        Kind.ShapeList => Kind.Shape,
        Kind.TransformList => Kind.Transform,
        Kind.KeyList => Kind.Key,
        Kind.PoseList => Kind.Pose,
        Kind.TurnList => Kind.Turn,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(list)),
    };

    // An expression that yields elements of `kind`: follows the references whose value a row is read as.
    private void AnalyzeElements(ExpressionNode expression, bool isNew, Kind kind, IReadOnlyList<IReadOnlyList<StatementNode>> scopes) {
        switch (expression) {
            case ArrayExpressionNode array:
                foreach (var element in array.Elements) { AnalyzeElement(element: element, isNew: isNew, kind: kind, scopes: scopes); }

                break;
            case ForExpressionNode loop:
                foreach (var element in loop.Body) { AnalyzeElement(element: element, isNew: isNew, kind: kind, scopes: scopes); }

                break;
            case ConditionalExpressionNode conditional:
                AnalyzeElements(expression: conditional.WhenTrue, isNew: isNew, kind: kind, scopes: scopes);
                AnalyzeElements(expression: conditional.WhenFalse, isNew: isNew, kind: kind, scopes: scopes);

                break;
            case CallExpressionNode { Name: "map" } call when (call.Arguments.Count == 2) && (call.Arguments[1].Value is LambdaExpressionNode lambda):
                AnalyzeElement(element: lambda.Body, isNew: isNew, kind: kind, scopes: scopes);

                break;
            case CallExpressionNode { Name: "flatMap" } call when (call.Arguments.Count == 2) && (call.Arguments[1].Value is LambdaExpressionNode lambda):
                AnalyzeElements(expression: lambda.Body, isNew: isNew, kind: kind, scopes: scopes);

                break;
            case CallExpressionNode { Name: "concat" } call:
                foreach (var argument in call.Arguments) { AnalyzeElements(expression: argument.Value, isNew: isNew, kind: kind, scopes: scopes); }

                break;
            case CallExpressionNode { Name: "filter" } call when (call.Arguments.Count >= 1):
                AnalyzeElements(expression: call.Arguments[0].Value, isNew: isNew, kind: kind, scopes: scopes);

                break;
            case IdentifierExpressionNode identifier:
                if (Resolve(name: identifier.Name, scopes: scopes) is { Node: LetNode } definition) {
                    Mark(definition: definition, isNew: isNew, kind: ListOf(element: kind), scopes: scopes);
                }

                break;
            case CallExpressionNode call:
                if (Resolve(name: call.Name, scopes: scopes) is { Node: LetNode { Value: FunctionExpressionNode } } function) {
                    Mark(definition: function, isNew: isNew, kind: ListOf(element: kind), scopes: scopes);
                }

                break;
        }
    }

    private void AnalyzeElement(ExpressionNode element, bool isNew, Kind kind, IReadOnlyList<IReadOnlyList<StatementNode>> scopes) {
        switch (element) {
            case ForExpressionNode loop:
                foreach (var produced in loop.Body) { AnalyzeElement(element: produced, isNew: isNew, kind: kind, scopes: scopes); }

                break;
            case ObjectExpressionNode @object:
                AnalyzeMembers(@object: @object, isNew: isNew, kind: kind, scopes: scopes);

                break;
            case WithExpressionNode derived:
                AnalyzeElement(element: derived.Base, isNew: isNew, kind: kind, scopes: scopes);
                AnalyzeElement(element: derived.Overrides, isNew: isNew, kind: kind, scopes: scopes);

                break;
            case ConditionalExpressionNode conditional:
                AnalyzeElement(element: conditional.WhenTrue, isNew: isNew, kind: kind, scopes: scopes);
                AnalyzeElement(element: conditional.WhenFalse, isNew: isNew, kind: kind, scopes: scopes);

                break;
            case CallExpressionNode call:
                if (Resolve(name: call.Name, scopes: scopes) is { Node: LetNode { Value: FunctionExpressionNode } } function) {
                    Mark(definition: function, isNew: isNew, kind: kind, scopes: scopes);
                }

                break;
            case IdentifierExpressionNode identifier:
                if (Resolve(name: identifier.Name, scopes: scopes) is { Node: LetNode } definition) {
                    Mark(definition: definition, isNew: isNew, kind: kind, scopes: scopes);
                }

                break;
        }
    }

    private void AnalyzeMembers(ObjectExpressionNode @object, bool isNew, Kind kind, IReadOnlyList<IReadOnlyList<StatementNode>> scopes) {
        foreach (var property in @object.Properties) {
            if (MemberKind(element: kind, member: property.Name) is { } member && IsList(kind: member)) {
                AnalyzeElements(expression: property.Value, isNew: isNew, kind: ElementOf(list: member), scopes: scopes);
            } else if ((MemberKind(element: kind, member: property.Name) is Kind.EffectorTarget)) {
                AnalyzeElement(element: property.Value, isNew: isNew, kind: Kind.EffectorTarget, scopes: scopes);
            }
        }
    }

    private static bool IsList(Kind kind) => kind is Kind.SwingList or Kind.SlideList or Kind.TextRunList or Kind.VolumeList or Kind.EffectorList or Kind.CameraList or Kind.FrameList or Kind.ClipList or Kind.ShapeList or Kind.TransformList or Kind.KeyList or Kind.PoseList or Kind.TurnList;

    private static Kind ListOf(Kind element) => element switch {
        Kind.Swing => Kind.SwingList,
        Kind.Slide => Kind.SlideList,
        Kind.TextRun => Kind.TextRunList,
        Kind.Volume => Kind.VolumeList,
        Kind.Effector => Kind.EffectorList,
        Kind.Camera => Kind.CameraList,
        Kind.Frame => Kind.FrameList,
        Kind.Clip => Kind.ClipList,
        Kind.Shape => Kind.ShapeList,
        Kind.Transform => Kind.TransformList,
        Kind.Key => Kind.KeyList,
        Kind.Pose => Kind.PoseList,
        Kind.Turn => Kind.TurnList,
        _ => element,
    };

    // The member kinds of each row kind that the retired conversion turned.
    private static Kind? MemberKind(Kind element, string member) => (element, member) switch {
        (Kind.Swing, "axis" or "pivot") => Kind.Vector,
        (Kind.Slide, "axis") => Kind.Vector,
        (Kind.TextRun or Kind.Volume, "position") => Kind.Vector,
        (Kind.TextRun or Kind.Volume, "rotation") => Kind.RotationExact,
        (Kind.Effector, "target") => Kind.EffectorTarget,
        (Kind.EffectorTarget, "direction" or "offset") => Kind.Vector,
        (Kind.Camera, "position") => Kind.Vector,
        (Kind.Frame, "transforms") => Kind.TransformList,
        (Kind.Transform, "position") => Kind.Vector,
        (Kind.Transform, "rotation") => Kind.RotationExact,
        (Kind.Clip, "keys") => Kind.KeyList,
        (Kind.Key, "poses") => Kind.PoseList,
        (Kind.Pose, "slide") => Kind.Vector,
        (Kind.Pose, "turn") => Kind.TurnList,
        (Kind.Turn, "axis") => Kind.Vector,
        (Kind.Shape, "position" or "joint") => Kind.Vector,
        (Kind.Shape, "rotation") => Kind.RotationExact,
        (Kind.Shape, "swings") => Kind.SwingList,
        (Kind.Shape, "slides") => Kind.SlideList,
        (Kind.Shape, "domain") => Kind.DomainList,
        _ => null,
    };

    private void Mark(Definition definition, bool isNew, Kind kind, IReadOnlyList<IReadOnlyList<StatementNode>> scopes) {
        var node = definition.Node;

        if (isNew) {
            if (!m_reachedByNew.Add(item: node)) { return; }
        } else if (m_convert.TryGetValue(key: node, value: out var held)) {
            if (held != kind) { m_refusals.Add(item: $"{Describe(node: node)} is read as both {held} and {kind}"); }
            return;
        } else {
            m_convert[node] = kind;
        }

        var previous = m_currentPath;

        m_currentPath = definition.Path;
        try {
            switch (node) {
                case TemplateNode template:
                    m_templates.Push(item: template);
                    try {
                        AnalyzeStatements(statements: template.Body.Statements, isNew: isNew, scopes: [template.Body.Statements, .. TopScopes(path: definition.Path)]);
                    } finally {
                        _ = m_templates.Pop();
                    }

                    break;
                case LetNode { Value: FunctionExpressionNode function }:
                    AnalyzeValue(value: function.Body, isNew: isNew, kind: kind, scopes: TopScopes(path: definition.Path));

                    break;
                case LetNode let:
                    AnalyzeValue(value: let.Value, isNew: isNew, kind: kind, scopes: TopScopes(path: definition.Path));

                    break;
            }
        } finally {
            m_currentPath = previous;
        }
    }

    private void AnalyzeValue(ExpressionNode value, bool isNew, Kind kind, IReadOnlyList<IReadOnlyList<StatementNode>> scopes) {
        if (IsList(kind: kind)) {
            AnalyzeElements(expression: value, isNew: isNew, kind: ElementOf(list: kind), scopes: scopes);
        } else {
            AnalyzeElement(element: value, isNew: isNew, kind: kind, scopes: scopes);
        }
    }

    private IReadOnlyList<IReadOnlyList<StatementNode>> TopScopes(string? path) => (((path is not null) && m_files.TryGetValue(key: path, value: out var statements)) ? [statements] : []);

    // A name resolves to the innermost local definition, then to a top-level one in the current file, then to the only
    // top-level one of the scan.
    private Definition? Resolve(string name, IReadOnlyList<IReadOnlyList<StatementNode>> scopes) {
        var bare = name[(name.LastIndexOf(value: '.') + 1)..];

        foreach (var scope in scopes) {
            foreach (var statement in scope) {
                if ((statement is TemplateNode template) && (template.Name == bare)) { return new Definition(Node: template, Path: m_currentPath); }
                if ((statement is LetNode let) && (let.Name == bare)) { return new Definition(Node: let, Path: m_currentPath); }
            }
        }

        if (m_topLevel.TryGetValue(key: bare, value: out var definitions)) {
            var local = definitions.Where(predicate: definition => (definition.Path == m_currentPath)).ToList();

            if (local.Count >= 1) { return local[0]; }
            if (definitions.Count == 1) { return definitions[0]; }
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The drop verdicts, from the compiled documents.
    // ---------------------------------------------------------------------------------------------------------------

    private void CollectSlotFramed(JsonNode document) {
        Walk(node: document, visit: @object => {
            if ((@object["prototypeId"] is JsonValue value) && value.TryGetValue<string>(value: out var id)) {
                var placement = @object.ContainsKey(propertyName: "position") || @object.ContainsKey(propertyName: "parent");
                var moving = @object.ContainsKey(propertyName: "inhabit") || @object.ContainsKey(propertyName: "attach") || @object.ContainsKey(propertyName: "turn");

                if (!placement || moving) { m_slotFramedPrototypes.Add(item: id); }
            }
            if (@object["responses"] is JsonArray responses) {
                foreach (var response in responses) {
                    if ((response?["prototypeId"] is JsonValue r) && r.TryGetValue<string>(value: out var responseId)) { m_slotFramedPrototypes.Add(item: responseId); }
                }
            }
            if (@object["map"] is JsonObject map) {
                foreach (var (_, entry) in map) {
                    if ((entry is JsonValue e) && e.TryGetValue<string>(value: out var dealtId)) { m_slotFramedPrototypes.Add(item: dealtId); }
                }
            }
        });
    }

    private static void Walk(JsonNode? node, Action<JsonObject> visit) {
        switch (node) {
            case JsonObject @object:
                visit(obj: @object);
                foreach (var (_, child) in @object) { Walk(node: child, visit: visit); }

                break;
            case JsonArray array:
                foreach (var child in array) { Walk(node: child, visit: visit); }

                break;
        }
    }

    private void CollectVerdicts(JsonObject document, SourceMap map, string path) {
        foreach (var (pointer, creation, prototypeId) in Creations(document: document)) {
            if (!IsOld(creation: creation)) { continue; }
            if (creation["shapes"] is not JsonArray shapes) { continue; }

            var context = new CreationContext(creation: creation, slotFramed: IsSlotFramed(creation: creation, prototypeId: prototypeId, framed: m_slotFramedPrototypes));

            for (var index = 0; (index < shapes.Count); index++) {
                if (shapes[index] is not JsonObject shape) { continue; }

                var droppable = context.MayDrop(shape: shape, index: index);
                var name = ((shape["name"] is JsonValue n) && n.TryGetValue<string>(value: out var text)) ? text : null;

                if (!map.TryGetOrigin(jsonPointer: $"{pointer}/shapes/{index}", origin: out var origin)) {
                    if (!droppable) { m_refusals.Add(item: $"{path}: shape {pointer}/shapes/{index} must keep its half turn, and the compile names no source for it"); }
                    continue;
                }

                m_dropVerdicts.Add(item: (Path.GetFullPath(path: (origin.SourcePath ?? path)), origin.Span.Offset, name, droppable));
            }
        }
    }

    private static bool IsOld(JsonObject creation) => ((creation["schema"] is JsonValue schema) && schema.TryGetValue<string>(value: out var text) && (text == OldSchema));

    private static IEnumerable<(string Pointer, JsonObject Creation, string? PrototypeId)> Creations(JsonObject document) {
        if ((document["schema"] is JsonValue schema) && schema.TryGetValue<string>(value: out var text) && IsCreationSchema(schema: text)) {
            yield return ("", document, null);
        }
        if (document["prototypes"] is JsonArray prototypes) {
            for (var index = 0; (index < prototypes.Count); index++) {
                if ((prototypes[index] is JsonObject prototype) && (prototype["document"] is JsonObject creation)) {
                    yield return ($"/prototypes/{index}/document", creation, prototype["id"]?.GetValue<string>());
                }
            }
        }
    }

    private static bool IsSlotFramed(JsonObject creation, string? prototypeId, HashSet<string> framed) {
        if ((prototypeId is not null) && framed.Contains(item: prototypeId)) { return true; }
        if (MotionMembers.Any(predicate: member => (creation[member] is JsonArray { Count: > 0 }))) { return true; }
        if (creation["shapes"] is JsonArray shapes) {
            foreach (var shape in shapes) {
                if ((shape?["swings"] is JsonArray { Count: > 0 }) || (shape?["slides"] is JsonArray { Count: > 0 })) { return true; }
            }
        }

        return false;
    }

    // What in a creation reads a shape's own axes.
    private sealed class CreationContext {
        private readonly HashSet<long> m_ids = [];
        private readonly HashSet<string> m_names = new(comparer: StringComparer.Ordinal);
        private readonly JsonArray? m_palette;
        private readonly bool m_slotFramed;

        public CreationContext(JsonObject creation, bool slotFramed) {
            m_slotFramed = slotFramed;
            m_palette = creation["palette"] as JsonArray;
            AddIds(list: creation["faces"], member: "shapeId");
            AddIds(list: creation["cameras"], member: "shapeId");
            AddIds(list: creation["textRuns"], member: "shapeId");
            AddIds(list: creation["parts"], member: "shapeId");
            if (creation["frames"] is JsonArray frames) {
                foreach (var frame in frames) { AddIds(list: frame?["transforms"], member: "id"); }
            }
            if (creation["chains"] is JsonArray chains) {
                foreach (var chain in chains) {
                    if (chain?["shapes"] is JsonArray ids) {
                        foreach (var id in ids) { if (id is JsonValue v && v.TryGetValue<long>(value: out var n)) { m_ids.Add(item: n); } }
                    }
                }
            }
            AddNames(list: creation["volumes"], member: "parent");
            if (creation["effectors"] is JsonArray effectors) {
                foreach (var effector in effectors) {
                    if (effector?["tip"] is JsonValue tip && tip.TryGetValue<string>(value: out var name)) { m_names.Add(item: name); }
                    if (effector?["chain"] is JsonArray chain) {
                        foreach (var link in chain) { if (link is JsonValue l && l.TryGetValue<string>(value: out var n)) { m_names.Add(item: n); } }
                    }
                }
            }
            if (m_palette is not null) {
                foreach (var entry in m_palette) { AddNames(list: entry?["paints"], member: "parent"); }
            }
        }

        private void AddIds(JsonNode? list, string member) {
            if (list is not JsonArray array) { return; }
            foreach (var item in array) {
                if ((item?[member] is JsonValue value) && value.TryGetValue<long>(value: out var id)) { m_ids.Add(item: id); }
            }
        }

        private void AddNames(JsonNode? list, string member) {
            if (list is not JsonArray array) { return; }
            foreach (var item in array) {
                if ((item?[member] is JsonValue value) && value.TryGetValue<string>(value: out var name)) { m_names.Add(item: name); }
            }
        }

        public bool MayDrop(JsonObject shape, int index) {
            if ((shape["type"] is not JsonValue type) || !type.TryGetValue<string>(value: out var typeName)) { return false; }

            // A prism's outline lies in its local x and y and its depth along z, centred: a trapezoid, a rectangle or an
            // ellipse reads the same mirrored in x, so the prism cannot tell its half turn.
            var symmetricPrism = (typeName == "Prism") && (shape["lift"] is null)
                && ((shape["profile"] is null) || ((shape["profile"]?["kind"] is JsonValue kind) && kind.TryGetValue<string>(value: out var kindName) && (kindName is "Trapezoid" or "RoundedRectangle" or "Ellipse" or "ChamferedRectangle")));

            if (!SymmetricTypes.Contains(item: typeName) && !symmetricPrism) { return false; }
            if (AsymmetricShapeMembers.Any(predicate: member => (shape[member] is not null) && !(symmetricPrism && (member is "profile" or "taper")))) { return false; }

            var id = ((shape["id"] is JsonValue idValue) && idValue.TryGetValue<long>(value: out var authored)) ? authored : index;

            if (m_ids.Contains(item: id)) { return false; }
            if ((shape["name"] is JsonValue nameValue) && nameValue.TryGetValue<string>(value: out var name) && m_names.Contains(item: name)) { return false; }
            if (m_slotFramed) {
                var material = ((shape["material"] is JsonValue m) && m.TryGetValue<long>(value: out var slot)) ? (int)slot : 0;

                if ((m_palette is not null) && (material >= 0) && (material < m_palette.Count) && (m_palette[material] is JsonObject entry) && FramedPaletteMembers.Any(predicate: member => (entry[member] is not null))) {
                    return false;
                }
            }

            return true;
        }
    }

    private bool MayDrop(BlockNode shape) => (m_shapeVerdicts.TryGetValue(key: shape, value: out var verdict) && verdict);

    // ---------------------------------------------------------------------------------------------------------------
    // Rewrite
    // ---------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override DocumentNode Apply(DocumentNode document) => Apply(document: document, path: null);

    /// <inheritdoc/>
    public override DocumentNode Apply(DocumentNode document, string? path) {
        ArgumentNullException.ThrowIfNull(argument: document);
        m_currentPath = path;

        var standalone = (document.Schema == OldSchema);
        var statements = (standalone
            ? ConvertCreationStatements(statements: document.Statements, setSchema: true)
            : VisitStatements(statements: document.Statements));

        if (ReferenceEquals(objA: statements, objB: document.Statements) && !standalone) {
            return document;
        }

        return document with {
            Schema = (standalone ? NewSchema : document.Schema),
            Statements = statements,
        };
    }

    // Walks statements that are not creation content, converting each node the analysis chose.
    private IReadOnlyList<StatementNode> VisitStatements(IReadOnlyList<StatementNode> statements) {
        List<StatementNode>? rewritten = null;

        for (var index = 0; (index < statements.Count); index++) {
            var statement = statements[index];
            var converted = VisitStatement(statement: statement);

            if (!ReferenceEquals(objA: converted, objB: statement)) {
                rewritten ??= [.. statements];
                rewritten[index] = converted;
            }
        }

        return ((rewritten is null) ? statements : rewritten);
    }

    private StatementNode VisitStatement(StatementNode statement) {
        if (m_convert.TryGetValue(key: statement, value: out var kind)) {
            return Convert(statement: statement, kind: kind);
        }

        return statement switch {
            BlockNode { Identifier: "document" } block when (CreationSchemaOf(document: block, inPrototype: false) == OldSchema) => block with {
                Statements = ConvertCreationStatements(statements: block.Statements, setSchema: true),
            },
            BlockNode { Identifier: "prototype" } prototype when HoldsOldDocument(prototype: prototype) => prototype with {
                Statements = [.. prototype.Statements.Select(selector: s => s switch {
                    BlockNode { Identifier: "document" } document => document with { Statements = ConvertCreationStatements(statements: document.Statements, setSchema: true) },
                    BlockNode { Identifier: "mesh" } => ConvertMesh(statement: s),
                    _ => VisitStatement(statement: s),
                })],
            },
            BlockNode block => Same(block, block with { Statements = VisitStatements(statements: block.Statements) }, block.Statements),
            TemplateNode template => Same(template, template with { Body = (BlockNode)VisitStatement(statement: template.Body) }, null),
            ForStatementNode loop => Same(loop, loop with { Body = VisitStatements(statements: loop.Body) }, loop.Body),
            ConditionalStatementNode conditional => conditional with {
                Then = VisitStatements(statements: conditional.Then),
                Else = ((conditional.Else is null) ? null : VisitStatements(statements: conditional.Else)),
            },
            _ => statement,
        };
    }

    private static StatementNode Same<T>(T original, T rewritten, IReadOnlyList<StatementNode>? before) where T : StatementNode => rewritten switch {
        BlockNode block when ReferenceEquals(objA: block.Statements, objB: before) => original,
        ForStatementNode loop when ReferenceEquals(objA: loop.Body, objB: before) => original,
        TemplateNode template when (original is TemplateNode o) && ReferenceEquals(objA: template.Body, objB: o.Body) => original,
        _ => rewritten,
    };

    private bool HoldsOldDocument(BlockNode prototype) => prototype.Statements.Any(predicate: statement => (statement is BlockNode { Identifier: "document" } block) && (CreationSchemaOf(document: block, inPrototype: true) == OldSchema));

    // A prototype's mesh is authored in its creation's frame.
    private StatementNode ConvertMesh(StatementNode statement) {
        if (statement is not BlockNode { Identifier: "mesh" } mesh) { return statement; }

        return mesh with {
            Statements = [.. mesh.Statements.Select(selector: s => ((s is PropertyNode { Name: "vertices" } vertices)
                ? vertices with { Value = MapElements(expression: vertices.Value, map: vertex => Replace(old: vertex, @new: FlipVector(vector: vertex))) }
                : s))],
        };
    }

    private StatementNode Convert(StatementNode statement, Kind kind) {
        switch (statement) {
            case TemplateNode template:
                return template with { Body = template.Body with { Statements = ConvertCreationStatements(statements: template.Body.Statements, setSchema: true) } };
            case LetNode { Value: FunctionExpressionNode function } let:
                return let with { Value = function with { Body = ConvertValue(value: function.Body, kind: kind) } };
            case LetNode let:
                return let with { Value = ConvertValue(value: let.Value, kind: kind) };
            default:
                return statement;
        }
    }

    private ExpressionNode ConvertValue(ExpressionNode value, Kind kind) => (IsList(kind: kind)
        ? ConvertElements(expression: value, kind: ElementOf(list: kind))
        : ConvertElement(element: value, kind: kind));

    private IReadOnlyList<StatementNode> ConvertCreationStatements(IReadOnlyList<StatementNode> statements, bool setSchema) {
        var converted = new List<StatementNode>(capacity: statements.Count);

        foreach (var statement in statements) {
            converted.Add(item: ConvertCreationStatement(statement: statement, setSchema: setSchema));
        }

        return converted;
    }

    private StatementNode ConvertCreationStatement(StatementNode statement, bool setSchema) {
        if (m_convert.TryGetValue(key: statement, value: out var kind)) {
            return Convert(statement: statement, kind: kind);
        }

        switch (statement) {
            case PropertyNode { Name: "schema", Value: LiteralExpressionNode { Value: OldSchema } literal } schema when setSchema:
                return schema with { Value = Replace(old: literal, @new: literal with { Value = NewSchema, RawFenced = false }, owner: schema) };
            case BlockNode { Identifier: "shape" } shape:
                return ConvertShapeBlock(shape: shape);
            case PropertyNode property when CreationLists.TryGetValue(key: property.Name, value: out var list):
                return property with { Value = ConvertElements(expression: property.Value, kind: ElementOf(list: list)) };
            case ExpressionStatementNode { Expression: CallExpressionNode call } when (Resolve(name: call.Name, scopes: [m_files.GetValueOrDefault(key: (m_currentPath ?? "")) ?? []]) is { Node: TemplateNode template }):
                foreach (var (argument, list) in ListArguments(call: call, template: template)) {
                    _ = ConvertElements(expression: argument.Value, kind: ElementOf(list: list));
                }

                return statement;
            case ForStatementNode loop:
                return loop with { Body = ConvertCreationStatements(statements: loop.Body, setSchema: false) };
            case ConditionalStatementNode conditional:
                return conditional with {
                    Then = ConvertCreationStatements(statements: conditional.Then, setSchema: false),
                    Else = ((conditional.Else is null) ? null : ConvertCreationStatements(statements: conditional.Else, setSchema: false)),
                };
            default:
                return VisitStatement(statement: statement);
        }
    }

    private StatementNode ConvertShapeBlock(BlockNode shape) {
        var symmetric = MayDrop(shape: shape);
        var statements = new List<StatementNode>(capacity: (shape.Statements.Count + 1));
        var rotated = false;
        var positionAt = -1;

        PropertyNode? position = null;

        foreach (var statement in shape.Statements) {
            switch (statement) {
                case PropertyNode { Name: "position" or "joint" } vector:
                    if (vector.Name == "position") { positionAt = statements.Count; position = vector; }
                    statements.Add(item: vector with { Value = Replace(old: vector.Value, @new: FlipVector(vector: vector.Value), owner: vector) });

                    break;
                case PropertyNode { Name: "rotation" } rotation:
                    rotated = true;
                    if ((symmetric ? Conjugate(rotation: rotation.Value) : HalfTurnThen(rotation: rotation.Value)) is { } value) {
                        statements.Add(item: rotation with { Value = Replace(old: rotation.Value, @new: value, owner: rotation) });
                    } else {
                        Remove(property: rotation);
                    }

                    break;
                case PropertyNode { Name: "swings" } swings:
                    statements.Add(item: swings with { Value = ConvertElements(expression: swings.Value, kind: Kind.Swing) });

                    break;
                case PropertyNode { Name: "slides" } slides:
                    statements.Add(item: slides with { Value = ConvertElements(expression: slides.Value, kind: Kind.Slide) });

                    break;
                case PropertyNode { Name: "domain" } domain:
                    statements.Add(item: domain with { Value = MapElements(expression: domain.Value, map: ConvertDomainOp) });

                    break;
                default:
                    statements.Add(item: statement);

                    break;
            }
        }

        if (!rotated && !symmetric) {
            var added = new PropertyNode(Name: "rotation", Value: HalfTurnRotation());

            statements.Insert(index: (positionAt + 1), item: added);
            Insert(after: position, before: shape.Statements.FirstOrDefault(), property: added);
        }

        return shape with { Statements = statements };
    }

    private ExpressionNode ConvertDomainOp(ExpressionNode op) {
        if (op is not CallExpressionNode { Name: "symmetry" } call) { return op; }

        return call with {
            Arguments = [.. call.Arguments.Select(selector: (argument, index) => (((argument.Name == "normal") || ((argument.Name is null) && (index == 0)))
                ? argument with { Value = Replace(old: argument.Value, @new: FlipVector(vector: argument.Value)) }
                : argument))],
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Edits: each changed value is spliced into the author's text, so everything the rewrite does not touch keeps its
    // layout and comments.
    // ---------------------------------------------------------------------------------------------------------------

    private readonly List<(int Offset, int Length, string Text)> m_edits = [];
    private string m_source = "";

    // A value's text is found by reading the author's text, not from a node's span: the region of a property's value runs
    // from after its name (and colon) to where the value ends.
    private T Replace<T>(ExpressionNode old, T @new, PropertyNode? owner = null) where T : ExpressionNode {
        if (ReferenceEquals(objA: old, objB: @new)) { return @new; }

        var text = Text(node: @new);

        if (owner is not null) {
            var nameEnd = (owner.Offset + NameLength(at: owner.Offset));
            var start = ValueStart(from: nameEnd);
            var container = (@new is ArrayExpressionNode or ObjectExpressionNode);

            m_edits.Add(item: (nameEnd, (ValueEnd(start: start) - nameEnd), (container ? $" {text}" : $": {text}")));
        } else {
            m_edits.Add(item: (old.Offset, (ValueEnd(start: old.Offset) - old.Offset), text));
        }

        return @new;
    }

    private int NameLength(int at) {
        var end = at;

        if ((end < m_source.Length) && (m_source[end] == '`')) {
            end = (m_source.IndexOf(value: '`', startIndex: (end + 1)) + 1);
            return (end - at);
        }
        while ((end < m_source.Length) && (char.IsLetterOrDigit(c: m_source[end]) || (m_source[end] is '_' or '$' or '.'))) { end++; }

        return (end - at);
    }

    private int ValueStart(int from) {
        var at = from;

        while ((at < m_source.Length) && (m_source[at] is ' ' or '\t')) { at++; }
        if ((at < m_source.Length) && (m_source[at] is ':' or '=')) { at++; }
        while ((at < m_source.Length) && (m_source[at] is ' ' or '\t')) { at++; }

        return at;
    }

    // Where a value that starts at `start` ends: at a line break, a comma or a closing bracket outside every bracket it
    // opens, unless the line ends on an operator that continues it.
    private int ValueEnd(int start) {
        var depth = 0;
        var at = start;

        while (at < m_source.Length) {
            var c = m_source[at];

            if (c == '"') {
                at = StringEnd(at: at);
                continue;
            }
            if ((c == '/') && ((at + 1) < m_source.Length) && (m_source[at + 1] == '/')) {
                if (depth == 0) { break; }
                at = m_source.IndexOf(value: '\n', startIndex: at);
                if (at < 0) { at = m_source.Length; }
                continue;
            }
            if (c is '(' or '[' or '{') {
                depth++;
            } else if (c is ')' or ']' or '}') {
                if (depth == 0) { break; }
                depth--;
                // A container written as the whole value ends where it closes, whatever stands beside it on the line.
                if ((depth == 0) && (m_source[start] is '[' or '{')) {
                    at++;
                    break;
                }
            } else if ((c == ',') && (depth == 0)) {
                break;
            } else if ((c is '\n' or '\r') && (depth == 0)) {
                var before = m_source[start..at].TrimEnd();

                if (!(before.EndsWith(value: '+') || before.EndsWith(value: '-') || before.EndsWith(value: '*') || before.EndsWith(value: '/') || before.EndsWith(value: '?') || before.EndsWith(value: ':') || before.EndsWith(value: " and", comparisonType: StringComparison.Ordinal) || before.EndsWith(value: " or", comparisonType: StringComparison.Ordinal))) {
                    break;
                }
            }
            at++;
        }

        while ((at > start) && char.IsWhiteSpace(c: m_source[at - 1])) { at--; }

        return at;
    }

    private int StringEnd(int at) {
        if (string.CompareOrdinal(strA: m_source, indexA: at, strB: "\"\"\"", indexB: 0, length: 3) == 0) {
            var close = m_source.IndexOf(value: "\"\"\"", startIndex: (at + 3), comparisonType: StringComparison.Ordinal);

            return ((close < 0) ? m_source.Length : (close + 3));
        }

        var i = (at + 1);

        while (i < m_source.Length) {
            if (m_source[i] == '\\') { i += 2; continue; }
            if (m_source[i] == '"') { return (i + 1); }
            i++;
        }

        return i;
    }

    // A property removed with its own line, or, written among others on one line, set to the identity.
    private void Remove(PropertyNode property) {
        var end = ValueEnd(start: ValueStart(from: (property.Offset + NameLength(at: property.Offset))));
        var lineStart = (m_source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (property.Offset - 1))) + 1);
        var lineEnd = m_source.IndexOf(value: '\n', startIndex: end);

        if (string.IsNullOrWhiteSpace(value: m_source[lineStart..property.Offset]) && (lineEnd >= 0) && string.IsNullOrWhiteSpace(value: m_source[end..lineEnd])) {
            m_edits.Add(item: (lineStart, ((lineEnd + 1) - lineStart), ""));
        } else {
            _ = Replace(old: property.Value, @new: new ArrayExpressionNode(Elements: [Literal(value: 0L), Literal(value: 0L), Literal(value: 0L), Literal(value: 1L)]), owner: property);
        }
    }

    // A property added after `after`, on its own line where `after` stands on its own line and beside it otherwise; with
    // no `after`, before `before`.
    private void Insert(StatementNode? after, StatementNode? before, PropertyNode property) {
        var text = $"{property.Name}: {Text(node: property.Value)}";
        var anchor = (after ?? before);

        if (anchor is null) { return; }

        var lineStart = (m_source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (anchor.Offset - 1))) + 1);
        var ownLine = string.IsNullOrWhiteSpace(value: m_source[lineStart..anchor.Offset]);
        var indent = (ownLine ? m_source[lineStart..anchor.Offset] : "");

        if (after is not null) {
            var end = ValueEnd(start: ValueStart(from: (after.Offset + NameLength(at: after.Offset))));

            m_edits.Add(item: (end, 0, (ownLine ? $"\n{indent}{text}" : $", {text}")));
        } else {
            m_edits.Add(item: (before!.Offset, 0, (ownLine ? $"{text}\n{indent}" : $"{text}, ")));
        }
    }

    private static string Text(ExpressionNode node) => PuckPrinter.PrintExpression(expression: node);

    /// <inheritdoc/>
    public override string? ApplyText(string source, DocumentNode document, string path) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: document);
        m_edits.Clear();
        m_source = source;
        _ = Apply(document: document, path: path);

        if (m_edits.Count == 0) { return null; }

        var builder = new System.Text.StringBuilder(value: source);

        foreach (var (offset, length, text) in m_edits.OrderByDescending(keySelector: static edit => edit.Offset).ThenByDescending(keySelector: static edit => edit.Length)) {
            builder.Remove(startIndex: offset, length: length).Insert(index: offset, value: text);
        }

        return builder.ToString();
    }

    private ExpressionNode ConvertElements(ExpressionNode expression, Kind kind) {
        switch (expression) {
            case ArrayExpressionNode array:
                return array with { Elements = [.. array.Elements.Select(selector: element => ConvertElement(element: element, kind: kind))] };
            case ForExpressionNode loop:
                return loop with { Body = [.. loop.Body.Select(selector: element => ConvertElement(element: element, kind: kind))] };
            case ConditionalExpressionNode conditional:
                return conditional with {
                    WhenTrue = ConvertElements(expression: conditional.WhenTrue, kind: kind),
                    WhenFalse = ConvertElements(expression: conditional.WhenFalse, kind: kind),
                };
            case CallExpressionNode { Name: "map" } call when (call.Arguments.Count == 2) && (call.Arguments[1].Value is LambdaExpressionNode lambda):
                return call with { Arguments = [call.Arguments[0], call.Arguments[1] with { Value = lambda with { Body = ConvertElement(element: lambda.Body, kind: kind) } }] };
            case CallExpressionNode { Name: "flatMap" } call when (call.Arguments.Count == 2) && (call.Arguments[1].Value is LambdaExpressionNode lambda):
                return call with { Arguments = [call.Arguments[0], call.Arguments[1] with { Value = lambda with { Body = ConvertElements(expression: lambda.Body, kind: kind) } }] };
            case CallExpressionNode { Name: "concat" } call:
                return call with { Arguments = [.. call.Arguments.Select(selector: argument => argument with { Value = ConvertElements(expression: argument.Value, kind: kind) })] };
            case CallExpressionNode { Name: "filter" } call when (call.Arguments.Count >= 1):
                return call with { Arguments = [call.Arguments[0] with { Value = ConvertElements(expression: call.Arguments[0].Value, kind: kind) }, .. call.Arguments.Skip(count: 1)] };
            default:
                // A reference: the definition is converted where it stands.
                return expression;
        }
    }

    private ExpressionNode ConvertElement(ExpressionNode element, Kind kind) {
        switch (element) {
            case ForExpressionNode loop:
                return loop with { Body = [.. loop.Body.Select(selector: produced => ConvertElement(element: produced, kind: kind))] };
            case ObjectExpressionNode @object:
                return ConvertMembers(@object: @object, kind: kind);
            case WithExpressionNode { Base: IdentifierExpressionNode @base, Overrides: ObjectExpressionNode overrides } derived when (kind is Kind.Shape or Kind.TextRun or Kind.Volume or Kind.Transform) && (Resolve(name: @base.Name, scopes: [m_files.GetValueOrDefault(key: (m_currentPath ?? "")) ?? []]) is null):
                // A row derived from one the rewrite cannot reach (a lambda's element): the override turns what it
                // keeps of the row as well as what it writes.
                var converted = ConvertElement(element: overrides, kind: kind);
                var position = overrides.Properties.FirstOrDefault(predicate: static property => property.Name is "position");

                if (!overrides.Properties.Any(predicate: static property => property.Name is "rotation")) {
                    var kept = new MemberAccessExpressionNode(Target: new WithExpressionNode(Base: new ObjectExpressionNode(Properties: [new PropertyNode(Name: "rotation", Value: new ArrayExpressionNode(Elements: [Literal(value: 0L), Literal(value: 0L), Literal(value: 0L), Literal(value: 1L)]))]), Overrides: @base) { Parenthesized = true }, Member: "rotation");

                    Insert(after: position, before: overrides.Properties.FirstOrDefault(), property: new PropertyNode(Name: "rotation", Value: Call("compose", HalfTurnRotation(), kept)));
                }
                if (position is null) {
                    var kept = new MemberAccessExpressionNode(Target: new WithExpressionNode(Base: new ObjectExpressionNode(Properties: [new PropertyNode(Name: "position", Value: new ArrayExpressionNode(Elements: [Literal(value: 0L), Literal(value: 0L), Literal(value: 0L)]))]), Overrides: @base) { Parenthesized = true }, Member: "position");

                    Insert(after: null, before: overrides.Properties.FirstOrDefault(), property: new PropertyNode(Name: "position", Value: FlipVector(vector: kept)));
                }

                return derived with { Overrides = converted };
            case WithExpressionNode derived:
                return derived with {
                    Base = ConvertElement(element: derived.Base, kind: kind),
                    Overrides = ConvertElement(element: derived.Overrides, kind: kind),
                };
            case ConditionalExpressionNode conditional:
                return conditional with {
                    WhenTrue = ConvertElement(element: conditional.WhenTrue, kind: kind),
                    WhenFalse = ConvertElement(element: conditional.WhenFalse, kind: kind),
                };
            default:
                return element;
        }
    }

    private ExpressionNode ConvertMembers(ObjectExpressionNode @object, Kind kind) {
        // A run riding a shape and a volume riding one are written in that shape's frame, which the shape carries.
        if ((kind is Kind.TextRun) && @object.Properties.Any(predicate: property => property.Name is "shapeId")) { return @object; }
        if ((kind is Kind.Volume) && @object.Properties.Any(predicate: property => property.Name is "parent")) { return @object; }

        var properties = new List<PropertyNode>(capacity: (@object.Properties.Count + 1));
        var rotated = false;
        var positionAt = -1;
        PropertyNode? position = null;

        foreach (var property in @object.Properties) {
            switch (MemberKind(element: kind, member: property.Name)) {
                case Kind.Vector:
                    if (property.Name == "position") { positionAt = properties.Count; position = property; }
                    properties.Add(item: property with { Value = Replace(old: property.Value, @new: FlipVector(vector: property.Value), owner: property) });

                    break;
                case Kind.RotationExact:
                    rotated = true;
                    if (HalfTurnThen(rotation: property.Value) is { } value) {
                        properties.Add(item: property with { Value = Replace(old: property.Value, @new: value, owner: property) });
                    } else {
                        Remove(property: property);
                    }

                    break;
                case Kind.DomainList:
                    properties.Add(item: property with { Value = MapElements(expression: property.Value, map: ConvertDomainOp) });

                    break;
                case Kind.EffectorTarget:
                    properties.Add(item: property with { Value = ConvertElement(element: property.Value, kind: Kind.EffectorTarget) });

                    break;
                case { } list when IsList(kind: list):
                    properties.Add(item: property with { Value = ConvertElements(expression: property.Value, kind: ElementOf(list: list)) });

                    break;
                default:
                    properties.Add(item: property);

                    break;
            }
        }

        // A row that leaves its rotation out (a shape, a run, a volume or a frame pose) still turns by the half turn; an
        // override (a `with` object) that leaves it out keeps its base's.
        if (!rotated && (kind is Kind.Shape or Kind.TextRun or Kind.Volume or Kind.Transform) && (positionAt >= 0) && !IsOverride(@object: @object)) {
            var added = new PropertyNode(Name: "rotation", Value: HalfTurnRotation());

            properties.Insert(index: (positionAt + 1), item: added);
            Insert(after: position, before: null, property: added);
        }

        return @object with { Properties = properties };
    }

    // An override names a few members; a whole row names its kind's identity (a run's text, a shape's type).
    private static bool IsOverride(ObjectExpressionNode @object) => !@object.Properties.Any(predicate: property => property.Name is "text" or "type" or "id" or "name");

    private static ExpressionNode MapElements(ExpressionNode expression, Func<ExpressionNode, ExpressionNode> map) => expression switch {
        ArrayExpressionNode array => array with { Elements = [.. array.Elements.Select(selector: map)] },
        _ => expression,
    };

    // ---------------------------------------------------------------------------------------------------------------
    // Vectors and rotations
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The vector with its x and z negated.</summary>
    private static ExpressionNode FlipVector(ExpressionNode vector) {
        switch (vector) {
            case ArrayExpressionNode { Elements.Count: 3 } array:
                return array with { Elements = [Negate(value: array.Elements[0]), array.Elements[1], Negate(value: array.Elements[2])] };
            case ArrayExpressionNode { Elements.Count: 2 } pair:
                // A two-element vertex (a profile's x, y) is not a creation-frame point; only a mesh's three-element one is.
                return pair;
            case BinaryExpressionNode { Operator: "+" or "-" } binary when IsVectorLiteral(expression: binary.Left) || IsVectorLiteral(expression: binary.Right):
                return binary with { Left = FlipVector(vector: binary.Left), Right = FlipVector(vector: binary.Right) };
            case BinaryExpressionNode { Operator: "*" } binary when IsMask(expression: binary.Right):
                // `v * [-1, 1, -1]` is a vector already turned: its turn back is `v`.
                return binary.Left;
            case ConditionalExpressionNode conditional:
                return conditional with { WhenTrue = FlipVector(vector: conditional.WhenTrue), WhenFalse = FlipVector(vector: conditional.WhenFalse) };
            case LiteralExpressionNode { Value: string }:
                // A state binding names a cell the world writes, in the frame the world writes it: nothing to turn.
                return vector;
            default:
                return new BinaryExpressionNode(Left: Operand(expression: vector), Operator: "*", Right: Mask(count: 3)) { Parenthesized = true };
        }
    }

    private static bool IsVectorLiteral(ExpressionNode expression) => expression is ArrayExpressionNode { Elements.Count: 3 };

    private static bool IsMask(ExpressionNode expression) => (expression is ArrayExpressionNode { Elements.Count: 3 or 4 } array)
        && (Number(expression: array.Elements[0]) == -1) && (Number(expression: array.Elements[1]) == 1) && (Number(expression: array.Elements[2]) == -1)
        && ((array.Elements.Count == 3) || (Number(expression: array.Elements[3]) == 1));

    private static ExpressionNode Mask(int count) => new ArrayExpressionNode(Elements: [.. Enumerable.Range(start: 0, count: count).Select(selector: k => Literal(value: (((k % 2) == 0) && (k < 3)) ? -1L : 1L))]);

    private static LiteralExpressionNode Literal(long value) => new(Value: value);

    private static LiteralExpressionNode Literal(double value) => new(
        Value: value,
        RawText: value.ToString(format: "R", provider: CultureInfo.InvariantCulture)
    );

    private static double? Number(ExpressionNode expression) => expression switch {
        LiteralExpressionNode { Value: long n, Unit: null } => n,
        LiteralExpressionNode { Value: int n, Unit: null } => n,
        LiteralExpressionNode { Value: double d, Unit: null } => d,
        LiteralExpressionNode { Value: decimal m, Unit: null } => (double)m,
        _ => null,
    };

    private static ExpressionNode Operand(ExpressionNode expression) => ((expression is BinaryExpressionNode or ConditionalExpressionNode or UnaryExpressionNode) && !expression.Parenthesized)
        ? (expression with { Parenthesized = true })
        : expression;

    /// <summary>The value negated, written as plainly as the value allows.</summary>
    private static ExpressionNode Negate(ExpressionNode value) {
        switch (value) {
            case LiteralExpressionNode { Value: long n } literal:
                return (n == 0) ? literal : literal with { Value = -n, RawText = ToggleSign(raw: literal.RawText) };
            case LiteralExpressionNode { Value: int n } literal:
                return (n == 0) ? literal : literal with { Value = -n, RawText = ToggleSign(raw: literal.RawText) };
            case LiteralExpressionNode { Value: double d } literal:
                return (d == 0) ? literal : literal with { Value = -d, RawText = ToggleSign(raw: literal.RawText) };
            case LiteralExpressionNode { Value: decimal m } literal:
                return (m == 0) ? literal : literal with { Value = -m, RawText = ToggleSign(raw: literal.RawText) };
            case UnaryExpressionNode { Operator: "-" } unary:
                return unary.Operand;
            case BinaryExpressionNode { Operator: "-" } binary when (Number(expression: binary.Left) == 0):
                return binary.Right;
            case BinaryExpressionNode { Operator: "-" } binary:
                return binary with { Left = binary.Right, Right = binary.Left };
            case BinaryExpressionNode { Operator: "*" or "/" } binary when Number(expression: binary.Left) is not null:
                return binary with { Left = Negate(value: binary.Left) };
            case BinaryExpressionNode { Operator: "*" } binary when Number(expression: binary.Right) is not null:
                return binary with { Right = Negate(value: binary.Right) };
            case ConditionalExpressionNode conditional:
                return conditional with { WhenTrue = Negate(value: conditional.WhenTrue), WhenFalse = Negate(value: conditional.WhenFalse) };
            case IdentifierExpressionNode or CallExpressionNode or MemberAccessExpressionNode:
                return new UnaryExpressionNode(Operator: "-", Operand: value);
            default:
                return new BinaryExpressionNode(Left: Literal(value: 0L), Operator: "-", Right: Operand(expression: value)) { Parenthesized = true };
        }
    }

    private static string? ToggleSign(string? raw) => raw switch {
        null => null,
        _ when raw.StartsWith(value: '-') => raw[1..],
        _ => ("-" + raw),
    };

    // The arguments of a call the rewrite writes, each but the last followed by its comma, as the parser reads a call broken
    // across lines.
    private static CallExpressionNode Call(string name, params ExpressionNode[] arguments) => new(
        Name: name,
        Arguments: [.. arguments.Select(selector: (value, index) => new ArgumentNode(Name: null, Value: value) { Trivia = SyntaxTrivia.None with { Separated = (index < (arguments.Length - 1)) } })]
    );

    private static ExpressionNode HalfTurnRotation() => Call("rotate", new ArrayExpressionNode(Elements: [Literal(value: 0L), Literal(value: 1L), Literal(value: 0L)]), Literal(value: 180L));

    /// <summary>The rotation the half turn about +Y followed by <paramref name="rotation"/> makes, or <see langword="null"/>
    /// for none (a half turn twice).</summary>
    private static ExpressionNode? HalfTurnThen(ExpressionNode rotation) {
        switch (rotation) {
            case ArrayExpressionNode { Elements.Count: 4 } quaternion when quaternion.Elements.All(predicate: e => Number(expression: e) is not null): {
                    // (0, 1, 0, 0) * (x, y, z, w) = (z, w, -x, -y): a permutation with two signs, exact.
                    var e = quaternion.Elements;
                    var permuted = new[] { e[2], e[3], Negate(value: e[0]), Negate(value: e[1]) };

                    if ((Number(expression: permuted[0]) == 0) && (Number(expression: permuted[1]) == 0) && (Number(expression: permuted[2]) == 0) && (Math.Abs(value: Number(expression: permuted[3])!.Value) == 1)) {
                        return null;
                    }

                    return quaternion with { Elements = [.. permuted.Select(selector: Clean)] };
                }
            case CallExpressionNode { Name: "rotate", Arguments.Count: 2 } call when IsUp(axis: call.Arguments[0].Value): {
                    var degrees = call.Arguments[1].Value;

                    if (Number(expression: degrees) is { } d) {
                        var turned = (((d + 180) % 360) + 360) % 360;

                        if (turned == 0) { return null; }

                        return call with { Arguments = [call.Arguments[0], call.Arguments[1] with { Value = Whole(value: turned, like: degrees) }] };
                    }

                    return call with {
                        Arguments = [call.Arguments[0], call.Arguments[1] with {
                            Value = new BinaryExpressionNode(Left: Operand(expression: degrees), Operator: "+", Right: Literal(value: 180L)) { Parenthesized = true },
                        }],
                    };
                }
            default:
                return Call("compose", HalfTurnRotation(), (ExpressionNode)rotation.WithTrivia(trivia: SyntaxTrivia.None));
        }
    }

    // A component written as -0 reads back as 0.
    private static ExpressionNode Clean(ExpressionNode value) => ((Number(expression: value) == 0) ? Literal(value: 0L) : value);

    private static LiteralExpressionNode Whole(double value, ExpressionNode like) => ((value == Math.Floor(d: value))
        ? Literal(value: (long)value) with { Unit = (like as LiteralExpressionNode)?.Unit }
        : Literal(value: value) with { Unit = (like as LiteralExpressionNode)?.Unit });

    private static bool IsUp(ExpressionNode axis) => (axis is ArrayExpressionNode { Elements.Count: 3 } array)
        && (Number(expression: array.Elements[0]) == 0) && (Number(expression: array.Elements[1]) is > 0) && (Number(expression: array.Elements[2]) == 0);

    /// <summary>The rotation turned into the engine frame without the half turn: its axis's x and z negated, which a shape
    /// symmetric under its own half turn cannot tell from the half turn followed by it.</summary>
    private static ExpressionNode? Conjugate(ExpressionNode rotation) {
        switch (rotation) {
            case ArrayExpressionNode { Elements.Count: 4 } quaternion:
                return quaternion with { Elements = [Clean(value: Negate(value: quaternion.Elements[0])), quaternion.Elements[1], Clean(value: Negate(value: quaternion.Elements[2])), quaternion.Elements[3]] };
            case CallExpressionNode { Name: "rotate", Arguments.Count: 2 } call:
                return call with { Arguments = [call.Arguments[0] with { Value = FlipVector(vector: call.Arguments[0].Value) }, call.Arguments[1]] };
            case CallExpressionNode { Name: "compose" } call:
                return call with { Arguments = [.. call.Arguments.Select(selector: argument => argument with { Value = (Conjugate(rotation: argument.Value) ?? argument.Value) })] };
            case ConditionalExpressionNode conditional:
                return conditional with { WhenTrue = (Conjugate(rotation: conditional.WhenTrue) ?? conditional.WhenTrue), WhenFalse = (Conjugate(rotation: conditional.WhenFalse) ?? conditional.WhenFalse) };
            default:
                return new BinaryExpressionNode(Left: Operand(expression: rotation), Operator: "*", Right: Mask(count: 4)) { Parenthesized = true };
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The proof
    // ---------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override string? DocumentDifference(JsonNode? before, JsonNode? after) {
        if ((before is not JsonObject beforeDocument) || (after is not JsonObject afterDocument)) {
            return base.DocumentDifference(before: before, after: after);
        }

        var expected = beforeDocument.DeepClone().AsObject();
        var allowances = new Dictionary<string, bool>(comparer: StringComparer.Ordinal);

        foreach (var (pointer, creation, prototypeId) in Creations(document: expected).ToList()) {
            if (!IsOld(creation: creation)) { continue; }

            var context = new CreationContext(creation: creation, slotFramed: IsSlotFramed(creation: creation, prototypeId: prototypeId, framed: m_slotFramedPrototypes));

            if (creation["shapes"] is JsonArray shapes) {
                for (var index = 0; (index < shapes.Count); index++) {
                    if (shapes[index] is JsonObject shape) { allowances[$"{pointer}/shapes/{index}"] = context.MayDrop(shape: shape, index: index); }
                }
            }

            RetiredConversion(creation: creation);
            creation["schema"] = NewSchema;
            if (pointer.Length > 0) {
                var prototype = creation.Parent!.AsObject();

                if (prototype["mesh"]?["vertices"] is JsonArray vertices) {
                    foreach (var vertex in vertices) { FlipInPlace(vector: vertex); }
                }
            }
        }

        return Compare(expected: expected, actual: afterDocument, path: "", allowances: allowances);
    }

    // The retired author-to-engine conversion, applied to a compiled creation: the one place it still exists.
    private static void RetiredConversion(JsonObject creation) {
        foreach (var effector in Items(node: creation["effectors"])) {
            if (effector["target"] is JsonObject target) { FlipInPlace(vector: target["direction"]); FlipInPlace(vector: target["offset"]); }
        }
        foreach (var clip in Items(node: creation["clips"])) {
            foreach (var key in Items(node: clip["keys"])) {
                foreach (var pose in Items(node: key["poses"])) {
                    FlipInPlace(vector: pose["slide"]);
                    foreach (var turn in Items(node: pose["turn"])) { FlipInPlace(vector: turn["axis"]); }
                }
            }
        }
        foreach (var camera in Items(node: creation["cameras"])) { FlipInPlace(vector: camera["position"]); }
        foreach (var frame in Items(node: creation["frames"])) {
            foreach (var transform in Items(node: frame["transforms"])) { TurnPose(row: transform); }
        }
        foreach (var shape in Items(node: creation["shapes"])) {
            TurnPose(row: shape);
            FlipInPlace(vector: shape["joint"]);
            foreach (var op in Items(node: shape["domain"])) {
                if ((op["$type"] is JsonValue type) && (type.GetValue<string>() == "symmetry")) { FlipInPlace(vector: op["normal"]); }
            }
            foreach (var slide in Items(node: shape["slides"])) { FlipInPlace(vector: slide["axis"]); }
            foreach (var swing in Items(node: shape["swings"])) { FlipInPlace(vector: swing["axis"]); FlipInPlace(vector: swing["pivot"]); }
        }
        foreach (var run in Items(node: creation["textRuns"])) {
            if (run["shapeId"] is null) { TurnPose(row: run); }
        }
        foreach (var volume in Items(node: creation["volumes"])) {
            if (volume["parent"] is null) { TurnPose(row: volume); }
        }
    }

    private static IEnumerable<JsonObject> Items(JsonNode? node) => ((node is JsonArray array) ? array.OfType<JsonObject>() : []);

    private static void TurnPose(JsonObject row) {
        row["position"] ??= new JsonArray(0, 0, 0);
        FlipInPlace(vector: row["position"]);

        var rotation = ReadQuaternion(node: row["rotation"]) ?? Quaternion.Identity;
        var turned = Quaternion.Normalize(value: (HalfTurn * rotation));

        row["rotation"] = new JsonArray(turned.X, turned.Y, turned.Z, turned.W);
    }

    private static Quaternion? ReadQuaternion(JsonNode? node) => ((node is JsonArray { Count: 4 } array)
        ? new Quaternion(x: Float(node: array[0]), y: Float(node: array[1]), z: Float(node: array[2]), w: Float(node: array[3]))
        : null);

    private static float Float(JsonNode? node) => (float)Num(node: node)!.Value;

    private static double? Num(JsonNode? node) => (node is JsonValue value)
        ? (value.TryGetValue<double>(value: out var d) ? d : value.TryGetValue<long>(value: out var l) ? l : value.TryGetValue<int>(value: out var i) ? i : value.TryGetValue<float>(value: out var f) ? f : value.TryGetValue<decimal>(value: out var m) ? (double)m : null)
        : null;

    private static void FlipInPlace(JsonNode? vector) {
        if (vector is not JsonArray { Count: 3 } array) { return; }
        array[0] = -Num(node: array[0])!.Value;
        array[2] = -Num(node: array[2])!.Value;
    }

    private static string? Compare(JsonNode? expected, JsonNode? actual, string path, Dictionary<string, bool> allowances) {
        if ((expected is null) && (actual is null)) { return null; }

        if (path.EndsWith(value: "/rotation", comparisonType: StringComparison.Ordinal) || path.EndsWith(value: "/position", comparisonType: StringComparison.Ordinal)) {
            return ComparePose(expected: expected, actual: actual, path: path, allowances: allowances);
        }
        if ((expected is null) || (actual is null)) {
            return $"{path}: expected {expected?.ToJsonString() ?? "nothing"}, found {actual?.ToJsonString() ?? "nothing"}";
        }

        switch (expected) {
            case JsonObject expectedObject when (actual is JsonObject actualObject): {
                    foreach (var key in expectedObject.Select(selector: p => p.Key).Union(second: actualObject.Select(selector: p => p.Key))) {
                        if (Compare(expected: expectedObject[key], actual: actualObject[key], path: $"{path}/{key}", allowances: allowances) is { } difference) {
                            return difference;
                        }
                    }

                    return null;
                }
            case JsonArray expectedArray when (actual is JsonArray actualArray): {
                    if (expectedArray.Count != actualArray.Count) { return $"{path}: expected {expectedArray.Count} entries, found {actualArray.Count}"; }
                    for (var index = 0; (index < expectedArray.Count); index++) {
                        if (Compare(expected: expectedArray[index], actual: actualArray[index], path: $"{path}/{index}", allowances: allowances) is { } difference) {
                            return difference;
                        }
                    }

                    return null;
                }
            case JsonValue expectedValue when (actual is JsonValue actualValue):
                if ((Num(node: expectedValue) is { } e) && (Num(node: actualValue) is { } a)) {
                    return (Close(a: e, b: a) ? null : $"{path}: expected {e}, found {a}");
                }

                return (JsonNode.DeepEquals(node1: expected, node2: actual) ? null : $"{path}: expected {expected.ToJsonString()}, found {actual.ToJsonString()}");
            default:
                return $"{path}: expected {expected.ToJsonString()}, found {actual.ToJsonString()}";
        }
    }

    private static bool Close(double a, double b) => (Math.Abs(value: (a - b)) <= (1e-6 * Math.Max(val1: 1, val2: Math.Max(val1: Math.Abs(value: a), val2: Math.Abs(value: b)))));

    private static string? ComparePose(JsonNode? expected, JsonNode? actual, string path, Dictionary<string, bool> allowances) {
        var isRotation = path.EndsWith(value: "/rotation", comparisonType: StringComparison.Ordinal);
        var identity = (isRotation ? new JsonArray(0, 0, 0, 1) : new JsonArray(0, 0, 0));
        var e = ((expected as JsonArray) ?? identity);
        var a = ((actual as JsonArray) ?? identity);

        if (!isRotation) {
            return Compare(expected: e.DeepClone(), actual: a.DeepClone(), path: (path + "#"), allowances: allowances);
        }

        // A rotation is read normalized, as the canonicalizer reads it: an authored literal of a few digits is not unit.
        var q = Quaternion.Normalize(value: ReadQuaternion(node: e)!.Value);
        var r = Quaternion.Normalize(value: ReadQuaternion(node: a)!.Value);

        if (SameRotation(q: q, r: r)) { return null; }

        var owner = path[..^"/rotation".Length];

        // A symmetric shape may drop its half turn: q is then (half turn) · conj, so r · (half turn) is q.
        if (allowances.TryGetValue(key: owner, value: out var mayDrop) && mayDrop && SameRotation(q: q, r: (Quaternion.Normalize(value: (r * HalfTurn))))) {
            return null;
        }

        return $"{path}: expected rotation {Text(q: q)}, found {Text(q: r)}{(allowances.ContainsKey(key: owner) ? (allowances[owner] ? " (a symmetric shape)" : " (a shape that must keep its half turn)") : "")}";
    }

    private static bool SameRotation(Quaternion q, Quaternion r) => (Math.Abs(value: Quaternion.Dot(quaternion1: q, quaternion2: r)) >= (1 - 1e-6));

    private static string Text(Quaternion q) => string.Create(provider: CultureInfo.InvariantCulture, $"[{q.X:0.#####}, {q.Y:0.#####}, {q.Z:0.#####}, {q.W:0.#####}]");
}
