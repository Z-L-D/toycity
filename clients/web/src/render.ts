import { City, lCells, SIDE_DX, SIDE_DY, stripPoints } from './state';
import type { Building, BuildingType } from './state';

export interface Preview {
  kind: 'line' | 'rect' | 'footprint' | 'cell' | 'cells';
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  ok: boolean;
  color: string;
  verticalFirst?: boolean;
  /** Transit stops: the walking reach to outline around the footprint, in cells. */
  catchment?: number;
  /** Roadside stops: the kerb of the road cell at x0, y0 the stop goes on (0 north, 1 east, 2 south, 3 west). */
  side?: number;
  /** kind 'cells': the cells to fill, with an optional colour each. */
  cells?: [number, number][];
  colors?: string[];
}

export interface OverlayView {
  kind: number;
  values: Uint8Array;
  scheme: 'good' | 'bad' | 'diverging' | 'binary';
}

const ZONE_COLORS = ['', '#4caf50', '#4a7fe0', '#e0b030'];
const CIVIC_LABEL: Record<string, string> = {
  power: '⚡', water: '💧', garbage: '🗑', police: 'P', fire: 'F', health: '+', education: 'E', park: '',
};
const TRANSIT_LABEL: Record<string, string> = { bus: '🚌', tram: '🚋', rail: '🚆' };

/** Pixels to a cell in the ground layer. */
const TERRAIN_DETAIL = 4;

function terrainColor(h: number, water: number): [number, number, number] {
  if (h < water) {
    const d = Math.min(1, (water - h) / 6);
    return [Math.round(60 - 30 * d), Math.round(130 - 50 * d), Math.round(200 - 40 * d)];
  }
  const t = Math.min(1, (h - water) / 18);
  // Lowland green to upland olive to rocky brown.
  if (t < 0.6) {
    const k = t / 0.6;
    return [Math.round(110 + 40 * k), Math.round(170 - 20 * k), Math.round(80 - 10 * k)];
  }
  const k = (t - 0.6) / 0.4;
  return [Math.round(150 + 20 * k), Math.round(150 - 30 * k), Math.round(70 + 20 * k)];
}

function overlayColor(v: number, scheme: OverlayView['scheme']): string | null {
  if (scheme === 'binary') return v === 0 ? 'rgba(200,40,40,0.45)' : v < 200 ? 'rgba(240,170,30,0.5)' : 'rgba(60,200,90,0.35)';
  if (v === 0 && scheme !== 'diverging') return null;
  if (scheme === 'diverging') {
    const d = v - 100; // desirability is sent as value + 100
    if (d >= 0) return `rgba(40,${150 + Math.min(105, d)},70,${Math.min(0.7, 0.15 + d / 120)})`;
    return `rgba(220,50,40,${Math.min(0.7, 0.15 + -d / 120)})`;
  }
  const a = 0.15 + (v / 255) * 0.6;
  if (scheme === 'bad') return `rgba(${180 + (v * 75) / 255},${120 - (v * 100) / 255},30,${a})`;
  return `rgba(40,${120 + (v * 135) / 255},${200 - (v * 120) / 255},${a})`;
}

function shade(hex: string, f: number): string {
  const n = parseInt(hex.slice(1), 16);
  const r = Math.min(255, Math.round(((n >> 16) & 255) * f));
  const g = Math.min(255, Math.round(((n >> 8) & 255) * f));
  const b = Math.min(255, Math.round((n & 255) * f));
  return `rgb(${r},${g},${b})`;
}

/** Blends two #rrggbb colours, t = 0 gives a and 1 gives b. */
function mix(a: string, b: string, t: number): string {
  const p = parseInt(a.slice(1), 16);
  const q = parseInt(b.slice(1), 16);
  const ch = (sh: number) => Math.round(((p >> sh) & 255) * (1 - t) + ((q >> sh) & 255) * t);
  return '#' + ((ch(16) << 16) | (ch(8) << 8) | ch(0)).toString(16).padStart(6, '0');
}

/** Draws the city top-down, SimCity 1 style, on a single canvas. */
export interface AgentTrip {
  id: number;
  departMinute: number;
  minutes: number;
  byTransit: boolean;
  cells: number[];
}

/** Evening trips leave this many minutes after the morning ones (7:45 becomes 17:00). */
export const EVENING_SHIFT = 9 * 60 + 15;
const RUSH_START = 7 * 60 + 30, RUSH_LENGTH = 150, RUSH_SECONDS = 15;

/** The map's commuter clock: minutes after midnight, cycling through the morning then the evening rush. */
export function rushClock(now = performance.now()): { minute: number; evening: boolean } {
  const cycle = (now / 1000) % (2 * RUSH_SECONDS);
  const evening = cycle >= RUSH_SECONDS;
  const m = ((cycle % RUSH_SECONDS) / RUSH_SECONDS) * RUSH_LENGTH;
  return { minute: RUSH_START + (evening ? EVENING_SHIFT : 0) + m, evening };
}

/** Where a commuter is on their trip at a moment of the rush clock: before leaving, on the way, or arrived. */
export function agentAt(t: AgentTrip, clock: { minute: number; evening: boolean }, width: number): { x: number; y: number; state: 'before' | 'moving' | 'after' } | null {
  const n = t.cells.length;
  if (n === 0) return null;
  const leave = clock.evening ? t.departMinute + EVENING_SHIFT : t.departMinute;
  const f = Math.max(0, Math.min(1, (clock.minute - leave) / Math.max(1, t.minutes)));
  const state = clock.minute < leave ? 'before' : f >= 1 ? 'after' : 'moving';
  const along = (clock.evening ? 1 - f : f) * (n - 1);
  const k = Math.floor(along);
  const a = t.cells[k], b = t.cells[Math.min(n - 1, k + 1)];
  const u = along - k;
  return { x: (a % width) * (1 - u) + (b % width) * u, y: Math.floor(a / width) * (1 - u) + Math.floor(b / width) * u, state };
}

/** A lot as the bus company's view draws it. */
export interface CompanyLot { buildingId: number; kind: string; x: number; y: number; w: number; h: number; leased: boolean; closed: boolean; reach: number; side: number }
/** What the company view draws: its lots, and on its rail lines the signals and how much of each hour the track is held. */
export interface CompanyMap {
  lots: CompanyLot[]; catchment: number; showCatchment: boolean; selected: number | null;
  signals?: { x: number; y: number; kind: number }[];
  reserved?: { x: number; y: number; percent: number }[];
}

export class Renderer {
  readonly ctx: CanvasRenderingContext2D;
  scale = 8;
  offX = 0;
  offY = 0;
  underground = false;
  overlay: OverlayView | null = null;
  preview: Preview | null = null;
  selected: { x: number; y: number } | null = null;
  hover: { x: number; y: number } | null = null;
  /** Followed commuters from the server, drawn as dots moving along their routes. */
  agents: AgentTrip[] = [];
  showAgents = true;
  /** An area pulsing for a few seconds: what an alert or petition is about. */
  flash: { x0: number; y0: number; x1: number; y1: number; until: number } | null = null;
  /** The followed citizen, if any: drawn larger with a ring. */
  followed: number | null = null;
  /** The bus company's running lines: drawn along their streets, with their buses going back and forth. */
  busLines: { color: string; cells: number[]; buses: number; running: boolean }[] = [];
  /** In the city view the routes show only when the Layers tab asks for them. */
  showRoutes = false;
  /**
   * Set while the bus company's own view is up: the city is dimmed and the company's lots stand out, with the
   * walking reach of its stops when asked for.
   */
  company: CompanyMap | null = null;
  /** What the neighbouring cities show across each side: roads, power lines and pipes, with their names. */
  borders: { side: number; name: string; paths: number[]; power: number[]; pipes: number[] }[] = [];
  private terrain: HTMLCanvasElement = document.createElement('canvas');
  private terrainVersion = -1;
  private blink = false;

