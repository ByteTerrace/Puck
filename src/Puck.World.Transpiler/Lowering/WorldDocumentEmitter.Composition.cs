using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    private const string CompositionAnnotation = "WorldComposition";

    private sealed class Composition(IEnumerable<StatementNode> statements) {
        public Dictionary<string, PendingWorld> Worlds { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);
        public List<WorldCompositionLink> Links { get; } = [];
        // The world declarations the source writes at its top level, the only ones a parse finds without expanding
        // anything (WorldSourceDeclaration).
        public HashSet<WorldDeclarationNode> TopLevel { get; } = new(
            collection: statements.OfType<WorldDeclarationNode>(),
            comparer: ReferenceEqualityComparer.Instance
        );
    }
    private sealed record PendingWorld(string Name, JsonObject Json, DocumentScope Scope, SourceMap Map, WorldDeclarationNode Declaration);

    // A world is admitted exactly as a parse reads it (WorldSourceDeclaration): declared at the top level of its source,
    // under a name written as a name. A reader resolves a document name to the source that emits it without compiling
    // any source, so a world whose declaration only an expansion reaches, or whose name only an evaluation computes,
    // would be a document no reader could find.
    private static void DeclareWorld(WorldDeclarationNode declaration, DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: CompositionAnnotation, value: out var value) || (value is not Composition composition)) {
            Refuse(message: "A world declaration belongs at the composition root, not inside a world module.", node: declaration, scope: scope);
            return;
        }
        if (!composition.TopLevel.Contains(item: declaration)) {
            Refuse(message: "A world is declared at the top level of its source, never inside a loop, a condition or another construct, so a reader finds every world a source emits without expanding it.", node: declaration, scope: scope);
            return;
        }
        if (WorldSourceDeclaration.WorldName(declaration: declaration) is not { } name) {
            Refuse(message: "A world's name is written as a name or a plain string, never computed, so a reader finds every world a source emits without evaluating it.", node: declaration, scope: scope);
            return;
        }

        // A world name heads the names of the worlds its tests generate, joined by '~', so it may never carry one.
        if (!GeneratedName.TryValidateAuthoredFile(
            name: name,
            reason: out var reserved
        )) {
            scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.GeneratedNameReserved, message: $"A world name {reserved}.", span: declaration.Span);
            return;
        }
        if (string.IsNullOrEmpty(value: name) || (name.Length > 100) || !char.IsAsciiLetter(c: name[0]) ||
            name.Any(predicate: static c => (!char.IsAsciiLetterOrDigit(c: c) && (c is not '_' and not '-'))) ||
            IsDeviceName(name: name)) {
            Refuse(message: "A world name must be a portable filename: an ASCII letter followed by at most 99 letters, digits, underscores or hyphens, and no reserved device name.", node: declaration, scope: scope);
            return;
        }
        if (composition.Worlds.ContainsKey(key: name)) {
            Refuse(message: $"World '{name}' is declared more than once (world filenames are case-insensitive).", node: declaration, scope: scope);
            return;
        }
        if (composition.Worlds.Count >= 256) {
            Refuse(message: "A source may declare at most 256 worlds.", node: declaration, scope: scope);
            return;
        }
        if (declaration.Entry && (composition.Worlds.Values.FirstOrDefault(predicate: static world => world.Declaration.Entry) is { } entry)) {
            scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.CompositionRefused, message: $"World '{name}' is declared the entry, but '{entry.Name}' already is; a composition boots into one world.", span: declaration.Span);
            return;
        }
        var root = new JsonObject();
        var annotations = CreateModuleAnnotations(expanded: root, scope: scope);
        var child = scope.WithConstants(new(scope.Constants, StringComparer.Ordinal), annotations).WithLocals(lambdaLocals: scope.Locals);
        var map = new SourceMap();

        using (scope.SourceMap?.PushOrigin(moduleInstance: name))
        using (scope.SourceMap?.PushIsolatedEntries()) {
            ExpandModuleUse(new ExpressionStatementNode(declaration.Module, declaration.Offset, declaration.Length, declaration.Line, declaration.Column) { IsUse = true }, declaration.Module, root, child, out var invocation);
            HoistModuleTests(call: declaration.Module, instance: name, invocation: invocation, qualify: false, scope: child);
            if (scope.SourceMap is { } sourceMap) { map.Restore(snapshot: sourceMap.Snapshot()); }
        }
        composition.Worlds.Add(key: name, value: new PendingWorld(Declaration: declaration, Json: root, Map: map, Name: name, Scope: child));
    }
    private static bool IsDeviceName(string name) => ((name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL") ||
        ((name.Length == 4) && (name.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "COM") || name.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "LPT")) && (name[3] is >= '1' and <= '9')));
    private static void DeclareLink(WorldLinkNode declaration, DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: CompositionAnnotation, value: out var value) || (value is not Composition composition)) {
            Refuse(message: "A border or door belongs at the composition root.", node: declaration, scope: scope);
            return;
        }
        if (!TryEndpoint(declaration.Left, scope, out var leftWorld, out var left) ||
            !TryEndpoint(declaration.Right, scope, out var rightWorld, out var right)) {
            Refuse(message: "A link endpoint must name world.endpoint or world.endpoint(argument).", node: declaration, scope: scope);
            return;
        }
        var options = new JsonObject();

        foreach (var property in declaration.Properties) {
            if ((property is not PropertyNode p) || options.ContainsKey(propertyName: p.Name)) {
                Refuse(message: "Link options must be distinct scalar or container properties.", node: property, scope: scope);
                continue;
            }
            var dimension = p.Name switch { "center" => "position", "width" or "height" or "hysteresis" => (p.Name + "Meters"), "pitch" or "yaw" => (p.Name + "Degrees"), _ => p.Name };

            options[p.Name] = DocumentLowering.LowerValue(p.Value, scope, dimension);
        }
        scope.Budget.Spend(count: 1, span: declaration.Span);
        var origin = (scope.SourceMap?.CaptureOrigin(span: declaration.Span) ?? new SourceOrigin(declaration.Span, null, null));

        composition.Links.Add(item: new WorldCompositionLink(declaration.Kind, leftWorld, left, rightWorld, right, options, origin));
    }
    private static bool TryEndpoint(ExpressionNode expression, DocumentScope scope, out string world, out string endpoint) {
        world = endpoint = "";
        if (expression is IdentifierExpressionNode qualified) {
            var name = QualifiedName.Parse(text: qualified.Name);

            if (!name.IsQualified || (name.Head.Length == 0) || (name.Tail.Length == 0)) { return false; }
            world = (scope.TryLowerBinding(name.Head, out var binding) ? (DocumentLowering.KeyText(node: binding) ?? "") : name.Head);
            endpoint = name.Tail;
            return (world.Length > 0);
        }
        // `world.endpoint.parts` read as members of the world's name: the endpoint is what the chain reads after it.
        if ((expression is MemberAccessExpressionNode member) && (QualifiedName.From(expression: member) is { } path)) {
            ExpressionNode target = member;

            while (target is MemberAccessExpressionNode access) { target = access.Target; }
            var id = ((IdentifierExpressionNode)target);

            world = (DocumentLowering.KeyText(node: DocumentLowering.LowerValue(id, scope)) ?? "");
            endpoint = path.ToString()[(id.Name.Length + 1)..];
            return (world.Length > 0);
        }
        if (expression is CallExpressionNode call) {
            var name = QualifiedName.Parse(text: call.Name);

            if ((name.Segments.Count != 2) || (name.Head.Length == 0) || (call.Arguments.Count != 1) || (call.Arguments[0].Name is not null)) { return false; }
            world = (scope.TryLowerBinding(name.Head, out var binding) ? (DocumentLowering.KeyText(node: binding) ?? "") : name.Head);
            var argument = DocumentLowering.KeyText(node: DocumentLowering.LowerValue(call.Arguments[0].Value, scope));

            endpoint = $"{name.Last}{argument}";
            return ((world.Length > 0) && !string.IsNullOrEmpty(value: argument));
        }
        return false;
    }
    private static void FinishComposition(Composition composition, JsonObject common, DocumentScope scope, List<WorldOutput> outputs, List<WorldTestWorld> rootTests, string stem) {
        var commonOrigins = (scope.SourceMap?.Snapshot() ?? new Dictionary<string, SourceOrigin>());

        foreach (var pending in composition.Worlds.Values) {
            var world = pending.Json;

            using (scope.SourceMap?.PushIsolatedEntries()) {
                scope.SourceMap?.Restore(snapshot: pending.Map.Snapshot());
                RefuseAuthoredGeneratedNames(
                    document: world,
                    scope: scope
                );
                RefuseReservedPlacementIds(
                    document: world,
                    scope: scope
                );
                MergeExpandedModule(world, ((JsonObject)scope.Budget.Copy(common, pending.Declaration.Span)!), pending.Declaration, scope, commonOrigins, "");
                if ((world["documentId"] is { } id) && ((id is not JsonValue idValue) || !idValue.TryGetValue<string>(value: out var authoredId) || (authoredId != pending.Name))) {
                    Refuse(message: $"World '{pending.Name}' cannot declare a different documentId; its declaration owns its identity.", node: pending.Declaration, scope: scope);
                }
                world["documentId"] = pending.Name;
                if (world["schema"] is null) { world["schema"] = WorldDocumentVocabulary.Schema; }
                EmitStateFamilies(root: world, scope: pending.Scope);
                if (scope.SourceMap is { } sourceMap) { pending.Map.Restore(snapshot: sourceMap.Snapshot()); }
            }
            WorldExpressionJson.Lower(node: world, diagnostics: scope.Diagnostics);
            WorldChannelNodes.Lower(document: world, type: typeof(WorldDefinition));
        }
        WorldCompositionLinks.Apply(composition.Worlds.ToDictionary(static p => p.Key, static p => p.Value.Json, StringComparer.Ordinal), composition.Links, scope.Diagnostics,
            composition.Worlds.ToDictionary(static p => p.Key, static p => p.Value.Map, StringComparer.Ordinal), scope.Budget);
        var subjects = new List<TestWorldSubject>(capacity: composition.Worlds.Count);

        foreach (var pending in composition.Worlds.Values) {
            var canonical = ((JsonObject)DocumentLowering.Canonicalize(node: pending.Json)!);

            outputs.Add(item: new WorldOutput(pending.Name, canonical, pending.Map, LowerTests(document: canonical, scope: pending.Scope, stem: pending.Name), pending.Declaration.Entry));
            subjects.Add(item: new TestWorldSubject(
                Entry: pending.Declaration.Entry,
                Json: canonical,
                Name: pending.Name,
                Scope: pending.Scope
            ));
        }
        // A subjectless test at the composition root is about the composed set: one generated document per world,
        // the entry world carrying the schedule that arms the rest.
        rootTests.AddRange(collection: LowerCompositionTests(
            scope: scope,
            stem: stem,
            worlds: subjects
        ));
    }
    private static void Refuse(string message, SyntaxNode node, DocumentScope scope) => scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.InvalidValue, message: message, span: node.Span);
}
