import type { Msg } from './net';

export interface Building {
  id: number;
  typeId: string;
  x: number;
  y: number;
  width: number;
  height: number;
  zone: number;
  density: number;
  wealth: number;
  name: string;
  residents: number;
  jobs: number;
  workers: number;
  powered: boolean;
  watered: boolean;
  abandoned: boolean;
  constructionDaysLeft: number;
  fundingPercent: number;
  condition: number;
  graffiti: number;
  burningDays: number;
  /** BuildingLook: 0 fine, 1 building, 2 struggling, 3 run down, 4 abandoned, 5 derelict, 6 burning. */
  look: number;
  riders: number;
  /** Roadside buildings: the kerb of the road cell at x, y. 0 north, 1 east, 2 south, 3 west. */
  side: number;
}

export interface Profile {
  id: string;
  name: string;
  speedKmh: number;
  cars: boolean;
  bikes: boolean;
  pedestrians: boolean;
  oneWay: boolean;
  frontage: boolean;
  costPerCell: number;
  color: string;
  widthM: number;
  /** "shared", "dedicated" or "" */
  tram: string;
  rail: boolean;
  /** Beta 3.2: the steepest its type climbs, whether a footpath turns to stairs, and the surfaces it may be built in (first = default). */
  maxGradePct: number;
  stairs: boolean;
  surfaces: string[];
}

/** What a path may be paved with (Beta 3.2). */
export interface Surface {
  id: string;
  name: string;
  maxGradePct: number;
  costPct: number;
  speedPct: number;
  color: string;
}

export interface BuildingType {
  id: string;
  name: string;
  category: string;
  zone: number;
  density: number;
  wealth: number;
  width: number;
  height: number;
  cost: number;
  placeable: boolean;
  color: string;
  unlockText: string;
  unique: boolean;
  income: number;
  attraction: number;
  /** "bus", "tram", "rail" or "" */
  transit: string;
  capacity: number;
  catchment: number;
  /** Stands on a road's kerb instead of on land (bus stops). */
  roadside: boolean;
  lotKind: string;
  maintenance: number;
  leaseRent: number;
  upgrades: { toTypeId: string; cost: number; riders: number; population: number; nearZone: number; text: string }[];
}

/** Kerb directions by side: north, east, south, west. */
export const SIDE_DX = [0, 1, 0, -1];
export const SIDE_DY = [-1, 0, 1, 0];

/** The client's mirror of the city, kept current from FullState and TickDelta messages. */
/** An arc or diagonal as drawn: see RoadStrip in the protocol. */
export interface Strip { path: number; curved: boolean; x0: number; y0: number; x1: number; y1: number; cx: number; cy: number; cells: number[] }

export class City {
  width = 0;
  height = 0;
  waterLevel = 8;
  seed = 0;
  heights = new Uint8Array(0);
  /** The ground at each grid corner, (width + 1) × (height + 1), in decimetres; empty if the server didn't send it. */
  corners = new Uint16Array(0);
  /** Each cell's river surface in steps, 0 for none: a river cell's water stands over its ground. */
  rivers = new Uint8Array(0);
  zones = new Uint8Array(0);
  paths = new Uint16Array(0);
  /** Each cell's path surface: index into `surfaces` + 1, 0 for none (Beta 3.2). */
  pathSurfaces = new Uint8Array(0);
  surfaces: Surface[] = [];
  powerLines = new Uint8Array(0);
  waterPipes = new Uint8Array(0);
  buildingAt = new Int32Array(0);
  buildings = new Map<number, Building>();
  stats: Msg = {};
  profiles: Profile[] = [];
  types: BuildingType[] = [];
  typeById = new Map<string, BuildingType>();
  lotDepth: number[] = [2, 3, 4];
  streetNames: string[] = ['Main'];
  streetKinds: string[] = ['Street'];
  /** Placeable building types the mayor may build now (landmarks and reward lots unlock over time). */
  unlocked = new Set<string>();
  /** Arcs and diagonals as they were drawn (part 9): drawn as one smooth road over their cells. */
  strips: Strip[] = [];
  /** Cells drawn by a strip rather than as road cells: every strip cell but its two ends. */
  underStrip = new Set<number>();
  /** Bumped when terrain changes, so the renderer can rebuild its cached ground layer. */
  terrainVersion = 0;
  /** Cells whose height changed since the renderer last looked, or null when it should redraw everything. */
  terrainDirty: number[] | null = null;
  loaded = false;

