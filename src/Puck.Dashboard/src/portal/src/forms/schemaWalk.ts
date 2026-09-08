import { getAt, type JsonPath } from "../document/jsonPath";

/**
 * A raw fragment of the single-file schema bundle (`puck schema --bundle`). Left as `any`
 * on purpose: this module reads a handful of JSON Schema keywords off it and otherwise
 * treats it as opaque data — it never validates a document against it (the engine does
 * that; see the module doc below).
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type JsonSchema = Record<string, any>;

/** One arm of a `$type`/`kind`-discriminated `anyOf` union. */
export interface UnionArm {
  readonly value: string;
  readonly schema: JsonSchema;
}

/** Present on a {@link SchemaNode} whose schema is a discriminated union. */
export interface UnionInfo {
  readonly key: string;
  readonly arms: readonly UnionArm[];
  /** The arm the supplied document's own `key` field selected, or `undefined` if none did. */
  readonly selected?: string;
}

/**
 * The effective schema at one document location: `$ref` followed, the matching `anyOf` arm
 * merged in when a document picked one, and any `kind`-conditional `allOf`/`if`/`then`
 * refinement (e.g. a state row's `value`/`min`/`max`/`cells[].value` narrowing to the type
 * its own `kind` implies) folded into `schema.properties`. `union` is set whenever `schema`
 * came from (or still is) a discriminated `anyOf`, whether or not a document resolved it to
 * one concrete arm.
 */
export interface SchemaNode {
  readonly path: JsonPath;
  readonly schema: JsonSchema;
  readonly union?: UnionInfo;
}

export interface FieldEntry {
  readonly key: string | number;
  readonly path: JsonPath;
  readonly node: SchemaNode;
  readonly required: boolean;
}

export interface RootSection {
  readonly key: string;
  readonly path: JsonPath;
  readonly title?: string;
  readonly description?: string;
}

export interface SchemaWalker {
  /**
   * The effective node at `path`. Without `document`, a discriminated union is left
   * unresolved (`union.selected` is `undefined`, listing every arm) and a `kind`-conditional
   * refinement never applies (there is no sibling value to read). With `document`, both read
   * the document's own value at the discriminator field's path (a sibling of this node) to
   * pick an arm / apply a refinement.
   */
  atPath(path: JsonPath, document?: unknown): SchemaNode;
  /** This node's declared children — object properties or array elements — each re-resolved through `atPath`. */
  children(node: SchemaNode, document?: unknown): FieldEntry[];
  isNullable(node: SchemaNode): boolean;
  enumValues(node: SchemaNode): readonly string[] | undefined;
  description(node: SchemaNode): string | undefined;
  title(node: SchemaNode): string | undefined;
  /** A minimal valid value for this node: required properties filled in, a discriminated union picking its first arm. */
  defaultFor(node: SchemaNode): unknown;
  /** The root document's own sections, in schema (declaration) order. */
  rootSections(): readonly RootSection[];
  /**
   * Whether `node` is the `cells` array of a `state.world`/`state.body`/`state.identity` row.
   * See the comment on `isCellsField` below for how this is decided from schema shape rather
   * than from the row's authored name.
   */
  isCellsField(node: SchemaNode): boolean;
}

const MAX_REF_HOPS = 32;

function normalizeTypeList(type: unknown): string[] {
  if (typeof type === "string") return [type];
  if (Array.isArray(type)) return type.filter((t): t is string => typeof t === "string");
  return [];
}

function isNullableSchema(schema: JsonSchema): boolean {
  if (normalizeTypeList(schema.type).includes("null")) return true;
  if (Array.isArray(schema.anyOf)) {
    return schema.anyOf.some((arm: JsonSchema) => arm && (arm.type === "null" || normalizeTypeList(arm.type).includes("null")));
  }
  return false;
}

/**
 * A reference site's own shape: `{ anyOf: [{ $ref }, { type: "null" }?] }` — the ONLY form the
 * bundle ever names a shared def by (see `WorldSchema.Bundle`'s own `BuildReferenceSite`; a bare
 * `{ $ref }` with no sibling never appears wrapped, so this pattern is unambiguous). Recognized by
 * shape alone — an arm carrying anything beyond its own single keyword (a REAL union arm, which
 * always adds `properties`/`const`/a description) never matches, so a genuine multi-arm
 * discriminated union is never mistaken for this.
 */
