import { getTopologyCoordinates } from "./evaluator";
import { validatePreviewExpression } from "./previewExpression";
export const MAX_DOCUMENT_BYTES = 2 * 1024 * 1024;
/** Structural intake checks and offline capabilities, not native engine validation. */
export function inspectWorldDocument(text: string): {
  world: any;
  previewIssues: string[];
} {
  if(new TextEncoder().encode(text).length > MAX_DOCUMENT_BYTES)
    throw new Error("Documents are limited to 2 MB in this studio.");
  const world = JSON.parse(text);
  // JSON.parse rounds large integer literals. Refuse application before a designer
  // or save can serialize that rounded value; the untouched draft remains exportable.
  const values = [world];
  let remainingValues = 100000;
  while (values.length) {
    if (--remainingValues < 0) throw new Error("Document structure exceeds the studio's 100,000-value limit.");
    const value = values.pop();
    if (typeof value === "number" && (!Number.isFinite(value) || (Number.isInteger(value) && !Number.isSafeInteger(value)))) {
      throw new Error("This document contains a number that JavaScript cannot preserve exactly. Keep it in the JSON draft and export it for native authoring.");
    }
    if (value && typeof value === "object") for (const entry of Object.values(value)) values.push(entry);
  }
  const object = (value: any, label: string) => {
    if(!value || typeof value !== "object" || Array.isArray(value))
      throw new Error(`${label} must be an object.`);
  };
  object(world, "Document");
  object(world.state, "state");
  const array = (value: any, label: string, limit: number): any[] => {
    if(!Array.isArray(value) || value.length > limit)
      throw new Error(`${label} must be an array with at most ${limit} entries.`);
    return value;
  };
  const named = (items: any[], label: string) => {
    const names = new Set<string>();
    for(const item of items) {
      object(item, label);
      if(typeof item.name !== "string" || !/^[A-Za-z_][A-Za-z0-9_-]{0,127}$/.test(item.name) || ["__proto__", "constructor", "prototype"].includes(item.name) || names.has(item.name))
        throw new Error(`${label} names must be unique, safe identifiers.`);
      names.add(item.name);
    }
    return names;
  };
  const issues = new Set<string>();
  const rows = array(world.state.world ?? [], "state.world", 512);
  const topologies = array(world.state.lattices ?? [], "state.lattices", 32);
  const rules = array(world.rules ?? [], "rules", 256);
  const registers = named(rows, "State");
  const topologyNames = named(topologies, "Topology");
  named(rules, "Rule");
  for(const topology of topologies) {
    if(!["grid", "box", "ring", "hex", "lattice"].includes(topology.$type))
      issues.add(`Topology '${topology.name}' requires native preview.`);
    else {
      const coordinates = getTopologyCoordinates(topology);
      if(coordinates.some(c => ![c.x, c.y, c.z].every(Number.isSafeInteger)))
        throw new Error("Coordinates must contain integer x, y and z values.");
      if(new Set(coordinates.map(c => [c.x, c.y, c.z].join(","))).size !== coordinates.length)
        throw new Error("Topology coordinates must be unique.");
    }
    if(topology.$type === "lattice" && !topology.coordinates?.length)
      issues.add(`Lattice '${topology.name}' needs explicit coordinates for offline preview.`);
    const directions = array(topology.directions ?? [], "directions", 64);
    named(directions, "Direction");
    for(const direction of directions)
      for(const axis of ["x", "y", "z"])
        if(!Number.isSafeInteger(direction[axis] ?? 0))
          throw new Error("Direction offsets must be integers.");
    if(topology.wrap)
      issues.add("Wrapped grids require native preview.");
  }
  for(const row of rows) {
    if(!["int", "bool"].includes(row.kind))
      issues.add(`State kind '${row.kind}' requires native preview.`);
    if(row.domain) {
      if(row.domain.$type !== "cellsOf")
        issues.add(`Domain '${row.domain.$type}' requires native preview.`);
      if(!topologyNames.has(row.domain.topology))
        throw new Error(`State '${row.name}' references an undeclared topology.`);
      if(row.domain.empty && row.domain.empty !== 0)
        issues.add("Nonzero empty cells require native preview.");
      const cells = array(row.cells ?? [], "cells", 4096);
      const seen = new Set<number>();
      const topology = topologies.find(t => t.name === row.domain.topology);
      const cellCount = ["grid", "box", "hex", "ring", "lattice"].includes(topology.$type) ? getTopologyCoordinates(topology).length : 4096;
      for(const cell of cells) {
        object(cell, "Cell");
        const key = Number(cell.key);
        if(!Number.isInteger(key) || key < 0 || key >= cellCount || seen.has(key) || !Number.isSafeInteger(cell.value))
          throw new Error("Authored cells need unique in-range numeric keys and exact integer values.");
        seen.add(key);
      }
    }
    if(row.value !== undefined && !["number", "boolean"].includes(typeof row.value))
      issues.add("Non-numeric values require native preview.");
    if(typeof row.value === "number" && !Number.isSafeInteger(row.value))
      issues.add(`State '${row.name}' is outside exact JSON integer preview.`);
    for(const bound of [row.min, row.max])
      if(bound !== undefined && !Number.isSafeInteger(bound))
        throw new Error("Preview bounds must be exact integers.");
  }
  let nodes = 0;
  const predicate = (gate: any, depth = 0): void => {
    if(!gate)
      return;
    if(++nodes > 4096 || depth > 32)
      throw new Error("Predicate complexity exceeds the offline preview limit.");
    object(gate, "Predicate");
    if(gate.$type === "all" || gate.$type === "any")
      array(gate.predicates ?? [], "predicates", 256).forEach(p => predicate(p, depth + 1));
    else if(gate.$type === "not")
      predicate(gate.predicate, depth + 1);
    else if(gate.$type !== "compareState")
      issues.add(`Predicate '${gate.$type}' requires native preview.`);
    else if(!registers.has(gate.state) || (gate.comparandState && !registers.has(gate.comparandState)))
      throw new Error("Predicate references an undeclared state.");
  };
  for(const rule of rules) {
    if(rule.forEach || rule.bindings || rule.zones)
      issues.add("Bound rules require native preview.");
    if(rule.mode && !["Edge", "Level"].includes(rule.mode))
      issues.add(`Rule mode '${rule.mode}' requires native preview.`);
    predicate(rule.gate);
    for(const effect of array(rule.effects ?? [], "effects", 32)) {
      object(effect, "Effect");
      if(!["setState", "addState"].includes(effect.$type))
        issues.add(`Effect '${effect.$type}' requires native preview.`);
      else if(!registers.has(effect.state))
        throw new Error(`Effect writes undeclared state '${effect.state}'.`);
      if(effect.fromState && !registers.has(effect.fromState))
        throw new Error("Effect reads an undeclared state.");
      if(effect.expression?.includes("$board:mask:") && topologies.length !== 1)
        issues.add("Mask preview currently requires one topology.");
      if(effect.expression !== undefined) {
        try {
          validatePreviewExpression(effect.expression);
        }
        catch(error) {
          issues.add(`${rule.name}: ${(error as Error).message}`);
        }
      }
    }
  }
  return { world, previewIssues: [...issues] };
}