  applyFull(f: Msg) {
    this.width = f.width;
    this.height = f.height;
    this.waterLevel = f.waterLevel;
    this.seed = f.seed;
    const n = this.width * this.height;
    this.heights = new Uint8Array(f.heights);
    this.rivers = f.rivers?.length === n ? new Uint8Array(f.rivers) : new Uint8Array(n);
    const cb = f.corners as Uint8Array | undefined;
    const nc = (this.width + 1) * (this.height + 1);
    this.corners = new Uint16Array(cb?.length === 2 * nc ? nc : 0);
    for (let k = 0; k < this.corners.length; k++) this.corners[k] = cb![2 * k] | (cb![2 * k + 1] << 8);
    this.zones = new Uint8Array(f.zones);
    const pb = new Uint8Array(f.paths);
    this.paths = new Uint16Array(n);
    for (let i = 0; i < n; i++) this.paths[i] = pb[2 * i] | (pb[2 * i + 1] << 8);
    const sb = f.pathSurfaces as Uint8Array | undefined;
    this.pathSurfaces = sb?.length === n ? new Uint8Array(sb) : new Uint8Array(n);
    this.powerLines = new Uint8Array(f.powerLines);
    this.waterPipes = new Uint8Array(f.waterPipes);
    this.profiles = f.catalog.profiles;
    this.surfaces = f.catalog.surfaces ?? [];
    this.types = f.catalog.buildingTypes;
    this.typeById = new Map(this.types.map((t) => [t.id, t]));
    if (f.catalog.lotDepth?.length === 3) this.lotDepth = f.catalog.lotDepth;
    if (f.catalog.streetNames?.length) this.streetNames = f.catalog.streetNames;
    if (f.catalog.streetKinds?.length) this.streetKinds = f.catalog.streetKinds;
    this.buildings.clear();
    this.buildingAt = new Int32Array(n);
    for (const b of f.buildings as Building[]) this.putBuilding(b);
    this.stats = f.stats;
    this.setStrips(f.strips ?? []);
    this.terrainDirty = null;
    this.terrainVersion++;
    this.loaded = true;
  }

  applyDelta(d: Msg) {
    let terrain = false;
    for (const c of d.cells as Msg[]) {
      const i = c.index;
      if (this.heights[i] !== c.height) {
        terrain = true;
        this.terrainDirty?.push(i);
      }
      this.heights[i] = c.height;
      this.zones[i] = c.zone;
      this.paths[i] = c.path;
      this.pathSurfaces[i] = c.surface ?? 0;
      this.powerLines[i] = c.powerLine ? 1 : 0;
      this.waterPipes[i] = c.waterPipe ? 1 : 0;
    }
    for (const id of d.removedBuildings as number[]) this.removeBuilding(id);
    for (const b of d.buildings as Building[]) {
      this.removeBuilding(b.id);
      this.putBuilding(b);
    }
    if (d.stats) this.stats = d.stats;
    if (d.stripsChanged) this.setStrips(d.strips ?? []);
    for (const u of (d.corners ?? []) as Msg[]) {
      if (u.index >= this.corners.length || this.corners[u.index] === u.dm) continue;
      this.corners[u.index] = u.dm;
      terrain = true;
      // The four cells around the corner.
      const cx = u.index % (this.width + 1), cy = Math.floor(u.index / (this.width + 1));
      for (const [x, y] of [[cx - 1, cy - 1], [cx, cy - 1], [cx - 1, cy], [cx, cy]])
        if (this.inBounds(x, y)) this.terrainDirty?.push(y * this.width + x);
    }
    if (terrain) this.terrainVersion++;
  }

