import { ServiceFactory, ServiceProvider } from "../../shared/interfaces";

export class DefaultServiceProvider<TServiceMap extends object = Record<string, unknown>> implements ServiceProvider<TServiceMap> {
  private factories = new Map<string, ServiceFactory<this, any>>();
  private instances = new Map<string, any>();

  tryAdd<TName extends Extract<keyof TServiceMap, string>>(name: TName,factory: ServiceFactory<this, TServiceMap[TName]>): boolean;
  tryAdd<T>(name: string, factory: ServiceFactory<this, T>): boolean;
  tryAdd<T>(name: string, factory: ServiceFactory<this, T>): boolean {
    const existingFactory = this.factories.get(name);

    if (!existingFactory) {
      this.factories.set(name, factory);
    }

    return !existingFactory;
  }
  tryGet<TName extends Extract<keyof TServiceMap, string>>(name: TName): TServiceMap[TName] | undefined;
  tryGet<T>(name: string): T | undefined;
  tryGet<T>(name: string): T | undefined {
    if (this.instances.has(name)) {
      return this.instances.get(name);
    }

    const factory = this.factories.get(name);

    if (!factory) {
      return undefined;
    }

    const instance = factory(this);

    this.instances.set(name, instance);

    return instance;
  }
}
