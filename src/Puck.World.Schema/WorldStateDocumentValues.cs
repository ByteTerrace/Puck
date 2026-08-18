using System.Collections;
using System.Reflection;
using System.Text.Json;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>Resolves state-backed values embedded anywhere in a world document against its Text state cells.</summary>
/// <remarks>
/// The walk deliberately operates at the completed-document boundary: an embedded creation is a document family in
/// its own right and cannot know the world state that contains it while it is being deserialized. Literal values are
/// untouched. Bound values retain their reference token, so serialization writes the reference back and a later
/// mutation of that state row can re-resolve a fresh candidate without mutating the live document's value objects.
/// </remarks>
public static class WorldStateDocumentValues {
    private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = [];
    private static readonly Lock PropertyCacheLock = new();

    private static PropertyInfo[] Properties(Type type) {
        lock (PropertyCacheLock) {
            if (!PropertyCache.TryGetValue(key: type, value: out var properties)) {
                properties = [.. type.GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
                    .Where(predicate: static property => property.CanRead && (property.GetIndexParameters().Length == 0))];
                PropertyCache.Add(key: type, value: properties);
            }
            return properties;
        }
    }

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive ||
        type.IsEnum ||
        type.IsPointer ||
        (type == typeof(string)) ||
        (type == typeof(decimal)) ||
        (type == typeof(DateTime)) ||
        (type == typeof(DateTimeOffset)) ||
        (type == typeof(Guid)) ||
        (type == typeof(JsonElement));

    private static bool TryVisit(object? value, string path, WorldDefinition definition, string? soughtRow, HashSet<object> seen, out bool found, out string reason) {
        found = false;
        reason = string.Empty;

        if (value is null) {
            return true;
        }

        if (value is IDocumentStateValue stateValue) {
            if (stateValue.Reference is not { } reference) {
                return true;
            }

            if (!WorldColor.TryParseBinding(value: reference, row: out var row, key: out var key)) {
                reason = $"{path} reference '{reference}' must be state.<row>[.<key>]";
                return false;
            }

            if (soughtRow is not null) {
                found = string.Equals(a: row, b: soughtRow, comparisonType: StringComparison.Ordinal);
                return true;
            }

            if (
                !WorldStateReader.TryRead(
                    definition: definition,
                    rowName: row,
                    key: key,
                    tick: 0UL,
                    row: out var stateRow,
                    rawValue: out _,
                    text: out var text
                ) ||
                (stateRow.Kind != CellKind.Text) ||
                (text is null)
            ) {
                reason = $"{path} reference '{reference}' must name a declared Text state cell";
                return false;
            }

            if (!stateValue.TryResolve(text: text, reason: out var parseReason)) {
                reason = $"{path} reference '{reference}' must hold {stateValue.ExpectedValue}: {parseReason}";
                return false;
            }

            return true;
        }

        var type = value.GetType();
        if (IsLeaf(type: type)) {
            return true;
        }

        if (!type.IsValueType && !seen.Add(item: value)) {
            return true;
        }

        if (value is IEnumerable sequence) {
            var index = 0;
            foreach (var item in sequence) {
                if (!TryVisit(
                    value: item,
                    path: $"{path}[{index}]",
                    definition: definition,
                    soughtRow: soughtRow,
                    seen: seen,
                    found: out var itemFound,
                    reason: out reason
                )) {
                    return false;
                }

                if (itemFound) {
                    found = true;
                    return true;
                }
                index++;
            }
            return true;
        }

        foreach (var property in Properties(type: type)) {
            if (!TryVisit(
                value: property.GetValue(obj: value),
                path: $"{path}.{char.ToLowerInvariant(property.Name[0])}{property.Name[1..]}",
                definition: definition,
                soughtRow: soughtRow,
                seen: seen,
                found: out var propertyFound,
                reason: out reason
            )) {
                return false;
            }

            if (propertyFound) {
                found = true;
                return true;
            }
        }

        return true;
    }

    /// <summary>Resolves every document-value reference in <paramref name="definition"/> in place.</summary>
    public static bool TryResolve(WorldDefinition definition, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        return TryVisit(
            value: definition,
            path: "definition",
            definition: definition,
            soughtRow: null,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance),
            found: out _,
            reason: out reason
        );
    }

    /// <summary>Reports whether any retained document-value reference reads <paramref name="rowName"/>.</summary>
    public static bool ReferencesRow(WorldDefinition definition, string rowName) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: rowName);
        return TryVisit(
            value: definition,
            path: "definition",
            definition: definition,
            soughtRow: rowName,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance),
            found: out var found,
            reason: out _
        ) && found;
    }

    /// <summary>
    /// Rehydrates and resolves a fresh candidate after a referenced state row changes, retaining the input object
    /// unchanged when the row has no consumers. Rehydration is intentional: record <c>with</c> composition shares
    /// unchanged embedded documents with the live definition, so resolving the candidate's mutable value holders in
    /// place would leak a rejected mutation into the live world.
    /// </summary>
    public static bool TryRefresh(WorldDefinition definition, string rowName, out WorldDefinition refreshed, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: rowName);

        if (!ReferencesRow(definition: definition, rowName: rowName)) {
            refreshed = definition;
            reason = string.Empty;
            return true;
        }

        try {
            refreshed = (JsonSerializer.Deserialize(
                utf8Json: WorldDefinitionSerialization.Serialize(definition: definition),
                jsonTypeInfo: WorldJsonContext.Default.WorldDefinition
            ) ?? throw new InvalidOperationException(message: "the refreshed world definition deserialized to null."));
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            refreshed = definition;
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }

        if (!TryResolve(definition: refreshed, reason: out reason)) {
            refreshed = definition;
            return false;
        }

        return true;
    }
}
