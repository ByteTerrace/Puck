# Puck.Analyzers

Puck.Analyzers provides the Roslyn analyzers used when building the repository.
It checks strict-enum usage, unmanaged function-pointer calls, environment
reads, any-address listeners, the GPU trait on test classes, references read
after their replacement, dropped rule outcomes, and `InternalsVisibleTo`
grants. It is a
compiler extension, not an engine runtime dependency or a published package.

## Usage

The shared [build properties](../../Directory.Build.props) attach the analyzer
to repository projects. The analyzer project itself is excluded to avoid a
build cycle. Its Roslyn dependency must remain compatible with the compiler
selected by the repository SDK; the project file documents that constraint.

The [CLI reference](../../docs/reference/cli.md) documents the related inspection tools.

ENV001 refuses any read of the process environment whose variable is not named,
with its reason and the assemblies that may read it, in `EnvironmentReadAllowlist`,
and any read whose name is not a compile-time constant. Nothing in Puck is
switched by an environment variable; the
[configuration guide](../../docs/development/contributing.md#configuration-and-diagnostics)
names what replaces one.

NET001 refuses a listener bound to every network interface: `IPAddress.Any`,
`IPAddress.IPv6Any` or `IPAddress.IPv6None`, Kestrel's `ListenAnyIP`,
`TcpListener.Create` or a `TcpListener` or `UdpClient` built from a port alone,
an `IPAddress` or `IPEndPoint` built from address 0, Orleans'
`listenOnAnyHostAddress: true`, and any string literal spelling `0.0.0.0`,
`[::]`, a bare `::` or a URL whose host is the `*` or `+` wildcard. Such a
listener raises a firewall prompt on every local run. A listener takes its
address from configuration and defaults to loopback. Only the deployment sites
in `AnyAddressBindAllowlist`, each named by assembly, file and member with its
reason, may name the any-address.

GPU001 refuses a test class that reaches the host's GPU without
`[Trait("Category", "Gpu")]`, the trait a test run beside a GPU leg leaves out
with `--filter-not-trait Category=Gpu` and that runs the class in its suite's
one serial collection of
[device laws](../../docs/development/contributing.md#device-laws). The ways onto the GPU carry
`[OpensGpuDevice]` (`build/OpensGpuDeviceAttribute.cs`, linked into every
project): the native device APIs and the test helpers that bring a device up.
Every member of a marked type is a way onto the GPU too. A helper that reaches
a marked member carries the mark and hands the obligation to its callers. A test
class takes the trait as xUnit gives it, from itself or a base type and never
from a type enclosing it, so a nested class that declares tests carries the
trait itself. The mark never stands in for the trait on what the runner calls
itself: a test class, a test method, or a test class's constructors and
lifecycle members. A test class built on a marked base type, or handed a class,
collection or assembly fixture that is marked or has a marked constructor or
lifecycle member, carries the trait. The rule runs only in assemblies that
reference xUnit v3. A device
reached through a composed host, or through a native entry point no mark names,
is outside what it sees.

INTEROP001 refuses a call through an unmanaged function pointer whose signature
mentions a type parameter anywhere but behind a pointer, because that call throws
`MarshalDirectiveException` at run time. The
[code conventions](../../docs/development/contributing.md#code-and-documentation-conventions)
state the rule.

STALE001 warns where a local or parameter of a `[Replaceable]` type (the state
arena, and the scratch and journal replaced with it) is read after a call that
can replace it, on a path of the method's control flow graph that writes no new
value to it: the reference names the old instance. A call replaces when its
member, or a member it overrides or implements, carries
`[Replaces(typeof(Root))]` (or `typeof(object)`, from an assembly that cannot
see the root), or when a method of the same compilation reaches such a call. A
variable handed to the call is the replacement and is exempt, and a test
assembly is not held to the rule. The marks are `build/ReplacementAttributes.cs`,
linked into every project like the GPU mark. STALE002 keeps them complete across
assemblies: a member that reaches a replacement and is reached by dispatch has
the mark on the member it overrides or implements, and a public member of a type
that hands out the root carries it. STALE003 holds a method that assigns a field
a property serves (the live pointer, as `WorldServer.Arena` serves `m_arena`)
outside a constructor to the mark. A replacement reached through a delegate, an
event, reflection, a constructor or a framework interface, and a capture held in
another object's field, are outside what the rule sees. Like every warning,
STALE001 fails the build under `TreatWarningsAsErrors`
([hazards](../../docs/development/hazards.md#a-reference-captured-across-a-replacement)).

OUTCOME001 to OUTCOME004 hold what the rule-fault types cannot hold by their
shape ([hazards](../../docs/development/hazards.md#a-result-a-caller-can-drop)).
OUTCOME001 refuses an expression statement or a discard of an `Outcome`,
`Resolved<T>`, `Verdict` or `RuleOutcome`, except in a `catch` that rethrows
and in the members `OutcomeDiscardAllowlist` names. OUTCOME002 refuses a
`TryFinite` or `TryNamed` whose bool is dropped, and a `Verdict` or
`FiringDecision` compared to one case other than `Faulted`. OUTCOME003 refuses a
call of `RuleExpressions.EvaluateDetached` from a file
`OutcomeDetachedAllowlist` does not name. OUTCOME004 refuses an `IRuleOperand`
that resolves an address against the reader itself. The rules read symbols, so
they follow an alias or a method group; a bool held in a local and never read is
outside them. A test assembly (one that references xUnit v3) is held to none of
them.

IVT001 refuses every `InternalsVisibleTo` grant, a test assembly included. It
reads the compiled assembly's attributes, so it sees an
`[assembly: InternalsVisibleTo(...)]` written in source and a csproj
`<InternalsVisibleTo>` item alike, which the SDK generates into the same
attribute. A member another assembly needs is public instead.

## Verification

The [analyzer tests](../../tests/Puck.Analyzers.Tests/README.md) compile source
fixtures in memory and inspect diagnostics. They exercise the
compiler integration separately from the production projects that load it.

## Documentation

📚 [Development](../../docs/development/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
