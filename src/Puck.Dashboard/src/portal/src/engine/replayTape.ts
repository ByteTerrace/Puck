import { StepTrace } from "./tickRunner";

export interface TickSnapshot {
  edgeLatches?: Record<string, boolean>;
  tickNumber: number;
  state: Record<string, any>;
  boardCells: Record<string, Record<number, number>>;
  trace?: StepTrace;
}

export class ReplayTape {
  private snapshots: TickSnapshot[] = [];
  private currentIndex: number = 0;

  constructor(initialState: Record<string, any>, initialBoardCells: Record<string, Record<number, number>>) {
    this.reset(initialState, initialBoardCells);
  }

  public reset(initialState: Record<string, any>, initialBoardCells: Record<string, Record<number, number>>) {
    this.snapshots = [
      {
        tickNumber: 0,
        state: { ...initialState },
        boardCells: JSON.parse(JSON.stringify(initialBoardCells)),
        trace: {
          tick: 0,
          intentDescription: "Initial World State",
          ruleEvents: [],
          allDeltas: [],
        },
      },
    ];
    this.currentIndex = 0;
  }

  public recordStep(
    tickNumber: number,
    state: Record<string, any>,
    boardCells: Record<string, Record<number, number>>,
    trace: StepTrace
  ) {
    // Truncate any redo history if we make a new move in the past
    if (this.currentIndex < this.snapshots.length - 1) {
      this.snapshots = this.snapshots.slice(0, this.currentIndex + 1);
    }

    this.snapshots.push({
      tickNumber,
      state: { ...state },
      boardCells: JSON.parse(JSON.stringify(boardCells)),
      trace,
    });
    this.currentIndex = this.snapshots.length - 1;
  }

  public getCurrentSnapshot(): TickSnapshot {
    return this.snapshots[this.currentIndex];
  }

  public canUndo(): boolean {
    return this.currentIndex > 0;
  }

  public canRedo(): boolean {
    return this.currentIndex < this.snapshots.length - 1;
  }

  public undo(): TickSnapshot | null {
    if (!this.canUndo()) return null;
    this.currentIndex--;
    return this.snapshots[this.currentIndex];
  }

  public redo(): TickSnapshot | null {
    if (!this.canRedo()) return null;
    this.currentIndex++;
    return this.snapshots[this.currentIndex];
  }

  public jumpTo(index: number): TickSnapshot | null {
    if (index >= 0 && index < this.snapshots.length) {
      this.currentIndex = index;
      return this.snapshots[this.currentIndex];
    }
    return null;
  }

  public getAllSnapshots(): TickSnapshot[] {
    return [...this.snapshots];
  }

  public getCurrentIndex(): number {
    return this.currentIndex;
  }

  public getLength(): number {
    return this.snapshots.length;
  }
}
