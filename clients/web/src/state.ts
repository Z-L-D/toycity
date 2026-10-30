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
  zones = new Uint8Array(0);
  paths = new Uint16Array(0);
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
  loaded = false;

  applyFull(f: Msg) {
    this.width = f.width;
    this.height = f.height;
    this.waterLevel = f.waterLevel;
    this.seed = f.seed;
    const n = this.width * this.height;
    this.heights = new Uint8Array(f.heights);
    this.zones = new Uint8Array(f.zones);
    const pb = new Uint8Array(f.paths);
    this.paths = new Uint16Array(n);
    for (let i = 0; i < n; i++) this.paths[i] = pb[2 * i] | (pb[2 * i + 1] << 8);
    this.powerLines = new Uint8Array(f.powerLines);
    this.waterPipes = new Uint8Array(f.waterPipes);
    this.profiles = f.catalog.profiles;
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
    this.terrainVersion++;
    this.loaded = true;
  }

  applyDelta(d: Msg) {
    let terrain = false;
    for (const c of d.cells as Msg[]) {
      const i = c.index;
      if (this.heights[i] !== c.height) terrain = true;
      this.heights[i] = c.height;
      this.zones[i] = c.zone;
      this.paths[i] = c.path;
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
    return this.heights[i] < this.waterLevel;
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