  /** True while something on the map moves, so the frame loop keeps drawing. */
  get animating(): boolean {
    const d = this.city.stats.disasters as unknown[] | undefined;
    if (d && d.length > 0) return true;
    if (this.flash && this.flash.until > performance.now()) return true;
    if (this.followed !== null) return true;
    if ((this.company || this.showRoutes) && this.busLines.some((l) => l.buses > 0) && this.city.stats.speed > 0) return true;
    return this.showAgents && this.agents.length > 0 && this.city.stats.speed > 0;
  }

  constructor(readonly canvas: HTMLCanvasElement, readonly city: City) {
    this.ctx = canvas.getContext('2d')!;
    setInterval(() => (this.blink = !this.blink), 600);
  }

  resize() {
    const dpr = window.devicePixelRatio || 1;
    const r = this.canvas.getBoundingClientRect();
    this.canvas.width = Math.round(r.width * dpr);
    this.canvas.height = Math.round(r.height * dpr);
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  centerOn(x: number, y: number) {
    const r = this.canvas.getBoundingClientRect();
    this.offX = r.width / 2 - (x + 0.5) * this.scale;
    this.offY = r.height / 2 - (y + 0.5) * this.scale;
  }

  zoomAt(sx: number, sy: number, factor: number) {
    const ns = Math.max(2, Math.min(48, this.scale * factor));
    const cx = (sx - this.offX) / this.scale;
    const cy = (sy - this.offY) / this.scale;
    this.scale = ns;
    this.offX = sx - cx * ns;
    this.offY = sy - cy * ns;
  }

  /** Where in its cell a screen point falls, 0..1 across and down. */
  cellFrac(sx: number, sy: number): { x: number; y: number } {
    const fx = (sx - this.offX) / this.scale, fy = (sy - this.offY) / this.scale;
    return { x: fx - Math.floor(fx), y: fy - Math.floor(fy) };
  }

  cellAt(sx: number, sy: number): { x: number; y: number } {
    return { x: Math.floor((sx - this.offX) / this.scale), y: Math.floor((sy - this.offY) / this.scale) };
  }

  /**
   * The ground layer, four pixels to a cell: heights blend between cell centres so hills shade smoothly, and the
   * shore is drawn where the blend of wet and dry cells crosses half, so coasts and lakes curve instead of stepping
   * (part 9). At a cell's centre the picture still agrees with the sim: water where the cell is water.
   */
  private rebuildTerrain() {
    const c = this.city;
    const K = TERRAIN_DETAIL;
    const W = c.width * K, H = c.height * K;
    this.terrain.width = W;
    this.terrain.height = H;
    const tctx = this.terrain.getContext('2d')!;
    const img = tctx.createImageData(W, H);
    const at = (x: number, y: number) => c.heights[Math.min(c.height - 1, Math.max(0, y)) * c.width + Math.min(c.width - 1, Math.max(0, x))];
    // Blends a per-cell value at a point measured in cells, between the four nearest centres.
    const blend = (f: (x: number, y: number) => number, fx: number, fy: number) => {
      const gx = fx - 0.5, gy = fy - 0.5;
      const x = Math.floor(gx), y = Math.floor(gy), u = gx - x, v = gy - y;
      return (f(x, y) * (1 - u) + f(x + 1, y) * u) * (1 - v) + (f(x, y + 1) * (1 - u) + f(x + 1, y + 1) * u) * v;
    };
    const wet = (x: number, y: number) => (at(x, y) < c.waterLevel ? 1 : 0);
    for (let py = 0; py < H; py++)
      for (let px = 0; px < W; px++) {
        const fx = (px + 0.5) / K, fy = (py + 0.5) / K;
        const h = blend(at, fx, fy);
        const water = blend(wet, fx, fy) > 0.5;
        // On the land side of a shore the blended height can dip under the waterline: keep it on the shore.
        let [r, g, b] = terrainColor(water ? Math.min(h, c.waterLevel - 0.01) : Math.max(h, c.waterLevel), c.waterLevel);
        if (!water) {
          // Hillshade: brighter on slopes facing north-west.
          const d = h - (blend(at, fx - 1 / K, fy) + blend(at, fx, fy - 1 / K)) / 2;
          r += d * 8 * K; g += d * 8 * K; b += d * 6 * K;
        }
        const o = 4 * (py * W + px);
        img.data[o] = r;
        img.data[o + 1] = g;
        img.data[o + 2] = b;
        img.data[o + 3] = 255;
      }
    tctx.putImageData(img, 0, 0);
    this.terrainVersion = c.terrainVersion;
  }

  draw() {
    const c = this.city;
    const ctx = this.ctx;
    const r = this.canvas.getBoundingClientRect();
    ctx.fillStyle = '#1b2230';
    ctx.fillRect(0, 0, r.width, r.height);
    if (!c.loaded) return;
    if (this.terrainVersion !== c.terrainVersion) this.rebuildTerrain();

    const s = this.scale;
    // An earthquake shakes the view for its first day or two.
    const quake = ((c.stats.disasters ?? []) as { kind: number; daysLeft: number }[]).some((d) => d.kind === 2 && d.daysLeft >= 2);
    const shake = quake ? { x: Math.sin(performance.now() / 23) * 4, y: Math.cos(performance.now() / 31) * 3 } : { x: 0, y: 0 };
    ctx.save();
    ctx.translate(shake.x, shake.y);
    ctx.imageSmoothingEnabled = false;
    ctx.globalAlpha = this.underground ? 0.35 : 1;
    ctx.drawImage(this.terrain, this.offX, this.offY, c.width * s, c.height * s);
    ctx.globalAlpha = 1;

    const x0 = Math.max(0, Math.floor(-this.offX / s));
    const y0 = Math.max(0, Math.floor(-this.offY / s));
    const x1 = Math.min(c.width - 1, Math.ceil((r.width - this.offX) / s));
    const y1 = Math.min(c.height - 1, Math.ceil((r.height - this.offY) / s));
    const px = (x: number) => this.offX + x * s;
    const py = (y: number) => this.offY + y * s;

    if (this.underground) {
      this.drawPipes(x0, y0, x1, y1, px, py);
    } else {
      this.drawZones(x0, y0, x1, y1, px, py);
      this.drawPaths(x0, y0, x1, y1, px, py);
      this.drawBuildings(x0, y0, x1, y1, px, py);
      this.drawPowerLines(x0, y0, x1, y1, px, py);
      this.drawDisasters(px, py);
      this.drawBorders(px, py);
      if (this.company) this.drawCompany(px, py, r.width, r.height);
      if (this.company || this.showRoutes) this.drawBusLines(px, py);
      if (!this.company) this.drawAgents(px, py, x0, y0, x1, y1);
    }

    if (this.overlay) {
      const v = this.overlay.values;
      for (let y = y0; y <= y1; y++)
        for (let x = x0; x <= x1; x++) {
          const col = overlayColor(v[y * c.width + x], this.overlay.scheme);
          if (!col) continue;
          ctx.fillStyle = col;
          ctx.fillRect(px(x), py(y), s, s);
        }
    }

    if (s >= 14) {
      ctx.strokeStyle = 'rgba(0,0,0,0.08)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      for (let x = x0; x <= x1 + 1; x++) { ctx.moveTo(px(x), py(y0)); ctx.lineTo(px(x), py(y1 + 1)); }
      for (let y = y0; y <= y1 + 1; y++) { ctx.moveTo(px(x0), py(y)); ctx.lineTo(px(x1 + 1), py(y)); }
      ctx.stroke();
    }

    if (s >= 9 && !this.company) this.drawStreetNames(x0, y0, x1, y1, px, py);
    this.drawFlash(px, py);
    this.drawPreview(px, py);
    if (this.selected) {
      ctx.strokeStyle = '#fff';
      ctx.lineWidth = 2;
      ctx.strokeRect(px(this.selected.x) + 1, py(this.selected.y) + 1, s - 2, s - 2);
    }
    ctx.restore();
  }

  /**
   * Street names along the roads, as the news and petitions use them: one label for each straight run that's on
   * screen and long enough to hold it. East-west runs read across; north-south avenues read up the road.
   */
  private drawStreetNames(x0: number, y0: number, x1: number, y1: number, px: (x: number) => number, py: (y: number) => number) {
    const c = this.city;
    const ctx = this.ctx;
    const s = this.scale;
    ctx.save();
    ctx.font = `600 ${Math.round(Math.max(10, Math.min(15, s * 0.5)))}px system-ui, sans-serif`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.lineJoin = 'round';
    ctx.lineWidth = 3;
    ctx.strokeStyle = 'rgba(20,24,32,0.85)';
    ctx.fillStyle = '#f4f1e6';
    const label = (text: string, cx: number, cy: number, cells: number, vertical: boolean) => {
      const w = ctx.measureText(text).width;
      if (w + s > cells * s) return;
      ctx.save();
      ctx.translate(cx, cy);
      if (vertical) ctx.rotate(-Math.PI / 2);
      ctx.strokeText(text, 0, 0);
      ctx.fillText(text, 0, 0);
      ctx.restore();
    };
    const path = (x: number, y: number) => !!c.paths[y * c.width + x];
    for (let y = y0; y <= y1; y++) {
      let start = -1;
      for (let x = x0; x <= x1 + 1; x++) {
        const on = x <= x1 && path(x, y) && c.roadEW(x, y);
        if (on && start < 0) start = x;
        if (!on && start >= 0) {
          label(c.roadName(false, y), px(start) + ((x - start) * s) / 2, py(y) + s / 2, x - start, false);
          start = -1;
        }
      }
    }
    for (let x = x0; x <= x1; x++) {
      let start = -1;
      for (let y = y0; y <= y1 + 1; y++) {
        const on = y <= y1 && path(x, y) && c.roadNS(x, y);  // through crossings, so an avenue gets one label
        if (on && start < 0) start = y;
        if (!on && start >= 0) {
          label(c.roadName(true, x), px(x) + s / 2, py(start) + ((y - start) * s) / 2, y - start, true);
          start = -1;
        }
      }
    }
    ctx.restore();
  }

  private drawFlash(px: (x: number) => number, py: (y: number) => number) {
    const f = this.flash;
    if (!f) return;
    const now = performance.now();
    if (now > f.until) { this.flash = null; return; }
    const ctx = this.ctx;
    const s = this.scale;
    // A slow pulse, fading out over the last second.
    const pulse = 0.5 + 0.5 * Math.sin(now / 180);
    const fade = Math.min(1, (f.until - now) / 1000);
    const x = px(f.x0), y = py(f.y0), w = (f.x1 - f.x0 + 1) * s, h = (f.y1 - f.y0 + 1) * s;
    ctx.fillStyle = `rgba(255, 220, 90, ${(0.1 + 0.2 * pulse) * fade})`;
    ctx.fillRect(x, y, w, h);
    ctx.strokeStyle = `rgba(255, 230, 120, ${(0.5 + 0.5 * pulse) * fade})`;
    ctx.lineWidth = 2;
    ctx.setLineDash([6, 4]);
    ctx.strokeRect(x + 1, y + 1, w - 2, h - 2);
    ctx.setLineDash([]);
  }

  /**
   * The commuters on the map. The view runs its own clock through the two rush hours (about fifteen seconds
   * each), whatever the game speed: each dot leaves home at its own time, follows its route cell by cell and
   * reaches work after its door-to-door minutes; in the evening it goes back the other way.
   */
  private drawAgents(px: (x: number) => number, py: (y: number) => number, x0: number, y0: number, x1: number, y1: number) {
    const ctx = this.ctx;
    const s = this.scale;
    const clock = rushClock();
    const r = Math.max(1.2, Math.min(3, s / 5));
    for (const t of this.showAgents ? this.agents : []) {
      if (t.id === this.followed) continue;
      const p = agentAt(t, clock, this.city.width);
      if (!p || p.state !== 'moving') continue;
      if (p.x < x0 - 1 || p.x > x1 + 1 || p.y < y0 - 1 || p.y > y1 + 1) continue;
      ctx.fillStyle = t.byTransit ? '#5ad1ff' : '#ffd15a';
      ctx.beginPath();
      ctx.arc(px(p.x + 0.5), py(p.y + 0.5), r, 0, Math.PI * 2);
      ctx.fill();
    }
    this.drawFollowed(px, py, clock, r);
  }

  /** The company's view: the city dimmed, its stops' walking reach, and its lots and the free ones marked. */
  private drawCompany(px: (x: number) => number, py: (y: number) => number, w: number, h: number) {
    const ctx = this.ctx;
    const s = this.scale;
    const co = this.company!;
    ctx.fillStyle = 'rgba(12,16,26,0.55)';
    ctx.fillRect(0, 0, w, h);
    if (co.showCatchment) {
      ctx.fillStyle = 'rgba(255,204,77,0.10)';
      ctx.strokeStyle = 'rgba(255,204,77,0.45)';
      ctx.lineWidth = 1;
      for (const l of co.lots) {
        if (!l.leased || !(l.kind === 'bus-stop' || l.kind === 'tram-stop' || l.kind === 'rail-station')) continue;
        const r = l.reach || co.catchment;
        ctx.fillRect(px(l.x - r), py(l.y - r), (l.w + 2 * r) * s, (l.h + 2 * r) * s);
        ctx.strokeRect(px(l.x - r) + 0.5, py(l.y - r) + 0.5, (l.w + 2 * r) * s - 1, (l.h + 2 * r) * s - 1);
      }
    }
    // Track the signals hold: green with room to spare, amber nearly full, red asked for more than it can take.
    for (const c of co.reserved ?? []) {
      ctx.fillStyle = c.percent > 100 ? 'rgba(230,80,70,0.55)' : c.percent > 70 ? 'rgba(240,180,60,0.5)' : 'rgba(90,200,120,0.4)';
      ctx.fillRect(px(c.x), py(c.y), s, s);
    }
    for (const sg of co.signals ?? []) {
      const r = Math.max(3, s * 0.35);
      ctx.beginPath();
      ctx.arc(px(sg.x) + s / 2, py(sg.y) + s / 2, r, 0, Math.PI * 2);
      ctx.fillStyle = sg.kind === 2 ? '#5ad1ff' : '#ff6b5e';
      ctx.fill();
      ctx.strokeStyle = '#fff';
      ctx.lineWidth = 1.5;
      ctx.stroke();
      if (s >= 12) {
        ctx.fillStyle = '#1b2230';
        ctx.font = `bold ${Math.round(r * 1.2)}px system-ui`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(sg.kind === 2 ? 'P' : 'B', px(sg.x) + s / 2, py(sg.y) + s / 2);
      }
    }
    for (const l of co.lots) {
      let x = px(l.x), y = py(l.y), bw = l.w * s, bh = l.h * s;
      // A roadside stop is marked on its kerb, not over the road.
      if (l.side >= 0) [x, y, bw, bh] = kerbRect(x - 1, y - 1, s + 2, l.side, 1, Math.max(5, s * 0.4));
      ctx.lineWidth = l.side >= 0 ? 1 : Math.max(2, s / 6);
      if (l.leased) {
        ctx.fillStyle = l.closed ? '#8a4a44' : '#ffcc4d';
        ctx.fillRect(x, y, bw, bh);
        ctx.strokeStyle = '#fff';
      } else {
        ctx.fillStyle = 'rgba(255,255,255,0.15)';
        ctx.fillRect(x, y, bw, bh);
        ctx.strokeStyle = 'rgba(255,255,255,0.7)';
        ctx.setLineDash([3, 3]);
      }
      ctx.strokeRect(x + 1, y + 1, bw - 2, bh - 2);
      ctx.setLineDash([]);
      if (co.selected === l.buildingId) {
        ctx.strokeStyle = '#5ad1ff';
        ctx.lineWidth = 2;
        ctx.strokeRect(x - 3, y - 3, bw + 6, bh + 6);
      }
      if (s >= 10 && l.kind.endsWith('-depot')) {
        ctx.fillStyle = l.leased ? '#1b2230' : '#fff';
        ctx.font = `${Math.round(s * 0.8)}px system-ui`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText('D', x + bw / 2, y + bh / 2);
      }
    }
  }

  /**
   * Bus lines along their streets, and their buses: each runs the line end to end and back on a cosmetic clock
   * (half a minute a round trip), spaced evenly. They stand still while the game is paused.
   */
  private drawBusLines(px: (x: number) => number, py: (y: number) => number) {
    const ctx = this.ctx;
    const s = this.scale;
    const w = this.city.width;
    const now = this.city.stats.speed > 0 ? performance.now() : (this.pausedAt ??= performance.now());
    if (this.city.stats.speed > 0) this.pausedAt = null;
    for (const line of this.busLines) {
      const cells = line.cells;
      if (cells.length < 2) continue;
      ctx.strokeStyle = line.color + 'aa';
      ctx.lineWidth = Math.max(2, s / 3);
      ctx.lineJoin = 'round';
      // A line that isn't running (no buses, every bus in the workshop) is drawn dashed, without buses.
      if (!line.running) ctx.setLineDash([s, s]);
      ctx.beginPath();
      cells.forEach((c, i) => (i ? ctx.lineTo : ctx.moveTo).call(ctx, px((c % w) + 0.5), py(Math.floor(c / w) + 0.5)));
      ctx.stroke();
      ctx.setLineDash([]);
      if (!line.running) continue;
      const last = cells.length - 1;
      const size = Math.max(3, s * 0.7);
      for (let b = 0; b < line.buses; b++) {
        const phase = (now / 30000 + b / line.buses) % 1;
        let u = phase * 2 * last;
        if (u > last) u = 2 * last - u;
        const i = Math.min(last - 1, Math.floor(u));
        const f = u - i;
        const ax = cells[i] % w, ay = Math.floor(cells[i] / w), bx = cells[i + 1] % w, by = Math.floor(cells[i + 1] / w);
        const x = px(ax + (bx - ax) * f + 0.5), y = py(ay + (by - ay) * f + 0.5);
        ctx.fillStyle = line.color;
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 1;
        ctx.fillRect(x - size / 2, y - size / 2, size, size);
        ctx.strokeRect(x - size / 2, y - size / 2, size, size);
      }
    }
  }
  private pausedAt: number | null = null;

  /** Where the followed citizen is now, and what they're doing; null when nobody is followed or they aren't drawn. */
  /**
   * Just past each edge with a neighbouring city: its roads as stubs (a road of yours meets it there), its power
   * lines and pipes as short marks, and its name.
   */
  private drawBorders(px: (x: number) => number, py: (y: number) => number) {
    const c = this.city, ctx = this.ctx, s = this.scale;
    const depth = Math.max(6, s * 1.5);
    for (const b of this.borders) {
      const along = b.side === 0 || b.side === 2;
      // Where cell k of this side is on screen, and which way is outward.
      const at = (k: number): [number, number, number, number] => {
        if (b.side === 0) return [px(k), py(0) - depth, s, depth];
        if (b.side === 2) return [px(k), py(c.height), s, depth];
        if (b.side === 1) return [px(c.width), py(k), depth, s];
        return [px(0) - depth, py(k), depth, s];
      };
      ctx.fillStyle = 'rgba(255,255,255,0.06)';
      const len = along ? c.width : c.height;
      const [ax, ay] = at(0), [bx, by, bw, bh] = at(len - 1);
      ctx.fillRect(ax, ay, bx + bw - ax, by + bh - ay);
      for (let j = 0; j + 1 < b.paths.length; j += 2) {
        const [x, y, w, h] = at(b.paths[j]);
        ctx.fillStyle = '#8a8f99';
        ctx.fillRect(x, y, w, h);
        ctx.strokeStyle = '#f0c030';
        ctx.lineWidth = 1;
        ctx.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1);
      }
      const mark = (ks: number[], colour: string, off: number) => {
        ctx.fillStyle = colour;
        for (const k of ks) {
          const [x, y, w, h] = at(k);
          if (along) ctx.fillRect(x + w / 2 - 1 + off, y, 2, h); else ctx.fillRect(x, y + h / 2 - 1 + off, w, 2);
        }
      };
      mark(b.power, '#ffd84a', -2);
      mark(b.pipes, '#5ec8ff', 2);
      const [mx, my, mw, mh] = at(Math.floor(len / 2));
      ctx.font = 'bold 12px system-ui, sans-serif';
      ctx.fillStyle = '#ffffffcc';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      const label = `${['↑', '→', '↓', '←'][b.side]} ${b.name}`;
      if (along) ctx.fillText(label, mx + mw / 2, b.side === 0 ? my - 10 : my + mh + 10);
      else {
        ctx.save();
        ctx.translate(b.side === 1 ? mx + mw + 10 : mx - 10, my + mh / 2);
        ctx.rotate(b.side === 1 ? Math.PI / 2 : -Math.PI / 2);
        ctx.fillText(label, 0, 0);
        ctx.restore();
      }
      ctx.textAlign = 'start';
    }
  }

