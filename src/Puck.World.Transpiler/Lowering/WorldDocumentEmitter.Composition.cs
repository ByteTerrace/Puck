using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    private const string CompositionAnnotation = "WorldComposition";

    private sealed class Composition {
        public Dictionary<string, PendingWorld> Worlds { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);
        public List<WorldCompositionLink> Links { get; } = [];
    }
    private sealed record PendingWorld(string Name, JsonObject Json, DocumentScope Scope, SourceMap Map, WorldDeclarationNode Declaration);

    private static void DeclareWorld(WorldDeclarationNode declaration, DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: CompositionAnnotation, value: out var value) || (value is not Composition composition)) {
            Refuse("A world declaration belongs at the composition root, not inside a world module.", declaration, scope);
            return;
        }
        var name = DocumentLowering.KeyText(node: DocumentLowering.LowerValue(declaration.Name, scope));

        if (string.IsNullOrEmpty(value: name) || (name.Length > 100) || !char.IsAsciiLetter(c: name[0]) ||
            name.Any(predicate: static c => (!char.IsAsciiLetterOrDigit(c: c) && (c is not '_' and not '-'))) ||
            IsDeviceName(name: name)) {
            Refuse("A world name must be a portable filename: an ASCII letter followed by at most 99 letters, digits, underscores or hyphens, and no reserved device name.", declaration, scope);
            return;
        }
        if (composition.Worlds.ContainsKey(key: name)) {
            Refuse($"World '{name}' is declared more than once (world filenames are case-insensitive).", declaration, scope);
            return;
        }
        if (composition.Worlds.Count >= 256) {
            Refuse("A source may declare at most 256 worlds.", declaration, scope);
            return;
        }
        var root = new JsonObject();
        var annotations = CreateModuleAnnotations(expanded: root, scope: scope);
        var child = scope.WithConstants(new(scope.Constants, StringComparer.Ordinal), annotations).WithLocals(lambdaLocals: scope.Locals);
        var map = new SourceMap();

        using (scope.SourceMap?.PushOrigin(moduleInstance: name))
        using (scope.SourceMap?.PushIsolatedEntries()) {
            ExpandModuleUse(new ExpressionStatementNode(declaration.Module, declaration.Offset, declaration.Length, declaration.Line, declaration.Column) { IsUse = true }, declaration.Module, root, child);
            if (scope.SourceMap is { } sourceMap) { map.Restore(sourceMap.Snapshot()); }
        }
        composition.Worlds.Add(key: name, value: new PendingWorld(Declaration: declaration, Json: root, Map: map, Name: name, Scope: child));
    }
    private static bool IsDeviceName(string name) => ((name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL") ||
        ((name.Length == 4) && (name.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "COM") || name.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "LPT")) && (name[3] is >= '1' and <= '9')));
    private static void DeclareLink(WorldLinkNode declaration, DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: CompositionAnnotation, value: out var value) || (value is not Composition composition)) {
            Refuse("A border or door belongs at the composition root.", declaration, scope);
            return;
        }
        if (!TryEndpoint(declaration.Left, scope, out var leftWorld, out var left) ||
            !TryEndpoint(declaration.Right, scope, out var rightWorld, out var right)) {
            Refuse("A link endpoint must name world.endpoint or world.endpoint(argument).", declaration, scope);
            return;
        }
        var options = new JsonObject();

        foreach (var property in declaration.Properties) {
            if ((property is not PropertyNode p) || options.ContainsKey(propertyName: p.Name)) {
                Refuse("Link options must be distinct scalar or container properties.", property, scope);
                continue;
            }
            var dimension = p.Name switch { "center" => "position", "width" or "height" or "hysteresis" => (p.Name + "Meters"), "pitch" or "yaw" => (p.Name + "Degrees"), _ => p.Name };

            options[p.Name] = DocumentLowering.LowerValue(p.Value, scope, dimension);
        }
        scope.Budget.Spend(1, declaration.Span);
        var origin = (scope.SourceMap?.CaptureOrigin(declaration.Span) ?? new SourceOrigin(declaration.Span, null, null));

        composition.Links.Add(item: new WorldCompositionLink(declaration.Kind, leftWorld, left, rightWorld, right, options, origin));
    }
    private static bool TryEndpoint(ExpressionNode expression, DocumentScope scope, out string world, out string endpoint) {
        world = endpoint = "";
        if (expression is IdentifierExpressionNode qualified) {
            var dot = qualified.Name.IndexOf(value: '.');

            if ((dot <= 0) || (dot == (qualified.Name.Length - 1))) { return false; }
            var receiver = qualified.Name[..dot];

            world = (scope.TryLowerBinding(receiver, out var binding) ? (DocumentLowering.KeyText(node: binding) ?? "") : receiver);
            endpoint = qualified.Name[(dot + 1)..];
            return (world.Length > 0);
        }
        if (expression is MemberAccessExpressionNode member) {
            var parts = new List<string>();
            ExpressionNode target = member;

            while (target is MemberAccessExpressionNode access) { parts.Add(item: access.Member); target = access.Target; }
            if (target is not IdentifierExpressionNode id) { return false; }
            world = (DocumentLowering.KeyText(node: DocumentLowering.LowerValue(id, scope)) ?? "");
            parts.Reverse();
            endpoint = string.Join(separator: '.', values: parts);
            return (world.Length > 0);
        }
        if (expression is CallExpressionNode call) {
            var dot = call.Name.IndexOf(value: '.');

            if ((dot <= 0) || (call.Name.IndexOf(startIndex: (dot + 1), value: '.') >= 0) || (call.Arguments.Count != 1) || (call.Arguments[0].Name is not null)) { return false; }
            world = (scope.TryLowerBinding(call.Name[..dot], out var binding) ? (DocumentLowering.KeyText(node: binding) ?? "") : call.Name[..dot]);
            var argument = DocumentLowering.KeyText(node: DocumentLowering.LowerValue(call.Arguments[0].Value, scope));

            endpoint = $"{call.Name[(dot + 1)..]}{argument}";
            return ((world.Length > 0) && !string.IsNullOrEmpty(value: argument));
        }
        return false;
    }
    private static void FinishComposition(Composition composition, JsonObject common, DocumentScope scope, List<WorldOutput> outputs) {
        if (GetOrCreateTests(scope: scope).Count > 0) {
            Refuse(message: "A composition-level test must select a world; distributed tests are not supported.", node: GetOrCreateTests(scope: scope)[0], scope: scope);
        }
        var commonOrigins = (scope.SourceMap?.Snapshot() ?? new Dictionary<string, SourceOrigin>());

        foreach (var pending in composition.Worlds.Values) {
            var world = pending.Json;

            using (scope.SourceMap?.PushIsolatedEntries()) {
                scope.SourceMap?.Restore(pending.Map.Snapshot());
                MergeExpandedModule(world, ((JsonObject)scope.Budget.Copy(common, pending.Declaration.Span)!), pending.Declaration, scope, commonOrigins, "");
                if ((world["documentId"] is { } id) && ((id is not JsonValue idValue) || !idValue.TryGetValue<string>(value: out var authoredId) || (authoredId != pending.Name))) {
                    Refuse($"World '{pending.Name}' cannot declare a different documentId; its declaration owns its identity.", pending.Declaration, scope);
                }
                world["documentId"] = pending.Name;
                if (world["schema"] is null) { world["schema"] = WorldDocumentVocabulary.Schema; }
                EmitStateFamilies(root: world, scope: pending.Scope);
                if (scope.SourceMap is { } sourceMap) { pending.Map.Restore(sourceMap.Snapshot()); }
            }
            WorldExpressionJson.Lower(node: world, diagnostics: scope.Diagnostics);
            WorldChannelNodes.Lower(document: world, type: typeof(WorldDefinition));
        }
        WorldCompositionLinks.Apply(composition.Worlds.ToDictionary(static p => p.Key, static p => p.Value.Json, StringComparer.Ordinal), composition.Links, scope.Diagnostics,
            composition.Worlds.ToDictionary(static p => p.Key, static p => p.Value.Map, StringComparer.Ordinal), scope.Budget);
        foreach (var pending in composition.Worlds.Values) {
            var canonical = ((JsonObject)Canonicalize(node: pending.Json)!);

            outputs.Add(item: new WorldOutput(pending.Name, canonical, pending.Map, LowerTests(document: canonical, scope: pending.Scope, stem: pending.Name)));
        }
    }
    private static void Refuse(string message, SyntaxNode node, DocumentScope scope) => scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.InvalidValue, message: message, span: node.Span);
}
