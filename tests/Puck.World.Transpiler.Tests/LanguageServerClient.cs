using System.Text.Json.Nodes;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A source with its cursor written in place as a <c>|</c>, read into the text a client sends and the
/// 0-based position it asks about.</summary>
/// <param name="Text">The source without the marker.</param>
/// <param name="Line">The cursor's 0-based line.</param>
/// <param name="Character">The cursor's 0-based character within that line.</param>
internal sealed record MarkedSource(string Text, int Line, int Character) {
    /// <summary>Reads the one <c>|</c> in <paramref name="marked"/> as the cursor.</summary>
    /// <param name="marked">The source with its cursor written as <c>|</c>.</param>
    /// <returns>The source and the cursor's position.</returns>
    public static MarkedSource Parse(string marked) {
        var offset = marked.IndexOf(value: '|');

        Assert.True(
            condition: (offset >= 0),
            userMessage: $"the source marks no cursor with '|':{Environment.NewLine}{marked}"
        );

        return At(
            offset: offset,
            text: marked.Remove(
                count: 1,
                startIndex: offset
            )
        );
    }
    /// <summary>Returns the position of <paramref name="offset"/> in <paramref name="text"/>.</summary>
    /// <param name="text">The source.</param>
    /// <param name="offset">The cursor's 0-based character offset into <paramref name="text"/>.</param>
    /// <returns>The source and the cursor's position.</returns>
    public static MarkedSource At(string text, int offset) {
        var before = text[..offset];

        return new MarkedSource(
            Character: ((offset - before.LastIndexOf(value: '\n')) - 1),
            Line: before.Count(predicate: static character => (character == '\n')),
            Text: text
        );
    }
}
/// <summary>One completion the language server offered.</summary>
/// <param name="Detail">The item's detail line, which is what says where the item came from.</param>
/// <param name="InsertText">The snippet the editor would insert.</param>
/// <param name="Label">The word the list shows.</param>
internal sealed record OfferedCompletion(string Label, string InsertText, string Detail);
/// <summary>One diagnostic the language server published.</summary>
/// <param name="Code">The diagnostic's code.</param>
/// <param name="Message">The diagnostic's text.</param>
/// <param name="Line">The 0-based line its range starts on.</param>
internal sealed record PublishedDiagnostic(string Code, string Message, int Line);
/// <summary>Everything the language server wrote during one exchange, in the order it wrote it.</summary>
/// <param name="Messages">Every message, parsed.</param>
internal sealed record LanguageServerTranscript(IReadOnlyList<JsonObject> Messages) {
    /// <summary>Gets the diagnostics a client shows once the server is done: each document's last
    /// <c>publishDiagnostics</c>, which replaces every earlier one for that document (a full-depth diagnosis publishes
    /// its source tier and then both tiers together).</summary>
    public IReadOnlyList<PublishedDiagnostic> Diagnostics => [.. Notifications(method: "textDocument/publishDiagnostics")
        .GroupBy(keySelector: static notification => (notification["params"]?["uri"]?.ToString() ?? ""))
        .SelectMany(selector: static published => Assert.IsType<JsonArray>(@object: published.Last()["params"]?["diagnostics"]))
        .Select(selector: static entry => new PublishedDiagnostic(
            Code: (entry?["code"]?.ToString() ?? ""),
            Line: (entry?["range"]?["start"]?["line"]?.GetValue<int>() ?? -1),
            Message: (entry?["message"]?.ToString() ?? "")
        ))];

    /// <summary>Returns every notification the server sent under <paramref name="method"/>.</summary>
    /// <param name="method">The notification's method name.</param>
    /// <returns>The notifications, in the order they were written.</returns>
    public IEnumerable<JsonObject> Notifications(string method) => Messages.Where(predicate: message => (
        (message["method"]?.ToString() == method) &&
        !message.ContainsKey(propertyName: "id")
    ));
    /// <summary>Returns the one response to the request numbered <paramref name="id"/>, failing the test when the
    /// server answered it other than exactly once.</summary>
    /// <param name="id">The request's id.</param>
    /// <returns>The response.</returns>
    public JsonObject Response(int id) {
        var responses = Messages.Where(predicate: message => (
            !message.ContainsKey(propertyName: "method") &&
            (message["id"] is JsonValue value) &&
            value.TryGetValue<int>(value: out var answered) &&
            (answered == id)
        )).ToArray();

        Assert.True(
            condition: (responses.Length == 1),
            userMessage: $"the server answered request {id} {responses.Length} times; it wrote: {string.Join(separator: " | ", values: Messages.Select(selector: static message => message.ToJsonString()))}"
        );

        return responses[0];
    }
    /// <summary>Returns the result of the request numbered <paramref name="id"/>.</summary>
    /// <param name="id">The request's id.</param>
    /// <returns>The response's <c>result</c>, or <see langword="null"/> when it carries none.</returns>
    public JsonNode? Result(int id) => Response(id: id)["result"];
}
/// <summary>The suite's one client of <see cref="PuckLanguageServer"/>'s protocol core: it hands the server each
/// message, lets the input go quiet after each so every pending diagnosis runs, and reads back every message the server
/// wrote, each of which must be a JSON-RPC 2.0 object. The stdio framing host is exercised on its own in
/// <c>LspTests</c>.</summary>
internal static class LanguageServerClient {
    /// <summary>The document address a session opens when the caller names none.</summary>
    public const string DefaultUri = "file:///probe.puck";

