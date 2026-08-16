using System.Buffers.Binary;

using Microsoft.Data.Sqlite;

using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

/// <summary>
/// Exercises a concrete receiver integration whose semantic effect and replay high-water mark share one
/// durable SQLite transaction. These tests complement the in-memory race controls: they prove the contract
/// survives database contention, rollback, connection disposal, and reopening the file.
/// </summary>
public sealed class DurableReplayTransactionTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    [Fact]
    public void Commit_PersistsTheEffectAndReplayMarkAcrossAClosedAndReopenedDatabase() {
        using var database = new DurableReplayDatabase();
        var requirement = VerifySequencedClaim();

        Assert.True(condition: database.TryCommit(requirement: requirement, effectKey: "wallet:user:durable", delta: 7));

        Assert.Equal(expected: 7L, actual: database.ReadEffect(effectKey: "wallet:user:durable"));
        Assert.Equal(expected: requirement.Sequence, actual: database.ReadSequence(requirement: requirement));
        Assert.True(condition: database.UsesFullSynchronousWrites());
    }
    [Fact]
    public void FailureBeforeCommit_RollsBackBothTheEffectAndReplayMark() {
        using var database = new DurableReplayDatabase();
        var requirement = VerifySequencedClaim();

        _ = Assert.Throws<InjectedCommitFailureException>(testCode: () => database.TryCommit(
            delta: 11,
            effectKey: "wallet:user:rollback",
            failBeforeCommit: true,
            requirement: requirement
        ));

        Assert.Null(@object: database.ReadEffect(effectKey: "wallet:user:rollback"));
        Assert.Null(@object: database.ReadSequence(requirement: requirement));
        Assert.True(condition: database.TryCommit(requirement: requirement, effectKey: "wallet:user:rollback", delta: 11));
        Assert.Equal(expected: 11L, actual: database.ReadEffect(effectKey: "wallet:user:rollback"));
    }
    [Fact]
    public async Task ConcurrentReceivers_CommitOneEffectAndOneReplayAdvanceExactlyOnce() {
        using var database = new DurableReplayDatabase();
        var requirement = VerifySequencedClaim();
        using var rendezvous = new Barrier(participantCount: 4);
        var tasks = Enumerable.Range(count: 4, start: 0)
            .Select(selector: _ => Task.Run(function: () => {
                rendezvous.SignalAndWait();

                return database.TryCommit(requirement: requirement, effectKey: "wallet:user:contended", delta: 5);
            }))
            .ToArray();

        await Task.WhenAll(tasks: tasks);

        Assert.Equal(expected: 1, actual: tasks.Count(predicate: task => task.Result));
        Assert.Equal(expected: 5L, actual: database.ReadEffect(effectKey: "wallet:user:contended"));
        Assert.Equal(expected: requirement.Sequence, actual: database.ReadSequence(requirement: requirement));
    }

    private static ReplayCommitRequirement VerifySequencedClaim() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:durable");
        var reach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:wallet" };
        var trust = new TrustList(
            entries: [
                new TrustListEntry(
                    PinnedId: keys.SubjectSigningId,
                    PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki,
                    Mode: AttestationTrustMode.SignsDirectly,
                    Reach: reach,
                    MaximumAge: null
                ),
            ],
            defaultMaximumAge: TimeSpan.FromHours(hours: 1),
            replayAcceptanceHorizon: TimeSpan.FromHours(hours: 1)
        );
        var claim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.durable-effect",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 1_800),
            audience: null,
            sequence: 42UL,
            text: "credit wallet exactly once"
        );
        var result = AttestationVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: [],
            trustList: trust,
            now: Now,
            expectedPurpose: "test.durable-effect",
            expectedAudience: null
        );

        Assert.True(condition: result.TryGetReplayCommit(requirement: out var requirement, slot: "slot:wallet"));

        return Assert.IsType<ReplayCommitRequirement>(@object: requirement);
    }
}

/// <summary>A file-backed receiver used to prove one real replay/effect transaction.</summary>
internal sealed class DurableReplayDatabase : IDisposable {
    private readonly string m_directory;
    private readonly string m_connectionString;

    internal DurableReplayDatabase() {
        m_directory = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-attestation-replay-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path: m_directory);

