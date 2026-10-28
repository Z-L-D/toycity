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
    if (terrain) this.terrainVersion++;
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