  followedNow(): { x: number; y: number; status: string; minute: number } | null {
    const t = this.agents.find((a) => a.id === this.followed);
    if (!t) return null;
    const clock = rushClock();
    const p = agentAt(t, clock, this.city.width);
    if (!p) return null;
    const how = t.byTransit ? 'riding transit' : 'driving';
    const status = clock.evening
      ? p.state === 'before' ? 'still at work' : p.state === 'moving' ? `${how} home` : 'home for the evening'
      : p.state === 'before' ? 'at home, getting ready' : p.state === 'moving' ? `${how} to work` : 'at work';
    return { x: p.x, y: p.y, status, minute: clock.minute };
  }

  /** The followed citizen: their whole route, and a ringed dot wherever they are, even at home or at work. */
  private drawFollowed(px: (x: number) => number, py: (y: number) => number, clock: { minute: number; evening: boolean }, r: number) {
    const t = this.agents.find((a) => a.id === this.followed);
    if (!t || t.cells.length === 0) return;
    const ctx = this.ctx;
    const w = this.city.width;
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.55)';
    ctx.lineWidth = Math.max(1.5, this.scale / 6);
    ctx.setLineDash([4, 4]);
    ctx.beginPath();
    t.cells.forEach((c, k) => {
      const x = px((c % w) + 0.5), y = py(Math.floor(c / w) + 0.5);
      if (k === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    });
    ctx.stroke();
    ctx.setLineDash([]);
    const p = agentAt(t, clock, w)!;
    const cx = px(p.x + 0.5), cy = py(p.y + 0.5);
    const pulse = 1 + 0.25 * Math.sin(performance.now() / 200);
    ctx.fillStyle = t.byTransit ? '#5ad1ff' : '#ffd15a';
    ctx.beginPath();
    ctx.arc(cx, cy, r * 2.2, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = '#fff';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(cx, cy, r * 2.2 + 4 * pulse, 0, Math.PI * 2);
    ctx.stroke();
  }

  private drawZones(x0: number, y0: number, x1: number, y1: number, px: (x: number) => number, py: (y: number) => number) {
    const c = this.city;
    const ctx = this.ctx;
    const s = this.scale;
    for (let y = y0; y <= y1; y++)
      for (let x = x0; x <= x1; x++) {
        const i = y * c.width + x;
        // Under an arc or diagonal, the zone beside it fills the triangles the road leaves.
        const strip = c.underStrip.has(i);
        const z = strip ? c.fillZone(i) : c.zones[i];
        if (!z || c.buildingAt[i]) continue;
        if (strip) {
          ctx.fillStyle = ZONE_COLORS[Math.floor(z / 3)];
          ctx.globalAlpha = 0.28 + (z % 3) * 0.1;
          ctx.fillRect(px(x), py(y), s, s);
          ctx.globalAlpha = 1;
          continue;
        }
        const kind = Math.floor(z / 3);
        const density = z % 3;
        ctx.fillStyle = ZONE_COLORS[kind];
        ctx.globalAlpha = 0.28 + density * 0.1;
        ctx.fillRect(px(x), py(y), s, s);
        if (!c.hasRoadAccess(x, y)) {
          // No road touches this lot, so it can never grow: grey it out with a red cross-hatch.
          ctx.globalAlpha = 0.55;
          ctx.fillStyle = '#5a5a5a';
          ctx.fillRect(px(x), py(y), s, s);
          ctx.globalAlpha = 0.9;
          ctx.strokeStyle = '#e05a4a';
          ctx.lineWidth = Math.max(1, s / 12);
          ctx.beginPath();
          ctx.moveTo(px(x) + s * 0.25, py(y) + s * 0.25); ctx.lineTo(px(x) + s * 0.75, py(y) + s * 0.75);
          ctx.moveTo(px(x) + s * 0.75, py(y) + s * 0.25); ctx.lineTo(px(x) + s * 0.25, py(y) + s * 0.75);
          ctx.stroke();
          ctx.globalAlpha = 1;
          continue;
        }
        ctx.globalAlpha = 0.8;
        const inset = Math.max(1, s * 0.3);
        ctx.strokeStyle = ZONE_COLORS[kind];
        ctx.lineWidth = Math.max(1, s / 10);
        ctx.strokeRect(px(x) + inset, py(y) + inset, s - 2 * inset, s - 2 * inset);
        ctx.globalAlpha = 1;
      }
  }

  private drawPaths(x0: number, y0: number, x1: number, y1: number, px: (x: number) => number, py: (y: number) => number) {
    const c = this.city;
    const ctx = this.ctx;
    const s = this.scale;
    for (let y = y0; y <= y1; y++)
      for (let x = x0; x <= x1; x++) {
        const i = y * c.width + x;
        const p = c.profileAt(i);
        if (!p || c.underStrip.has(i)) continue;
        const w = Math.max(s * 0.3, Math.min(s, (p.widthM / 16) * s));
        const half = w / 2;
        const cx = px(x) + s / 2;
        const cy = py(y) + s / 2;
        // Rail lines only join rail; everything else joins everything but rail (no level crossings yet).
        const joins = (j: number) => !!c.paths[j] && !c.underStrip.has(j) && (c.profileAt(j)?.rail ?? false) === p.rail;
        const east = x + 1 < c.width && joins(i + 1);
        const south = y + 1 < c.height && joins(i + c.width);
        const west = x > 0 && joins(i - 1);
        const north = y > 0 && joins(i - c.width);
        ctx.fillStyle = p.color;
        ctx.fillRect(cx - half, cy - half, w, w);
        if (east) ctx.fillRect(cx, cy - half, s, w);
        if (south) ctx.fillRect(cx - half, cy, w, s);
        if (s >= 12 && p.cars && !p.tram) {
          ctx.fillStyle = p.id.startsWith('highway') ? '#e8d36a' : 'rgba(255,255,255,0.55)';
          const d = Math.max(1, s / 12);
          if (east) ctx.fillRect(cx + s * 0.2, cy - d / 2, s * 0.4, d);
          if (south) ctx.fillRect(cx - d / 2, cy + s * 0.2, d, s * 0.4);
        }
        if (p.rail || p.tram) {
          // Two rails (and sleepers on a rail line) along each joined direction.
          const gauge = Math.max(1.5, w * (p.rail ? 0.32 : 0.22));
          const lw = Math.max(1, s / 14);
          const horiz = east || west || !(north || south);
          const vert = north || south;
          if (p.rail && s >= 8) {
            ctx.fillStyle = '#3a2c20';
            for (let k = 0; k < 3; k++) {
              const o = (k / 3 - 0.33) * s;
              if (horiz) ctx.fillRect(cx + o - lw / 2, cy - gauge * 0.9, lw * 1.5, gauge * 1.8);
              if (vert) ctx.fillRect(cx - gauge * 0.9, cy + o - lw / 2, gauge * 1.8, lw * 1.5);
            }
          }
          ctx.fillStyle = p.rail ? '#c9ccd2' : 'rgba(225,225,235,0.85)';
          const lx0 = west ? px(x) : cx, lx1 = east ? px(x) + s : cx;
          const ly0 = north ? py(y) : cy, ly1 = south ? py(y) + s : cy;
          if (horiz) for (const o of [-gauge / 2, gauge / 2]) ctx.fillRect(Math.min(lx0, cx - half), cy + o - lw / 2, Math.max(lx1, cx + half) - Math.min(lx0, cx - half), lw);
          if (vert) for (const o of [-gauge / 2, gauge / 2]) ctx.fillRect(cx + o - lw / 2, Math.min(ly0, cy - half), lw, Math.max(ly1, cy + half) - Math.min(ly0, cy - half));
        }
      }
    this.drawStrips(px, py);
  }

  /** Arcs and diagonals as the line the player drew, over the cells the sim runs them on. */
  private drawStrips(px: (x: number) => number, py: (y: number) => number) {
    const c = this.city;
    const ctx = this.ctx;
    const s = this.scale;
    ctx.save();
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    for (const st of c.strips) {
      const p = c.profiles[st.path - 1];
      if (!p) continue;
      const pts = stripPoints(st);
      const trace = () => {
        ctx.beginPath();
        pts.forEach(([x, y], k) => (k ? ctx.lineTo(px(x), py(y)) : ctx.moveTo(px(x), py(y))));
      };
      const w = Math.max(s * 0.3, Math.min(s, (p.widthM / 16) * s));
      trace();
      ctx.strokeStyle = p.color;
      ctx.lineWidth = w;
      ctx.stroke();
      if (p.rail || p.tram) {
        // Two rails: a wide stroke in the rails' colour with the bed's colour down its middle.
        const gauge = Math.max(1.5, w * (p.rail ? 0.32 : 0.22));
        const lw = Math.max(1, s / 14);
        ctx.strokeStyle = p.rail ? '#c9ccd2' : 'rgba(225,225,235,0.85)';
        ctx.lineWidth = gauge + lw;
        ctx.stroke();
        ctx.strokeStyle = p.color;
        ctx.lineWidth = Math.max(0.5, gauge - lw);
        ctx.stroke();
      } else if (s >= 12 && p.cars) {
        ctx.strokeStyle = p.id.startsWith('highway') ? '#e8d36a' : 'rgba(255,255,255,0.55)';
        ctx.lineWidth = Math.max(1, s / 12);
        ctx.setLineDash([s * 0.4, s * 0.6]);
        ctx.stroke();
        ctx.setLineDash([]);
      }
    }
    ctx.restore();
  }

  private drawPowerLines(x0: number, y0: number, x1: number, y1: number, px: (x: number) => number, py: (y: number) => number) {
    const c = this.city;
    const ctx = this.ctx;
    const s = this.scale;
    ctx.strokeStyle = '#ffd84a';
    ctx.lineWidth = Math.max(1, s / 8);
    ctx.beginPath();
    for (let y = y0; y <= y1; y++)
      for (let x = x0; x <= x1; x++) {
        const i = y * c.width + x;
        if (!c.powerLines[i]) continue;
        const cx = px(x) + s / 2;
        const cy = py(y) + s / 2;
        const linked = (j: number) => c.powerLines[j] || c.buildingAt[j];
        if (x + 1 < c.width && linked(i + 1)) { ctx.moveTo(cx, cy); ctx.lineTo(cx + s, cy); }
        if (y + 1 < c.height && linked(i + c.width)) { ctx.moveTo(cx, cy); ctx.lineTo(cx, cy + s); }
        if (x > 0 && c.buildingAt[i - 1] && !c.powerLines[i - 1]) { ctx.moveTo(cx, cy); ctx.lineTo(cx - s / 2, cy); }
        if (y > 0 && c.buildingAt[i - c.width] && !c.powerLines[i - c.width]) { ctx.moveTo(cx, cy); ctx.lineTo(cx, cy - s / 2); }
        ctx.moveTo(cx + 1, cy);
        ctx.arc(cx, cy, Math.max(1, s / 7), 0, Math.PI * 2);
      }
    ctx.stroke();
  }

  private drawPipes(x0: number, y0: number, x1: number, y1: number, px: (x: number) => number, py: (y: number) => number) {
    const c = this.city;
    const ctx = this.ctx;
    const s = this.scale;
    // Faint building outlines so the player can see what the pipes serve.
    ctx.strokeStyle = 'rgba(255,255,255,0.25)';
    ctx.lineWidth = 1;
    for (const b of c.buildings.values()) {
      if (b.x > x1 || b.y > y1 || b.x + b.width < x0 || b.y + b.height < y0) continue;
      ctx.strokeRect(px(b.x) + 1, py(b.y) + 1, b.width * s - 2, b.height * s - 2);
    }
    ctx.strokeStyle = '#5ec8ff';
    ctx.lineWidth = Math.max(2, s / 4);
    ctx.lineCap = 'round';
    ctx.beginPath();
    for (let y = y0; y <= y1; y++)
      for (let x = x0; x <= x1; x++) {
        const i = y * c.width + x;
        if (!c.waterPipes[i]) continue;
        const cx = px(x) + s / 2;
        const cy = py(y) + s / 2;
        ctx.moveTo(cx, cy);
        ctx.lineTo(cx, cy);
        if (x + 1 < c.width && c.waterPipes[i + 1]) { ctx.moveTo(cx, cy); ctx.lineTo(cx + s, cy); }
        if (y + 1 < c.height && c.waterPipes[i + c.width]) { ctx.moveTo(cx, cy); ctx.lineTo(cx, cy + s); }
      }
    ctx.stroke();
    ctx.lineCap = 'butt';
  }

  private drawBuildings(x0: number, y0: number, x1: number, y1: number, px: (x: number) => number, py: (y: number) => number) {
    const c = this.city;
    const ctx = this.ctx;
    const s = this.scale;
    for (const b of c.buildings.values()) {
      if (b.x > x1 || b.y > y1 || b.x + b.width < x0 || b.y + b.height < y0) continue;
      const t = c.typeById.get(b.typeId);
      if (t?.roadside) { this.drawStop(b, t, px(b.x), py(b.y)); continue; }
      const bx = px(b.x);
      const by = py(b.y);
      const bw = b.width * s;
      const bh = b.height * s;
      const inset = Math.max(0.5, s * 0.08);
      const civic = t?.placeable ?? false;
      let color = t?.color ?? '#999';
      if (!civic) color = shade(color, 1.05 - b.density * 0.18);
      // Wear shows: run-down buildings fade, derelict ones go dark.
      if (b.look === 3) color = shade(mix(color, '#8a8070', 0.45), 0.85);
      if (b.abandoned) color = b.look === 5 ? '#4a4744' : '#6b6b6b';
      ctx.fillStyle = color;
      ctx.fillRect(bx + inset, by + inset, bw - 2 * inset, bh - 2 * inset);
      // A darker "roof edge" gives the blocks a little depth.
      ctx.fillStyle = 'rgba(0,0,0,0.22)';
      ctx.fillRect(bx + inset, by + bh - inset - Math.max(1, s * 0.15), bw - 2 * inset, Math.max(1, s * 0.15));

      if (b.constructionDaysLeft > 0) {
        ctx.save();
        ctx.beginPath();
        ctx.rect(bx + inset, by + inset, bw - 2 * inset, bh - 2 * inset);
        ctx.clip();
        ctx.strokeStyle = 'rgba(255,200,40,0.85)';
        ctx.lineWidth = Math.max(1, s / 6);
        ctx.beginPath();
        for (let k = -bh; k < bw; k += Math.max(3, s / 2)) { ctx.moveTo(bx + k, by + bh); ctx.lineTo(bx + k + bh, by); }
        ctx.stroke();
        ctx.restore();
      } else if (b.abandoned) {
        ctx.strokeStyle = '#2b2b2b';
        ctx.lineWidth = Math.max(1, s / 8);
        ctx.beginPath();
        ctx.moveTo(bx + inset * 3, by + inset * 3); ctx.lineTo(bx + bw - inset * 3, by + bh - inset * 3);
        ctx.moveTo(bx + bw - inset * 3, by + inset * 3); ctx.lineTo(bx + inset * 3, by + bh - inset * 3);
        ctx.stroke();
      } else if (!civic && s >= 10) {
        // Windows: more of them for denser buildings.
        ctx.fillStyle = 'rgba(255,255,230,0.55)';
        const n = 1 + b.density;
        const g = (bw - 2 * inset) / (n * 2 + 1);
        for (let a = 0; a < n; a++)
          for (let k = 0; k < n; k++) ctx.fillRect(bx + inset + g * (1 + 2 * a), by + inset + g * (1 + 2 * k), g, g);
      }

      if (b.graffiti > 0 && s >= 6 && b.constructionDaysLeft === 0) {
        // Tags: a few bright squiggles along the bottom wall.
        const tagColors = ['#ff4fd8', '#39e6ff', '#b6ff3b'];
        ctx.lineWidth = Math.max(1, s / 10);
        for (let k = 0; k < b.graffiti; k++) {
          ctx.strokeStyle = tagColors[(b.id + k) % 3];
          const gx = bx + inset + ((k * 0.3 + 0.1) * (bw - 2 * inset));
          const gy = by + bh - inset - Math.max(2, s * 0.3);
          const gw = Math.max(3, bw * 0.22);
          ctx.beginPath();
          ctx.moveTo(gx, gy);
          ctx.bezierCurveTo(gx + gw * 0.3, gy - s * 0.25, gx + gw * 0.6, gy + s * 0.2, gx + gw, gy - s * 0.1);
          ctx.stroke();
        }
      }
      if (b.look === 3 && s >= 10) {
        // Cracks.
        ctx.strokeStyle = 'rgba(40,30,20,0.6)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(bx + bw * 0.2, by + inset); ctx.lineTo(bx + bw * 0.32, by + bh * 0.4); ctx.lineTo(bx + bw * 0.26, by + bh * 0.62);
        ctx.stroke();
      }
      if (b.burningDays > 0) this.drawFlames(bx, by, bw, bh, b.id);

      if (civic && t) {
        const label = t.category === 'transit' ? TRANSIT_LABEL[t.transit] ?? '' : CIVIC_LABEL[t.category] ?? '';
        if (label && s * Math.min(b.width, b.height) >= 12) {
          ctx.fillStyle = t.category === 'health' || t.category === 'education' ? '#333' : '#fff';
          ctx.font = `${Math.round(Math.min(bw, bh) * 0.45)}px system-ui, sans-serif`;
          ctx.textAlign = 'center';
          ctx.textBaseline = 'middle';
          ctx.fillText(label, bx + bw / 2, by + bh / 2);
        }
        if (t.category === 'park') {
          ctx.fillStyle = '#176b30';
          const r = Math.max(1, s * 0.18);
          for (let k = 0; k < b.width * b.height * 2; k++) {
            const tx = bx + ((k * 37) % 97) / 97 * (bw - 2 * r) + r;
            const ty = by + ((k * 61) % 89) / 89 * (bh - 2 * r) + r;
            ctx.beginPath(); ctx.arc(tx, ty, r, 0, Math.PI * 2); ctx.fill();
          }
        }
      }

      if (b.constructionDaysLeft === 0 && !b.abandoned && this.blink) {
        const icon = Math.max(6, Math.min(16, s * 0.7));
        ctx.font = `${icon}px system-ui, sans-serif`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        if (!b.powered) ctx.fillText('⚡', bx + bw / 2, by + bh / 2);
        else if (!b.watered) ctx.fillText('💧', bx + bw / 2, by + bh / 2);
      }
    }
  }

  /**
   * A roadside stop: a sign along the road cell's kerb in its type's colour, longer for the bigger stops, and a "B"
   * painted in the lane in front of it.
   */
  private drawStop(b: Building, t: BuildingType, x: number, y: number) {
    const ctx = this.ctx;
    const s = this.scale;
    const len = t.capacity <= 400 ? 0.5 : t.capacity <= 900 ? 0.75 : 1;
    const [rx, ry, rw, rh] = kerbRect(x, y, s, b.side, len, Math.max(2, s * 0.2));
    ctx.fillStyle = b.constructionDaysLeft > 0 ? 'rgba(255,200,40,0.85)' : b.abandoned ? '#6b6b6b' : t.color;
    ctx.fillRect(rx, ry, rw, rh);
    ctx.strokeStyle = 'rgba(0,0,0,0.45)';
    ctx.lineWidth = 1;
    ctx.strokeRect(rx + 0.5, ry + 0.5, rw - 1, rh - 1);
    if (t.income > 0 && s >= 8) {
      // A kiosk: a white dot on the sign.
      ctx.fillStyle = '#fff';
      ctx.beginPath(); ctx.arc(rx + rw / 2, ry + rh / 2, Math.max(1, Math.min(rw, rh) * 0.3), 0, Math.PI * 2); ctx.fill();
    }
    if (s >= 9) {
      // In the half of the road nearest the stop, so stops on both kerbs each keep their own.
      const lx = x + s / 2 + SIDE_DX[b.side] * s * 0.22, ly = y + s / 2 + SIDE_DY[b.side] * s * 0.22;
      ctx.fillStyle = 'rgba(255,236,150,0.95)';
      ctx.font = `bold ${Math.round(s * 0.34)}px system-ui, sans-serif`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText('B', lx, ly);
    }
  }

  private drawFlames(bx: number, by: number, bw: number, bh: number, seed: number) {
    const ctx = this.ctx;
    const t = performance.now() / 120;
    ctx.fillStyle = 'rgba(20,10,0,0.35)';
    ctx.fillRect(bx, by, bw, bh);
    const n = Math.max(2, Math.round(bw / Math.max(4, this.scale * 0.5)));
    for (let k = 0; k < n; k++) {
      const fx = bx + (k + 0.5) * (bw / n);
      const flick = Math.sin(t + k * 1.7 + seed) * 0.5 + 0.5;
      const fh = bh * (0.45 + 0.4 * flick);
      const fw = bw / n * 0.9;
      ctx.fillStyle = `rgba(255,${120 + Math.round(flick * 80)},20,0.85)`;
      ctx.beginPath();
      ctx.moveTo(fx - fw / 2, by + bh);
      ctx.quadraticCurveTo(fx - fw / 2, by + bh - fh * 0.6, fx, by + bh - fh);
      ctx.quadraticCurveTo(fx + fw / 2, by + bh - fh * 0.6, fx + fw / 2, by + bh);
      ctx.fill();
      ctx.fillStyle = 'rgba(255,240,120,0.9)';
      ctx.beginPath();
      ctx.ellipse(fx, by + bh - fh * 0.3, fw * 0.2, fh * 0.25, 0, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  /** Tornadoes and the Clockwork Colossus, at their sub-cell positions. */
  private drawDisasters(px: (x: number) => number, py: (y: number) => number) {
    const ctx = this.ctx;
    const s = this.scale;
    const now = performance.now();
    for (const d of (this.city.stats.disasters ?? []) as { kind: number; x: number; y: number; daysLeft: number }[]) {
      const cx = px(d.x / 256);
      const cy = py(d.y / 256);
      if (d.kind === 1) {
        // A funnel of spinning ellipses, wide at the top.
        for (let k = 0; k < 7; k++) {
          const r = s * (0.4 + k * 0.28);
          const wob = Math.sin(now / 90 + k) * s * 0.15;
          ctx.strokeStyle = `rgba(200,205,215,${0.85 - k * 0.09})`;
          ctx.lineWidth = Math.max(1.5, s * 0.18);
          ctx.beginPath();
          ctx.ellipse(cx + wob, cy - k * s * 0.45, r, r * 0.35, 0, now / 120 + k, now / 120 + k + Math.PI * 1.6);
          ctx.stroke();
        }
      } else if (d.kind === 3) {
        // The Clockwork Colossus: a tin box robot two cells wide with a turning key.
        const w = s * 2;
        const step = Math.sin(now / 160) * s * 0.12;
        const x0 = px(Math.floor(d.x / 256));
        const y0 = py(Math.floor(d.y / 256));
        ctx.fillStyle = 'rgba(0,0,0,0.3)';
        ctx.beginPath(); ctx.ellipse(x0 + w / 2, y0 + w * 0.95, w * 0.55, w * 0.18, 0, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = '#9aa7b4';
        ctx.fillRect(x0 + w * 0.15, y0 + w * 0.1 + step, w * 0.7, w * 0.6);             // body
        ctx.fillStyle = '#c8d2dc';
        ctx.fillRect(x0 + w * 0.28, y0 - w * 0.28 + step, w * 0.44, w * 0.36);          // head
        ctx.fillStyle = '#ff3b30';
        ctx.fillRect(x0 + w * 0.34, y0 - w * 0.18 + step, w * 0.1, w * 0.08);           // eyes
        ctx.fillRect(x0 + w * 0.56, y0 - w * 0.18 + step, w * 0.1, w * 0.08);
        ctx.fillStyle = '#6d7884';
        ctx.fillRect(x0 + w * 0.2, y0 + w * 0.7 - step, w * 0.2, w * 0.25);            // legs
        ctx.fillRect(x0 + w * 0.6, y0 + w * 0.7 + step, w * 0.2, w * 0.25);
        ctx.fillStyle = '#e0b030';
        const turn = Math.cos(now / 200);
        ctx.fillRect(x0 + w * 0.5 - w * 0.25 * Math.abs(turn), y0 + w * 0.3 + step, w * 0.5 * Math.abs(turn), w * 0.08); // key
        ctx.fillRect(x0 + w * 0.47, y0 + w * 0.2 + step, w * 0.06, w * 0.25);
      }
    }
  }

  private drawPreview(px: (x: number) => number, py: (y: number) => number) {
    const p = this.preview;
    const ctx = this.ctx;
    const s = this.scale;
    if (!p) {
      if (this.hover) {
        ctx.strokeStyle = 'rgba(255,255,255,0.7)';
        ctx.lineWidth = 1;
        ctx.strokeRect(px(this.hover.x) + 0.5, py(this.hover.y) + 0.5, s - 1, s - 1);
      }
      return;
    }
    ctx.fillStyle = p.ok ? p.color : 'rgba(230,60,60,0.55)';
    ctx.strokeStyle = p.ok ? 'rgba(255,255,255,0.9)' : '#ff5050';
    ctx.lineWidth = 1.5;
    if (p.kind === 'cells') {
      (p.cells ?? []).forEach(([x, y], k) => {
        ctx.fillStyle = p.ok ? p.colors?.[k] ?? p.color : 'rgba(230,60,60,0.55)';
        ctx.fillRect(px(x), py(y), s, s);
      });
    } else if (p.side !== undefined) {
      const [rx, ry, rw, rh] = kerbRect(px(p.x0), py(p.y0), s, p.side, 1, Math.max(3, s * 0.28));
      ctx.fillRect(rx, ry, rw, rh);
      ctx.strokeRect(rx + 0.5, ry + 0.5, rw - 1, rh - 1);
      if (p.catchment) {
        const r = p.catchment;
        ctx.save();
        ctx.setLineDash([4, 4]);
        ctx.strokeStyle = 'rgba(255,255,255,0.75)';
        ctx.fillStyle = 'rgba(255,255,255,0.06)';
        ctx.fillRect(px(p.x0 - r), py(p.y0 - r), (1 + 2 * r) * s, (1 + 2 * r) * s);
        ctx.strokeRect(px(p.x0 - r) + 0.5, py(p.y0 - r) + 0.5, (1 + 2 * r) * s - 1, (1 + 2 * r) * s - 1);
        ctx.restore();
      }
    } else if (p.kind === 'line') {
      const cells = p.verticalFirst
        ? [...lCells(p.x0, p.y0, p.x0, p.y1), ...lCells(p.x0, p.y1, p.x1, p.y1).slice(1)]
        : lCells(p.x0, p.y0, p.x1, p.y1);
      for (const [x, y] of cells) ctx.fillRect(px(x), py(y), s, s);
    } else {
      const xa = Math.min(p.x0, p.x1);
      const ya = Math.min(p.y0, p.y1);
      const w = Math.abs(p.x1 - p.x0) + 1;
      const h = Math.abs(p.y1 - p.y0) + 1;
      ctx.fillRect(px(xa), py(ya), w * s, h * s);
      ctx.strokeRect(px(xa) + 0.5, py(ya) + 0.5, w * s - 1, h * s - 1);
      if (p.catchment) {
        const r = p.catchment;
        ctx.save();
        ctx.setLineDash([4, 4]);
        ctx.strokeStyle = 'rgba(255,255,255,0.75)';
        ctx.fillStyle = 'rgba(255,255,255,0.06)';
        ctx.fillRect(px(xa - r), py(ya - r), (w + 2 * r) * s, (h + 2 * r) * s);
        ctx.strokeRect(px(xa - r) + 0.5, py(ya - r) + 0.5, (w + 2 * r) * s - 1, (h + 2 * r) * s - 1);
        ctx.restore();
      }
    }
  }
}

/** The strip along one side of a cell at screen (x, y): len is its share of the side, thick its depth in pixels. */
export function kerbRect(x: number, y: number, s: number, side: number, len: number, thick: number): [number, number, number, number] {
  const l = s * len, off = (s - l) / 2;
  switch (side) {
    case 0: return [x + off, y, l, thick];
    case 1: return [x + s - thick, y + off, thick, l];
    case 2: return [x + off, y + s - thick, l, thick];
    default: return [x, y + off, thick, l];
  }
}
