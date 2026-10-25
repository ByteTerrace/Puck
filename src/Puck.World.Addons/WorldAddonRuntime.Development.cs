using System.Text;
using Puck.Maths;
using Puck.Scripting;
using Puck.World.Protocol;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly record struct Utf8Entry(byte[] Bytes, string Text);

    // Host work uses the guest's remaining tick fuel as well as bounded bytes/cells. There are no imports,
    // clocks or external reads. Meter before copying/decoding, including refused requests.
    private static bool ChargeHostFuel(MountedAddon addon, ulong cost) {
        var allowed = addon.Instance.TryChargeHostFuel(cost: cost, spent: out var spent);

        addon.LastTickFuelConsumed = addon.LastTickFuelConsumed.AddSaturating(right: spent);
        addon.PeakTickFuelConsumed = Math.Max(val1: addon.PeakTickFuelConsumed, val2: addon.LastTickFuelConsumed);
        addon.TotalFuelConsumed = addon.TotalFuelConsumed.AddSaturating(right: spent);
        return allowed;
    }
    private static bool TryReadUtf8(MountedAddon addon, long pointer, long length, int ceiling, out string text, out AddonVerdict refusal) {
        text = "";
        refusal = AddonVerdict.PayloadTooLarge;
        if (!ChargeHostFuel(addon: addon, cost: 32)) {
            refusal = AddonVerdict.QuotaExhausted;
            return false;
        }
        if (unchecked((ulong)length) > ((ulong)ceiling)) {
            return false;
        }
        if (!ChargeHostFuel(addon: addon, cost: ((ulong)length))) {
            refusal = AddonVerdict.QuotaExhausted;
            return false;
        }
        Span<byte> bytes = stackalloc byte[((int)length)];

        refusal = AddonVerdict.MalformedPayload;
        if (!addon.Instance.TryCopyMemory(destination: bytes, error: out _, length: length, pointer: pointer)) {
            return false;
        }
        var address = (pointer, ((int)length));
        var cached = addon.Utf8.TryGetValue(key: address, value: out var entry);

        if (cached && bytes.SequenceEqual(other: entry.Bytes)) {
            text = entry.Text;
            return true;
        }
        try {
            text = StrictUtf8.GetString(bytes: bytes);
            if (cached) {
                bytes.CopyTo(destination: entry.Bytes);
                addon.Utf8[address] = new Utf8Entry(Bytes: entry.Bytes, Text: text);
            } else {
                if (addon.Utf8.Count == 64) { addon.Utf8.Clear(); }
                addon.Utf8.Add(key: address, value: new Utf8Entry(Bytes: bytes.ToArray(), Text: text));
            }
            return true;
        } catch (DecoderFallbackException) {
            return false;
        }
    }
    private void ResolveLog(MountedAddon addon, in AddonQuerySubmission query) {
        if (!ChargeHostFuel(addon: addon, cost: 16) || (addon.LogLinesThisTick >= 8)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.QuotaExhausted);
            return;
        }
        addon.LogLinesThisTick++;
        if (!TryReadUtf8(addon, query.A, query.B, 2048, out var text, out var refusal)) {
            QueueAnswer(addon, query.Ordinal, refusal);
            return;
        }
        // One guest line cannot forge host categories or inject extra log entries.
        text = string.Create(text.Length, text, static (span, source) => {
            for (var i = 0; (i < span.Length); i++) {
                span[i] = (char.IsControl(c: source[i]) ? ' ' : source[i]);
            }
        });
        Console.Error.WriteLine(value: $"[addon.log] {addon.Instance.Name}: {text}");
        QueueAnswer(addon, query.Ordinal, AddonVerdict.Applied);
    }
    private void ResolveStateRead(MountedAddon addon, in AddonQuerySubmission query) {
        if (!ChargeHostFuel(addon: addon, cost: 64)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.QuotaExhausted);
            return;
        }
        var grants = m_server.Grants;
        var handle = new WorldHandle(query.HandleIndex, query.HandleGeneration, addon.Principal, WorldCapability.Observe);

        if (!grants.HandleTable(addon.Principal, WorldCapability.Observe).TryResolve(handle: handle, subject: out var subject) || (subject.Kind != GrantSubjectKind.State)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.StaleHandle);
            return;
        }
        if (!IsRequested(addon: addon, capability: WorldCapability.Observe, subject: subject)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.AttenuatedToEmpty);
            return;
        }
        var verdict = grants.Allows(addon.Principal, WorldCapability.Observe, subject);

        if (!verdict.IsAllowed) {
            QueueAnswer(addon, query.Ordinal, WorldAddonWire.FromRule(rule: verdict.Rule));
            return;
        }
        addon.StateReadCounts.TryGetValue(key: subject, value: out var count);
        if (!grants.TryGetBudget(addon.Principal, WorldCapability.Observe, subject, out var budget) || (count >= budget)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.QuotaExhausted);
            return;
        }
        addon.StateReadCounts[subject] = (count + 1);
        if (!TryReadUtf8(addon, query.A, query.B, 128, out var key, out var refusal)) {
            QueueAnswer(addon, query.Ordinal, refusal);
            return;
        }
        if (!CellName.TryParse(candidate: key, name: out var name, reason: out _) ||
            !TryReadDisclosed(addon: addon, subject: subject, name: name, value: out var value) || (value.Kind is CellKind.Text or CellKind.Vector)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.NoSuchSubject);
            return;
        }
        QueuePart(addon, query.Ordinal, WorldAddonWire.FromRule(rule: verdict.Rule), 0, value.Raw, ((long)value.Kind));
    }
    // The one door an addon reads a state cell through. The Observe grant decides whether the addon may ask about the
    // row; the cell's own disclosure decides what it may learn, composed for the addon's own principal from the live
    // store on every call (a bound handle never caches it), so a cell its visibility or its containing zone withholds
    // reads exactly as an absent one does and a mod cannot probe for hidden values.
    private bool TryReadDisclosed(MountedAddon addon, GrantSubject subject, CellName name, out CellValue value) {
        var arena = m_server.Arena;

        value = default;
        return (IsDisclosed(addon: addon, subject: subject, name: name) &&
            arena.Catalog.TryResolve(StateLane.Document, subject.Id!, out var row) &&
            arena.Keys.TryResolve(key: out var address, name: name) &&
            arena.TryReadLive(row.Ordinal, address, m_server.DeliveryTime, out value));
    }
    // Whether the addon's principal may read the cell now; false for a row the document does not declare. A write door
    // asks this before it looks for the cell, so a hidden cell is refused as an absent one is.
    private bool IsDisclosed(MountedAddon addon, GrantSubject subject, CellName name) {
        var definition = m_server.Definition;

        foreach (var candidate in definition.State) {
            if (string.Equals(a: candidate.Name.Value, b: subject.Id, comparisonType: StringComparison.Ordinal)) {
                return WorldStateDisclosure.CanRead(arena: m_server.Arena, definition: definition, key: name, row: candidate, recipient: addon.Principal);
            }
        }
        return false;
    }
}