  private setStrips(strips: Strip[]) {
    this.strips = strips;
    this.underStrip = new Set();
    for (const s of strips) for (let k = 1; k + 1 < s.cells.length; k++) this.underStrip.add(s.cells[k]);
    for (const s of strips) if (s.cells.length) { this.underStrip.delete(s.cells[0]); this.underStrip.delete(s.cells[s.cells.length - 1]); }
  }

  /** The zone the open ground under a strip takes: the one most cells around it have, or 0. Mirrors RoadShape.FillZone. */
  fillZone(i: number): number {
    const x = i % this.width, y = (i / this.width) | 0;
    const count = new Array<number>(16).fill(0);
    let best = 0;
    for (let dy = -1; dy <= 1; dy++)
      for (let dx = -1; dx <= 1; dx++) {
        const nx = x + dx, ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= this.width || ny >= this.height) continue;
        const j = ny * this.width + nx;
        const z = this.zones[j];
        if (z === 0 || z >= 16 || this.paths[j]) continue;
        if (++count[z] > count[best]) best = z;
      }
    return best;
  }

  private putBuilding(b: Building) {
    this.buildings.set(b.id, b);
    if (this.isRoadside(b)) return;
    for (let y = b.y; y < b.y + b.height; y++)
      for (let x = b.x; x < b.x + b.width; x++) this.buildingAt[y * this.width + x] = b.id + 1;
  }

  private removeBuilding(id: number) {
    const b = this.buildings.get(id);
    if (!b) return;
    if (this.isRoadside(b)) { this.buildings.delete(id); return; }
    for (let y = b.y; y < b.y + b.height; y++)
      for (let x = b.x; x < b.x + b.width; x++) {
        const i = y * this.width + x;
        if (this.buildingAt[i] === id + 1) this.buildingAt[i] = 0;
      }
    this.buildings.delete(id);
  }

  /** A road's name from the line it runs along, as the server names it (Voice.RoadName). */
  roadName(northSouth: boolean, line: number): string {
    if (northSouth) {
      const n = line + 1, t = n % 100 >= 11 && n % 100 <= 13 ? 'th' : ['th', 'st', 'nd', 'rd'][n % 10] ?? 'th';
      return `${n}${t} Avenue`;
    }
    const kinds = this.streetKinds.filter((k) => k !== 'Avenue');
    if (!kinds.length) kinds.push('Street');
    const n = this.streetNames.length;
    const gcd = (a: number, b: number): number => (b ? gcd(b, a % b) : a);
    const name = n === 1 ? 0 : gcd(7, n) === 1 ? (line * 7) % n : line % n;
    return `${this.streetNames[name]} ${kinds[Math.floor(line / n) % kinds.length]}`;
  }

  /** Whether a path cell has a path to its east or west, and to its north or south. */
  roadEW(x: number, y: number) {
    return (x > 0 && !!this.paths[y * this.width + x - 1]) || (x < this.width - 1 && !!this.paths[y * this.width + x + 1]);
  }

  roadNS(x: number, y: number) {
    return (y > 0 && !!this.paths[(y - 1) * this.width + x]) || (y < this.height - 1 && !!this.paths[(y + 1) * this.width + x]);
  }

  inBounds(x: number, y: number) {
    return x >= 0 && y >= 0 && x < this.width && y < this.height;
  }

  isWater(i: number) {
    return this.heights[i] < this.waterLevel || this.rivers[i] > this.heights[i];
  }

  isRiver(i: number) {
    return this.rivers[i] > this.heights[i];
  }

  /** How many steps the rivers stand over their usual surface today: 0 normal, up to 3. */
  get floodStage(): number { return this.stats.floodStage ?? 0; }

  private floodKey = '';
  private floodCells = new Uint8Array(0);
  /**
   * Each cell's floodwater surface in steps, or 0 where it's dry, worked out as the server does (World.FloodedCells):
   * from each river cell outward up to five cells, onto dry ground lower than the river's raised surface.
   */
  get flooded(): Uint8Array {
    const stage = this.floodStage, key = `${stage}:${this.terrainVersion}:${this.width}`;
    if (key === this.floodKey) return this.floodCells;
    this.floodKey = key;
    const w = this.width, h = this.height, n = w * h, ground = this.heights, river = this.rivers;
    const water = new Uint8Array(n);
    this.floodCells = water;
    if (stage <= 0 || river.length !== n) return water;
    const dist = new Int32Array(n);
    const queue: number[] = [];
    for (let i = 0; i < n; i++) if (river[i] > ground[i]) { water[i] = Math.min(255, river[i] + stage); queue.push(i); }
    for (let q = 0; q < queue.length; q++) {
      const c = queue[q];
      if (dist[c] >= 5) continue;
      const x = c % w, y = (c / w) | 0;
      for (const [dx, dy] of [[0, -1], [1, 0], [0, 1], [-1, 0]]) {
        const nx = x + dx, ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
        const j = ny * w + nx;
        if (water[j] || ground[j] < this.waterLevel || river[j] > ground[j] || ground[j] >= water[c]) continue;
        water[j] = water[c];
        dist[j] = dist[c] + 1;
        queue.push(j);
      }
    }
    for (let i = 0; i < n; i++) if (river[i] > ground[i]) water[i] = 0;
    return water;
  }

  isRoadside(b: Building) {
    return !!this.typeById.get(b.typeId)?.roadside;
  }

  /** The roadside buildings (bus stops) on a road cell. */
  stopsAt(x: number, y: number): Building[] {
    const out: Building[] = [];
    for (const b of this.buildings.values()) if (b.x === x && b.y === y && this.isRoadside(b)) out.push(b);
    return out;
  }

  /** The building on a cell, or else a stop on its kerb, or a stop on the kerb of a road beside it facing it. */
  buildingOrStopAt(x: number, y: number): Building | undefined {
    return this.buildingAtCell(x, y) ?? this.stopsAt(x, y)[0] ?? this.stopsFacing(x, y)[0];
  }

  /** Stops on the kerbs of the roads beside a cell that face it. */
  stopsFacing(x: number, y: number): Building[] {
    const out: Building[] = [];
    for (let k = 0; k < 4; k++) for (const s of this.stopsAt(x - SIDE_DX[k], y - SIDE_DY[k])) if (s.side === k) out.push(s);
    return out;
  }

  buildingAtCell(x: number, y: number): Building | undefined {
    if (!this.inBounds(x, y)) return undefined;
    const id = this.buildingAt[y * this.width + x];
    return id ? this.buildings.get(id - 1) : undefined;
  }

  /**
   * Rough client-side check that some lot containing this cell could face a road: a path with frontage lies within
   * the zone's deepest lot size, in a straight line through the same zone. The server's why-panel is the real answer.
   */
  hasRoadAccess(x: number, y: number): boolean {
    const i0 = y * this.width + x;
    const code = this.zones[i0];
    const depth = this.lotDepth[code % 3] ?? 2;
    for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      for (let k = 1; k <= depth; k++) {
        const nx = x + dx * k;
        const ny = y + dy * k;
        if (!this.inBounds(nx, ny)) break;
        const j = ny * this.width + nx;
        if (this.profileAt(j)?.frontage) return true;
        if (this.zones[j] !== code) break;
      }
    }
    return false;
  }

  profileAt(i: number): Profile | undefined {
    const p = this.paths[i];
    return p ? this.profiles[p - 1] : undefined;
  }

  /** The surface code (index + 1) a profile is built in when asked for `id`, as the server picks it; -1 if not allowed. */
  surfaceCode(p: Profile, id: string): number {
    const list = p.surfaces ?? [];
    if (list.length === 0) return id ? -1 : 0;
    const want = id || list[0];
    if (!list.includes(want)) return -1;
    return this.surfaces.findIndex((s) => s.id === want) + 1;
  }

  /** The block of open land around a cell, enclosed by paths, or null when it isn't enclosed. Mirrors World.BlockAt. */
  blockAt(x: number, y: number, max = 1600): number[] | null {
    if (!this.inBounds(x, y)) return null;
    const open = (i: number) => {
      if (this.paths[i] || this.isWater(i)) return false;
      const id = this.buildingAt[i];
      const b = id ? this.buildings.get(id - 1) : undefined;
      return !(b && this.typeById.get(b.typeId)?.placeable);
    };
    const start = y * this.width + x;
    if (!open(start)) return null;
    const cells = [start];
    const seen = new Set<number>([start]);
    for (let q = 0; q < cells.length; q++) {
      const c = cells[q], cx = c % this.width, cy = (c / this.width) | 0;
      for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
        const nx = cx + dx, ny = cy + dy;
        if (!this.inBounds(nx, ny)) return null;
        const n = ny * this.width + nx;
        if (!open(n) || seen.has(n)) continue;
        seen.add(n);
        cells.push(n);
        if (cells.length > max) return null;
      }
    }
    return cells;
  }

  /** Steps from the nearest frontage path for each block cell (1 touches the road), 0 when no road reaches it. */
  frontageDepth(block: number[]): Map<number, number> {
    const inBlock = new Set(block);
    const depth = new Map<number, number>();
    const queue: number[] = [];
    const dirs = [[1, 0], [-1, 0], [0, 1], [0, -1]];
    for (const c of block) {
      const cx = c % this.width, cy = (c / this.width) | 0;
      if (dirs.some(([dx, dy]) => this.inBounds(cx + dx, cy + dy) && this.profileAt((cy + dy) * this.width + cx + dx)?.frontage)) { depth.set(c, 1); queue.push(c); }
    }
    for (let q = 0; q < queue.length; q++) {
      const c = queue[q], cx = c % this.width, cy = (c / this.width) | 0;
      for (const [dx, dy] of dirs) {
        if (!this.inBounds(cx + dx, cy + dy)) continue;
        const n = (cy + dy) * this.width + cx + dx;
        if (!inBlock.has(n) || depth.has(n)) continue;
        depth.set(n, depth.get(c)! + 1);
        queue.push(n);
      }
    }
    for (const c of block) if (!depth.has(c)) depth.set(c, 0);
    return depth;
  }
}

