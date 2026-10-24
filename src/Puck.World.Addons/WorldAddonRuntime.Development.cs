using System.Text;
using Puck.Maths;
using Puck.Scripting;
using Puck.World.Protocol;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Host work uses the guest's remaining tick fuel as well as bounded bytes/cells. There are no imports,
    // clocks or external reads. Meter before copying/decoding, including refused requests.
    private static bool ChargeHostFuel(MountedAddon addon, ulong cost) {
        var remaining = (ulong)addon.Instance.FuelPerTick - addon.LastTickFuelConsumed;
        var charged = Math.Min(remaining, cost);
        addon.LastTickFuelConsumed += charged;
        addon.TotalFuelConsumed = addon.TotalFuelConsumed.AddSaturating(right: charged);
        return remaining >= cost;
    }

    private static bool TryReadUtf8(MountedAddon addon, long pointer, long length, int ceiling, out string text, out AddonVerdict refusal) {
        text = "";
        refusal = AddonVerdict.PayloadTooLarge;
        if (!ChargeHostFuel(addon, 32)) {
            refusal = AddonVerdict.QuotaExhausted;
            return false;
        }
        if (unchecked((ulong)length) > (ulong)ceiling) {
            return false;
        }
        if (!ChargeHostFuel(addon, (ulong)length)) {
            refusal = AddonVerdict.QuotaExhausted;
            return false;
        }
        Span<byte> bytes = stackalloc byte[(int)length];
        refusal = AddonVerdict.MalformedPayload;
        if (!addon.Instance.TryCopyMemory(pointer, length, bytes, out _)) {
            return false;
        }
        try {
            text = StrictUtf8.GetString(bytes);
            return true;
        } catch (DecoderFallbackException) {
            return false;
        }
    }

    private void ResolveLog(MountedAddon addon, in AddonQuerySubmission query) {
        if (!ChargeHostFuel(addon, 16) || addon.LoggedThisTick) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.QuotaExhausted);
            return;
        }
        addon.LoggedThisTick = true;
        if (!TryReadUtf8(addon, query.A, query.B, 256, out var text, out var refusal)) {
            QueueAnswer(addon, query.Ordinal, refusal);
            return;
        }
        // One guest line cannot forge host categories or inject extra log entries.
        text = string.Create(text.Length, text, static (span, source) => {
            for (var i = 0; i < span.Length; i++) {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
        Console.Error.WriteLine($"[addon.log] {addon.Instance.Name}: {text}");
        QueueAnswer(addon, query.Ordinal, AddonVerdict.Applied);
    }

    private void ResolveStateRead(MountedAddon addon, in AddonQuerySubmission query) {
        if (!ChargeHostFuel(addon, 64)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.QuotaExhausted);
            return;
        }
        var grants = m_server.Grants;
        var handle = new WorldHandle(query.HandleIndex, query.HandleGeneration, addon.Principal, WorldCapability.Observe);
        if (!grants.HandleTable(addon.Principal, WorldCapability.Observe).TryResolve(handle, out var subject) || subject.Kind != GrantSubjectKind.State) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.StaleHandle);
            return;
        }
        if (!IsRequested(addon, WorldCapability.Observe, subject)) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.AttenuatedToEmpty);
            return;
        }
        var verdict = grants.Allows(addon.Principal, WorldCapability.Observe, subject);
        if (!verdict.IsAllowed) {
            QueueAnswer(addon, query.Ordinal, WorldAddonWire.FromRule(verdict.Rule));
            return;
        }
        addon.StateReadCounts.TryGetValue(subject, out var count);
        if (!grants.TryGetBudget(addon.Principal, WorldCapability.Observe, subject, out var budget) || count >= budget) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.QuotaExhausted);
            return;
        }
        addon.StateReadCounts[subject] = count + 1;
        if (!TryReadUtf8(addon, query.A, query.B, 128, out var key, out var refusal)) {
            QueueAnswer(addon, query.Ordinal, refusal);
            return;
        }
        if (!WorldStateReader.TryRead(m_server.Definition, subject.Id!, key, m_server.CompletedTick, m_server.CompletedEngineTicks,
            out var row, out var value, out _) || value is null) {
            QueueAnswer(addon, query.Ordinal, AddonVerdict.NoSuchSubject);
            return;
        }
        QueuePart(addon, query.Ordinal, WorldAddonWire.FromRule(verdict.Rule), 0, value.Value, (long)row.Kind);
    }
}
