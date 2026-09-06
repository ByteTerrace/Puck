import { TokenCredential } from "@azure/identity";
import {
  combineReducers,
  configureStore,
  createSlice,
  Reducer,
  ThunkDispatch,
} from "@reduxjs/toolkit";
import { createDynamicMiddleware } from "@reduxjs/toolkit/react";
import { onboardingSlice } from "./onboarding";

export const createHostStore = (tokenCredential: TokenCredential) => {
  const dynamicMiddleware = createDynamicMiddleware();
  const dynamicReducers: Record<string, Reducer> = {};
  const hostSlice = createSlice({
    initialState: {},
    name: "host",
    reducers: {},
  });
  const reducer = {
    host: hostSlice.reducer,
    onboarding: onboardingSlice.reducer,
  };
  const store = configureStore({
    middleware: (getDefaultMiddleware) =>
      getDefaultMiddleware({
        thunk: {
          extraArgument: {
            tokenCredential: tokenCredential,
          },
        },
      }).concat(dynamicMiddleware.middleware),
    reducer: reducer,
  });

  return {
    middleware: dynamicMiddleware,
    reducers: {
      set: (key: string, reducer: Reducer) => {
        dynamicReducers[key] = reducer;

        store.replaceReducer(
          combineReducers({
            ...reducer,
            ...dynamicReducers,
          }) as typeof reducer
        );
      },
    },
    store: store,
  };
};

export type HostDispatch = HostStore["dispatch"];
export type HostState = ReturnType<HostStore["getState"]>;
export type HostStore = ReturnType<typeof createHostStore>["store"];
export type HostThunkExtra = HostStore extends {
  dispatch: ThunkDispatch<any, infer E, any>;
}
  ? E
  : never;
