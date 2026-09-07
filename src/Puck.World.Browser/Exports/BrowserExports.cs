using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Puck.World.Browser.Engine;

namespace Puck.World.Browser.Exports;

/// <summary>The <c>[JSExport]</c> surface <c>dotnet.js</c>' <c>getAssemblyExports</c> resolves. Every member takes
/// and returns JSON strings — a <see langword="long"/>/<see langword="ulong"/> as a decimal string within that JSON,
/// never a bare number, so no value this engine carries depends on JavaScript's <c>Number</c> holding it exactly.
/// The runtime is single-threaded and every session lives behind an opaque handle in
/// <see cref="BrowserSessionRegistry"/>, since a JSExport call carries no state of its own between calls. Each
/// export is a thin marshalling wrapper over <see cref="Puck.World.Browser.Engine"/>, which carries no JS-interop
/// attribute so it links unmodified into <c>tests/Puck.World.Browser.Tests</c>.</summary>
[SupportedOSPlatform(platformName: "browser")]
public static partial class BrowserExports {
    /// <summary>Returns the engine identity: <c>{"schemaVersion","engine","commit"}</c>.</summary>
    [JSExport]
    public static string Version() => Write(value: BrowserEngineInfo.Describe(), info: BrowserExportsJsonContext.Default.BrowserVersion);

    /// <summary>Parses, migrates, and validates a standalone world document.</summary>
    /// <param name="json">The candidate document's UTF-8 JSON text.</param>
    /// <returns><c>{ok, document, deferred[]}</c> on success, or <c>{ok:false, errors:[{path, message}]}</c>.</returns>
    [JSExport]
    public static string Parse(string json) => Write(value: BrowserParser.Parse(utf8Json: Utf8(text: json)), info: BrowserExportsJsonContext.Default.BrowserParseResult);
    /// <summary>Composes a fragment under a host document as an aliased import, then parses and validates the
    /// composition — every error and deferred notice mapped back to the fragment's own bare row names.</summary>
    /// <param name="fragmentJson">The fragment's UTF-8 JSON text.</param>
    /// <param name="hostJson">The host document's UTF-8 JSON text.</param>
    /// <param name="alias">The alias the fragment composes under.</param>
    /// <returns>The same shape as <see cref="Parse"/>.</returns>
    [JSExport]
    public static string ParseFragment(string fragmentJson, string hostJson, string alias) => Write(
        value: BrowserParser.ParseFragment(fragmentUtf8: Utf8(text: fragmentJson), hostUtf8: Utf8(text: hostJson), alias: alias),
        info: BrowserExportsJsonContext.Default.BrowserParseResult
    );
    /// <summary>Parses and re-serializes a standalone document to its canonical byte form.</summary>
    /// <param name="json">The candidate document's UTF-8 JSON text.</param>
    /// <returns>The same shape as <see cref="Parse"/>.</returns>
    [JSExport]
    public static string Canonicalize(string json) => Write(value: BrowserParser.Canonicalize(utf8Json: Utf8(text: json)), info: BrowserExportsJsonContext.Default.BrowserParseResult);

