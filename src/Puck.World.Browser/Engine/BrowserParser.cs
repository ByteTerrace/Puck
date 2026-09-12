using Puck.Abstractions.Machines;
using System.Text;

namespace Puck.World.Browser.Engine;

/// <summary>One <c>Parse</c>/<c>ParseFragment</c>/<c>Canonicalize</c> outcome: the canonical document text and any
/// platform-deferred notices on success, or the split <c>(path, message)</c> errors on failure. Never both.</summary>
/// <param name="Ok">Whether the candidate parsed, composed (for a fragment), and validated.</param>
/// <param name="Document">The canonical UTF-8 JSON text on success; <see langword="null"/> on failure.</param>
/// <param name="Errors">The split validator messages on failure; <see langword="null"/> on success.</param>
/// <param name="Deferred">Platform-deferred admission notices (see <see cref="Puck.Attestation.TrustListEntry.ValidateKeyMaterial"/>)
/// on success; <see langword="null"/> on failure.</param>
public sealed record BrowserParseResult(bool Ok, string? Document, IReadOnlyList<BrowserErrorPath>? Errors, IReadOnlyList<string>? Deferred) {
    /// <summary>Builds a failure result from unsplit messages.</summary>
    internal static BrowserParseResult Failure(IEnumerable<string> messages) => new(
        Ok: false,
        Document: null,
        Errors: [.. messages.Select(selector: BrowserErrorPaths.Split)],
        Deferred: null
    );
}