/** Cells of an L-shaped run, horizontal leg first (matches the server). */
/**
 * Cells of a curve from (x0,y0) bent toward (cx,cy) and ending at (x1,y1), snapped to the grid. The same integer
 * steps as World.CurveCells on the server, so the preview matches what gets built.
 */
export function curveCells(x0: number, y0: number, cx: number, cy: number, x1: number, y1: number): [number, number][] {
  const cells: [number, number][] = [];
  const seen = new Set<string>();
  const add = (x: number, y: number) => { const k = `${x},${y}`; if (!seen.has(k)) { seen.add(k); cells.push([x, y]); } };
  const n = 4 * (Math.abs(cx - x0) + Math.abs(cy - y0) + Math.abs(x1 - cx) + Math.abs(y1 - cy)) + 1;
  const nn = n * n;
  const off = 1 << 20;
  const round = (num: number) => Math.floor((2 * num + nn) / (2 * nn)) - off;
  let lx = x0;
  let ly = y0;
  add(x0, y0);
  for (let k = 1; k <= n; k++) {
    const a = (n - k) * (n - k), b = 2 * (n - k) * k, c = k * k;
    const x = round(a * (x0 + off) + b * (cx + off) + c * (x1 + off));
    const y = round(a * (y0 + off) + b * (cy + off) + c * (y1 + off));
    while (lx !== x || ly !== y) {
      if (lx !== x) lx += Math.sign(x - lx);
      else ly += Math.sign(y - ly);
      add(lx, ly);
    }
  }
  return cells;
}

