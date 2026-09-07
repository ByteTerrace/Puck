using System.Text.Json.Serialization;
using Puck.World.Browser.Engine;

namespace Puck.World.Browser.Exports;

/// <summary>The <c>Compile</c> export's result envelope: an opaque decimal-string handle on success, or the split
/// validator errors on failure.</summary>
public readonly record struct BrowserCompileResult(bool Ok, string? Handle, IReadOnlyList<BrowserErrorPath>? Errors);
/// <summary>The plain success/failure envelope every handle-taking mutation export (<c>Release</c>, <c>Rebind</c>,
/// <c>WriteRow</c>) returns.</summary>
public readonly record struct BrowserOutcome(bool Ok, string? Error);
/// <summary>The <c>Evaluate</c> export's result envelope.</summary>
public readonly record struct BrowserEvaluateResult(bool Ok, [property: JsonConverter(typeof(LongAsStringJsonConverter))] long Value, string? Error);
/// <summary>The <c>BoardMask</c> export's result envelope.</summary>
public readonly record struct BrowserBoardMaskResult(bool Ok, [property: JsonConverter(typeof(UInt64AsStringJsonConverter))] ulong Mask, string? Error);
/// <summary>The <c>StateHash</c> export's result envelope — <see cref="Ok"/> is <see langword="false"/> only when
/// <c>handle</c> names no live session.</summary>
public readonly record struct BrowserStateHashResult(bool Ok, [property: JsonConverter(typeof(UInt64AsStringJsonConverter))] ulong Hash, string? Error);
/// <summary>The <c>Cells</c> export's result envelope.</summary>
public readonly record struct BrowserCellsResult(bool Ok, IReadOnlyList<BrowserCellGeometry>? Cells, string? Error);
/// <summary>The <c>Rows</c> export's result envelope.</summary>
public readonly record struct BrowserRowsResult(bool Ok, IReadOnlyList<BrowserRowSnapshot>? Rows, string? Error);
/// <summary>The <c>Judge</c> export's result envelope.</summary>
public readonly record struct BrowserJudgeExportResult(bool Ok, BrowserJudgeResult? Trace, string? Error);

/// <summary>The source-generated metadata for every JSON shape an export marshals — one context so a caller reading
/// two different exports' output sees consistent property naming (camelCase) throughout.</summary>
[JsonSerializable(typeof(BrowserVersion))]
[JsonSerializable(typeof(BrowserParseResult))]
[JsonSerializable(typeof(BrowserCompileResult))]
[JsonSerializable(typeof(BrowserOutcome))]
[JsonSerializable(typeof(BrowserCellValue))]
[JsonSerializable(typeof(BrowserEvaluateResult))]
[JsonSerializable(typeof(BrowserBoardMaskResult))]
[JsonSerializable(typeof(BrowserStateHashResult))]
[JsonSerializable(typeof(BrowserCellsResult))]
[JsonSerializable(typeof(BrowserRowsResult))]
[JsonSerializable(typeof(BrowserJudgeExportResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BrowserExportsJsonContext : JsonSerializerContext {
}