/// <summary>The pure C# core behind the <c>Parse</c>/<c>ParseFragment</c>/<c>Canonicalize</c>/<c>Compile</c> exports —
/// parses and structurally validates a standalone world document or a fragment composed under a host, never touching
/// the file system (every byte arrives from the caller).</summary>
public static class BrowserParser {
    /// <summary>Parses, migrates, and validates a standalone world document from its UTF-8 JSON bytes.</summary>
    /// <param name="utf8Json">The candidate document's UTF-8 JSON bytes.</param>
    /// <returns>The parse result.</returns>
    public static BrowserParseResult Parse(byte[] utf8Json) {
        ArgumentNullException.ThrowIfNull(argument: utf8Json);

        var errors = new List<string>();
        var deferred = new List<string>();

        if (!TryParseAndValidate(
            compilation: out _,
            definition: out var definition,
            deferred: deferred,
            errors: errors,
            utf8Json: utf8Json
        )) {
            return BrowserParseResult.Failure(messages: errors);
        }

        return new BrowserParseResult(
            Deferred: deferred,
            Document: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition!)),
            Errors: null,
            Ok: true
        );
    }
    /// <summary>Composes <paramref name="fragmentUtf8"/> under <paramref name="hostUtf8"/> as an aliased import (see
    /// <see cref="WorldDefinitionFileSource.TryComposeFragmentBytes"/>), then parses, migrates, and validates the
    /// composition — the entry point every district and game fragment previews through. Every error and deferred
    /// notice is mapped back to the fragment's own bare row names, the alias prefix stripped.</summary>
    /// <param name="fragmentUtf8">The fragment's UTF-8 JSON bytes (a document carrying <c>exports</c>).</param>
    /// <param name="hostUtf8">The host document's UTF-8 JSON bytes (the basis the fragment composes under).</param>
    /// <param name="alias">The alias the fragment composes under.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for the selected host catalog.</param>
    /// <param name="catalog">The selected host machine catalog used for provider composition and validation, or null for structural browser composition.</param>
    /// <returns>The parse result.</returns>
    public static BrowserParseResult ParseFragment(byte[] fragmentUtf8, byte[] hostUtf8, string alias, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        ArgumentNullException.ThrowIfNull(argument: fragmentUtf8);
        ArgumentNullException.ThrowIfNull(argument: hostUtf8);
        ArgumentException.ThrowIfNullOrEmpty(argument: alias);

        if (!WorldDefinitionFileSource.TryComposeFragmentBytes(
            alias: alias,
            composed: out var composed,
            fragmentBytes: fragmentUtf8,
            hostBytes: hostUtf8,
            reason: out var composeReason,
            catalogFingerprint: catalogFingerprint,
            catalog: catalog
        )) {
            return new BrowserParseResult(Ok: false, Document: null, Errors: [BrowserErrorPaths.Split(message: composeReason)], Deferred: null);
        }

        var errors = new List<string>();
        var deferred = new List<string>();
        var composedBytes = Encoding.UTF8.GetBytes(s: composed!.ToJsonString());

        if (!TryParseAndValidate(
            compilation: out _,
            definition: out var definition,
            deferred: deferred,
            errors: errors,
            utf8Json: composedBytes,
            machines: catalog
        )) {
            return new BrowserParseResult(Ok: false, Document: null, Errors: BrowserErrorPaths.SplitFragment(messages: errors, alias: alias), Deferred: null);
        }

        var prefix = $"{alias}_";
        var strippedDeferred = deferred.Select(selector: message => message.Replace(oldValue: prefix, newValue: string.Empty, comparisonType: StringComparison.Ordinal)).ToArray();

        return new BrowserParseResult(
            Deferred: strippedDeferred,
            Document: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition!)),
            Errors: null,
            Ok: true
        );
    }
    /// <summary>Parses and re-serializes a standalone document to its canonical byte form — identically to
    /// <see cref="Parse"/>, exposed under its own name for a caller (a form round-trip proof, an official-content
    /// build step) that wants canonical bytes and has no use for the parse-result envelope's other fields.</summary>
    /// <param name="utf8Json">The candidate document's UTF-8 JSON bytes.</param>
    /// <returns>The parse result, whose <see cref="BrowserParseResult.Document"/> is the canonical text on success.</returns>
    public static BrowserParseResult Canonicalize(byte[] utf8Json) => Parse(utf8Json: utf8Json);

    // The shared parse-validate pipeline Parse/ParseFragment/Compile all ride: decode, strict-parse (draw sites
    // deferred), schema check, migrate, resolve draw sites, then validate — the same order
    // WorldDefinitionFileSource.TryParseComposed runs for a file load, minus the file-load boundary's own basis/
    // imports composition (the caller already composed a fragment, or is parsing a flat standalone document).
    internal static bool TryParseAndValidate(byte[] utf8Json, ICollection<string> errors, ICollection<string> deferred, out WorldDefinition? definition, out WorldRuleCompilation? compilation, IMachineValidationCatalog? machines = null) {
        definition = null;
        compilation = null;

        string json;

        try {
            json = Encoding.UTF8.GetString(bytes: utf8Json);
        } catch (Exception exception) when ((exception is ArgumentException or DecoderFallbackException)) {
            errors.Add(item: $"the document is not valid UTF-8 text: {exception.Message}");

            return false;
        }

        if (!WorldJsonPayload.TryParse(
            deferDrawSites: true,
            error: out var parseError,
            info: WorldJsonContext.Default.WorldDefinition,
            json: json,
            value: out var parsed
        )) {
            errors.Add(item: parseError);

            return false;
        }

        if (!string.Equals(
            a: parsed.Schema,
            b: WorldDefinition.SchemaVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            errors.Add(item: $"schema '{(parsed.Schema ?? "(absent)")}' is not '{WorldDefinition.SchemaVersion}'.");

            return false;
        }

        parsed = WorldDefinitionMigrations.Apply(definition: parsed);

        // The same first-fill draw step WorldDefinitionLoader.TryResolveDrawsAndRevalidate runs between parse and
        // the state-reference resolve below: a reference into a draw site (e.g. a creation driver's cadence naming
        // "state.strideCadence") cannot resolve until the row it names has a value, and a boot-drawn row gets one
        // only here. BootInstanceName is a fixed seed rung — a browser preview draws the same way every time it
        // parses the same document, rather than varying with a caller-supplied identity it has no use for.
        if (!WorldDrawBootResolver.TryResolve(
            definition: parsed,
            instanceIdentity: WorldDefinitionLoader.BootInstanceName,
            reason: out var drawReason,
            resolved: out var drawn
        )) {
            errors.Add(item: drawReason);

            return false;
        }

        if (!WorldStateDocumentValues.TryResolve(
            definition: drawn,
            reason: out var spatialReason
        )) {
            errors.Add(item: spatialReason);

            return false;
        }

        var ok = WorldDefinitionValidator.TryValidateLocally(
            compilation: out compilation,
            definition: drawn,
            deferred: deferred,
            errors: errors,
            machines: machines
        );

        definition = (ok ? drawn : null);

        return ok;
    }
}