/** As World: a bridge cell over a river costs this many times the path. */
export const BRIDGE_COST_TIMES = 5;
export const PAD_COST_PER_STEP = 5, MAX_PAD_RISE = 3;

// Path grading, ported line for line from PathGrade.cs (Beta 3.2), so previews price what the server builds.
const CELL_DM = 160, BANK_DM_PER_CELL = 100, BANK_RADIUS = 10, COST_PER_STEP = 10, COST_PER_STEP_DM = 20, BANK_COST_DIVISOR = 10;
export const maxDeltaDm = (pct: number) => Math.max(1, Math.trunc((pct * CELL_DM + 50) / 100));
export const gradePctOf = (profileMax: number, stairs: boolean, surfaceMax: number) => (stairs || surfaceMax < 0 ? profileMax : Math.min(profileMax, surfaceMax));

/** The heights (dm) a run is graded to at `step` dm a cell; null when fixed cells are too far apart. As PathGrade.Grade. */
export function grade(h: number[], fixed: boolean[], floor: number, step: number, atLeast?: number[]): number[] | null {
  const n = h.length, inf = 1 << 28;
  const under = new Array<number>(n), over = new Array<number>(n), lo = new Array<number>(n), hi = new Array<number>(n);
  for (let k = 0; k < n; k++) {
    under[k] = k > 0 ? Math.min(h[k], under[k - 1] + step) : h[k];
    over[k] = k > 0 ? Math.max(h[k], over[k - 1] - step) : h[k];
    lo[k] = Math.max(Math.max(fixed[k] ? h[k] : -inf, atLeast ? atLeast[k] : -inf), k > 0 ? lo[k - 1] - step : -inf);
    hi[k] = Math.min(fixed[k] ? h[k] : inf, k > 0 ? hi[k - 1] + step : inf);
  }
  for (let k = n - 2; k >= 0; k--) {
    under[k] = Math.min(under[k], under[k + 1] + step);
    over[k] = Math.max(over[k], over[k + 1] - step);
    lo[k] = Math.max(lo[k], lo[k + 1] - step);
    hi[k] = Math.min(hi[k], hi[k + 1] + step);
  }
  const g = new Array<number>(n);
  for (let k = 0; k < n; k++) {
    const low = fixed[k] ? lo[k] : Math.max(lo[k], floor);
    if (low > hi[k]) return null;
    g[k] = fixed[k] ? h[k] : Math.min(hi[k], Math.max(low, (under[k] + over[k]) >> 1));
  }
  return g;
}

