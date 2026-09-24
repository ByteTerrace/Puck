using System.Security.Cryptography;
using System.Text;

namespace Puck.Mcp;

// Continuous access evaluation for rejections that arrive after dispatch, when the response can no longer carry a
// bearer challenge. A subject's latest rejection challenges that subject's next request that presents the rejected
// token or one issued before it; a token issued since then is the re-authentication the challenge asked for, and
// clears it. At most one rejection is held per granted subject, and a revoked grant drops its subject's.
internal sealed class RemoteMcpChallenges {
    private readonly Lock m_gate = new();

    private readonly RemoteMcpAccessPolicy m_policy;

    private readonly Dictionary<string, Rejection> m_rejections = new(comparer: StringComparer.Ordinal);

    public RemoteMcpChallenges(RemoteMcpAccessPolicy policy) {
        m_policy = policy;
        policy.Changed += ForgetRevoked;
    }

    private static byte[] Fingerprint(string token) => SHA256.HashData(source: Encoding.UTF8.GetBytes(s: token));
    private void ForgetRevoked() {
        lock (m_gate) {
            foreach (var subject in m_rejections.Keys) {
                if (!m_policy.Allows(subject: subject)) { m_rejections.Remove(key: subject); }
            }
        }
    }

    /// <summary>Records that a downstream service rejected the caller's token after its call was dispatched.</summary>
    /// <param name="caller">The caller whose token was rejected.</param>
    /// <param name="encodedClaims">The base64 claims challenge, or null when sign-in alone is required.</param>
    internal void Remember(RemoteMcpCaller caller, string? encodedClaims) {
        var rejection = new Rejection(
            EncodedClaims: encodedClaims,
            Fingerprint: Fingerprint(token: caller.UserAssertion),
            IssuedAt: caller.IssuedAt
        );

        lock (m_gate) {
            // A rejection of an older token never lowers the bar a newer rejected token set.
            if (
                m_rejections.TryGetValue(
                    key: caller.Subject,
                    value: out var held
                ) &&
                (held.IssuedAt > rejection.IssuedAt)
            ) { return; }
            m_rejections[caller.Subject] = rejection;
        }
    }
    /// <summary>Decides whether the caller's request must be challenged because its token predates a rejection.</summary>
    /// <param name="caller">The validated caller.</param>
    /// <param name="encodedClaims">The claims challenge to return, or null when sign-in alone is required.</param>
    /// <returns>Whether the request must be answered with a challenge instead of being dispatched.</returns>
    internal bool TryChallenge(RemoteMcpCaller caller, out string? encodedClaims) {
        encodedClaims = null;
        lock (m_gate) {
            if (!m_rejections.TryGetValue(
                key: caller.Subject,
                value: out var rejection
            )) { return false; }
            if (
                CryptographicOperations.FixedTimeEquals(
                    left: rejection.Fingerprint,
                    right: Fingerprint(token: caller.UserAssertion)
                ) ||
                (caller.IssuedAt < rejection.IssuedAt)
            ) {
                encodedClaims = rejection.EncodedClaims;
                return true;
            }
            m_rejections.Remove(key: caller.Subject);
            return false;
        }
    }

    private sealed record Rejection(byte[] Fingerprint, DateTimeOffset? IssuedAt, string? EncodedClaims);
}