function extractRefSite(schema: JsonSchema): { ref: string; nullable: boolean } | undefined {
  if (!Array.isArray(schema.anyOf) || schema.anyOf.length === 0 || schema.anyOf.length > 2) return undefined;
  let ref: string | undefined;
  let nullable = false;
  for (const arm of schema.anyOf) {
    if (!arm || typeof arm !== "object") return undefined;
    const keys = Object.keys(arm);
    if (keys.length === 1 && keys[0] === "$ref" && typeof arm.$ref === "string") {
      if (ref !== undefined) return undefined;
      ref = arm.$ref;
    } else if (keys.length === 1 && keys[0] === "type" && arm.type === "null") {
      nullable = true;
    } else {
      return undefined;
    }
  }
  return ref === undefined ? undefined : { ref, nullable };
}

/** Merges `"null"` into an existing `type` (absent, a bare string, or already an array). */
function mergeNullType(type: unknown): string | string[] {
  if (type === undefined) return "null";
  const list = normalizeTypeList(type);
  return list.includes("null") ? list : [...list, "null"];
}

function stripAnyOf(schema: JsonSchema): JsonSchema {
  const { anyOf: _anyOf, ...rest } = schema;
  return rest;
}

/** Folds one discriminated `anyOf` arm's own properties/required over the union's base shape. */
function mergeArm(base: JsonSchema, arm: JsonSchema): JsonSchema {
  const merged: JsonSchema = { ...base };
  merged.properties = { ...(base.properties ?? {}), ...(arm.properties ?? {}) };
  const required = new Set<string>([
    ...(Array.isArray(base.required) ? base.required : []),
    ...(Array.isArray(arm.required) ? arm.required : []),
  ]);
  if (required.size) merged.required = [...required];
  if (arm.additionalProperties !== undefined) merged.additionalProperties = arm.additionalProperties;
  if (typeof arm.description === "string") merged.description = arm.description;
  return merged;
}

/** Recursively overlays `overlay` onto `base`; arrays and primitives from `overlay` win outright. */
function deepMergeSchema(base: unknown, overlay: unknown): unknown {
  if (overlay === undefined) return base;
  if (Array.isArray(base) || Array.isArray(overlay)) return overlay;
  if (base && overlay && typeof base === "object" && typeof overlay === "object") {
    const result: Record<string, unknown> = { ...(base as Record<string, unknown>) };
    for (const key of Object.keys(overlay as Record<string, unknown>)) {
      result[key] = deepMergeSchema((base as Record<string, unknown>)[key], (overlay as Record<string, unknown>)[key]);
    }
    return result;
  }
  return overlay;
}

interface Refinement {
  readonly key: string;
  readonly value: string;
  readonly thenProperties: Record<string, JsonSchema>;
}

/**
 * Reads the `kind`-style `allOf`/`if`/`then` refinements off a schema (e.g. a state row's
 * value/min/max/cells narrowing per its own `kind`). Only single-field `const` conditions
 * whose `then` carries `properties` are rendering-relevant; the bundle's other `allOf`
 * entries are pure `not`-shaped mutual-exclusion constraints the engine enforces and this
 * walker has no business re-checking (see the module doc: no client-side validation).
 */
function collectRefinements(schema: JsonSchema): Refinement[] {
  if (!Array.isArray(schema.allOf)) return [];
  const refinements: Refinement[] = [];
  for (const entry of schema.allOf) {
    const ifClause = entry?.if;
    const thenClause = entry?.then;
    if (!ifClause || !thenClause || !thenClause.properties || typeof thenClause.properties !== "object") continue;
    const ifProperties = ifClause.properties;
    if (!ifProperties || typeof ifProperties !== "object") continue;
    const keys = Object.keys(ifProperties);
    if (keys.length !== 1) continue;
    const [key] = keys;
    const constValue = ifProperties[key]?.const;
    if (typeof constValue !== "string") continue;
    refinements.push({ key, value: constValue, thenProperties: thenClause.properties });
  }
  return refinements;
}

/**
 * Finds a discriminated `anyOf`: every arm resolves (after `$ref`) to an object declaring the
 * SAME property with a string `const`. `$type` wins when present (the common case), then
 * `kind`, else the alphabetically first shared key — deterministic without hard-coding either
 * spelling as the only one this schema ever uses.
 */
