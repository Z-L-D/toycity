import { City, lCells } from './state';

export interface Preview {
  kind: 'line' | 'rect' | 'footprint' | 'cell';
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  ok: boolean;
  color: string;
  verticalFirst?: boolean;
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

/** Draws the city top-down, SimCity 1 style, on a single canvas. */
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
  private terrain: HTMLCanvasElement = document.createElement('canvas');
  private terrainVersion = -1;
  private blink = false;

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

  cellAt(sx: number, sy: number): { x: number; y: number } {
    return { x: Math.floor((sx - this.offX) / this.scale), y: Math.floor((sy - this.offY) / this.scale) };
  }

  private rebuildTerrain() {
    const c = this.city;
    this.terrain.width = c.width;
    this.terrain.height = c.height;
    const tctx = this.terrain.getContext('2d')!;
    const img = tctx.createImageData(c.width, c.height);
    for (let i = 0; i < c.width * c.height; i++) {
      let [r, g, b] = terrainColor(c.heights[i], c.waterLevel);
      // Hillshade: brighter on slopes facing north-west.
      const x = i % c.width;
      const y = (i / c.width) | 0;
      if (c.heights[i] >= c.waterLevel && x > 0 && y > 0) {
        const d = c.heights[i] - (c.heights[i - 1] + c.heights[i - c.width]) / 2;
        r += d * 8; g += d * 8; b += d * 6;
      }
      img.data[4 * i] = r;
      img.data[4 * i + 1] = g;
      img.data[4 * i + 2] = b;
      img.data[4 * i + 3] = 255;
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

    this.drawPreview(px, py);
    if (this.selected) {
      ctx.strokeStyle = '#fff';
      ctx.lineWidth = 2;
      ctx.strokeRect(px(this.selected.x) + 1, py(this.selected.y) + 1, s - 2, s - 2);
    }
  }

  private drawZones(x0: number, y0: number, x1: number, y1: number, px: (x: number) => number, py: (y: number) => number) {
    const c = this.city;
    const ctx = this.ctx;
    const s = this.scale;
    for (let y = y0; y <= y1; y++)
      for (let x = x0; x <= x1; x++) {
        const i = y * c.width + x;
        const z = c.zones[i];
        if (!z || c.buildingAt[i]) continue;
        const kind = Math.floor(z / 3);
        const density = z % 3;
        ctx.fillStyle = ZONE_COLORS[kind];
        ctx.globalAlpha = 0.28 + density * 0.1;
        ctx.fillRect(px(x), py(y), s, s);
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
        if (!p) continue;
        const w = Math.max(s * 0.3, Math.min(s, (p.widthM / 16) * s));
        const half = w / 2;
        const cx = px(x) + s / 2;
        const cy = py(y) + s / 2;
        ctx.fillStyle = p.color;
        ctx.fillRect(cx - half, cy - half, w, w);
        if (x + 1 < c.width && c.paths[i + 1]) ctx.fillRect(cx, cy - half, s, w);
        if (y + 1 < c.height && c.paths[i + c.width]) ctx.fillRect(cx - half, cy, w, s);
        if (s >= 12 && p.cars) {
          ctx.fillStyle = p.id.startsWith('highway') ? '#e8d36a' : 'rgba(255,255,255,0.55)';
          const d = Math.max(1, s / 12);
          if (x + 1 < c.width && c.paths[i + 1]) ctx.fillRect(cx + s * 0.2, cy - d / 2, s * 0.4, d);
          if (y + 1 < c.height && c.paths[i + c.width]) ctx.fillRect(cx - d / 2, cy + s * 0.2, d, s * 0.4);
        }
      }
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
      const bx = px(b.x);
      const by = py(b.y);
      const bw = b.width * s;
      const bh = b.height * s;
      const inset = Math.max(0.5, s * 0.08);
      const civic = t?.placeable ?? false;
      let color = t?.color ?? '#999';
      if (!civic) color = shade(color, 1.05 - b.density * 0.18);
      if (b.abandoned) color = '#6b6b6b';
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

      if (civic && t) {
        const label = CIVIC_LABEL[t.category] ?? '';
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
    if (p.kind === 'line') {
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
    }
  }
}
