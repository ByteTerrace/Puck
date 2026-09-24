import type { TokenCredential } from "@azure/identity";
import type { AccountInfo } from "@azure/msal-browser";
import type { ComponentType, ReactNode } from "react";
import type { OnboardingActor } from "./onboarding";

export type ServiceFactory<TServiceProvider, T> = (serviceProvider: TServiceProvider) => T;

export interface HostContextValue {
  activeAccount: AccountInfo | null;
  isSignedIn: boolean;
  /** The signed-in account's setup (see `onboardingMachine`); the portal reads its state and may send `RETRY`. */
  onboarding: OnboardingActor;
  serviceProvider: ServiceProvider<HostServiceProviders>;
  signIn: () => Promise<void>;
  signOut: () => Promise<void>;
}
export interface HostProviderProperties {
  children: ReactNode;
  onboarding: OnboardingActor;
  serviceProvider: ServiceProvider<HostServiceProviders>;
  signInScopes: string[];
  tokenCredential: TokenCredential;
}
/**
 * The portal's federated module (`portal/portal-app`): the one contract between the host and the portal. The host
 * passes its context; the portal served on its own renders with none.
 */
export interface PortalModule {
  default: ComponentType<{ context?: HostContextValue }>;
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