    /// <summary>Parses, validates, and compiles a standalone document, installing it behind a fresh handle.</summary>
    /// <param name="json">The candidate document's UTF-8 JSON text.</param>
    /// <returns><c>{ok, handle}</c> on success, or <c>{ok:false, errors[]}</c>.</returns>
    [JSExport]
    public static string Compile(string json) {
        var errors = new List<string>();
        var deferred = new List<string>();

        if (!BrowserParser.TryParseAndValidate(utf8Json: Utf8(text: json), errors: errors, deferred: deferred, definition: out var definition, compilation: out var compilation)) {
            return Write(value: new BrowserCompileResult(Ok: false, Handle: null, Errors: [.. errors.Select(selector: BrowserErrorPaths.Split)]), info: BrowserExportsJsonContext.Default.BrowserCompileResult);
        }

        var session = new BrowserSession(definition: definition!, compilation: compilation!);
        var handle = BrowserSessionRegistry.Add(session: session);

        return Write(value: new BrowserCompileResult(Ok: true, Handle: handle.ToString(provider: CultureInfo.InvariantCulture), Errors: null), info: BrowserExportsJsonContext.Default.BrowserCompileResult);
    }
    /// <summary>Releases a compiled session's handle.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns><c>{ok}</c>.</returns>
    [JSExport]
    public static string Release(string handle) => Write(
        value: new BrowserOutcome(Ok: BrowserSessionRegistry.Release(handle: ParseHandle(handle: handle)), Error: null),
        info: BrowserExportsJsonContext.Default.BrowserOutcome
    );
    /// <summary>Reads every row's authored cells through the installed frame.</summary>
    /// <param name="handle">The session handle.</param>
    /// <returns><c>{ok, rows[]}</c>.</returns>
    [JSExport]
    public static string Rows(string handle) {
        if (!BrowserSessionRegistry.TryGet(handle: ParseHandle(handle: handle), session: out var session)) {
            return Write(value: new BrowserRowsResult(Ok: false, Rows: null, Error: NoSuchHandle(handle: handle)), info: BrowserExportsJsonContext.Default.BrowserRowsResult);
        }

        return Write(value: new BrowserRowsResult(Ok: true, Rows: session!.Rows(), Error: null), info: BrowserExportsJsonContext.Default.BrowserRowsResult);
    }
    /// <summary>Rebinds a session to an edited document whose state rows lay out identically to the installed one.</summary>
    /// <param name="handle">The session handle.</param>
    /// <param name="json">The candidate replacement document's UTF-8 JSON text.</param>
    /// <returns><c>{ok, error?}</c>.</returns>
    [JSExport]
    public static string Rebind(string handle, string json) {
        if (!BrowserSessionRegistry.TryGet(handle: ParseHandle(handle: handle), session: out var session)) {
            return Write(value: new BrowserOutcome(Ok: false, Error: NoSuchHandle(handle: handle)), info: BrowserExportsJsonContext.Default.BrowserOutcome);
        }

        var errors = new List<string>();
        var deferred = new List<string>();

        if (!BrowserParser.TryParseAndValidate(utf8Json: Utf8(text: json), errors: errors, deferred: deferred, definition: out var definition, compilation: out _)) {
            return Write(value: new BrowserOutcome(Ok: false, Error: string.Join(separator: "; ", values: errors)), info: BrowserExportsJsonContext.Default.BrowserOutcome);
        }

        var ok = session!.TryRebind(definition: definition!, reason: out var reason);

        return Write(value: new BrowserOutcome(Ok: ok, Error: (ok ? null : reason)), info: BrowserExportsJsonContext.Default.BrowserOutcome);
    }
    /// <summary>Judges one tick, capturing every rule's evaluations and the frame's own before/after diff.</summary>
    /// <param name="handle">The session handle.</param>
    /// <param name="tick">The tick, as a decimal string.</param>
    /// <returns><c>{ok, trace}</c>.</returns>
    [JSExport]
    public static string Judge(string handle, string tick) {
        if (!BrowserSessionRegistry.TryGet(handle: ParseHandle(handle: handle), session: out var session)) {
            return Write(value: new BrowserJudgeExportResult(Ok: false, Trace: null, Error: NoSuchHandle(handle: handle)), info: BrowserExportsJsonContext.Default.BrowserJudgeExportResult);
        }

        var trace = session!.Judge(tick: ulong.Parse(s: tick, provider: CultureInfo.InvariantCulture));

        return Write(value: new BrowserJudgeExportResult(Ok: true, Trace: trace, Error: null), info: BrowserExportsJsonContext.Default.BrowserJudgeExportResult);
    }
    /// <summary>Reads one cell through the installed frame.</summary>
    /// <param name="handle">The session handle.</param>
    /// <param name="row">The row name.</param>
    /// <param name="key">The cell key.</param>
    /// <returns><c>{found, value, text}</c>.</returns>
    [JSExport]
    public static string ReadRow(string handle, string row, string key) {
        if (!BrowserSessionRegistry.TryGet(handle: ParseHandle(handle: handle), session: out var session)) {
            return Write(value: new BrowserCellValue(Found: false, Value: 0L, Text: null), info: BrowserExportsJsonContext.Default.BrowserCellValue);
        }

        return Write(value: session!.ReadRow(row: row, key: key), info: BrowserExportsJsonContext.Default.BrowserCellValue);
    }
    /// <summary>Writes one cell through the installed frame.</summary>
    /// <param name="handle">The session handle.</param>
    /// <param name="row">The row name.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="value">The operand, as a decimal string.</param>
    /// <param name="write">Either <c>"set"</c> or <c>"add"</c>.</param>
    /// <returns><c>{ok, error?}</c>.</returns>
    [JSExport]
    public static string WriteRow(string handle, string row, string key, string value, string write) {
        if (!BrowserSessionRegistry.TryGet(handle: ParseHandle(handle: handle), session: out var session)) {
            return Write(value: new BrowserOutcome(Ok: false, Error: NoSuchHandle(handle: handle)), info: BrowserExportsJsonContext.Default.BrowserOutcome);
        }

        if (write is not ("set" or "add")) {
            return Write(value: new BrowserOutcome(Ok: false, Error: $"write '{write}' must be 'set' or 'add'."), info: BrowserExportsJsonContext.Default.BrowserOutcome);
        }

        var ok = session!.TryWriteRow(
            add: (write == "add"),
            key: key,
            reason: out var reason,
            row: row,
            value: long.Parse(s: value, provider: CultureInfo.InvariantCulture)
        );

        return Write(value: new BrowserOutcome(Ok: ok, Error: (ok ? null : reason)), info: BrowserExportsJsonContext.Default.BrowserOutcome);
    }
    /// <summary>Evaluates one infix or postfix expression against the installed frame.</summary>
    /// <param name="handle">The session handle.</param>
    /// <param name="expression">The infix or <c>{"tokens":[...]}</c> postfix spelling.</param>
    /// <param name="kind">The kind the expression must produce — one of <see cref="CellKind"/>'s declared names (<c>Int</c>/<c>Fixed</c>).</param>
    /// <param name="tick">The tick a time-sensitive token answers as of, as a decimal string.</param>
    /// <returns><c>{ok, value, error?}</c>.</returns>
    [JSExport]
    public static string Evaluate(string handle, string expression, string kind, string tick) {
        if (!BrowserSessionRegistry.TryGet(handle: ParseHandle(handle: handle), session: out var session)) {
            return Write(value: new BrowserEvaluateResult(Ok: false, Value: 0L, Error: NoSuchHandle(handle: handle)), info: BrowserExportsJsonContext.Default.BrowserEvaluateResult);
        }

        if (!Enum.TryParse<CellKind>(value: kind, result: out var parsedKind)) {
            return Write(value: new BrowserEvaluateResult(Ok: false, Value: 0L, Error: $"kind '{kind}' is not a declared CellKind."), info: BrowserExportsJsonContext.Default.BrowserEvaluateResult);
        }

        var ok = session!.TryEvaluate(
            error: out var error,
            expression: expression,
            kind: parsedKind,
            tick: ulong.Parse(s: tick, provider: CultureInfo.InvariantCulture),
            value: out var value
        );

        return Write(value: new BrowserEvaluateResult(Ok: ok, Value: value, Error: (ok ? null : error)), info: BrowserExportsJsonContext.Default.BrowserEvaluateResult);
    }
    /// <summary>Reads a board row's occupancy as one 64-bit mask.</summary>
    /// <param name="handle">The session handle.</param>
    /// <param name="row">The board row's name.</param>
    /// <returns><c>{ok, mask, error?}</c>.</returns>
    [JSExport]
    public static string BoardMask(string handle, string row) {
        if (!BrowserSessionRegistry.TryGet(handle: ParseHandle(handle: handle), session: out var session)) {
            return Write(value: new BrowserBoardMaskResult(Ok: false, Mask: 0UL, Error: NoSuchHandle(handle: handle)), info: BrowserExportsJsonContext.Default.BrowserBoardMaskResult);
        }

        var ok = session!.TryBoardMask(row: row, mask: out var mask);

        return Write(
            value: new BrowserBoardMaskResult(Ok: ok, Mask: mask, Error: (ok ? null : $"'{row}' is not a board row of at most {Puck.State.BoardMask.MaxCells} cells.")),
            info: BrowserExportsJsonContext.Default.BrowserBoardMaskResult
        );
    }
    /// <summary>Gets the installed frame's deterministic content hash.</summary>
    /// <param name="handle">The session handle.</param>
    /// <returns><c>{ok, hash, error?}</c>.</returns>
    [JSExport]
    public static string StateHash(string handle) {
        if (!BrowserSessionRegistry.TryGet(handle: ParseHandle(handle: handle), session: out var session)) {
            return Write(value: new BrowserStateHashResult(Ok: false, Hash: 0UL, Error: NoSuchHandle(handle: handle)), info: BrowserExportsJsonContext.Default.BrowserStateHashResult);
        }

        return Write(value: new BrowserStateHashResult(Ok: true, Hash: session!.StateHash(), Error: null), info: BrowserExportsJsonContext.Default.BrowserStateHashResult);
    }
    /// <summary>Compiles a standalone <c>LatticeTopology</c> JSON object into its per-cell world-space geometry.</summary>
    /// <param name="topologyJson">One <c>LatticeTopology</c> JSON object.</param>
    /// <returns><c>{ok, cells[], error?}</c>.</returns>
    [JSExport]
    public static string Cells(string topologyJson) {
        var ok = BrowserTopology.TryCells(topologyJson: topologyJson, cells: out var cells, reason: out var reason);

        return Write(value: new BrowserCellsResult(Ok: ok, Cells: (ok ? cells : null), Error: (ok ? null : reason)), info: BrowserExportsJsonContext.Default.BrowserCellsResult);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(s: text);
    private static string Write<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.Serialize(value: value, jsonTypeInfo: info);
    private static long ParseHandle(string handle) => (long.TryParse(s: handle, provider: CultureInfo.InvariantCulture, result: out var parsed) ? parsed : -1L);
    private static string NoSuchHandle(string handle) => $"handle '{handle}' names no live session (never compiled, or already released).";
}
