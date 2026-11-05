// The weather radar and station (Beta 4.1, part 3): a word-for-word port of the client model's Radar, so both clients
// paint the same echoes from the same storm cells, and the panel that shows it. Base reflectivity or base velocity at
// one of the four lowest NEXRAD tilts, still or looping over the last hour; a storm is set going by dragging on the
// scope from where it starts to where it'll be an hour later.

import type { Msg } from './net';

export const TILTS = [0.5, 0.9, 1.3, 1.8];
export const RANGE_KM = 40;
export const CELL_KM = 0.016;
const BEAM_EARTH_KM = 8495;
export const LOOP_FRAMES = 6;

export interface Cell { x: number; y: number; vx: number; vy: number; along: number; across: number; peak: number; topKm: number; kind: number; seed: number }

export function beamHeightKm(rangeKm: number, tiltDeg: number): number {
  return rangeKm * Math.sin(tiltDeg * Math.PI / 180) + rangeKm * rangeKm / (2 * BEAM_EARTH_KM);
}

function hash(ix: number, iy: number, seed: number): number {
  let h = (Math.imul(ix, 374761393) + Math.imul(iy, 668265263) + Math.imul(seed, 2246822519 | 0)) >>> 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177) >>> 0;
  return (h ^ (h >>> 16)) >>> 0;
}

export function noise(x: number, y: number, seed: number): number {
  const ix = Math.floor(x), iy = Math.floor(y);
  let fx = x - ix, fy = y - iy;
  fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
  const S = 1 / 4294967296;
  const a = hash(ix, iy, seed) * S, b = hash(ix + 1, iy, seed) * S, c = hash(ix, iy + 1, seed) * S, d = hash(ix + 1, iy + 1, seed) * S;
  return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
}

/** What one cell gives the radar at a point (km from it): dBZ (below 0 for none) and knots (negative toward it). */
export function sample(c: Cell, x: number, y: number, tiltDeg: number): [number, number] {
  const sp = Math.sqrt(c.vx * c.vx + c.vy * c.vy);
  const ux = sp > 0.01 ? c.vx / sp : 1, uy = sp > 0.01 ? c.vy / sp : 0;
  const dx = x - c.x, dy = y - c.y;
  const a = (dx * ux + dy * uy) / c.along, b = (-dx * uy + dy * ux) / c.across;
  const d2 = a * a + b * b;
  if (d2 > 1.6) return [-1, 0];
  const n1 = noise(x * 0.55, y * 0.55, c.seed), n2 = noise(x * 1.7, y * 1.7, (c.seed ^ 0x9e37) >>> 0);
  const edge = d2 + (n1 - 0.5) * 0.7 + (n2 - 0.5) * 0.25;
  if (edge > 1) return [-1, 0];
  let dbz = c.peak - 18 * Math.max(0, edge) + (n2 - 0.5) * 6 + (n1 - 0.5) * 4;
  if (c.kind === 3) dbz += 10 * Math.max(0, 1 - d2 * 5);
  const range = Math.sqrt(x * x + y * y);
  const h = beamHeightKm(range, tiltDeg), top = Math.max(0.5, c.topKm);
  if (h > top) return [-1, 0];
  dbz -= 10 * (h / top) * (h / top);
  let knots = 0;
  if (range > 0.05) {
    let vr = (c.vx * x + c.vy * y) / range;
    if (c.kind === 3) {
      const dd = Math.sqrt(dx * dx + dy * dy);
      const spin = 30 * Math.exp(-d2 * 6) * Math.min(1, d2 * 12);
      if (dd > 0.01) vr += spin * (dy * x - dx * y) / (dd * range);
    }
    knots = vr / 1.852 + (n2 - 0.5) * 8;
  }
  return [dbz, knots];
}

export function sampleAll(cells: Cell[], x: number, y: number, tiltDeg: number): [number, number] {
  let best = -1, kt = 0;
  for (const c of cells) {
    const [z, v] = sample(c, x, y, tiltDeg);
    if (z > best) { best = z; kt = v; }
  }
  return [best, kt];
}

export const DBZ_COLOURS = [0x04e9e7, 0x019ff4, 0x0300f4, 0x02fd02, 0x01c501, 0x008e00, 0xfdf802, 0xe5bc00,
  0xfd9500, 0xfd0000, 0xd40000, 0xbc0000, 0xf800fd, 0x9854c6, 0xfdfdfd];
