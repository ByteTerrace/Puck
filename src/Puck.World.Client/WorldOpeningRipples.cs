using System.Numerics;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>
/// The contacts of the openings that ripple (a window whose source authors <c>ripple</c>): a body that comes within
/// <see cref="Reach"/> of an opening's glass, inside its outline, sends rings across it, and a body that passes through
/// sends stronger ones. Each opening keeps the last <see cref="SdfScreenRipple.Contacts"/> contacts; a contact lasts
/// <see cref="SdfScreenRipple.Lifetime"/> seconds. Presentation only: the contacts read the bodies' render poses and the
/// presentation clock, and nothing in the simulation reads them.
/// </summary>
public sealed class WorldOpeningRipples {
    /// <summary>The distance, in metres, from a body's centre to the glass's plane within which the body touches it.</summary>
    public const float Reach = 0.45f;

    /// <summary>The height, in metres, over a body's origin of the point that touches a wall's glass.</summary>
    public const float BodyCentre = 1f;

    /// <summary>The metres past the glass's outline within which a body still touches it, a body's half width.</summary>
    public const float Margin = 0.35f;

    /// <summary>The strength of a contact made by touching the glass.</summary>
    public const float TouchStrength = 0.75f;

    /// <summary>The strength of a contact made by passing through the glass.</summary>
    public const float CrossStrength = 1f;

    private readonly Dictionary<int, Opening> m_openings = [];

    /// <summary>Advances an opening to <paramref name="seconds"/> and returns its ripple: the contacts bodies made since
    /// the last call and the contacts still showing. A call whose time is not past the last one makes no contact.</summary>
    /// <param name="screen">The opening's screen index.</param>
    /// <param name="origin">The glass's centre, in world space.</param>
    /// <param name="right">The unit axis the glass's U increases along.</param>
    /// <param name="up">The unit axis the glass's V decreases along.</param>
    /// <param name="halfWidth">The glass's half width, in metres.</param>
    /// <param name="halfHeight">The glass's half height, in metres.</param>
    /// <param name="bodies">The bodies' render poses, by entity index.</param>
    /// <param name="capacity">The count of entity indices to read.</param>
    /// <param name="seconds">The presentation clock, in seconds.</param>
    /// <returns>The opening's ripple.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bodies"/> is <see langword="null"/>.</exception>
    public SdfScreenRipple Advance(int screen, Vector3 origin, Vector3 right, Vector3 up, float halfWidth, float halfHeight, ISdfAnchorSource bodies, int capacity, double seconds) {
        ArgumentNullException.ThrowIfNull(argument: bodies);

        if (!m_openings.TryGetValue(
            key: screen,
            value: out var opening
        )) {
            opening = new Opening(capacity: capacity);
            m_openings[screen] = opening;
        }

        if (seconds > opening.Seconds) {
            var normal = Vector3.Normalize(value: Vector3.Cross(
                vector1: right,
                vector2: up
            ));
            // A wall's glass is touched at a body's middle, a floor's or a ceiling's at its feet.
            var lift = ((MathF.Abs(x: normal.Y) < 0.5f) ? BodyCentre : 0f);

            for (var body = 0; (body < capacity); body++) {
                if (!bodies.TryResolveAnchor(
                    anchor: out var anchor,
                    anchorId: body
                )) {
                    opening.Distance[body] = float.NaN;
                    opening.Touching[body] = false;

                    continue;
                }

                var offset = ((anchor.Position + (Vector3.UnitY * lift)) - origin);
                var distance = Vector3.Dot(
                    vector1: offset,
                    vector2: normal
                );
                var across = Vector3.Dot(
                    vector1: offset,
                    vector2: right
                );
                var along = Vector3.Dot(
                    vector1: offset,
                    vector2: up
                );
                var inside = (
                    (MathF.Abs(x: across) <= (halfWidth + Margin)) &&
                    (MathF.Abs(x: along) <= (halfHeight + Margin))
                );
                var touching = (inside && (MathF.Abs(x: distance) <= Reach));
                var previous = opening.Distance[body];
                var crossed = (
                    inside &&
                    !float.IsNaN(f: previous) &&
                    ((previous < 0f) != (distance < 0f)) &&
                    (MathF.Abs(x: previous) <= (2f * Reach))
                );

                if (crossed || (touching && !opening.Touching[body])) {
                    opening.Add(
                        seconds: seconds,
                        strength: (crossed ? CrossStrength : TouchStrength),
                        u: Math.Clamp(
                            max: 1f,
                            min: 0f,
                            value: (0.5f + (0.5f * (across / halfWidth)))
                        ),
                        v: Math.Clamp(
                            max: 1f,
                            min: 0f,
                            value: (0.5f - (0.5f * (along / halfHeight)))
                        )
                    );
                }

                opening.Distance[body] = distance;
                opening.Touching[body] = touching;
            }

            opening.Seconds = seconds;
        }

        return new SdfScreenRipple(
            Clock: ((float)(seconds % SdfScreenRipple.ClockPeriod)),
            Contact0: opening.ContactAt(
                seconds: seconds,
                slot: 0
            ),
            Contact1: opening.ContactAt(
                seconds: seconds,
                slot: 1
            ),
            Contact2: opening.ContactAt(
                seconds: seconds,
                slot: 2
            ),
            Contact3: opening.ContactAt(
                seconds: seconds,
                slot: 3
            )
        );
    }

    // One opening's memory: each body's last signed distance to the glass (NaN for none) and whether it touched, and the
    // contacts showing.
    private sealed class Opening(int capacity) {
        private readonly float[] m_u = new float[SdfScreenRipple.Contacts];
        private readonly float[] m_v = new float[SdfScreenRipple.Contacts];
        private readonly double[] m_start = new double[SdfScreenRipple.Contacts];
        private readonly float[] m_strength = new float[SdfScreenRipple.Contacts];

        public float[] Distance { get; } = Enumerable.Repeat(
            count: capacity,
            element: float.NaN
        ).ToArray();
        public double Seconds { get; set; } = double.NegativeInfinity;
        public bool[] Touching { get; } = new bool[capacity];

        // Records a contact in the slot that is empty or oldest.
        public void Add(double seconds, float strength, float u, float v) {
            var slot = 0;

            for (var candidate = 0; (candidate < SdfScreenRipple.Contacts); candidate++) {
                if (m_start[candidate] < m_start[slot]) {
                    slot = candidate;
                }
            }

            m_start[slot] = seconds;
            m_strength[slot] = strength;
            m_u[slot] = u;
            m_v[slot] = v;
        }
        public Vector4 ContactAt(double seconds, int slot) {
            var age = ((float)(seconds - m_start[slot]));

            return (((m_strength[slot] > 0f) && (age >= 0f) && (age < SdfScreenRipple.Lifetime))
                ? SdfScreenRipple.Contact(
                    age: age,
                    strength: m_strength[slot],
                    u: m_u[slot],
                    v: m_v[slot]
                )
                : default
            );
        }
    }
}
