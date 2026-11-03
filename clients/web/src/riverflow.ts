/**
 * How much a river carries and how high its water stands, as the sim works it out (RiverFlow.cs in ToyCity.Sim):
 * the map draws the water and the floods by the same sums the sim damages buildings by.
 */
const STEP_DM = 20;
export const FLOW_LEAST = 25, FLOW_MOST = 600, REACH = 5;

/** The flood stage a flow brings: in its banks under twice the usual, up to 3 at four and a half times. */
export function stageOf(pct: number): number { return pct >= 450 ? 3 : pct >= 300 ? 2 : pct >= 200 ? 1 : 0; }
/** The flow a stage stands for, from a server before flows. */
export function ofStage(stage: number): number { return stage <= 0 ? 100 : stage === 1 ? 230 : stage === 2 ? 350 : 500; }

/** How big the water is around each cell: the cells of river, pond or lake within two cells, 0 where there is none. */
export function sizes(w: number, h: number, ground: Uint8Array, river: Uint8Array): Uint8Array {
  const n = w * h, wet = new Uint8Array(n), size = new Uint8Array(n);
  for (let i = 0; i < n; i++) wet[i] = river[i] > ground[i] ? 1 : 0;
  for (let i = 0; i < n; i++) {
    if (!wet[i]) continue;
    const x = i % w, y = (i / w) | 0;
    let k = 0;
    for (let dy = -2; dy <= 2; dy++)
      for (let dx = -2; dx <= 2; dx++) {
        const nx = x + dx, ny = y + dy;
        if (nx >= 0 && ny >= 0 && nx < w && ny < h) k += wet[ny * w + nx];
      }
    size[i] = k;
  }
  return size;
}

export function usualDepthDm(size: number): number { return Math.min(30, Math.max(10, 8 + size)); }

/** How far the water stands over (or under) its usual level at a flow, in decimetres: depth goes as flow to the 0.6. */
export function riseDm(size: number, pct: number): number {
  // As the sim's MathF.Round: half away from zero would differ only on exact halves; it rounds to even.
  const v = usualDepthDm(size) * (Math.pow(Math.min(FLOW_MOST, Math.max(FLOW_LEAST, pct)) / 100, 0.6) - 1);
  const r = Math.round(v);
  return Math.abs(v % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r;
}

/** A river cell's water surface in decimetres at a flow: a third of a step under its surface step, plus the rise. */
export function waterDm(river: number, size: number, pct: number): number {
  return Math.max((river - 1) * STEP_DM + 3, river * STEP_DM - 7 + riseDm(size, pct));
}

/** The floodwater for a map at a flow, in steps (0 dry), as the sim's RiverFlow.Flooded. */
export function flooded(w: number, h: number, ground: Uint8Array, river: Uint8Array, waterLevel: number, pct: number): Uint8Array {
  const n = w * h, water = new Uint8Array(n);
  if (stageOf(pct) <= 0 || river.length !== n) return water;
  const size = sizes(w, h, ground, river), dm = new Int32Array(n), dist = new Int32Array(n);
  const queue: number[] = [];
  for (let i = 0; i < n; i++) if (river[i] > ground[i]) { dm[i] = waterDm(river[i], size[i], pct); queue.push(i); }
  for (let q = 0; q < queue.length; q++) {
    const c = queue[q];
    if (dist[c] >= REACH) continue;
    const x = c % w, y = (c / w) | 0;
    for (const [dx, dy] of [[0, -1], [1, 0], [0, 1], [-1, 0]]) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
      const j = ny * w + nx;
      if (dm[j] || ground[j] < waterLevel || river[j] > ground[j] || ground[j] * STEP_DM + 7 > dm[c]) continue;
      dm[j] = dm[c];
      dist[j] = dist[c] + 1;
      queue.push(j);
    }
  }
  for (let i = 0; i < n; i++) if (dm[i] && river[i] <= ground[i]) water[i] = Math.min(255, Math.floor((dm[i] + STEP_DM - 1) / STEP_DM));
  return water;
}
