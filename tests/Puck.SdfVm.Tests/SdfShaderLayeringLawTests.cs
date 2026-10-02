using System.Text.RegularExpressions;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The SDF kernels' module tree (<c>src/Puck.SdfVm/Assets/Shaders/Sdf</c>) is layered, lowest first: the generated
/// declarations (<c>isa</c>), the field interpreter (<c>field</c>), the frame's data (<c>frame</c>), the march
/// (<c>march</c>), the surface resolve (<c>surface</c>), shading (<c>shade</c> and <c>sky</c>), the debug views (<c>debug</c>), and the
/// pass entry points and bodies (<c>passes</c>). A module depends only on modules of its own layer or a lower one: it
/// includes none above it, and it uses no symbol that only a module above it declares, because an aggregator that
/// includes a higher module first would otherwise hide the dependency. Every source lives in a layer's directory, and
/// every include resolves to a source in the tree or the shared reconstruction module, which has no SDF dependency.
/// </summary>
public sealed partial class SdfShaderLayeringLawTests {
    private const string SharedReconstruction = "Puck.Shaders/Assets/Shaders/Shared/reconstruction.hlsli";

    private static readonly string[] Layers = ["isa", "field", "frame", "march", "surface", "shade", "debug", "passes"];

    [Fact]
    public void NoModuleDependsOnAHigherLayer() {
        var root = RepositoryPaths.Resolve(relativePath: SdfWorldInterfaces.KernelDirectory);
        var files = Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*.hlsl*")
            .Where(predicate: static path => (path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".hlsl") || path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".hlsli")))
            .ToDictionary(
                elementSelector: File.ReadAllText,
                keySelector: path => Path.GetRelativePath(path: path, relativeTo: root).Replace(newChar: '/', oldChar: '\\'),
                comparer: StringComparer.Ordinal
            );

        files.Add(key: SharedReconstruction, value: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: ("src/" + SharedReconstruction))));
        Assert.NotEmpty(collection: files);
        // Joined, so a failure names every violation rather than the first few.
        Assert.Equal(
            actual: string.Join(separator: Environment.NewLine, values: Violations(files: files)),
            expected: string.Empty
        );
    }
    // The check refuses an upward include by name, and accepts one within a layer or to a lower one; it also refuses a
    // source outside every layer and an include that resolves nowhere in the tree.
    [Fact]
    public void AnUpwardIncludeIsRefusedByName() {
        var files = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["field/a.hlsli"] = "#include \"b.hlsli\"\n#include \"../isa/c.hlsli\"\n",
            ["field/b.hlsli"] = string.Empty,
            ["isa/c.hlsli"] = string.Empty,
            ["frame/d.hlsli"] = "  #  include \"../march/e.hlsli\"\n",
            ["march/e.hlsli"] = "#include \"../passes/missing.hlsli\"\n",
            ["loose.hlsli"] = string.Empty,
            ["sky/kinds/f.hlsli"] = "#include \"../../field/a.hlsli\"\n#include \"../../shade/g.hlsli\"\n#include \"../../debug/h.hlsli\"\n",
            ["shade/g.hlsli"] = string.Empty,
            ["debug/h.hlsli"] = string.Empty,
        };

        Assert.Equal(
            actual: Violations(files: files),
            expected: [
                "frame/d.hlsli includes march/e.hlsli, a higher layer",
                "loose.hlsli is in no layer",
                "march/e.hlsli includes passes/missing.hlsli, which is not in the tree",
                "sky/kinds/f.hlsli includes debug/h.hlsli, a higher layer",
            ]
        );
    }
    // The check refuses a use of a function, constant, global or macro that only a higher layer declares, a macro tested
    // in a conditional included, and a name used outside the one function whose parameter or local shares it. It accepts
    // a name its own layer or a lower one declares, a member access, a name no module declares (an intrinsic), a
    // parameter or local that shares a higher module's name inside its function, and a macro a pass defines that a module
    // only tests in a conditional.
    [Fact]
    public void AnUpwardSymbolUseIsRefusedByName() {
        var files = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["isa/a.hlsli"] = "[[vk::binding(0, 3)]] StructuredBuffer<float4> lights : register(t0, space3);\nstruct Row { float4 value; };\n",
            ["field/b.hlsli"] = "static const uint RowCount = 4u;\n#define FIELD_SCALE 2.0\nfloat fieldAt(float3 p) { return (length(p) * FIELD_SCALE); }\n",
            ["surface/c.hlsli"] = string.Join(
                separator: '\n',
                "// levelOf() in a comment is no use.",
                "float surfaceAt(float3 p) {",
                "#ifdef PASS_FLAG",
                "    float local = lights[RowCount].x;",
                "#endif",
                "    Row row; row.levelOf = 0;",
                "    return (fieldAt(p) + levelOf(p) + DEBUG_TINT + debugMode);",
                "}",
                "#ifdef DEBUG_OVERLAY",
                "float tintOf(float Tint) { return Tint; }",
                "#endif",
                "float tinted() { return Tint; }",
                "float shaded() { { float Shade = 0.0; } return Shade; }",
                "float looped() { for (int Loop = 0; Loop < 1; ++Loop) { } return Loop; }"
            ),
            ["debug/d.hlsli"] = "#define DEBUG_TINT 0.5\n#define DEBUG_OVERLAY\nstatic const int debugMode = 3;\nstatic const float Tint = 1.0;\nstatic const float Shade = 0.5;\nstatic const float Loop = 0.25;\nfloat levelOf(float3 p) { return p.x; }\n",
            ["field/f.hlsli"] = "float probe(uint debugMode) { float levelOf = 1.0; return (debugMode * levelOf); }\nfloat counted() { float total = 0.0; for (int Loop = 0; Loop < 2; ++Loop) { total += Loop; } for (int Shade = 0; Shade < 2; ++Shade) total += Shade; return total; }\n",
            ["passes/e.comp.hlsl"] = "#define PASS_FLAG\n[numthreads(8, 8, 1)] void main(uint3 id : SV_DispatchThreadID) { surfaceAt((float3)id); }\n",
        };

        Assert.Equal(
            actual: Violations(files: files),
            expected: [
                "surface/c.hlsli uses DEBUG_OVERLAY from debug/d.hlsli, a higher layer",
                "surface/c.hlsli uses DEBUG_TINT from debug/d.hlsli, a higher layer",
                "surface/c.hlsli uses Loop from debug/d.hlsli, a higher layer",
                "surface/c.hlsli uses Shade from debug/d.hlsli, a higher layer",
                "surface/c.hlsli uses Tint from debug/d.hlsli, a higher layer",
                "surface/c.hlsli uses debugMode from debug/d.hlsli, a higher layer",
                "surface/c.hlsli uses levelOf from debug/d.hlsli, a higher layer",
            ]
        );
    }

    // Every rule a tree of sources (keyed by path relative to the tree's root, forward slashes) breaks, in path order.
    private static List<string> Violations(IReadOnlyDictionary<string, string> files) {
        var violations = new List<string>();
        var declarers = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);
        var code = files.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static file => StripComments(text: file.Value),
            keySelector: static file => file.Key
        );

        foreach (var (path, text) in code) {
            foreach (var symbol in Declarations(code: text)) {
                if (!declarers.TryGetValue(key: symbol, value: out var paths)) {
                    declarers[symbol] = paths = [];
                }

                paths.Add(item: path);
            }
        }

        foreach (var (path, text) in code.OrderBy(keySelector: static file => file.Key, comparer: StringComparer.Ordinal)) {
            var layer = LayerOf(path: path);

            if (layer < 0) {
                violations.Add(item: $"{path} is in no layer");

                continue;
            }

            foreach (Match include in IncludePattern().Matches(input: text)) {
                var target = Resolve(
                    from: path,
                    include: include.Groups[1].Value
                );

                if (!files.ContainsKey(key: target)) {
                    violations.Add(item: $"{path} includes {target}, which is not in the tree");
                } else if (LayerOf(path: target) > layer) {
                    violations.Add(item: $"{path} includes {target}, a higher layer");
                }
            }

            var (uses, tested) = Uses(code: text);
            var upward = new SortedDictionary<string, IEnumerable<string>>(comparer: StringComparer.Ordinal);

            // A macro a pass defines configures the modules it includes, so a conditional that tests one is no use of a
            // higher layer; any other name is a use wherever it appears.
            foreach (var (symbols, passMacros) in new[] { (uses, false), (tested, true) }) {
                foreach (var symbol in symbols) {
                    if (!declarers.TryGetValue(key: symbol, value: out var paths)) {
                        continue;
                    }

                    var counted = paths.Where(predicate: declarer => (!passMacros || (LayerOf(path: declarer) != (Layers.Length - 1)))).ToArray();

                    if ((counted.Length > 0) && counted.All(predicate: declarer => ((declarer != path) && (LayerOf(path: declarer) > layer)))) {
                        upward[symbol] = counted;
                    }
                }
            }

            foreach (var (symbol, paths) in upward) {
                violations.Add(item: $"{path} uses {symbol} from {string.Join(separator: ", ", values: paths.Order(comparer: StringComparer.Ordinal))}, a higher layer");
            }
        }

        return violations;
    }
    // A source's layer, by the directory it sits in, or -1 when it sits in none.
    private static int LayerOf(string path) {
        if (path == SharedReconstruction) { return 0; }
        var slash = path.IndexOf(comparisonType: StringComparison.Ordinal, value: '/');

        return ((slash < 0)
            ? -1
            : Array.IndexOf(array: Layers, value: ((path[..slash] == "sky") ? "shade" : path[..slash])));
    }
    // An include's target relative to the tree's root: DXC resolves a quoted include against the including file's
    // directory, and the build passes no include directory.
    private static string Resolve(string from, string include) {
        var segments = new List<string>(collection: from.Split(separator: '/')[..^1]);

        foreach (var segment in include.Split(separator: '/')) {
            if (segment == "..") {
                if (segments.Count > 0) {
                    segments.RemoveAt(index: (segments.Count - 1));
                }
            } else if (segment != ".") {
                segments.Add(item: segment);
            }
        }

        return string.Join(separator: '/', values: segments);
    }
    // The source with each comment replaced by the line breaks it spanned, so every line keeps its place.
    private static string StripComments(string text) => CommentPattern().Replace(
        evaluator: static comment => new string(
            c: '\n',
            count: comment.Value.Count(predicate: static c => (c == '\n'))
        ),
        input: text
    );
    // The names a module declares at file scope: its macros, structs, functions, constants, globals and resources. A
    // name is declared where it follows a type (an identifier or a template's closing bracket) outside every brace and
    // parenthesis, and is followed by a parameter list, an initializer, an array bound, a register or semantic, or the
    // declaration's end.
    private static HashSet<string> Declarations(string code) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var braces = 0;
        var parens = 0;
        string? previous = null;

        foreach (var line in code.Split(separator: '\n')) {
            if (DirectivePattern().Match(input: line) is { Success: true } directive) {
                if (directive.Groups[1].Value == "define") {
                    _ = names.Add(item: directive.Groups[2].Value);
                }

                continue;
            }

            foreach (Match token in TokenPattern().Matches(input: line)) {
                var value = token.Value;

                switch (value) {
                    case "{":
                        braces++;
                        break;
                    case "}":
                        braces--;
                        break;
                    case "(":
                        parens++;
                        break;
                    case ")":
                        parens--;
                        break;
                }

                if ((braces == 0) && (parens == 0) && IsIdentifier(token: previous) && (previous != "return") && IsIdentifier(token: value)) {
                    var rest = line.AsSpan(start: (token.Index + value.Length)).TrimStart();

                    if (
                        (previous == "struct") ||
                        (rest.Length == 0) ||
                        (rest[0] is '(' or ';' or '=' or '[' or ':' or ',')
                    ) {
                        _ = names.Add(item: value);
                    }
                }

                previous = ((value == ">") ? "type" : value);
            }
        }

        return names;
    }
    // The names a module uses in code, and the macros its conditionals test. A use is an identifier outside a comment and
    // a member access; a macro definition's body is code wherever it expands. A parameter or local is its function's
    // own name: inside that one function a use of it is no use of a declaration elsewhere, and outside it, it is.
    private static (HashSet<string> Uses, HashSet<string> Tested) Uses(string code) {
        var uses = new HashSet<string>(comparer: StringComparer.Ordinal);
        var tested = new HashSet<string>(comparer: StringComparer.Ordinal);
        var lines = code.Split(separator: '\n');

        for (var index = 0; (index < lines.Length); index++) {
            if (DirectivePattern().Match(input: lines[index]) is not { Success: true } directive) {
                continue;
            }

            var keyword = directive.Groups[1].Value;
            var rest = lines[index][(directive.Groups[1].Index + keyword.Length)..];

            if (keyword == "define") {
                foreach (Match use in UsePattern().Matches(input: lines[index][(directive.Groups[2].Index + directive.Groups[2].Length)..])) {
                    _ = uses.Add(item: use.Value);
                }
            } else if (keyword is "if" or "ifdef" or "ifndef" or "elif") {
                foreach (Match use in UsePattern().Matches(input: rest)) {
                    if (use.Value != "defined") {
                        _ = tested.Add(item: use.Value);
                    }
                }
            }

            lines[index] = string.Empty;
        }

        // Each file-scope declaration or definition in turn: it ends at a semicolon or at the brace that closes its body,
        // outside every brace.
        var body = string.Join(separator: '\n', values: lines);
        var depth = 0;
        var start = 0;

        for (var index = 0; (index <= body.Length); index++) {
            var end = (index == body.Length);

            if (!end) {
                switch (body[index]) {
                    case '{':
                        depth++;
                        continue;
                    case '}':
                        depth--;
                        end = (depth == 0);
                        break;
                    case ';':
                        end = (depth == 0);
                        break;
                }
            }
            if (!end) {
                continue;
            }

            var scope = body[start..Math.Min(val1: (index + 1), val2: body.Length)];
            var blocks = BlocksOf(scope: scope);
            var locals = LocalDeclarations(
                blocks: blocks,
                scope: scope
            );

            foreach (Match use in UsePattern().Matches(input: scope)) {
                if (!locals.TryGetValue(key: use.Value, value: out var declaredIn) || !declaredIn.Any(predicate: block => blocks.Encloses(block: block, index: use.Index))) {
                    _ = uses.Add(item: use.Value);
                }
            }

            start = (index + 1);
        }

        return (uses, tested);
    }
    // The blocks of one file-scope declaration or definition: each brace pair is a block inside the one around it, and
    // everything outside every brace (a parameter list) is block 0, which encloses the whole definition. A for loop's
    // header belongs to its loop: to its body's block, or, for a braceless body, to a block of its own that also holds
    // that one statement, so the initializer's local is in scope in the header and the body and nowhere after.
    private static ScopeBlocks BlocksOf(string scope) {
        var innermost = new int[scope.Length];
        var parents = new List<int> { -1 };
        var open = new Stack<int>();

        open.Push(item: 0);

        for (var index = 0; (index < scope.Length); index++) {
            if (scope[index] == '{') {
                parents.Add(item: open.Peek());
                open.Push(item: (parents.Count - 1));
            }

            innermost[index] = open.Peek();

            if ((scope[index] == '}') && (open.Count > 1)) {
                _ = open.Pop();
            }
        }

        foreach (Match loop in ForPattern().Matches(input: scope)) {
            var header = ((loop.Index + loop.Length) - 1);
            var close = header;

            for (var depth = 0; (close < scope.Length); close++) {
                depth += ((scope[close] == '(') ? 1 : ((scope[close] == ')') ? -1 : 0));

                if (depth == 0) {
                    break;
                }
            }

            var next = (close + 1);

            while ((next < scope.Length) && char.IsWhiteSpace(c: scope[next])) {
                next++;
            }

            var end = close;
            int body;

            if ((next < scope.Length) && (scope[next] == '{')) {
                body = innermost[next];
            } else {
                parents.Add(item: innermost[header]);
                body = (parents.Count - 1);
                end = scope.IndexOf(value: ';', startIndex: Math.Min(val1: next, val2: (scope.Length - 1)));
                end = ((end < 0) ? (scope.Length - 1) : end);
            }

            for (var index = header; (index <= end); index++) {
                innermost[index] = body;
            }
        }

        return new ScopeBlocks(
            Innermost: innermost,
            Parents: parents
        );
    }
    // The names one file-scope declaration or definition declares, its parameters and locals included, each with the
    // blocks it is declared in: a name that follows a type and is followed by an initializer, an array bound, a
    // semantic, a separator or the end of a declaration or parameter list. A parameter is declared in block 0, so it
    // is in scope across the whole definition; a local only inside its own block (BlocksOf).
    private static Dictionary<string, List<int>> LocalDeclarations(ScopeBlocks blocks, string scope) {
        var names = new Dictionary<string, List<int>>(comparer: StringComparer.Ordinal);

        foreach (Match declaration in LocalDeclarationPattern().Matches(input: scope)) {
            if (declaration.Groups[1].Value is ("return" or "else" or "case" or "in" or "out" or "inout")) {
                continue;
            }

            var name = declaration.Groups[2];

            if (!names.TryGetValue(key: name.Value, value: out var declaredIn)) {
                names[name.Value] = declaredIn = [];
            }

            declaredIn.Add(item: blocks.Innermost[name.Index]);
        }

        return names;
    }

    // The block each character of one definition sits in, innermost first, and each block's enclosing block.
    private sealed record ScopeBlocks(int[] Innermost, List<int> Parents) {
        // Whether the block encloses the character at the index: it is that character's block or one around it.
        public bool Encloses(int block, int index) {
            for (var current = Innermost[index]; (current >= 0); current = Parents[current]) {
                if (current == block) {
                    return true;
                }
            }

            return false;
        }
    }

    private static bool IsIdentifier(string? token) => ((token is { Length: > 0 }) && (char.IsLetter(c: token[0]) || (token[0] == '_')));
    [GeneratedRegex(pattern: "//[^\\n]*|/\\*.*?\\*/", options: RegexOptions.Singleline)]
    private static partial Regex CommentPattern();
    [GeneratedRegex(pattern: "\\bfor\\s*\\(")]
    private static partial Regex ForPattern();
    [GeneratedRegex(pattern: "^\\s*#\\s*(\\w+)\\s*(\\w*)")]
    private static partial Regex DirectivePattern();
    [GeneratedRegex(pattern: "([A-Za-z_]\\w*|>)\\s+([A-Za-z_]\\w*)\\s*(?=[;=,)\\[:])")]
    private static partial Regex LocalDeclarationPattern();
    [GeneratedRegex(pattern: "^\\s*#\\s*include\\s+\"([^\"]+)\"", options: RegexOptions.Multiline)]
    private static partial Regex IncludePattern();
    [GeneratedRegex(pattern: "[A-Za-z_]\\w*|\\d[\\w.]*|\\S")]
    private static partial Regex TokenPattern();
    [GeneratedRegex(pattern: "(?<![\\w.])[A-Za-z_]\\w*")]
    private static partial Regex UsePattern();
}
