import React, { Suspense, lazy, useCallback, useEffect, useMemo, useState } from "react";
import { Badge, Button, Checkbox, Group, Select, SegmentedControl, Slider, Stack, Text, TextInput, Title } from "@mantine/core";
import { RiBox3Line, RiFocus3Line, RiFullscreenLine, RiShapesLine } from "@remixicon/react";
import { StudioContext, useStudioComposition, useStudioGeometry, useStudioPreview, useStudioSelection } from "../../context/StudioContext";
import {
  authoredCellValues,
  emptyValueFor,
  listTopologies,
  parseCellAddresses,
  parseRowValue,
  rowsBoundToTopology,
} from "../../authoring/documentTools";
import { isVolumetric, projectDirections, projectScene } from "../../authoring/sceneProjection";
import { appearanceFor, readPresentation } from "../../authoring/presentation";
import { ExplorerItem } from "../../ui/ExplorerItem";
import { cellNumber } from "../../native/engineTypes";
import { EmptyState } from "../../ui/EmptyState";
import { Kicker } from "../../ui/Kicker";
import { SectionFallback } from "../../ui/SectionFallback";
import UniversalTopologyView from "./UniversalTopologyView";
import classes from "./WorldWorkbench.module.css";

const SpatialTopology3D = lazy(() => import("./SpatialTopology3D"));
const EMPTY_GEOMETRY: readonly import("../../native/engineTypes").EngineCell[] = [];
const ignoreMetrics = () => {};

class ViewportBoundary extends React.Component<{ children: React.ReactNode; onFallback: () => void }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() {
    return this.state.failed
      ? <EmptyState icon={<RiBox3Line size={22} />} title="3D is unavailable"
        action={<Button onClick={this.props.onFallback} size="xs" variant="default">Use accessible 2D view</Button>}>
        3D is unavailable on this device.
      </EmptyState>
      : this.props.children;
  }
}

/**
 * The studio's spatial surface, bound entirely to `StudioContext` — no props: a topology explorer,
 * the matching viewport (`UniversalTopologyView` for a single-layer topology, `SpatialTopology3D`
 * for a volumetric one — decided by the engine's own geometry, never a topology `$type` special
 * case), and an inspector for selection and (in Preview mode) writing a live cell. Everything it shows comes from
 * the newest clean composition of the workspace's root; the source is where a cell's authored value changes.
 * Geometry comes only from `useStudioGeometry()` — the engine's own `cells()` answer — never a TypeScript ordinal
 * formula.
 */
