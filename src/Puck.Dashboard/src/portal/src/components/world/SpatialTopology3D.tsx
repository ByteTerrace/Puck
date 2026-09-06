import React, { useLayoutEffect, useMemo, useRef, useEffect } from "react";
import { Canvas, useFrame, useThree } from "@react-three/fiber";
import { OrbitControls, PerspectiveCamera, OrthographicCamera, Html } from "@react-three/drei";
import { Box3, Color, InstancedMesh, Matrix4, Vector3 } from "three";
import type { OrbitControls as OrbitControlsImpl } from "three-stdlib";
import type { SceneProjection, SceneCell } from "../../authoring/sceneProjection";
import { appearanceFor, type ValueAppearance } from "../../authoring/presentation";
import type { CellReference } from "../../authoring/documentTools";

export interface ViewportMetrics { frames: number; calls: number; triangles: number; geometries: number; textures: number }
export interface SpatialTopology3DProps {
  scene: SceneProjection;
  visible: SceneCell[];
  values: Record<number, number>;
  empty: number;
  bindings?: Record<string, ValueAppearance>;
  selection: Set<number>;
  highlights: Set<number>;
  onSelect: (ref: CellReference, additive: boolean) => void;
  cameraRequest: { id: number; axis: "orbit" | "top" | "front" | "side"; selection: boolean };
  orthographic: boolean;
  relationships?: Float32Array;
  onMetricsReady: (read: (() => ViewportMetrics) | null) => void;
}

function CellInstances({ scene, visible, values, empty, bindings, selection, highlights, onSelect, shape }: Pick<SpatialTopology3DProps,
  "scene" | "visible" | "values" | "empty" | "bindings" | "selection" | "highlights" | "onSelect"> & { shape: "cube" | "sphere" | "diamond" }) {
  const mesh = useRef<InstancedMesh>(null);
  const { invalidate } = useThree();
  const cache = useRef<{ positions: string[]; colors: string[] }>({ positions: [], colors: [] });
  const previousMesh = useRef<InstancedMesh | null>(null);
  const matrix = useMemo(() => new Matrix4(), []);
  const color = useMemo(() => new Color(), []);
  useLayoutEffect(() => {
    const target = mesh.current;
    if (!target) return;
    if (previousMesh.current !== target) { cache.current = { positions: [], colors: [] }; previousMesh.current = target; }
    let moved = false, colored = false;
    target.count = visible.length;
    for (let i = 0; i < visible.length; i++) {
      const cell = visible[i], value = values[cell.ref.index] ?? empty;
      const appearance = appearanceFor(value, bindings), selected = selection.has(cell.ref.index);
      const scale = scene.unit * (selected ? 0.66 : value === empty ? 0.22 : 0.52);
      const stamp = [...cell.position, scale].join(",");
      if (cache.current.positions[i] !== stamp) {
        matrix.makeScale(scale, scale, scale).setPosition(...cell.position);
        target.setMatrixAt(i, matrix);
        target.instanceMatrix.addUpdateRange(i * 16, 16);
        cache.current.positions[i] = stamp; moved = true;
      }
      const tint = selected ? "#ffe3a3" : highlights.has(cell.ref.index) ? "#67e8f9" : appearance.color;
      if (cache.current.colors[i] !== tint) {
        target.setColorAt(i, color.set(tint));
        target.instanceColor!.addUpdateRange(i * 3, 3);
        cache.current.colors[i] = tint; colored = true;
      }
    }
    if (moved) { target.instanceMatrix.needsUpdate = true; target.computeBoundingSphere(); }
    if (colored && target.instanceColor) target.instanceColor.needsUpdate = true;
    invalidate();
  }, [visible, values, empty, bindings, selection, highlights, scene.unit, matrix, color, invalidate]);

  return (
    <instancedMesh ref={mesh} args={[undefined, undefined, scene.cells.length]} frustumCulled={false}
      onClick={event => {
        event.stopPropagation();
        if (event.delta > 4 || event.instanceId === undefined) return;
        const cell = visible[event.instanceId];
        if (cell) onSelect(cell.ref, event.shiftKey || event.ctrlKey || event.metaKey);
      }}>
      {shape === "sphere" ? <sphereGeometry args={[0.5, 12, 8]} /> : shape === "diamond" ? <octahedronGeometry args={[0.65, 0]} /> : <boxGeometry args={[1, 1, 1]} />}
      <meshStandardMaterial roughness={0.85} metalness={0.05} />
    </instancedMesh>
  );
}