export const VELOCITY_COLOURS = [0x02fc02, 0x01e401, 0x01c501, 0x07ac04, 0x068f03, 0x046f02, 0x788c78, 0x8c7878,
  0x6e0000, 0x8e0000, 0xac0000, 0xc50000, 0xe40000, 0xfc0000];

export function colourForDbz(dbz: number): number | null {
  return dbz < 5 ? null : DBZ_COLOURS[Math.min(DBZ_COLOURS.length - 1, Math.floor((dbz - 5) / 5))];
}

export function colourForKnots(kt: number): number {
  return VELOCITY_COLOURS[Math.max(0, Math.min(VELOCITY_COLOURS.length - 1, Math.floor(kt / 10) + 7))];
}

/** Paints the radar into an RGBA buffer `size` pixels square, the radar in the middle, RANGE_KM to each edge. */
export function paint(cells: Cell[], velocity: boolean, tiltDeg: number, size: number, rgba: Uint8ClampedArray) {
  rgba.fill(0);
  if (cells.length === 0) return;
  const km = 2 * RANGE_KM / size;
  for (let py = 0; py < size; py++)
    for (let px = 0; px < size; px++) {
      const x = (px + 0.5) * km - RANGE_KM, y = (py + 0.5) * km - RANGE_KM;
      if (x * x + y * y > RANGE_KM * RANGE_KM) continue;
      const [z, kt] = sampleAll(cells, x, y, tiltDeg);
      const col = !velocity ? colourForDbz(z) : z >= 5 ? colourForKnots(kt) : null;
      if (col == null) continue;
      const o = (py * size + px) * 4;
      rgba[o] = col >> 16; rgba[o + 1] = (col >> 8) & 255; rgba[o + 2] = col & 255; rgba[o + 3] = 235;
    }
}

export function frame(sky: Msg, minutes: number): Cell[] {
  const h = minutes / 60;
  return ((sky.cells ?? []) as Cell[]).map((c) => ({ ...c, x: c.x + c.vx * h, y: c.y + c.vy * h }));
}

const POINTS = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'];
export const compass16 = (deg: number) => POINTS[Math.round((((deg % 360) + 360) % 360) / 22.5) % 16];

export function station(sky: Msg): [string, string][] {
  const f = (v: number | undefined) => (v ?? 0).toFixed(1);
  return [
    ['Temperature', `${f(sky.tempC)} °C`],
    ['Dew point', `${f(sky.dewC)} °C`],
    ['Humidity', `${sky.humidity ?? 0} %`],
    ['Wind', (sky.windKmh ?? 0) <= 1 ? 'Calm' : `${compass16(sky.windFromDeg ?? 0)} ${sky.windKmh} km/h`],
    ['Pressure', `${f(sky.pressureHpa)} hPa`],
    ['Rain now', (sky.rateMmH ?? 0) < 0.05 ? 'None' : `${f(sky.rateMmH)} mm/h`],
    ['Since midnight', `${f(sky.todayMm)} mm`],
  ];
}

/** A storm drawn on the scope, km from the radar: the action's start cell, heading (0 east, 90 south) and speed. */
export function drawn(x0: number, y0: number, x1: number, y1: number, mapW: number, mapH: number) {
  const dx = x1 - x0, dy = y1 - y0;
  const headingDeg = ((Math.round(Math.atan2(dy, dx) * 180 / Math.PI) % 360) + 360) % 360;
  const speedKmh = Math.max(5, Math.min(120, Math.round(Math.hypot(dx, dy))));
  return { x: Math.floor(x0 / CELL_KM + mapW / 2), y: Math.floor(y0 / CELL_KM + mapH / 2), headingDeg, speedKmh };
}

const hex = (c: number) => `#${c.toString(16).padStart(6, '0')}`;
const PX = 400;

/** The radar window: the scope on the left, the station and the controls on the right. */
export class RadarPanel {
  readonly el: HTMLDivElement;
  private scope: HTMLCanvasElement;
  private off = document.createElement('canvas');
  private frames: ImageData[] = [];
  private skyKey = '';
  private frameAt = 0;
  private drag: { a: [number, number]; b: [number, number] } | null = null;
  private timer = 0;