export const WorldWorkbench: React.FC = () => {
  const actor = StudioContext.useActorRef();
  const composition = useStudioComposition();
  const geometryMap = useStudioGeometry();
  const selection = useStudioSelection();
  const preview = useStudioPreview();

  const topologies = useMemo(() => listTopologies(composition), [composition]);
  const topologyName = selection.topology;
  useEffect(() => {
    if (!topologyName && topologies.length > 0) {
      actor.send({ type: "SELECT_TOPOLOGY", name: topologies[0].name });
    }
  }, [topologyName, topologies, actor]);
  const topology = topologies.find(t => t.name === topologyName);
  const geometry = topologyName ? (geometryMap[topologyName] ?? EMPTY_GEOMETRY) : EMPTY_GEOMETRY;

  const domainRows = useMemo(() => (topologyName ? rowsBoundToTopology(composition, topologyName) : []), [composition, topologyName]);
  const [rowName, setRowName] = useState<string | null>(null);
  useEffect(() => { setRowName(null); }, [topologyName]);
  const row = domainRows.find(r => r.name === rowName) ?? domainRows[0];

  const [view, setView] = useState<"3d" | "2d">("2d");
  const [layerIndex, setLayerIndex] = useState<number | "all">("all");
  const [source, setSource] = useState<"author" | "preview">("author");
  useEffect(() => {
    setView(isVolumetric(geometry) ? "3d" : "2d");
    setLayerIndex("all");
    setSource("author");
  }, [topologyName, geometry]);

  const [separation, setSeparation] = useState(1.6);
  const [orthographic, setOrthographic] = useState(false);
  const [showHidden, setShowHidden] = useState(true);
  const [showRelationships, setShowRelationships] = useState(false);
  const [cameraRequest, setCameraRequest] = useState<{ id: number; axis: "orbit" | "top" | "front" | "side"; selection: boolean }>({ id: 0, axis: "orbit", selection: false });

  const scene = useMemo(() => projectScene(geometry, topology?.cellSize ?? 1, separation), [geometry, topology?.cellSize, separation]);

  const authoredValues = useMemo(() => authoredCellValues(row), [row]);
  const livePreviewRow = preview.rows.find(r => r.name === row?.name);
  const previewValues = useMemo(
    // A board colouring reads whole-number cells alone; a text, bool, or vector row contributes none.
    () => new Map((livePreviewRow?.cells ?? []).flatMap(cell => {
      const number = cellNumber(cell.value);
      return (number === null ? [] : [[Number(cell.key), number] as const]);
    })),
    [livePreviewRow],
  );
  const values = source === "author" ? authoredValues : previewValues;
  const empty = emptyValueFor(row);
  const bindings = useMemo(() => readPresentation(composition)[row?.name ?? ""], [composition, row?.name]);

  const selectedSet = useMemo(() => new Set(selection.ordinals), [selection.ordinals]);
  const effectiveLayerIndex = (view === "2d" && layerIndex === "all" && scene.layers.length > 1)
    ? (scene.cells.find(c => selectedSet.has(c.ordinal))?.layerIndex ?? 0)
    : layerIndex;
  const visible = useMemo(
    () => scene.cells.filter(cell =>
      (effectiveLayerIndex === "all" || cell.layerIndex === effectiveLayerIndex) &&
      (showHidden || !appearanceFor(values.get(cell.ordinal) ?? empty, bindings).hidden)),
    [scene, effectiveLayerIndex, showHidden, values, empty, bindings],
  );
  const relationships = useMemo(() => (topology && showRelationships ? projectDirections(topology, scene) : undefined), [topology, scene, showRelationships]);

  const onSelect = useCallback((ordinal: number, additive: boolean) => {
    actor.send({ type: "SELECT_CELLS", ordinals: [ordinal], mode: additive ? "toggle" : "replace" });
  }, [actor]);
  const frame = (axis = cameraRequest.axis, selectionOnly = false) => setCameraRequest(c => ({ id: c.id + 1, axis, selection: selectionOnly }));

  const [valueText, setValueText] = useState("1");
  const [address, setAddress] = useState("0");
  const [localError, setLocalError] = useState<string | null>(null);

  const writeValue = useMemo(() => {
    if (!row) return undefined;
    try { return parseRowValue(row, valueText); } catch { return undefined; }
  }, [row, valueText]);

  const primary = scene.cells.find(c => selectedSet.has(c.ordinal));
  const selectedValues = new Set([...selectedSet].map(ordinal => (values.get(ordinal) ?? empty).toString()));
  const visibleSelected = visible.filter(cell => selectedSet.has(cell.ordinal)).length;

  const switchView = (value: string) => {
    setView(value as "2d" | "3d");
    if (value === "2d" && effectiveLayerIndex === "all" && scene.layers.length > 1) {
      setLayerIndex(primary?.layerIndex ?? 0);
    }
  };

  return <section className={classes.workspace} aria-label="World authoring workspace">
    <aside className={classes.explorer} aria-label="Document explorer">
      <Kicker c="dimmed" mb="md">Document explorer</Kicker>
      <Text className={classes.groupLabel}>Topologies</Text>
      <Stack gap={2}>
        {topologies.map(t => <ExplorerItem key={t.name} name={t.name} selected={t.name === topologyName}
          aside={<span className={classes.kind}>{t.$type}</span>}
          onClick={() => { actor.send({ type: "SELECT_TOPOLOGY", name: t.name }); setLocalError(null); }} />)}
        {!topologies.length && <Text size="sm" c="dimmed" px="xs">No topology declared. Author one under state.lattices.</Text>}
      </Stack>
      <Text className={classes.groupLabel} mt="lg">Cell states</Text>
      <Stack gap={2}>
        {domainRows.map(r => <ExplorerItem key={r.name} name={r.name} selected={r.name === row?.name}
          aside={<span className={classes.kind}>{r.kind}</span>}
          onClick={() => setRowName(r.name)} />)}
      </Stack>
      {!domainRows.length && topologyName && <Text size="sm" c="dimmed" px="xs">No cell state bound to this topology yet.</Text>}
      <Text size="xs" c="dimmed" mt="md" px="xs">Selection uses native cell addresses. View controls do not change the document.</Text>
    </aside>

    <div className={classes.viewport}>
      <div className={classes.toolbar}>
        <Group justify="space-between" gap="sm" wrap="nowrap">
          <div className={classes.heading}>
            <Title order={2} size="h4" className={classes.title}>{topology?.name ?? "No topology"}</Title>
            <Text size="xs" c="dimmed" ff="monospace">{scene.cells.length} cells · {row?.name ?? "Geometry"}</Text>
          </div>
          <SegmentedControl aria-label="Viewport dimension" size="xs" value={view} onChange={switchView} data={[{ value: "3d", label: "3D" }, { value: "2d", label: "2D" }]} />
        </Group>
        <Group gap="xs" mt="sm">
          <SegmentedControl aria-label="Displayed state source" size="xs" value={source} onChange={value => setSource(value as "author" | "preview")}
            data={[{ value: "author", label: "Author" }, { value: "preview", label: "Preview" }]} />
          <Badge color={source === "author" ? "jade" : "yellow"}>{source === "author" ? "Authored values" : "Live preview"}</Badge>
          <Select aria-label="Visible layer" size="xs" w={140} allowDeselect={false} value={String(effectiveLayerIndex)}
            data={[...(view === "3d" || scene.layers.length <= 1 ? [{ value: "all", label: "All layers" }] : []),
              ...scene.layers.map((_, index) => ({ value: String(index), label: "Layer " + (index + 1) + " / " + scene.layers.length }))]}
            onChange={value => setLayerIndex(value === "all" || value === null ? "all" : Number(value))} />
        </Group>
        {view === "3d" && <Group gap={6} mt="sm">
          <Button size="xs" variant="default" leftSection={<RiFullscreenLine size={16} />} onClick={() => frame("orbit")}>Fit world</Button>
          <Button size="xs" variant="default" leftSection={<RiFocus3Line size={16} />} disabled={!visibleSelected} onClick={() => frame(cameraRequest.axis, true)}>Fit selection</Button>
          <Button size="xs" variant="subtle" color="gray" onClick={() => frame("top")}>Top</Button>
          <Button size="xs" variant="subtle" color="gray" onClick={() => frame("front")}>Front</Button>
          <Button size="xs" variant="subtle" color="gray" onClick={() => frame("side")}>Side</Button>
          <Checkbox size="xs" label="Orthographic" checked={orthographic} onChange={e => setOrthographic(e.currentTarget.checked)} />
        </Group>}
      </div>
      <div className={classes.stage}>
        {scene.cells.length
          ? view === "3d"
            ? <ViewportBoundary key={topologyName ?? ""} onFallback={() => switchView("2d")}>
              <Suspense fallback={<SectionFallback label="Loading spatial tools…" />}>
                <SpatialTopology3D scene={scene} visible={visible} values={values} empty={empty} bindings={bindings} selection={selectedSet}
                  onSelect={onSelect} cameraRequest={cameraRequest} orthographic={orthographic}
                  relationships={effectiveLayerIndex === "all" && showHidden ? relationships : undefined} onMetricsReady={ignoreMetrics} />
              </Suspense>
            </ViewportBoundary>
            : <UniversalTopologyView key={(topologyName ?? "") + String(effectiveLayerIndex)} cells={visible} values={values} empty={empty}
              selection={selectedSet} bindings={bindings} onSelect={onSelect} />
          : <EmptyState icon={<RiShapesLine size={22} />} title={topologyName ? "No geometry" : "No topology selected"}>
            {topologyName ? "This topology has no computed geometry yet." : "Select a topology to view its cells."}
          </EmptyState>}
      </div>
      <div className={classes.footer}>
        <Group justify="space-between" gap="sm">
          <Text size="xs" c="dimmed">{view === "3d" ? "Drag to orbit · Right-drag to pan · Scroll to zoom" : "Native cell order · Keyboard selection"}</Text>
          <Text size="xs" ff="monospace">{selectedSet.size} selected{selectedSet.size !== visibleSelected ? " · " + (selectedSet.size - visibleSelected) + " outside view" : ""}</Text>
        </Group>
        <Group gap="xs" mt="xs">
          <Button size="xs" variant="default" disabled={!visible.length} onClick={() => actor.send({ type: "SELECT_CELLS", ordinals: visible.map(c => c.ordinal), mode: "replace" })}>Select visible</Button>
          <Button size="xs" variant="subtle" color="gray" disabled={!selection.ordinals.length} onClick={() => actor.send({ type: "SELECT_CELLS", ordinals: [], mode: "replace" })}>Clear selection</Button>
          <Checkbox size="xs" label="Show hidden values" checked={showHidden} onChange={e => setShowHidden(e.currentTarget.checked)} />
        </Group>
        {view === "3d" && <details className={classes.disclosure}><summary>View options</summary>
          <Text size="xs" mt="xs">Layer separation · <span className={classes.number}>{separation.toFixed(1)}×</span></Text>
          <Slider aria-label="Layer separation" min={1} max={4} step={0.1} value={separation} onChange={setSeparation} mb="md" />
          <Checkbox size="xs" label="Authored directions (grid/box only)" checked={showRelationships} onChange={e => setShowRelationships(e.currentTarget.checked)} />
        </details>}
      </div>
    </div>

    <aside className={classes.inspector} aria-label="Selection inspector">
      <Kicker c="dimmed" mb="md">Inspector</Kicker>
      <Text fw={600} size="sm" role="status">{selectedSet.size ? selectedSet.size + (selectedSet.size === 1 ? " cell selected" : " cells selected") : "Select a cell"}</Text>
      {primary && <Text size="xs" c="dimmed" mt={4} ff="monospace">#{primary.ordinal}{selectedSet.size > 1 ? " and " + (selectedSet.size - 1) + " more" : ""}</Text>}
      <Text size="sm" mt="xs">{selectedSet.size ? (selectedValues.size === 1 ? "Value: " + [...selectedValues][0] : "Mixed values (" + selectedValues.size + ")") : "Click to select. Shift-click adds or removes."}</Text>
      <Group align="end" gap={6} mt="md" wrap="nowrap">
        <TextInput label="Cell address" description="Ranges: 0-15, 32, 63" value={address} onChange={e => setAddress(e.currentTarget.value)}
          className={classes.grow} classNames={{ input: classes.mono }} size="xs" />
        <Button aria-label="Select address" size="xs" variant="default" disabled={!geometry.length} onClick={() => {
          try {
            const ordinals = parseCellAddresses(address, geometry);
            actor.send({ type: "SELECT_CELLS", ordinals, mode: "replace" });
            setLocalError(null);
          } catch (error) { setLocalError((error as Error).message); }
        }}>Select</Button>
      </Group>
      <div className={classes.inspectorSection}>
        <Text fw={600} size="sm">Preview write</Text>
        <Text size="xs" c="dimmed" mb="sm">Writes a value into the selected cell of the running preview. The source is where an authored value changes.</Text>
        <TextInput label="Value" value={valueText} onChange={e => setValueText(e.currentTarget.value)} size="sm" classNames={{ input: classes.mono }} />
        <Button variant="default" size="xs" fullWidth mt="sm" disabled={preview.status !== "ready" || !row || writeValue === undefined || selectedSet.size !== 1} onClick={() => {
          if (!row || writeValue === undefined || !primary) return;
          actor.send({ type: "PREVIEW_WRITE", row: row.name, key: livePreviewRow?.keyed ? String(primary.ordinal) : undefined, value: writeValue, write: "set" });
        }}>Write to the selected cell</Button>
      </div>
      {localError && <Text role="alert" size="sm" c="red" mt="sm">{localError}</Text>}
    </aside>
  </section>;
};

export default WorldWorkbench;