    /// <summary>Returns the completion items of a completion response's result, failing the test when the result is
    /// not a completion list.</summary>
    /// <param name="result">A completion response's <c>result</c>.</param>
    /// <returns>The offered items, in the order the server sent them.</returns>
    public static IReadOnlyList<OfferedCompletion> Completions(JsonNode? result) => [.. Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: result)["items"])
        .Select(selector: static item => new OfferedCompletion(
            Detail: (item?["detail"]?.ToString() ?? ""),
            InsertText: (item?["insertText"]?.ToString() ?? ""),
            Label: (item?["label"]?.ToString() ?? "")
        ))];
    /// <summary>Returns every completion the server offers with the cursor at one position.</summary>
    /// <param name="source">The document as authored.</param>
    /// <param name="line">The cursor's 0-based line.</param>
    /// <param name="character">The cursor's 0-based character within that line.</param>
    /// <returns>The offered items, in the order the server sent them.</returns>
    public static IReadOnlyList<OfferedCompletion> CompletionsAt(string source, int line, int character) => Completions(result: RequestAtAsync(
        cursor: new MarkedSource(
            Character: character,
            Line: line,
            Text: source
        ),
        method: "textDocument/completion"
    ).GetAwaiter().GetResult());
    /// <summary>Returns the labels of every completion the server offers at a marked cursor.</summary>
    /// <param name="markedSource">The document with its cursor written as <c>|</c>.</param>
    /// <param name="vocabularyResolver">The server's vocabulary resolver; its default when omitted.</param>
    /// <returns>The offered labels.</returns>
    public static async Task<HashSet<string>> CompletionLabelsAsync(string markedSource, DocumentVocabularyResolver? vocabularyResolver = null) =>
        [.. Completions(result: await RequestAtAsync(
            cursor: MarkedSource.Parse(marked: markedSource),
            method: "textDocument/completion",
            vocabularyResolver: vocabularyResolver
        )).Select(selector: static item => item.Label)];
    /// <summary>Opens <paramref name="text"/>, sends one request about the whole document, and returns its
    /// result.</summary>
    /// <param name="text">The document's text.</param>
    /// <param name="method">The request's method name, such as <c>textDocument/documentSymbol</c>.</param>
    /// <param name="options">The request's <c>options</c> parameter, when it carries one.</param>
    /// <param name="uri">The document's address.</param>
    /// <returns>The response's <c>result</c>.</returns>
    public static async Task<JsonNode?> DocumentRequestAsync(string text, string method, JsonObject? options = null, string uri = DefaultUri) {
        var @params = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } };

        if (options is not null) {
            @params["options"] = options;
        }

        return (await SessionAsync(
            requests: [Request(
                id: 2,
                method: method,
                @params: @params
            )],
            text: text,
            uri: uri
        )).Result(id: 2);
    }
    /// <summary>Runs the server over exactly <paramref name="messages"/> and returns what it wrote.</summary>
    /// <param name="messages">The messages the client sends, in order.</param>
    /// <param name="vocabularyResolver">The server's vocabulary resolver; its default when omitted.</param>
    /// <returns>Everything the server wrote.</returns>
    public static Task<LanguageServerTranscript> ExchangeAsync(IEnumerable<JsonObject> messages, DocumentVocabularyResolver? vocabularyResolver = null) {
        var server = new PuckLanguageServer(vocabularyResolver: vocabularyResolver);
        var written = new List<JsonObject>();

        // An editor that pauses after every message: each is handled, then the input is quiet and every pending
        // diagnosis runs before the next message arrives.
        foreach (var message in messages) {
            if (!server.IsRunning) {
                break;
            }

            server.Handle(
                message: message.ToJsonString(),
                send: written.Add
            );

            while (server.RunPendingDiagnosis(
                cancellationToken: TestContext.Current.CancellationToken,
                send: written.Add
            )) {
            }
        }

        foreach (var parsed in written) {
            Assert.Equal(
                actual: parsed["jsonrpc"]?.ToString(),
                expected: "2.0"
            );
        }

        return Task.FromResult(result: new LanguageServerTranscript(Messages: written));
    }
    /// <summary>Returns the text of the hover card the server shows at a marked cursor.</summary>
    /// <param name="markedSource">The document with its cursor written as <c>|</c>.</param>
    /// <param name="uri">The document's address.</param>
    /// <param name="vocabularyResolver">The server's vocabulary resolver; its default when omitted.</param>
    /// <returns>The card's markdown, or <see langword="null"/> when the server shows none.</returns>
    public static async Task<string?> HoverAsync(string markedSource, string uri = DefaultUri, DocumentVocabularyResolver? vocabularyResolver = null) => HoverText(result: await RequestAtAsync(
        cursor: MarkedSource.Parse(marked: markedSource),
        method: "textDocument/hover",
        uri: uri,
        vocabularyResolver: vocabularyResolver
    ));
    /// <summary>Returns the text of a hover response's card.</summary>
    /// <param name="result">A hover response's <c>result</c>.</param>
    /// <returns>The card's markdown, or <see langword="null"/> when the result carries none.</returns>
    public static string? HoverText(JsonNode? result) => result?["contents"]?["value"]?.ToString();
    /// <summary>Returns a <c>textDocument/didOpen</c> notification.</summary>
    /// <param name="text">The document's text.</param>
    /// <param name="uri">The document's address.</param>
    /// <returns>The notification.</returns>
    public static JsonObject Open(string text, string uri = DefaultUri) => Notification(
        method: "textDocument/didOpen",
        @params: new JsonObject {
            ["textDocument"] = new JsonObject {
                ["uri"] = uri,
                ["languageId"] = "puck",
                ["version"] = 1,
                ["text"] = text,
            },
        }
    );
    /// <summary>Returns a notification.</summary>
    /// <param name="method">The method name.</param>
    /// <param name="params">The parameters, when it carries any.</param>
    /// <returns>The notification.</returns>
    public static JsonObject Notification(string method, JsonObject? @params = null) {
        var message = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };

        if (@params is not null) {
            message["params"] = @params;
        }

        return message;
    }
    /// <summary>Returns a position request about a document.</summary>
    /// <param name="id">The request's id.</param>
    /// <param name="method">The method name.</param>
    /// <param name="line">The 0-based line.</param>
    /// <param name="character">The 0-based character within that line.</param>
    /// <param name="uri">The document's address.</param>
    /// <returns>The request.</returns>
    public static JsonObject PositionRequest(int id, string method, int line, int character, string uri = DefaultUri) => Request(
        id: id,
        method: method,
        @params: new JsonObject {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
        }
    );
    /// <summary>Returns every diagnostic the server publishes when a document is opened, failing the test when it
    /// publishes nothing at all.</summary>
    /// <param name="text">The document's text.</param>
    /// <param name="uri">The document's address; a file address lets the server resolve the document's
    /// neighbours.</param>
    /// <returns>The published diagnostics.</returns>
    public static async Task<IReadOnlyList<PublishedDiagnostic>> PublishedAsync(string text, string uri = DefaultUri) {
        var transcript = await SessionAsync(
            text: text,
            uri: uri
        );

        Assert.NotEmpty(collection: transcript.Notifications(method: "textDocument/publishDiagnostics"));

        return transcript.Diagnostics;
    }
    /// <summary>Returns a request.</summary>
    /// <param name="id">The request's id.</param>
    /// <param name="method">The method name.</param>
    /// <param name="params">The parameters, when it carries any.</param>
    /// <returns>The request.</returns>
    public static JsonObject Request(int id, string method, JsonObject? @params = null) {
        var message = Notification(
            method: method,
            @params: @params
        );

        message["id"] = id;

        return message;
    }
    /// <summary>Opens <see cref="MarkedSource.Text"/>, sends one position request at its cursor, and returns that
    /// request's result.</summary>
    /// <param name="cursor">The document and the position asked about.</param>
    /// <param name="method">The request's method name.</param>
    /// <param name="uri">The document's address.</param>
    /// <param name="vocabularyResolver">The server's vocabulary resolver; its default when omitted.</param>
    /// <returns>The response's <c>result</c>.</returns>
    public static async Task<JsonNode?> RequestAtAsync(MarkedSource cursor, string method, string uri = DefaultUri, DocumentVocabularyResolver? vocabularyResolver = null) =>
        (await SessionAsync(
            requests: [PositionRequest(
                character: cursor.Character,
                id: 2,
                line: cursor.Line,
                method: method,
                uri: uri
            )],
            text: cursor.Text,
            uri: uri,
            vocabularyResolver: vocabularyResolver
        )).Result(id: 2);
    /// <summary>Runs one editor session: <c>initialize</c> as request 1, opening <paramref name="text"/>, each of
    /// <paramref name="requests"/>, then <c>shutdown</c> as request 9999 and <c>exit</c>.</summary>
    /// <param name="text">The document's text.</param>
    /// <param name="requests">The messages sent after the document opens; their ids must avoid 1 and 9999.</param>
    /// <param name="uri">The document's address.</param>
    /// <param name="vocabularyResolver">The server's vocabulary resolver; its default when omitted.</param>
    /// <returns>Everything the server wrote.</returns>
    public static Task<LanguageServerTranscript> SessionAsync(string text, IEnumerable<JsonObject>? requests = null, string uri = DefaultUri, DocumentVocabularyResolver? vocabularyResolver = null) =>
        ExchangeAsync(
            messages: [
                Request(
                    id: 1,
                    method: "initialize",
                    @params: []
                ),
                Open(
                    text: text,
                    uri: uri
                ),
                .. (requests ?? []),
                Request(
                    id: 9999,
                    method: "shutdown"
                ),
                Notification(method: "exit"),
            ],
            vocabularyResolver: vocabularyResolver
        );
}