  constructor(private getSky: () => Msg | undefined, private mapSize: () => [number, number], private send: (a: Msg, label: string) => void) {
    this.el = document.createElement('div');
    this.el.id = 'radar';
    this.el.className = 'glass hidden';
    this.el.innerHTML = `
      <div class="radar-left">
        <div class="radar-title" id="radar-title"></div>
        <canvas id="radar-scope" width="520" height="520"></canvas>
        <div class="radar-legend" id="radar-legend"></div>
      </div>
      <div class="radar-right">
        <h4>Weather station</h4>
        <dl id="radar-station"></dl>
        <h4>Radar</h4>
        <select id="radar-product"><option value="0">Base reflectivity</option><option value="1">Base velocity</option></select>
        <select id="radar-tilt">${TILTS.map((t, i) => `<option value="${i}">Tilt ${i + 1} · ${t.toFixed(1)}°</option>`).join('')}</select>
        <label><input type="checkbox" id="radar-loop"> Loop the last hour</label>
        <h4>Set a storm going</h4>
        <label><input type="checkbox" id="radar-draw"> Draw on the radar</label>
        <select id="radar-strength"><option value="1">Light</option><option value="2" selected>Heavy</option><option value="3">Severe</option></select>
        <p class="muted">Drag from where it starts to where it'll be in an hour.</p>
        <button id="radar-close">Close</button>
      </div>`;
    document.body.appendChild(this.el);
    this.scope = this.el.querySelector('#radar-scope')!;
    this.off.width = this.off.height = PX;
    for (const id of ['#radar-product', '#radar-tilt', '#radar-loop']) this.el.querySelector(id)!.addEventListener('change', () => this.refresh(true));
    this.el.querySelector('#radar-close')!.addEventListener('click', () => this.toggle(false));
    const km = (e: PointerEvent): [number, number] => {
      const r = this.scope.getBoundingClientRect();
      return [((e.clientX - r.left) / r.width - 0.5) * 2 * RANGE_KM, ((e.clientY - r.top) / r.height - 0.5) * 2 * RANGE_KM];
    };
    this.scope.addEventListener('pointerdown', (e) => { if (!this.drawing) return; const p = km(e); this.drag = { a: p, b: p }; this.scope.setPointerCapture(e.pointerId); this.draw(); });
    this.scope.addEventListener('pointermove', (e) => { if (this.drag) { this.drag.b = km(e); this.draw(); } });
    this.scope.addEventListener('pointerup', () => {
      const d = this.drag;
      this.drag = null;
      if (d && Math.hypot(d.b[0] - d.a[0], d.b[1] - d.a[1]) > 1) {
        const [w, h] = this.mapSize();
        const s = drawn(d.a[0], d.a[1], d.b[0], d.b[1], w, h);
        const strength = Number((this.el.querySelector('#radar-strength') as HTMLSelectElement).value);
        this.send({ placeStorm: { ...s, strength } }, `Set a storm going at ${s.speedKmh} km/h`);
        (this.el.querySelector('#radar-draw') as HTMLInputElement).checked = false;
      }
      this.draw();
    });
    this.legend();
  }

  get open() { return !this.el.classList.contains('hidden'); }
  private get velocity() { return (this.el.querySelector('#radar-product') as HTMLSelectElement).value === '1'; }
  private get tilt() { return TILTS[Number((this.el.querySelector('#radar-tilt') as HTMLSelectElement).value)]; }
  private get looping() { return (this.el.querySelector('#radar-loop') as HTMLInputElement).checked; }
  private get drawing() { return (this.el.querySelector('#radar-draw') as HTMLInputElement).checked; }

  toggle(show = !this.open) {
    this.el.classList.toggle('hidden', !show);
    window.clearInterval(this.timer);
    if (!show) return;
    this.refresh(true);
    this.timer = window.setInterval(() => {
      if (this.looping && this.frames.length > 1) { this.frameAt = (this.frameAt + 1) % (this.frames.length + 2); this.draw(); }
    }, 450);
  }

  private legend() {
    const v = this.velocity;
    const cols = v ? VELOCITY_COLOURS : DBZ_COLOURS;
    this.el.querySelector('#radar-legend')!.innerHTML = `<span class="muted">${v ? 'kt' : 'dBZ'}</span>` +
      cols.map((c, i) => `<span style="background:${hex(c)};color:${(((c >> 16) * 0.3 + ((c >> 8) & 255) * 0.59 + (c & 255) * 0.11) < 115) ? '#fff' : '#000'}">${v ? (i - 7) * 10 : 5 + 5 * i}</span>`).join('');
    this.el.querySelector('#radar-title')!.textContent = `${v ? 'Base velocity' : 'Base reflectivity'} · ${this.tilt.toFixed(1)}°`;
  }

