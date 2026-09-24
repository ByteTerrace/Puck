using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;

namespace Puck.World.Browser.Engine;

/// <summary>The language server as the browser engine hosts it: the one <see cref="PuckLanguageServer"/> protocol core,
/// handed one message per call, over a <see cref="BrowserWorkspace"/>.</summary>
/// <remarks>
/// <para>The engine runs on one thread and cannot interrupt a running call, so diagnostics never run inside
/// <see cref="Exchange"/>: an edit only marks its document pending, and the host calls <see cref="Idle"/> when its
/// inbox is empty, one unit of diagnostic work per call, yielding between calls so requests interleave. Which document
/// runs next, and whether a result is still current, is the core's decision, the same one the stdio host follows.</para>
/// <para>A document opened or changed under a <c>file:</c> URI inside the workspace is written into it as well, so the
/// open buffers are the workspace: a source that imports an edited document compiles against the edit, as do
/// <see cref="BrowserWorkspace.Compile"/> and <see cref="BrowserWorkspace.Compose"/>.</para>
/// </remarks>
public sealed class BrowserLanguageServer {
    private readonly BrowserWorkspace m_workspace;

    /// <summary>Creates a language server over <paramref name="workspace"/>.</summary>
    /// <param name="workspace">The workspace its <c>file:</c> URIs name.</param>
    public BrowserLanguageServer(BrowserWorkspace workspace) {
        ArgumentNullException.ThrowIfNull(argument: workspace);
        m_workspace = workspace;
    }

    /// <summary>Gets the protocol core, whose counts (<see cref="PuckLanguageServer.SourceDiagnoses"/>,
    /// <see cref="PuckLanguageServer.SemanticDiagnoses"/>) a test reads.</summary>
    public PuckLanguageServer Server { get; } = new();

    /// <summary>Handles one JSON-RPC message.</summary>
    /// <param name="message">The message, as text.</param>
    /// <returns>A JSON array of every message the server wrote in reply, in order.</returns>
    public string Exchange(string message) {
        ArgumentNullException.ThrowIfNull(argument: message);

        WriteThrough(message: message);

        var written = new JsonArray();

        Server.Handle(
            message: message,
            send: reply => written.Add(item: reply)
        );

        return written.ToJsonString();
    }
    /// <summary>Runs one unit of pending diagnostic work: the most recently edited document awaiting diagnosis, at its
    /// latest text.</summary>
    /// <returns><c>{ran, pending, messages}</c>: whether a unit ran, whether more work awaits, and every message the
    /// unit wrote (its <c>textDocument/publishDiagnostics</c>).</returns>
    public string Idle() {
        var written = new JsonArray();
        var ran = Server.RunPendingDiagnosis(send: reply => written.Add(item: reply));

        return new JsonObject {
            ["ran"] = ran,
            ["pending"] = Server.HasPendingDiagnostics,
            ["messages"] = written,
        }.ToJsonString();
    }

    private void WriteThrough(string message) {
        JsonObject? parsed;

        try {
            parsed = (JsonNode.Parse(json: message) as JsonObject);
        } catch (System.Text.Json.JsonException) {
            return;
        }

        var text = (parsed?["method"]?.ToString() switch {
            "textDocument/didOpen" => parsed["params"]?["textDocument"]?["text"],
            "textDocument/didChange" => (((parsed["params"]?["contentChanges"] as JsonArray) is { Count: > 0 } changes)
                ? changes[^1]?["text"]
                : null),
            _ => null,
        })?.ToString();

        if (
            (text is null) ||
            !Uri.TryCreate(
                result: out var uri,
                uriKind: UriKind.Absolute,
                uriString: (parsed?["params"]?["textDocument"]?["uri"]?.ToString() ?? "")
            ) ||
            !uri.IsFile
        ) {
            return;
        }

        var relative = m_workspace.Relative(fullPath: uri.LocalPath);

        if (m_workspace.TryResolve(
            error: out _,
            fullPath: out _,
            path: relative
        )) {
            _ = m_workspace.Write(
                path: relative,
                text: text
            );
        }
    }
}