/** The steepest grade (percent) a run needs between two fixed cells. As PathGrade.Needed. */
export function neededPct(h: number[], fixed: boolean[]): number {
  let best = 0, last = -1;
  for (let k = 0; k < h.length; k++) {
    if (!fixed[k]) continue;
    if (last >= 0) best = Math.max(best, Math.ceil((Math.abs(h[k] - h[last]) * 100) / ((k - last) * CELL_DM)));
    last = k;
  }
  return best;
}

export function cellDm(c: City, i: number): number {
  const cw = c.width + 1, x = i % c.width, y = (i / c.width) | 0, k = y * cw + x;
  return (c.corners[k] + c.corners[k + 1] + c.corners[k + cw] + c.corners[k + cw + 1] + 2) >> 2;
}

/** The corners a path and its banks move. As PathGrade.Shape. */
export function shape(c: City, run: number[], target: number[], locked: (i: number) => boolean, floorDm: number): Map<number, number> {
  const width = c.width, height = c.height, cw = width + 1, ch = height + 1, corners = c.corners;
  const lockedCorner = new Set<number>();
  const isLocked = (k: number) => {
    if (lockedCorner.has(k)) return true;
    const cx = k % cw, cy = (k / cw) | 0;
    for (let dy = -1; dy <= 0; dy++)
      for (let dx = -1; dx <= 0; dx++) {
        const x = cx + dx, y = cy + dy;
        if (x < 0 || y < 0 || x >= width || y >= height) continue;
        if (locked(y * width + x)) { lockedCorner.add(k); return true; }
      }
    return false;
  };
  const sum = new Map<number, [number, number]>();
  run.forEach((cell, k) => {
    const x = cell % width, y = (cell / width) | 0;
    for (let dy = 0; dy <= 1; dy++)
      for (let dx = 0; dx <= 1; dx++) {
        const q = (y + dy) * cw + x + dx;
        const s = sum.get(q);
        sum.set(q, s ? [s[0] + target[k], s[1] + 1] : [target[k], 1]);
      }
  });
  const val = new Map<number, number>();
  const dist = new Map<number, number>();
  const queue: number[] = [];
  for (const q of [...sum.keys()].sort((a, b) => a - b)) {
    const [s, n] = sum.get(q)!;
    const v = isLocked(q) ? corners[q] : Math.trunc((s + (n >> 1)) / n);
    val.set(q, v);
    dist.set(q, 0);
    if (v !== corners[q]) queue.push(q);
  }
  const get = (q: number) => val.get(q) ?? corners[q];
  for (let head = 0; head < queue.length; head++) {
    const q = queue[head], d = dist.get(q)!;
    if (d >= BANK_RADIUS) continue;
    const cx = q % cw, cy = (q / cw) | 0, here = get(q);
    for (let dy = -1; dy <= 1; dy++)
      for (let dx = -1; dx <= 1; dx++) {
        if (dx === 0 && dy === 0) continue;
        const nx = cx + dx, ny = cy + dy;
        if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
        const n = ny * cw + nx;
        if (sum.has(n) || isLocked(n)) continue;
        const reach = Math.max(dx !== 0 && dy !== 0 ? Math.trunc((BANK_DM_PER_CELL * 141) / 100) : BANK_DM_PER_CELL, Math.abs(corners[q] - corners[n]));
        const now = get(n);
        let eased = Math.min(here + reach, Math.max(here - reach, now));
        if (eased < floorDm && now >= floorDm) eased = floorDm;
        if (eased === now) continue;
        val.set(n, eased);
        if (!dist.has(n) || dist.get(n)! > d + 1) dist.set(n, d + 1);
        queue.push(n);
      }
  }
  const moved = new Map<number, number>();
  for (const [q, v] of val) if (v !== corners[q]) moved.set(q, v);
  return moved;
}

