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
}

export interface Profile {
  id: string;
  name: string;
  speedKmh: number;
  cars: boolean;
  bikes: boolean;
  pedestrians: boolean;
  oneWay: boolean;
  costPerCell: number;
  color: string;
  widthM: number;
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
}

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
    for (let y = b.y; y < b.y + b.height; y++)
      for (let x = b.x; x < b.x + b.width; x++) this.buildingAt[y * this.width + x] = b.id + 1;
  }

  private removeBuilding(id: number) {
    const b = this.buildings.get(id);
    if (!b) return;
    for (let y = b.y; y < b.y + b.height; y++)
      for (let x = b.x; x < b.x + b.width; x++) {
        const i = y * this.width + x;
        if (this.buildingAt[i] === id + 1) this.buildingAt[i] = 0;
      }
    this.buildings.delete(id);
  }

  inBounds(x: number, y: number) {
    return x >= 0 && y >= 0 && x < this.width && y < this.height;
  }

  isWater(i: number) {
    return this.heights[i] < this.waterLevel;
  }

  buildingAtCell(x: number, y: number): Building | undefined {
    if (!this.inBounds(x, y)) return undefined;
    const id = this.buildingAt[y * this.width + x];
    return id ? this.buildings.get(id - 1) : undefined;
  }

  profileAt(i: number): Profile | undefined {
    const p = this.paths[i];
    return p ? this.profiles[p - 1] : undefined;
  }
}

/** Cells of an L-shaped run, horizontal leg first (matches the server). */
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
