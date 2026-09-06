export type DisposeFunction = () => void;
export type RequestFunction<TArgs, TResponse> = (
  args: TArgs,
  context: {
    attempt: number;
    signal: AbortSignal;
  }
) => Promise<RequestResult<TResponse>>;
export type RequestDefinitionMap = Record<string, RequestDefinition<any, any>>;
export type RequestListener<TResponse> = (
  state: RequestState<TResponse>
) => void | Promise<void>;
export type RequestStatus = "error" | "idle" | "loading" | "success";
export type TypedApiClient<T extends RequestDefinitionMap> = ApiClient & {
  [K in keyof T]: T[K] extends RequestDefinition<infer TArgs, infer TResponse>
    ? (args: TArgs, timeout?: number) => RequestController<TArgs, TResponse>
    : never;
};
export type UnsubscribeFunction = () => void;

export interface RequestDefinition<TArgs, TResponse> {
  disposeTimeout?: number;
  enableAutoRefresh?: boolean;
  keySelector?: (args: TArgs) => string;
  requestFunction: RequestFunction<TArgs, TResponse>;
  shouldRetry?: (attempt: number, error: unknown) => boolean | Promise<boolean>;
  staleRequestTimeout?: number;
}
export interface RequestResult<TResponse> {
  onDispose?: DisposeFunction;
  response: TResponse;
}
export interface RequestState<TResponse> {
  error?: unknown;
  lastUpdated?: number;
  response?: TResponse;
  status: RequestStatus;
}

export class ApiClient {
  private static getStableKey(
    arg: any,
    circularReferenceTracker = new WeakSet<object>()
  ): string {
    if (undefined === arg) {
      return "undefined";
    }

    if (null === arg) {
      return "null";
    }

    if (arg instanceof Date && !isNaN(arg.getTime())) {
      return arg.toISOString();
    }

    switch (typeof arg) {
      case "bigint":
        return `${arg}n`;
      case "function":
        throw new Error("getStableKey cannot serialize function arguments; use a custom keySelector.");
      case "number":
        if (isNaN(arg)) {
          return "NaN";
        }

        if (!isFinite(arg)) {
          return 0 < arg ? "Infinity" : "-Infinity";
        }

        return JSON.stringify(arg);
      case "object":
        break;
      case "symbol":
        throw new Error("getStableKey cannot serialize symbol arguments; use a custom keySelector.");
      default:
        return JSON.stringify(arg);
    }

    if (circularReferenceTracker.has(arg)) {
      throw new Error("getStableKey detected a circular reference.");
    }

    circularReferenceTracker.add(arg); // TODO: Improve on this. Many edge-cases are not handled properly.

    if (Array.isArray(arg)) {
      return `[${arg
        .map((a) => ApiClient.getStableKey(a, circularReferenceTracker))
        .join(",")}]`;
    }

    return `{${Object.keys(arg)
      .sort()
      .map(
        (key) =>
          `${JSON.stringify(key)}:${ApiClient.getStableKey(
            arg[key],
            circularReferenceTracker
          )}`
      )
      .join(",")}}`;
  }

  static create<T extends RequestDefinitionMap>(definitions: T): TypedApiClient<T> {
    const client = new ApiClient() as TypedApiClient<T>;

    for (const [name, definition] of Object.entries(definitions)) {
      (client as any)[name] = (args: any) =>
        client.getOrAdd(definition, args);
    }

    return client;
  }

  private requestDefinitionCache = new WeakMap<
    RequestDefinition<any, any>,
    Map<string, RequestController<any, any>>
  >();

  getOrAdd<TArgs, TResponse>(
    definition: RequestDefinition<TArgs, TResponse>,
    args: TArgs
  ): RequestController<TArgs, TResponse> {
    let cache = this.requestDefinitionCache.get(definition);

    if (!cache) {
      this.requestDefinitionCache.set(definition, (cache = new Map()));
    }

    const argsKey = definition.keySelector
      ? definition.keySelector(args)
      : ApiClient.getStableKey(args);

    let request = cache.get(argsKey);

    if (!request) {
      request = new RequestController<TArgs, TResponse>(
        definition,
        args,
        () => {
          cache!.delete(argsKey);
        }
      );
      cache.set(argsKey, request);
    }

    return request as RequestController<TArgs, TResponse>;
  }
}
export class RequestController<TArgs, TResponse> {
  private abortController?: AbortController;
  private disposeTimer?: ReturnType<typeof setTimeout>;
  private isDisposed = false;
  private listeners = new Map<RequestListener<TResponse>, number>();
  private referenceCount = 0;
  private responseDisposeFunction?: DisposeFunction;
  private staleRefreshTimer?: ReturnType<typeof setTimeout>;

