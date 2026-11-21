namespace Puck.Cli;

/// <summary>Waits until the host has room to take a heavy start, which is how <c>puck lease</c> and the MCP adapter's World
/// start reach the one admission (<c>HostAdmission</c>, in the harness assembly, which sits above this one: the root hands
/// it in, as it does <see cref="LeaseSettle"/>).</summary>
/// <param name="checkout">The checkout whose drive's free disk is judged.</param>
/// <param name="step">The step's name, for the wait lines.</param>
/// <param name="device">Whether the step opens a GPU device, and so also waits while the GPU is full beside another working
/// device holder.</param>
/// <param name="heavySuite">Whether the step runs heavy work, and so also waits for CPU headroom while another process's heavy hold runs.</param>
/// <param name="profile">What the start is, which sets the commit it is expected to peak at: its kind, and for a windowed
/// World its backend and whether its mesh store is warm.</param>
/// <param name="bound">How long to wait, at most, or <see langword="null"/> for the admission's own bound.</param>
/// <param name="reserve">Whether an admitted start reserves its expected peak; pre-lease judgements reserve nothing.</param>
/// <param name="progress">Receives the wait lines.</param>
/// <param name="stage">Receives what the wait is on, as a phrase, on every reading while it waits; <see langword="null"/> to
/// ignore it.</param>
/// <param name="cancellationToken">Stops the wait.</param>
/// <returns><see langword="true"/> once the host can take the step; <see langword="false"/> when the bound passed first.</returns>
public delegate Task<bool> HostAdmit(string checkout, string step, bool device, bool heavySuite, HostStartProfile profile, TimeSpan? bound, bool reserve, TextWriter progress, Action<string>? stage, CancellationToken cancellationToken);
