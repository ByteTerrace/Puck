using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// Writes an accepted <see cref="WorldSessionLever"/> onto the live presentation service registered under its
/// <see cref="WorldSessionLever.Name"/> — the client half of the lever path, and the only place these knobs are
/// written. The console modules that reference <see cref="WorldRenderSettings"/>, <c>PresentPacingControl</c>, and the
/// audio director read their echoes only; the write travels
/// <c>verb → IServerLink.SubmitSessionLever → WorldServer.ApplySessionLever (the Mutate check) → IClientSink → here</c>.
/// </summary>
/// <remarks>
/// <para>Reached only past the server's grant check, so this type never decides authority — it dispatches. That split
/// is deliberate: one boundary owns the authority question, and adding a knob here can never accidentally add an
/// unchecked write, because the only way in is through the checked path.</para>
/// <para>The knob vocabulary is whatever the composition root registered (<see cref="WorldSessionLevers"/>), keyed by
/// the token the verb speaks. Registration is composition-time only: a duplicate name throws rather than shadowing a
/// live knob, and a lever naming nothing registered is refused by name at <see cref="Apply"/> rather than dropped.</para>
/// <para>Every knob here is presentation state (render settings, present pacing, overlay visibility, audio mix gain).
/// Nothing under <c>Server/</c> reads any of it, which is what makes the lever's <see cref="double"/> value lanes
/// safe — see <see cref="WorldSessionLever"/>'s own remarks for why a simulation-read knob may not become a lever.</para>
/// </remarks>
public sealed class WorldSessionLeverSink {
    private readonly Dictionary<string, Action<WorldSessionLever>> m_setters = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets the registered knob tokens.</summary>
    public IReadOnlyCollection<string> Names => m_setters.Keys;

    /// <summary>Applies one accepted lever, refusing by name on stderr when its token names no registered setter.</summary>
    /// <param name="lever">The lever to write.</param>
    public void Apply(WorldSessionLever lever) {
        if (!TryApply(lever: lever)) {
            Console.Error.WriteLine(value: $"[world.lever: '{lever.Name}' names no registered knob — accepted by the server and dropped here; registered: {string.Join(separator: ", ", values: m_setters.Keys.Order(comparer: StringComparer.Ordinal))}]");
        }
    }
    /// <summary>Determines whether a knob token has a registered setter.</summary>
    /// <param name="name">The token to test.</param>
    /// <returns><see langword="true"/> when the token is registered.</returns>
    public bool IsRegistered(string name) {
        ArgumentNullException.ThrowIfNull(name);

        return m_setters.ContainsKey(key: name);
    }
    /// <summary>Registers one knob's setter.</summary>
    /// <param name="name">The token the verb speaks and the lever carries.</param>
    /// <param name="setter">Writes the lever onto its live presentation service.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or already registered.</exception>
    public void Register(string name, Action<WorldSessionLever> setter) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);
        ArgumentNullException.ThrowIfNull(setter);

        if (!m_setters.TryAdd(
            key: name,
            value: setter
        )) {
            throw new ArgumentException(
                message: $"The session-lever knob '{name}' is already registered.",
                paramName: nameof(name)
            );
        }
    }
    /// <summary>Applies one accepted lever when its token names a registered setter.</summary>
    /// <param name="lever">The lever to write.</param>
    /// <returns><see langword="true"/> when a setter ran.</returns>
    public bool TryApply(WorldSessionLever lever) {
        if (
            (lever.Name is not { Length: > 0 } name) ||
            !m_setters.TryGetValue(
            key: name,
            value: out var setter
        )
        ) {
            return false;
        }

        setter(obj: lever);

        return true;
    }
}
