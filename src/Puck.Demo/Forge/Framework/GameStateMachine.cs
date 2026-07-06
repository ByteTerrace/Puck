namespace Puck.Demo.Forge.Framework;

/// <summary>
/// The frame-synchronous game state machine. A game defines each state at BUILD time as a pair of emission callbacks
/// (enter runs once on a switch, tick runs every frame); state changes are REQUESTED by writing
/// <see cref="FrameworkMemoryMap.PendingState"/> (<c>0xFF</c> = none) and consumed at the top of the next frame's
/// dispatch, so a state never runs half a frame. Dispatch is a chain of compare + conditional-call thunk pairs in
/// CODE, resolved by the emitter's ordinary label fixups — no runtime jump table, no address arithmetic.
/// </summary>
internal sealed class GameStateMachine {
    private sealed record StateDefinition(byte Id, int EnterLabel, int TickLabel, Action<Sm83Emitter> EmitEnter, Action<Sm83Emitter> EmitTick);

    /// <summary>The <see cref="FrameworkMemoryMap.PendingState"/> value meaning "no switch requested".</summary>
    public const byte NoPendingState = 0xFF;

    private readonly Sm83Emitter m_emitter;
    private readonly List<StateDefinition> m_states = [];

    /// <summary>Creates the machine over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    public GameStateMachine(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        m_emitter = emitter;
    }

    /// <summary>Registers a state. Call for every state before <see cref="EmitFrameDispatch"/>.</summary>
    /// <param name="id">The state id (unique, not <c>0xFF</c>).</param>
    /// <param name="emitEnter">Emits the state's enter body (runs once per switch, as a subroutine).</param>
    /// <param name="emitTick">Emits the state's per-frame tick body (runs every frame while current).</param>
    public void DefineState(byte id, Action<Sm83Emitter> emitEnter, Action<Sm83Emitter> emitTick) {
        ArgumentNullException.ThrowIfNull(emitEnter);
        ArgumentNullException.ThrowIfNull(emitTick);

        if ((id == NoPendingState) || m_states.Exists(match: state => (state.Id == id))) {
            throw new ArgumentException(message: $"State id {id} is reserved or already defined.", paramName: nameof(id));
        }

        m_states.Add(item: new StateDefinition(Id: id, EnterLabel: m_emitter.NewLabel(), TickLabel: m_emitter.NewLabel(), EmitEnter: emitEnter, EmitTick: emitTick));
    }

    /// <summary>Emits a state-switch request at the current point (consumed at the next frame's dispatch).</summary>
    /// <param name="id">The requested state id.</param>
    public void EmitRequestState(byte id) {
        m_emitter.LoadAImmediate(value: id);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.PendingState);
    }

    /// <summary>Emits the per-frame dispatch (main loop only): consume a pending switch (assign the state and call its
    /// enter thunk), then call the current state's tick thunk.</summary>
    public void EmitFrameDispatch() {
        var noSwitch = m_emitter.NewLabel();

        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.PendingState);
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: NoPendingState);
        m_emitter.JumpRelative(condition: Condition.Zero, label: noSwitch);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.GameState);
        m_emitter.LoadAImmediate(value: NoPendingState);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.PendingState);

        foreach (var state in m_states) {
            m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.GameState);
            m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: state.Id);
            m_emitter.Call(condition: Condition.Zero, label: state.EnterLabel);
        }

        m_emitter.MarkLabel(label: noSwitch);

        foreach (var state in m_states) {
            m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.GameState);
            m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: state.Id);
            m_emitter.Call(condition: Condition.Zero, label: state.TickLabel);
        }
    }

    /// <summary>Emits every state's enter/tick bodies as subroutines. Called once by the framework facade.</summary>
    public void EmitLibrary() {
        foreach (var state in m_states) {
            m_emitter.MarkLabel(label: state.EnterLabel);
            state.EmitEnter(m_emitter);
            m_emitter.Return();
            m_emitter.MarkLabel(label: state.TickLabel);
            state.EmitTick(m_emitter);
            m_emitter.Return();
        }
    }
}