  /** Called on every city tick, and when a control changes: repaints if the sky has moved on. */
  refresh(force = false) {
    if (!this.open) return;
    const sky = this.getSky() ?? {};
    this.el.querySelector('#radar-station')!.innerHTML = station(sky).map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('');
    const key = `${sky.clockMinute}:${(sky.cells ?? []).length}`;
    if (!force && key === this.skyKey) return;
    this.skyKey = key;
    this.legend();
    this.frames = [];
    const n = this.looping ? LOOP_FRAMES : 1;
    for (let k = n - 1; k >= 0; k--) {
      const img = new ImageData(PX, PX);
      paint(frame(sky, -10 * k), this.velocity, this.tilt, PX, img.data);
      this.frames.push(img);
    }
    this.frameAt = this.frames.length - 1;
    this.draw();
  }

  private draw() {
    const g = this.scope.getContext('2d')!;
    const S = this.scope.width, mid = S / 2, r = S / 2;
    const sky = this.getSky() ?? {};
    g.fillStyle = '#0b0f16'; g.fillRect(0, 0, S, S);
    g.fillStyle = '#1a2017'; g.beginPath(); g.arc(mid, mid, r, 0, Math.PI * 2); g.fill();
    g.strokeStyle = 'rgba(255,255,255,0.08)'; g.lineWidth = 1;
    for (let k = -3; k <= 3; k++) {
      const o = mid + k * r / 4;
      g.beginPath(); g.moveTo(o, 0); g.lineTo(o, S); g.moveTo(0, o); g.lineTo(S, o); g.stroke();
    }
    const f = Math.min(this.frameAt, this.frames.length - 1);
    if (f >= 0) {
      this.off.getContext('2d')!.putImageData(this.frames[f], 0, 0);
      g.imageSmoothingEnabled = true;
      g.drawImage(this.off, 0, 0, S, S);
    }
    g.strokeStyle = 'rgba(255,255,255,0.3)'; g.fillStyle = 'rgba(255,255,255,0.3)'; g.font = '10px sans-serif';
    for (let k = 1; k <= 4; k++) {
      g.beginPath(); g.arc(mid, mid, r * k / 4, 0, Math.PI * 2); g.stroke();
      g.fillText(`${k * 10} km`, mid + 4, mid - r * k / 4 + 12);
    }
    const [w, h] = this.mapSize();
    const hw = w * CELL_KM / (2 * RANGE_KM) * S / 2, hh = h * CELL_KM / (2 * RANGE_KM) * S / 2;
    g.strokeStyle = 'rgba(255,204,77,0.9)'; g.lineWidth = 1.5; g.strokeRect(mid - hw, mid - hh, hw * 2, hh * 2);
    g.fillStyle = '#fff'; g.beginPath(); g.arc(mid, mid, 2.5, 0, Math.PI * 2); g.fill();
    const minute = (sky.clockMinute ?? 0) - 10 * (this.frames.length - 1 - Math.max(0, f));
    const pad = (n: number) => String(n).padStart(2, '0');
    g.font = '14px sans-serif'; g.fillStyle = 'rgba(255,255,255,0.9)';
    g.fillText(`${pad(Math.floor(minute % 1440 / 60))}:${pad(minute % 60)}${this.frames.length > 1 ? `  frame ${Math.max(0, f) + 1}/${this.frames.length}` : ''}`, 8, S - 10);
    if (!(sky.cells ?? []).length) { g.fillStyle = 'rgba(255,255,255,0.6)'; g.fillText('Clear: no echoes', 8, 20); }
    if (this.drag) {
      const P = (p: [number, number]) => [(p[0] / (2 * RANGE_KM) + 0.5) * S, (p[1] / (2 * RANGE_KM) + 0.5) * S];
      const [px, py] = P(this.drag.a), [qx, qy] = P(this.drag.b);
      g.strokeStyle = g.fillStyle = '#ffcc4d'; g.lineWidth = 2.5;
      g.beginPath(); g.arc(px, py, 6, 0, Math.PI * 2); g.globalAlpha = 0.5; g.fill(); g.globalAlpha = 1;
      g.beginPath(); g.moveTo(px, py); g.lineTo(qx, qy);
      const a = Math.atan2(qy - py, qx - px);
      for (const t of [0.45, -0.45]) { g.moveTo(qx, qy); g.lineTo(qx - Math.cos(a + t) * 12, qy - Math.sin(a + t) * 12); }
      g.stroke();
      g.font = '13px sans-serif';
      g.fillText(`${Math.round(Math.hypot(this.drag.b[0] - this.drag.a[0], this.drag.b[1] - this.drag.a[1]))} km/h`, qx + 8, qy - 6);
    }
  }
}
