# Security review

Puck is a pre-release C# engine for document-defined worlds. We would value an
independent review of how it decides **who may act, what they may see, and which
code it may run**. The most useful starting points are world-to-world identity,
hidden game state, and interrupted software updates.

Local review has repaired concrete defects and retained executable regressions. This
brief helps you challenge the resulting boundaries and their assumptions.
Deployment security and production readiness still need evidence. The
[technical reference](security-review-reference.md) has the full source map,
repaired findings, and remaining questions.

## A little context

A **world** is a running simulation hosted by a server. Players have a
**principal** (the identity attached to an action) and **grants** (permissions to
act on particular parts of that world). Some state is private to particular
players, such as a hand of cards.

**Federation** lets independently hosted worlds transfer players and forward
actions. An **attestation** is a signed statement that the receiver checks
against its trusted keys and policy. For example, a destination must prove which
world it is before the source trusts it with a transfer. A valid signature
identifies the speaker; it cannot prove that the speaker simulated the game
honestly. **Replay protection** prevents an old valid statement from authorizing
a fresh effect.

Puck also installs signed releases and runs addons compiled to WebAssembly
(WASM). Those introduce separate boundaries around downloaded files, guest
permissions, and CPU/memory consumption.

## Where your time would help most

If time is short, start with world identity and transfer. The other tracks offer
useful starting points for application-security and update-system expertise.

| Start here | Question to answer | First code and tests |
|---|---|---|
| World identity and transfer | Can a hostile peer impersonate a world, reuse a proof on another connection, or leave two live owners after an interrupted transfer? | [Remote authority](../../src/Puck.World.Server/WorldRemoteAuthority.cs), [peer host](../../src/Puck.World.Server/WorldPeerHost.cs), and [authentication tests](../../tests/Puck.World.Presentation.Tests/FederationTransferLawTests.Authentication.cs). |
| Hidden state and player permissions | Can a player learn a hidden value through a public result, or cause an effect after their permission is withdrawn? | [Transform checks](../../src/Puck.State.Rules/Compiler/RuleCompiler.Transforms.cs), [mutation admission](../../src/Puck.World.Server/WorldDocument.StateTransformAdmission.cs), and [disclosure tests](../../tests/Puck.World.Server.Tests/TransformDisclosureLawTests.cs). |
| Update recovery | Can interruption, rollback, or concurrent processes install bytes outside the accepted release, activate an unauthorized older version, or revive a rejected release? | [Update service](../../src/Puck.Launcher/Release/UpdateService.cs), [startup stub](../../src/Puck.Launcher.Stub/Program.cs), and [transaction tests](../../tests/Puck.Launcher.Tests/Release/UpdateInstallTransactionTests.cs). |

For cryptographic review, start with the [attestation verifier](../../src/Puck.Attestation/AttestationVerifier.cs)
and [sealing construction](../../src/Puck.Attestation/SealedAttestation.cs).
For cloud identity review, start with the [key endpoint](../../src/Puck.Azure.Functions/HttpTriggers/AttestKeys.cs)
and [binding service](../../src/Puck.Azure.Functions/Services/DefaultBindingService.cs).
Actual deployed identity, storage, and key-management settings require a separate
operational review; the local fixtures do not establish those settings.

## Questions that still need evidence

**Failure and revocation schedules.** Try interruption during a world transfer,
then duplicate or reorder requests after reconnection. Check when an existing
connection loses authority after trust, token validity, or admission policy
changes. Per-effect permissions and connection authentication are separate
checks; one does not automatically establish the other.

**Whole-process resource limits.** Input and guest-resource caps do not bound
the total number of guests, native compilation work, or authenticated federation
connections. Review how a hostile authorized caller can accumulate that work.
These are further engineering and review tasks, not claims that the existing
limits solve denial of service.

**Storage and deployed policy.** Actual process termination is covered by the
update tests. Storage power loss, directory durability and adversarial
filesystem links need further experiments. Deployed Azure identity, storage
and key settings need direct inspection; local generated-key fixtures cannot
establish them.

## What has already been checked

The local work covers source review and selected test suites with
generated keys, temporary install state, and loopback peers. Repairs cover
verification limits, reciprocal federation identity, transfer arithmetic,
caller-bound key renewal, bounded response reads, complete signed release file
sets, channel checks, and replay-safe update recovery. Further checks close
hidden transform inputs and runtime privacy gaps, enforce full addon integrity
pins and guest-resource limits, and demonstrate binding-command admission with
the real registry. The [findings table](security-review-reference.md#local-findings-and-regression-cases)
links each repair to its regression case and states the attacker's prerequisites.

The accompanying commits record exact commands and results. These checks are
selected suites, not a full security audit or whole-repository test run. They do
not establish deployed policy, native-runtime exploit resistance, sustained
fuzzing, or power-loss durability. Attestation, networking and release cases
also run under Linux in WSL. The updater cases include both injected exceptions
and killed child processes at replay-reservation and activation boundaries;
they do not simulate every operating-system or storage failure.

## Try a first local run

Use Windows PowerShell at the root of the supplied security branch. Install the
SDK pinned in [global.json](../../global.json); restore needs the package feeds in
[nuget.config](../../nuget.config). Run a test assembly only after its build
succeeds. These selections need no cloud account or GPU:

```powershell
dotnet build tests/Puck.Attestation.Tests -c Release -nodeReuse:false
dotnet tests/Puck.Attestation.Tests/bin/Release/net11.0/Puck.Attestation.Tests.dll --progress off

dotnet build tests/Puck.Launcher.Tests -c Release -nodeReuse:false
dotnet tests/Puck.Launcher.Tests/bin/Release/net11.0/Puck.Launcher.Tests.dll --filter-namespace Puck.Launcher.Tests.Release --progress off
```

Expect the selected cases to pass. The release suite skips an explicit child
helper in its ordinary enumeration; its parent cases run that helper in their
own processes. A build failure means there is no current
test result to interpret. The [wider selections](security-review-reference.md#running-local-checks)
cover federation, admission, key renewal, local control, and addons. Use those
when they match the boundary you are investigating.

## A useful finding

Please include the entry point, what the attacker controls, any permissions or
keys they need, a small reproducer, and the expected versus observed outcome.
For an interrupted operation, identify the last durable write and the first
incorrect action after restart. A source-level concern with explicit assumptions
is also useful; label what still needs reproduction. This makes it possible to
repair the issue and retain a regression that proves the boundary.