function findDiscriminatedArms(
  schema: JsonSchema,
  deref: (schema: JsonSchema) => JsonSchema,
): { key: string; arms: UnionArm[] } | undefined {
  const anyOf = schema.anyOf;
  if (!Array.isArray(anyOf) || !anyOf.length) return undefined;
  const resolvedArms = anyOf.map((arm: JsonSchema) => deref(arm));
  let candidateKeys: string[] | undefined;
  for (const arm of resolvedArms) {
    if (!arm || typeof arm !== "object" || !arm.properties || typeof arm.properties !== "object") {
      candidateKeys = [];
      break;
    }
    const keys = Object.keys(arm.properties).filter(key => typeof arm.properties[key]?.const === "string");
    candidateKeys = candidateKeys === undefined ? keys : candidateKeys.filter(key => keys.includes(key));
  }
  if (!candidateKeys || !candidateKeys.length) return undefined;
  const key = candidateKeys.includes("$type") ? "$type" : candidateKeys.includes("kind") ? "kind" : [...candidateKeys].sort()[0];
  const arms = resolvedArms.map(arm => ({ value: arm.properties[key].const as string, schema: arm }));
  return { key, arms };
}

function navigateOneSegment(schema: JsonSchema, segment: string | number): JsonSchema {
  if (!schema || typeof schema !== "object") return {};
  if (typeof segment === "number") {
    const items = schema.items;
    if (Array.isArray(items)) return items[segment] ?? items[items.length - 1] ?? {};
    return items ?? {};
  }
  if (schema.properties && Object.prototype.hasOwnProperty.call(schema.properties, segment)) return schema.properties[segment];
  if (schema.additionalProperties && typeof schema.additionalProperties === "object") return schema.additionalProperties;
  return {};
}

function defaultForSchema(schema: JsonSchema, deref: (schema: JsonSchema) => JsonSchema): unknown {
  const resolved = deref(schema);
  if (resolved.const !== undefined) return resolved.const;
  if (resolved.default !== undefined) return resolved.default;
  if (Array.isArray(resolved.anyOf)) {
    const found = findDiscriminatedArms(resolved, deref);
    if (found && found.arms.length) return defaultForSchema(mergeArm(stripAnyOf(resolved), found.arms[0].schema), deref);
  }
  if (Array.isArray(resolved.enum) && resolved.enum.length) return resolved.enum[0];
  const types = normalizeTypeList(resolved.type);
  if (types.includes("object") || resolved.properties) {
    const properties = resolved.properties ?? {};
    const required: string[] = Array.isArray(resolved.required) ? resolved.required : [];
    const value: Record<string, unknown> = {};
    for (const key of required) value[key] = defaultForSchema(properties[key] ?? {}, deref);
    return value;
  }
  if (types.includes("array") || resolved.items) {
    const minItems = typeof resolved.minItems === "number" ? resolved.minItems : 0;
    const itemSchema = Array.isArray(resolved.items) ? (resolved.items[0] ?? {}) : (resolved.items ?? {});
    const value: unknown[] = [];
    for (let i = 0; i < minItems; i++) value.push(defaultForSchema(itemSchema, deref));
    return value;
  }
  if (types.includes("string")) return "";
  if (types.includes("integer") || types.includes("number")) return 0;
  if (types.includes("boolean")) return false;
  return null;
}

function lookupPointer(bundle: JsonSchema, ref: string): JsonSchema {
  const hash = ref.indexOf("#");
  const pointer = hash >= 0 ? ref.slice(hash + 1) : ref;
  if (!pointer) return bundle;
  const tokens = pointer
    .split("/")
    .slice(1)
    .map(token => token.replace(/~1/g, "/").replace(/~0/g, "~"));
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  let current: any = bundle;
  for (const token of tokens) {
    if (current === undefined || current === null) throw new Error(`Unresolvable schema $ref: ${ref}`);
    current = current[token];
  }
  if (current === undefined) throw new Error(`Unresolvable schema $ref: ${ref}`);
  return current;
}

/** Resolves a chain of `$ref` hops (rare beyond one hop in this bundle), capped at {@link MAX_REF_HOPS}. */
function makeDeref(bundle: JsonSchema): (schema: JsonSchema) => JsonSchema {
  return function deref(schema: JsonSchema): JsonSchema {
    let current = schema;
    let hops = 0;
    while (current && typeof current === "object" && hops < MAX_REF_HOPS) {
      if (typeof current.$ref === "string") {
        const target = lookupPointer(bundle, current.$ref);
        const { $ref: _ref, ...siblings } = current;
        current = { ...target, ...siblings };
        hops++;
        continue;
      }
      const refSite = extractRefSite(current);
      if (!refSite) break;
      const target = lookupPointer(bundle, refSite.ref);
      const { anyOf: _anyOf, ...siblings } = current;
      current = {
        ...target,
        ...siblings,
        ...(refSite.nullable ? { type: mergeNullType(target.type) } : {}),
      };
      hops++;
    }
    return current;
  };
}