        m_connectionString = new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(path1: m_directory, path2: "receiver.sqlite3"),
            DefaultTimeout = 30,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE replay_marks (
                domain TEXT NOT NULL,
                subject TEXT NOT NULL,
                epoch_start INTEGER NOT NULL,
                retain_through INTEGER NOT NULL,
                sequence BLOB NOT NULL CHECK (length(sequence) = 8),
                PRIMARY KEY (domain, subject, epoch_start)
            ) STRICT;
            CREATE TABLE effects (
                effect_key TEXT PRIMARY KEY,
                amount INTEGER NOT NULL
            ) STRICT;
            """;
        _ = command.ExecuteNonQuery();
    }

    internal bool TryCommit(ReplayCommitRequirement requirement, string effectKey, long delta, bool failBeforeCommit = false) {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadSequence(connection: connection, requirement: requirement, transaction: transaction);

        if ((current is not null) && (requirement.Sequence <= current.Value)) {
            return false;
        }

        using (var effect = connection.CreateCommand()) {
            effect.Transaction = transaction;
            effect.CommandText = """
                INSERT INTO effects (effect_key, amount) VALUES ($effect_key, $delta)
                ON CONFLICT (effect_key) DO UPDATE SET amount = amount + excluded.amount;
                """;
            _ = effect.Parameters.AddWithValue(parameterName: "$effect_key", value: effectKey);
            _ = effect.Parameters.AddWithValue(parameterName: "$delta", value: delta);
            _ = effect.ExecuteNonQuery();
        }

        using (var replay = connection.CreateCommand()) {
            replay.Transaction = transaction;
            replay.CommandText = """
                INSERT INTO replay_marks (domain, subject, epoch_start, retain_through, sequence)
                VALUES ($domain, $subject, $epoch_start, $retain_through, $sequence)
                ON CONFLICT (domain, subject, epoch_start) DO UPDATE SET
                    retain_through = excluded.retain_through,
                    sequence = excluded.sequence;
                """;
            _ = replay.Parameters.AddWithValue(parameterName: "$domain", value: requirement.Domain);
            _ = replay.Parameters.AddWithValue(parameterName: "$subject", value: requirement.Subject);
            _ = replay.Parameters.AddWithValue(parameterName: "$epoch_start", value: requirement.EpochStartUnixSeconds);
            _ = replay.Parameters.AddWithValue(parameterName: "$retain_through", value: requirement.RetainThroughUnixSeconds);
            _ = replay.Parameters.AddWithValue(parameterName: "$sequence", value: EncodeSequence(value: requirement.Sequence));
            _ = replay.ExecuteNonQuery();
        }

        if (failBeforeCommit) {
            throw new InjectedCommitFailureException();
        }

        transaction.Commit();

        return true;
    }
    internal long? ReadEffect(string effectKey) {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT amount FROM effects WHERE effect_key = $effect_key;";
        _ = command.Parameters.AddWithValue(parameterName: "$effect_key", value: effectKey);

        var value = command.ExecuteScalar();

        return ((value is null) ? null : Convert.ToInt64(value: value, provider: System.Globalization.CultureInfo.InvariantCulture));
    }
    internal ulong? ReadSequence(ReplayCommitRequirement requirement) {
        using var connection = OpenConnection();

        return ReadSequence(connection: connection, requirement: requirement, transaction: null);
    }
    internal bool UsesFullSynchronousWrites() {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = "PRAGMA synchronous;";

        return (Convert.ToInt32(value: command.ExecuteScalar(), provider: System.Globalization.CultureInfo.InvariantCulture) == 2);
    }

    public void Dispose() => Directory.Delete(path: m_directory, recursive: true);

    private SqliteConnection OpenConnection() {
        var connection = new SqliteConnection(connectionString: m_connectionString);

        connection.Open();

        using var command = connection.CreateCommand();

        command.CommandText = "PRAGMA synchronous = FULL; PRAGMA busy_timeout = 30000;";
        _ = command.ExecuteNonQuery();

        return connection;
    }
    private static ulong? ReadSequence(SqliteConnection connection, SqliteTransaction? transaction, ReplayCommitRequirement requirement) {
        using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence FROM replay_marks
            WHERE domain = $domain AND subject = $subject AND epoch_start = $epoch_start;
            """;
        _ = command.Parameters.AddWithValue(parameterName: "$domain", value: requirement.Domain);
        _ = command.Parameters.AddWithValue(parameterName: "$subject", value: requirement.Subject);
        _ = command.Parameters.AddWithValue(parameterName: "$epoch_start", value: requirement.EpochStartUnixSeconds);

        var value = command.ExecuteScalar();

        return ((value is byte[] bytes) ? BinaryPrimitives.ReadUInt64BigEndian(source: bytes) : null);
    }
    private static byte[] EncodeSequence(ulong value) {
        var bytes = new byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(destination: bytes, value: value);

        return bytes;
    }
}
internal sealed class InjectedCommitFailureException : Exception;