/** Dollars of earthworks: full price under the path, a tenth for its banks. As PathGrade.Cost. */
export function earthCost(c: City, run: number[], moved: Map<number, number>): number {
  const cw = c.width + 1, own = new Set<number>();
  for (const i of run) {
    const x = i % c.width, y = (i / c.width) | 0;
    own.add(y * cw + x); own.add(y * cw + x + 1); own.add((y + 1) * cw + x); own.add((y + 1) * cw + x + 1);
  }
  let path = 0, banks = 0;
  for (const [q, v] of moved) {
    const d = Math.abs(v - c.corners[q]);
    if (own.has(q)) path += d; else banks += d;
  }
  return Math.trunc((path * COST_PER_STEP * BANK_COST_DIVISOR + banks * COST_PER_STEP + COST_PER_STEP_DM * BANK_COST_DIVISOR - 1) / (COST_PER_STEP_DM * BANK_COST_DIVISOR));
}

/** A path run's price (cells, bridges, grading) and why it can't be built, as the server works it out. */
export interface PathPlan { cost: number; grading: number; problem: string | null; pct: number }

export function planPath(c: City, profileId: string, surfaceId: string, cells: [number, number][]): PathPlan {
  const prof = c.profiles.find((p) => p.id === profileId);
  if (!prof) return { cost: 0, grading: 0, problem: `Unknown path type '${profileId}'`, pct: 0 };
  const pi = c.profiles.indexOf(prof) + 1;
  const surface = c.surfaceCode(prof, surfaceId);
  if (surface < 0) return { cost: 0, grading: 0, problem: `A ${prof.name.toLowerCase()} can't be built in that surface`, pct: 0 };
  const costPct = surface > 0 ? c.surfaces[surface - 1].costPct : 100;
  const pct = gradePctOf(prof.maxGradePct ?? 12, !!prof.stairs, surface > 0 ? c.surfaces[surface - 1].maxGradePct : -1);
  let cost = 0;
  for (const [x, y] of cells) {
    if (!c.inBounds(x, y)) continue;
    const i = y * c.width + x;
    if (c.isWater(i) && !c.isRiver(i)) continue;
    if (c.paths[i] !== pi || (c.pathSurfaces[i] ?? 0) !== surface)
      cost += Math.trunc((Number(prof.costPerCell) * costPct) / 100) * (c.isRiver(i) ? BRIDGE_COST_TIMES : 1);
  }
  if (cells.some(([x, y]) => !c.inBounds(x, y))) return { cost, grading: 0, problem: null, pct };
  const idx = cells.map(([x, y]) => y * c.width + x);
  // A bridge's deck clears its river by at least a step.
  const h = idx.map((i) => (c.isRiver(i) ? (c.rivers[i] + 1) * 20 : cellDm(c, i)));
  const fixed = idx.map((i) => !!c.paths[i]);
  const deck = idx.map((i) => (c.isRiver(i) ? (c.rivers[i] + 1) * 20 : -(1 << 28)));
  const floor = c.waterLevel * 20;
  const g = grade(h, fixed, floor, maxDeltaDm(pct), deck);
  if (!g) return { cost, grading: 0, problem: `Too steep: the paths it joins need a ${neededPct(h, fixed)}% climb; this climbs at most ${pct}%`, pct };
  const moved = shape(c, idx, g, (i) => !!c.buildingAt[i] || c.isWater(i) || !!c.paths[i], floor);
  const grading = earthCost(c, idx, moved);
  return { cost: cost + grading, grading, problem: null, pct };
}

