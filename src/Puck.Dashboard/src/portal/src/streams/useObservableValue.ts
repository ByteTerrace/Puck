import { useSyncExternalStore } from "react";
import { type BehaviorSubject, skip } from "rxjs";

// One subscribe function per subject, so React keeps a single subscription for as long as the subject is the same.
const subscribers = new WeakMap<BehaviorSubject<unknown>, (onChange: () => void) => () => void>();

function subscriberFor(subject: BehaviorSubject<unknown>) {
  let subscribe = subscribers.get(subject);

  if (!subscribe) {
    subscribe = (onChange) => {
      // The subject replays its current value on subscribe; React already read it through getValue.
      const subscription = subject.pipe(skip(1)).subscribe(onChange);

      return () => subscription.unsubscribe();
    };
    subscribers.set(subject, subscribe);
  }

  return subscribe;
}

/**
 * The React boundary for a stream that always has a current value: renders with the subject's value and re-renders
 * on each change, through `useSyncExternalStore` so concurrent rendering never sees a torn value.
 */
export function useObservableValue<T>(subject: BehaviorSubject<T>): T {
  return useSyncExternalStore(subscriberFor(subject as BehaviorSubject<unknown>), () => subject.getValue());
}
