import { TokenCredential } from "@azure/identity";
import { AccountInfo } from "@azure/msal-browser";
import { AddMiddleware, Dispatch, Reducer, UnknownAction } from "@reduxjs/toolkit";
import React from "react";
import { HostStore } from "./store";

export type ServiceFactory<TServiceProvider, T> = (serviceProvider: TServiceProvider) => T;

export interface HostContextValue {
  activeAccount: AccountInfo | null;
  isSignedIn: boolean;
  serviceProvider: ServiceProvider<HostServiceProviders>;
  signIn: () => Promise<void>;
  signOut: () => Promise<void>;
  store: {
    addMiddleware: AddMiddleware<any, Dispatch<UnknownAction>>;
    setReducer: (key: string, reducer: Reducer) => void;
    value: HostStore;
  };
}
export interface HostProviderProperties {
  children: React.ReactNode;
  serviceProvider: ServiceProvider<HostServiceProviders>;
  signInScopes: string[];
}
export interface HostServiceProviders {
  tokenCredential: TokenCredential;
}
export interface ServiceProvider<TServiceMap extends object = Record<string, unknown>> {
  tryAdd<TName extends Extract<keyof TServiceMap, string>>(name: TName, factory: ServiceFactory<this, TServiceMap[TName]>): boolean;
  tryAdd<T>(name: string, factory: ServiceFactory<this, T>): boolean;
  tryGet<TName extends Extract<keyof TServiceMap, string>>(name: TName): TServiceMap[TName] | undefined;
  tryGet<T>(name: string): T | undefined;
}