/**
 * The node-kind categories {@link classify} sorts a resolved node into, driving which Mantine
 * control `SchemaNode` renders. `"union"` means an unresolved discriminated `anyOf` — one
 * whose document value did not (yet) pick an arm; once an arm is picked, `classify` reports
 * the ARM's own kind instead (typically `"object"`).
 */
export type NodeKind = "string" | "integer" | "number" | "boolean" | "enum" | "object" | "array" | "union" | "unknown";

export function classify(node: SchemaNode): NodeKind {
  const schema = node.schema ?? {};
  if (node.union && node.union.selected === undefined) return "union";
  if (Array.isArray(schema.enum) && schema.enum.length) return "enum";
  if (typeof schema.const === "string") return "enum"; // a fixed one-choice discriminator, e.g. an already-picked arm's own `$type`
  const types = normalizeTypeList(schema.type);
  if (types.includes("string")) return "string";
  if (types.includes("integer")) return "integer";
  if (types.includes("number")) return "number";
  if (types.includes("boolean")) return "boolean";
  if (types.includes("array") || schema.items) return "array";
  if (types.includes("object") || schema.properties) return "object";
  return "unknown";
}

/**
 * Builds a walker over one schema bundle. The walker only RENDERS a document against the
 * schema — it never validates one; the engine (`puck.exe`, or the native world when this
 * document is applied) is the one place a document's correctness is actually decided.
 */