  state: RequestState<TResponse> = { status: "idle" };

  private get hasReferences() {
    return 0 !== this.referenceCount;
  }

  constructor(
    private readonly definition: RequestDefinition<TArgs, TResponse>,
    private readonly args: TArgs,
    private readonly onDispose?: () => void
  ) {
    this.disposeTimer = this.createDisposeTimer();
    this.fetch().catch((e) =>
      console.error(
        "RequestController.constructor failed on async fetch attempt.",
        e
      )
    );
  }

  private addListener(listener: RequestListener<TResponse>): number {
    const listenerCount = this.listeners.get(listener) ?? 0;

    this.listeners.set(listener, listenerCount + 1);

    return ++this.referenceCount;
  }
  private clearAllTimers() {
    this.clearDisposeTimer();
    this.clearStaleRefreshTimer();
  }
  private clearDisposeTimer() {
    if (this.disposeTimer) {
      clearTimeout(this.disposeTimer);
      this.disposeTimer = undefined;
    }
  }
  private clearStaleRefreshTimer() {
    if (this.staleRefreshTimer) {
      clearTimeout(this.staleRefreshTimer);
      this.staleRefreshTimer = undefined;
    }
  }
  private createDisposeTimer() {
    return setTimeout(() => {
      if (!this.isDisposed && !this.hasReferences) {
        this.dispose();
      }
    }, this.definition.disposeTimeout ?? 17000);
  }
  private dispose() {
    this.clearAllTimers();
    this.isDisposed = true;
    this.abortController?.abort();
    this.referenceCount = 0;
    this.listeners.clear();

    try {
      this.responseDisposeFunction?.();
    } catch (e) {
      console.error("RequestController.dispose failed to dispose of response resources.", e);
    } finally {
      this.responseDisposeFunction = undefined;
    }

    try {
      this.onDispose?.();
    } catch (e) {
      console.error("RequestController.dispose failed to dispose of request resources.", e);
    }
  }
  private async fetch() {
    this.clearStaleRefreshTimer();

    try {
      this.abortController = new AbortController();

      const signal = this.abortController.signal;

      this.notifyListeners({ error: undefined, status: "loading" });

      let attempt = 0;

      while (!signal.aborted) {
        attempt++;

        try {
          const result = await this.definition.requestFunction(this.args, {
            attempt,
            signal,
          });

          if (signal.aborted) {
            return;
          }

          //#region [Handle Request]
          // Notes:
          // 1) We hold a reference to the previous dispose function to delay disposal until AFTER we notify
          //    listeners. This ensures we don't dispose of a resource before listeners have had a chance to
          //    react to the notification.
          // 2) The new dispose function is set BEFORE notifying listeners to ensure that any subsequent
          //    calls to tryRefetch will dispose of the correct resources.
          // 3) We schedule a stale refresh AFTER notifying listeners to ensure that the timer is reset based
          //    on the time the last request occurred.
          // 4) Any exceptions thrown by the previous dispose function are caught and logged so they don't
          //    interfere with the request that just succeeded.
          const previousDisposeFunction = this.responseDisposeFunction; // 1)

          this.responseDisposeFunction = result.onDispose; // 2)
          this.notifyListeners({
            error: undefined,
            lastUpdated: Date.now(),
            response: result.response,
            status: "success",
          });
          this.scheduleStaleRefresh(); // 3)

          if (previousDisposeFunction) {
            try {
              previousDisposeFunction();
            } catch (e) { // 4)
              console.error(
                "RequestController.fetch failed to dispose of previous request resources.",
                e
              );
            }
          }

          return;
          //#endregion
        } catch (e) {
          //#region [Handle Exception]
          // Notes:
          // 1) We clear the stale refresh timer to prevent unnecessary fetch attempts while handling errors.
          // 2) If the request was aborted, we exit the loop without notifying listeners.
          // 3) We consult the shouldRetry callback (if provided) to determine if we should retry the request.
          // 4) If the request was aborted during shouldRetry, we exit the loop without notifying listeners.
          // 5) If no shouldRetry callback is provided, we notify listeners of the error and exit the loop.
          this.clearStaleRefreshTimer(); // 1)

          if (signal.aborted) { // 2)
            return;
          }

          const shouldRetry = this.definition.shouldRetry // 3)
            ? await this.definition.shouldRetry(attempt, e)
            : false;

          if (signal.aborted) { // 4)
            return;
          }

          if (!shouldRetry) { // 5)
            this.notifyListeners({ error: e, status: "error" });
            return;
          }
          //#endregion
        }
      }
    } catch (e) {
      if (!this.abortController?.signal.aborted) {
        this.clearStaleRefreshTimer();
        this.notifyListeners({ error: e, status: "error" });
      }
    }
  }
  private notifyListeners(partial: Partial<RequestState<TResponse>>) {
    this.state = { ...this.state, ...partial };

    for (const listener of this.listeners.keys()) {
      try {
        const result = listener(this.state);

        if (result instanceof Promise) {
          result.catch((e) =>
            console.error(
              "RequestController.notifyListener failed on async listener notification.",
              e
            )
          );
        }
      } catch (e) {
        console.error(
          "RequestController.notifyListener failed on sync listener notification.",
          e
        );
      }
    }
  }
  private removeListener(listener: RequestListener<TResponse>): number {
    //#region [Decrement Listener Count]
    // Notes:
    // 1) This block is never expected to throw under normal circumstances.
    // 2) Each listener may be added multiple times; we only remove one reference per call.
    const listenerCount = this.listeners.get(listener);

    if (!listenerCount) {
      throw new Error("RequestController.removeListener called for unknown listener."); // 1)
    }

    if (1 < listenerCount) { // 2)
      this.listeners.set(listener, listenerCount - 1);
    } else {
      this.listeners.delete(listener);
    }
    //#endregion
    //#region [Decrement Reference Count]
    // Notes:
    // 1) Existing timers are cleared to reset the disposal countdown and prevent unnecessary refresh.
    // 2) Disposal is scheduled when the reference count reaches zero.
    // 3) We clamp referenceCount to zero as a precaution so that any dependencies behave correctly.
    if (0 >= --this.referenceCount) {
      console.assert(
        0 === this.listeners.size,
        "Warning: RequestController listener count was not zero when reference count reached zero."
      )
      console.assert(
        0 <= this.referenceCount,
        "Warning: RequestController reference count went below zero."
      );

      this.clearAllTimers(); // 1)
      this.disposeTimer = this.createDisposeTimer(); // 2)
      this.referenceCount = 0; // 3)
    }

    return this.referenceCount;
    //#endregion
  }
  private scheduleStaleRefresh() {
    this.clearStaleRefreshTimer();

    const { enableAutoRefresh, staleRequestTimeout } = this.definition;

    if (
      this.isDisposed ||
      !this.hasReferences ||
      !enableAutoRefresh ||
      undefined === staleRequestTimeout
    ) {
      return;
    }

    this.staleRefreshTimer = setTimeout(() => {
      if (!this.isDisposed && this.hasReferences) {
        this.tryRefetch(false);
      }
    }, staleRequestTimeout);
  }