/** A building's pad: the steps of earth it moves, or null when the lot rises more than a pad can level. */
export function padSteps(c: City, x0: number, y0: number, w: number, h: number): number | null {
  let lo = Infinity, hi = -Infinity, sum = 0;
  for (let y = y0; y < y0 + h; y++)
    for (let x = x0; x < x0 + w; x++) {
      if (!c.inBounds(x, y)) return 0;
      const v = c.heights[y * c.width + x];
      lo = Math.min(lo, v); hi = Math.max(hi, v); sum += v;
    }
  if (hi - lo > MAX_PAD_RISE) return null;
  const n = w * h, level = Math.max(c.waterLevel, Math.floor((2 * sum + n) / (2 * n)));
  let steps = 0;
  for (let y = y0; y < y0 + h; y++) for (let x = x0; x < x0 + w; x++) steps += Math.abs(c.heights[y * c.width + x] - level);
  return steps;
}

/** Cells of a straight line at any angle, snapped to the grid: the same integer steps as World.LineCells. */
export function lineCells(x0: number, y0: number, x1: number, y1: number): [number, number][] {
  const cells: [number, number][] = [[x0, y0]];
  const n = 4 * (Math.abs(x1 - x0) + Math.abs(y1 - y0)) + 1;
  const off = 1 << 20;
  const round = (num: number) => Math.floor((2 * num + n) / (2 * n)) - off;
  let lx = x0;
  let ly = y0;
  for (let k = 1; k <= n; k++) {
    const x = round((n - k) * (x0 + off) + k * (x1 + off));
    const y = round((n - k) * (y0 + off) + k * (y1 + off));
    while (lx !== x || ly !== y) {
      if (lx !== x) lx += Math.sign(x - lx);
      else ly += Math.sign(y - ly);
      cells.push([lx, ly]);
    }
  }
  return cells;
}

/** Points along a strip in cells (a cell's centre is x + 0.5), about four to a cell. Mirrors RoadShape.Points. */
export function stripPoints(s: Strip): [number, number][] {
  const x0 = s.x0 + 0.5, y0 = s.y0 + 0.5, x1 = s.x1 + 0.5, y1 = s.y1 + 0.5;
  const cx = s.curved ? s.cx + 0.5 : (x0 + x1) / 2, cy = s.curved ? s.cy + 0.5 : (y0 + y1) / 2;
  const len = Math.hypot(cx - x0, cy - y0) + Math.hypot(x1 - cx, y1 - cy);
  const n = Math.max(2, Math.ceil(len * 4));
  const pts: [number, number][] = [];
  for (let k = 0; k <= n; k++) {
    const t = k / n, a = (1 - t) * (1 - t), b = 2 * (1 - t) * t, c = t * t;
    pts.push([a * x0 + b * cx + c * x1, a * y0 + b * cy + c * y1]);
  }
  return pts;
}

export function lCells(x0: number, y0: number, x1: number, y1: number): [number, number][] {
  const cells: [number, number][] = [[x0, y0]];
  const sx = Math.sign(x1 - x0);
  const sy = Math.sign(y1 - y0);
  let x = x0;
  let y = y0;
  while (x !== x1) { x += sx; cells.push([x, y]); }
  while (y !== y1) { y += sy; cells.push([x, y]); }
  return cells;
}