export function resolve(bundle: JsonSchema): SchemaWalker {
  const deref = makeDeref(bundle);

  function normalizeAt(path: JsonPath, raw: JsonSchema, document: unknown | undefined): SchemaNode {
    let schema = raw;
    let union: UnionInfo | undefined;
    for (let iteration = 0; iteration < 16; iteration++) {
      let changed = false;
      const derefed = deref(schema);
      if (derefed !== schema) {
        schema = derefed;
        changed = true;
      }
      if (!union && Array.isArray(schema.anyOf)) {
        const found = findDiscriminatedArms(schema, deref);
        if (found) {
          const discriminatorValue = document !== undefined ? getAt(document, [...path, found.key]) : undefined;
          const matched = typeof discriminatorValue === "string" ? found.arms.find(arm => arm.value === discriminatorValue) : undefined;
          if (matched) {
            schema = mergeArm(stripAnyOf(schema), matched.schema);
            union = { key: found.key, arms: found.arms, selected: matched.value };
          } else {
            union = { key: found.key, arms: found.arms, selected: undefined };
          }
          changed = true;
        }
      }
      if (Array.isArray(schema.allOf)) {
        const refinements = collectRefinements(schema);
        if (refinements.length) {
          const properties = { ...(schema.properties ?? {}) };
          let appliedAny = false;
          for (const refinement of refinements) {
            const siblingValue = document !== undefined ? getAt(document, [...path, refinement.key]) : undefined;
            if (siblingValue !== refinement.value) continue;
            for (const key of Object.keys(refinement.thenProperties)) {
              properties[key] = deepMergeSchema(properties[key] ?? {}, refinement.thenProperties[key]);
            }
            appliedAny = true;
          }
          if (appliedAny) {
            schema = { ...schema, properties };
            changed = true;
          }
        }
      }
      if (!changed) break;
    }
    return { path, schema, union };
  }

  function atPath(path: JsonPath, document?: unknown): SchemaNode {
    if (path.length === 0) return normalizeAt([], bundle, document);
    const parent = atPath(path.slice(0, -1), document);
    const segment = path[path.length - 1];
    const raw = navigateOneSegment(parent.schema, segment);
    return normalizeAt(path, raw, document);
  }

  function children(node: SchemaNode, document?: unknown): FieldEntry[] {
    const schema = node.schema;
    const types = normalizeTypeList(schema.type);
    if (schema.properties || types.includes("object")) {
      const properties = schema.properties ?? {};
      const required = new Set<string>(Array.isArray(schema.required) ? schema.required : []);
      return Object.keys(properties).map(key => {
        const path = [...node.path, key];
        return { key, path, node: atPath(path, document), required: required.has(key) };
      });
    }
    if (schema.items || types.includes("array")) {
      const fixedArity = typeof schema.minItems === "number" && schema.minItems === schema.maxItems && !Array.isArray(schema.items);
      if (fixedArity) {
        const count = schema.minItems as number;
        return Array.from({ length: count }, (_, index) => {
          const path = [...node.path, index];
          return { key: index, path, node: atPath(path, document), required: true };
        });
      }
      const array = document !== undefined ? getAt(document, node.path) : undefined;
      if (!Array.isArray(array)) return [];
      return array.map((_, index) => {
        const path = [...node.path, index];
        return { key: index, path, node: atPath(path, document), required: false };
      });
    }
    return [];
  }

  function rootSections(): RootSection[] {
    const properties = bundle.properties ?? {};
    return Object.keys(properties).map(key => {
      const schema = properties[key] ?? {};
      return {
        key,
        path: [key] as JsonPath,
        title: typeof schema.title === "string" ? schema.title : undefined,
        description: typeof schema.description === "string" ? schema.description : undefined,
      };
    });
  }

  // The row schema for each state lane (`state.world`/`state.body`/`state.identity`) is a
  // stable object reference: `state` itself is a bundle $ref (resolved through `atPath`, which
  // this depends on), but its OWN "properties" object is never re-spread by `deref` — only the
  // wrapping node is — so `properties[lane].items`, read straight off the wrapper's target, is
  // the exact same reference on every call. That makes it a clean SCHEMA-LOCATION handle for "is
  // this the cells array of a state row" — comparing against it (further down, in
  // `isCellsField`) never depends on how a row or its cells happen to be spelled in an authored
  // document, unlike a name-based guess (checking a row's own `name`, or assuming any
  // `{key, value}`-shaped array anywhere is a cells table).
  const stateSchema = atPath(["state"]).schema;
  const rowItemSchemas = new Set<JsonSchema>(
    ["world", "body", "identity"]
      .map(lane => stateSchema.properties?.[lane]?.items)
      .filter((schema): schema is JsonSchema => !!schema),
  );

  function isCellsField(node: SchemaNode): boolean {
    if (node.path.length < 2) return false;
    const last = node.path[node.path.length - 1];
    if (last !== "cells") return false;
    const rowPath = node.path.slice(0, -1);
    const rowIndex = rowPath[rowPath.length - 1];
    const arrayNode = atPath(rowPath.slice(0, -1));
    const rawRowSchema = navigateOneSegment(arrayNode.schema, rowIndex);
    return rowItemSchemas.has(rawRowSchema);
  }

  return {
    atPath,
    children,
    isNullable: node => isNullableSchema(node.schema),
    enumValues: node =>
      Array.isArray(node.schema.enum) ? [...node.schema.enum]
      : typeof node.schema.const === "string" ? [node.schema.const]
      : undefined,
    description: node => (typeof node.schema.description === "string" ? node.schema.description : undefined),
    title: node => (typeof node.schema.title === "string" ? node.schema.title : undefined),
    defaultFor: node => {
      if (node.union) {
        const arm = node.union.selected
          ? node.union.arms.find(candidate => candidate.value === node.union!.selected)
          : node.union.arms[0];
        if (arm) return defaultForSchema(mergeArm(stripAnyOf(node.schema), arm.schema), deref);
      }
      return defaultForSchema(node.schema, deref);
    },
    rootSections,
    isCellsField,
  };
}

/** Returns `array` with the element at `index` swapped with its neighbour in `direction`; a no-op move returns an equal-valued copy. */
export function computeArrayMove<T>(array: readonly T[], index: number, direction: -1 | 1): T[] {
  const target = index + direction;
  if (index < 0 || index >= array.length || target < 0 || target >= array.length) return array.slice();
  const next = array.slice();
  const [item] = next.splice(index, 1);
  next.splice(target, 0, item);
  return next;
}

/** The value a discriminated-union field should take on after switching to arm `armValue`: that arm's own minimal default. */
export function computeArmSwitch(walker: SchemaWalker, node: SchemaNode, armValue: string): unknown {
  if (!node.union || !node.union.arms.some(arm => arm.value === armValue)) return walker.defaultFor(node);
  return walker.defaultFor({ path: node.path, schema: node.schema, union: { ...node.union, selected: armValue } });
}