  tryRefetch(force: boolean = false) {
    if (this.isDisposed) {
      throw new Error("RequestController.tryRefetch cannot be called after disposal.");
    }

    if (
      "loading" === this.state.status &&
      !this.abortController?.signal.aborted &&
      !force
    ) {
      return false;
    }

    this.abortController?.abort();
    this.fetch().catch((e) =>
      console.error("RequestController.tryRefetch failed on async fetch attempt.", e)
    );

    return true;
  }
  subscribe(listener: RequestListener<TResponse>): UnsubscribeFunction {
    if (this.isDisposed) {
      throw new Error(
        "RequestController.subscribe cannot be called after disposal."
      );
    }

    this.clearDisposeTimer();
    this.addListener(listener);

    try {
      if (this.abortController?.signal.aborted) {
        this.tryRefetch(false);
      } else {
        const { staleRequestTimeout } = this.definition;
        const { lastUpdated, status } = this.state;

        if (
          "loading" !== status &&
          undefined !== staleRequestTimeout &&
          undefined !== lastUpdated &&
          staleRequestTimeout < Date.now() - lastUpdated
        ) {
          this.tryRefetch(false);
        } else {
          const result = listener(this.state);

          if (result instanceof Promise) {
            result.catch((e) =>
              console.error(
                "RequestController.subscribe failed on async listener notification.",
                e
              )
            );
          }
        }
      }
    } catch (e) {
      this.removeListener(listener);

      throw e;
    }

    let isUnsubscribed = false;

    return () => {
      if (!isUnsubscribed) {
        isUnsubscribed = true;

        this.removeListener(listener);
      }
    };
  }
}