function Scene({ scene, visible, values, empty, bindings, selection, highlights, onSelect, cameraRequest, orthographic, relationships, onMetricsReady }: SpatialTopology3DProps) {
  const controls = useRef<OrbitControlsImpl>(null);
  const { camera, invalidate, size, gl } = useThree();
  const frames = useRef(0);
  useFrame(() => { frames.current++; });
  useEffect(() => {
    onMetricsReady(() => ({ frames: frames.current, calls: gl.info.render.calls, triangles: gl.info.render.triangles,
      geometries: gl.info.memory.geometries, textures: gl.info.memory.textures }));
    return () => onMetricsReady(null);
  }, [gl, onMetricsReady]);
  // Reframe on explicit navigation or geometry changes, never on hover or value edits.
  const selectionRef = useRef(selection); selectionRef.current = selection;
  const visibleRef = useRef(visible); visibleRef.current = visible;
  useLayoutEffect(() => {
    const candidates = cameraRequest.selection ? visibleRef.current.filter(c => selectionRef.current.has(c.ref.index)) : scene.cells;
    const bounds = candidates.length ? new Box3().setFromPoints(candidates.map(c => new Vector3(...c.position))).expandByScalar(scene.unit * 0.6)
      : new Box3(new Vector3(...scene.bounds.min), new Vector3(...scene.bounds.max));
    const center = bounds.getCenter(new Vector3());
    const extent = bounds.getSize(new Vector3());
    const radius = Math.max(extent.length() / 2, scene.unit);
    const directions = { orbit: [1.25, 0.95, 1.55], top: [0, 1, 0.001], front: [0, 0, 1], side: [1, 0, 0] };
    const direction = new Vector3(...directions[cameraRequest.axis]).normalize();
    const aspect = size.width / Math.max(1, size.height);
    const distance = radius / Math.sin(Math.atan(Math.tan(Math.PI / 8) * Math.min(1, aspect))) * 1.15;
    camera.position.copy(center).addScaledVector(direction, distance);
    camera.near = Math.max(radius / 1000, 0.00001); camera.far = distance + radius * 10;
    if (orthographic) camera.zoom = Math.min(size.width, size.height) / (radius * 2.4);
    camera.lookAt(center); camera.updateProjectionMatrix();
    if (controls.current) { controls.current.target.copy(center); controls.current.update(); }
    invalidate();
  }, [camera, cameraRequest, scene, orthographic, size.width, size.height, invalidate]);
  const layerLines = useMemo(() => {
    const points: number[] = [];
    const layers = new Map<number, { minX: number; maxX: number; minZ: number; maxZ: number; y: number }>();
    for (const cell of visible) {
      const [x, y, z] = cell.position;
      const layer = layers.get(cell.coordinate.z);
      if (layer) { layer.minX = Math.min(layer.minX, x); layer.maxX = Math.max(layer.maxX, x); layer.minZ = Math.min(layer.minZ, z); layer.maxZ = Math.max(layer.maxZ, z); }
      else layers.set(cell.coordinate.z, { minX: x, maxX: x, minZ: z, maxZ: z, y });
    }
    for (const layer of layers.values()) {
      const pad = scene.unit / 2;
      const x0 = layer.minX - pad, x1 = layer.maxX + pad, z0 = layer.minZ - pad, z1 = layer.maxZ + pad, y = layer.y - scene.unit * 0.38;
      points.push(x0,y,z0,x1,y,z0, x1,y,z0,x1,y,z1, x1,y,z1,x0,y,z1, x0,y,z1,x0,y,z0);
    }
    return new Float32Array(points);
  }, [scene, visible]);
  const batches = useMemo(() => (["cube", "sphere", "diamond"] as const).map(shape => [shape,
    visible.filter(cell => (appearanceFor(values[cell.ref.index] ?? empty, bindings).shape ?? "cube") === shape),
  ] as const), [visible, values, empty, bindings]);
  const primary = visible.find(c => selection.has(c.ref.index));
  return <>
    <ambientLight intensity={1.5} />
    <directionalLight position={[6, 10, 8]} intensity={2} />
    <OrbitControls ref={controls} makeDefault enableDamping={false} />
    {batches.map(([shape, cells]) => cells.length > 0 && <CellInstances key={shape} shape={shape} visible={cells}
      scene={scene} values={values} empty={empty} bindings={bindings} selection={selection} highlights={highlights} onSelect={onSelect} />)}
    <lineSegments>
      <bufferGeometry><bufferAttribute attach="attributes-position" args={[layerLines, 3]} /></bufferGeometry>
      <lineBasicMaterial color="#536273" transparent opacity={0.55} />
    </lineSegments>
    {relationships && <lineSegments>
      <bufferGeometry><bufferAttribute attach="attributes-position" args={[relationships, 3]} /></bufferGeometry>
      <lineBasicMaterial color="#8cacc1" transparent opacity={0.25} />
    </lineSegments>}
    {primary && <Html position={primary.position} center style={{ pointerEvents: "none", transform: "translateY(-32px)", whiteSpace: "nowrap" }}>
      <span className="studio-scene-label">#{primary.ref.index} · {appearanceFor(values[primary.ref.index] ?? empty, bindings).label}</span>
    </Html>}
  </>;
}

const SpatialTopology3D = React.memo((props: SpatialTopology3DProps) => <div className="studio-3d" role="img"
  aria-label={"Spatial view of " + props.scene.cells.length + " cells. Use the 2D view or cell address inspector for keyboard selection."}>
  <Canvas frameloop="demand" dpr={[1, 1.5]} gl={{ antialias: true, alpha: false }} onCreated={({ gl }) => gl.setClearColor("#151d27")}>
    {props.orthographic ? <OrthographicCamera makeDefault position={[8, 6, 10]} /> : <PerspectiveCamera makeDefault fov={45} position={[8, 6, 10]} />}
    <Scene {...props} />
  </Canvas>
</div>);
export default SpatialTopology3D;
