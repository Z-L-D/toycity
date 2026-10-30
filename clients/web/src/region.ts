// The region map: every city tile of the region on one screen, SC4-style. It replaces the city's whole interface
// while it's open. Open a neighbour's city, found one on open land, or open a city in a window of its own.
import type { Connection, Msg } from './net';

export interface RegionHost {
  conn: Connection;
  toast(text: string, severity?: string): void;
  /** The region's speed changed, or who holds it: the top bar shows it. */
  onRegion(r: Msg): void;
}

const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector(sel) as T;
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
const money = (n: number) => (n < 0 ? '-$' : '$') + Math.abs(Math.round(n)).toLocaleString('en-US');
const PICTURE = 32;
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

export class Region {
  state: Msg | null = null;
  open = false;
  /** What the player asked for and is waiting on, shown at once (moving city, founding one). */
  private pending = '';
  /** The tile picked on the map. */
  private picked: { col: number; row: number } | null = null;

  constructor(private readonly host: RegionHost) {
    $('#btn-region').addEventListener('click', () => this.toggle());
    $('#rg-close').addEventListener('click', () => this.toggle(false));
  }

  toggle(open = !this.open) {
    this.open = open;
    document.body.classList.toggle('region-mode', open);
    if (open) {
      this.host.conn.send({ region: {} });
      if (this.state) this.picked = { col: this.state.hereCol, row: this.state.hereRow };
    }
    this.render();
  }

  /** True when this player is mayor of any city in the region: mayors may ask for a slower clock. */
  isMayor(player: string): boolean {
    return !!this.state?.tiles.some((t: Msg) => t.founded && t.mayor === player);
  }

  onRegion(body: Msg) {
    this.state = body;
    if (!this.picked) this.picked = { col: body.hereCol, row: body.hereRow };
    if (this.pending.startsWith('Founding') && this.tile(this.picked.col, this.picked.row)?.founded) this.pending = '';
    this.host.onRegion(body);
    this.render();
  }

  /** A new city arrived over this connection: the move is done. */
  onFull() {
    if (!this.pending.startsWith('Opening')) return;
    this.pending = '';
    this.toggle(false);
  }

  refused() {
    if (!this.pending) return;
    this.pending = '';
    this.render();
  }

  private tile(col: number, row: number): Msg | undefined {
    return this.state?.tiles.find((t: Msg) => t.col === col && t.row === row);
  }

  private goTo(t: Msg) {
    this.pending = `Opening ${t.name}…`;
    this.render();
    this.host.conn.send({ goTo: { col: t.col, row: t.row } });
  }

  private found(t: Msg) {
    const name = ($('#rg-name') as HTMLInputElement).value.trim() || 'New City';
    const starter = ($('#rg-starter') as HTMLInputElement).checked;
    this.pending = `Founding ${name}…`;
    this.render();
    this.host.conn.send({ foundCity: { col: t.col, row: t.row, name, starterTown: starter } });
  }

  private ownWindow(t: Msg) {
    const url = new URL(location.href);
    url.searchParams.set('tile', `${t.col},${t.row}`);
    window.open(url.toString(), `toycity-${t.col}-${t.row}`);
  }

  render() {
    const el = $('#region');
    el.classList.toggle('hidden', !this.open);
    if (!this.open) return;
    const s = this.state;
    if (!s) { $('#rg-body').innerHTML = '<p class="muted">Loading the region…</p>'; return; }
    const year = 2000 + Math.floor(s.tick / 360), month = Math.floor(s.tick / 30) % 12;
    const speedNote = s.speedSetBy ? ` · held at ${['paused', 'slow', 'normal', 'fast'][s.speed]} by ${esc(s.speedSetBy)}` : '';
    $('#rg-title').innerHTML = `🗺 <b>${esc(s.name)}</b> <span class="muted">${MONTHS[month]} ${year} · ${s.cols} × ${s.rows} tiles${speedNote}</span>`;
    const pop = s.tiles.reduce((n: number, t: Msg) => n + t.population, 0);
    const cities = s.tiles.filter((t: Msg) => t.founded).length;
    const cells = s.tiles.map((t: Msg) => {
      const here = t.col === s.hereCol && t.row === s.hereRow;
      const picked = this.picked?.col === t.col && this.picked?.row === t.row;
      return `<button class="rg-tile${t.founded ? '' : ' open'}${here ? ' here' : ''}${picked ? ' picked' : ''}" data-col="${t.col}" data-row="${t.row}"
        style="grid-column:${t.col + 1};grid-row:${t.row + 1}" title="${esc(t.name)}">
        <canvas width="${PICTURE}" height="${PICTURE}"></canvas>
        <span class="rg-label">${esc(t.founded ? t.name : '')}${t.players.length ? ` <span class="rg-who">👤${t.players.length}</span>` : ''}</span>
      </button>`;
    }).join('');
    const p = this.picked ? this.tile(this.picked.col, this.picked.row) : undefined;
    let side = '';
    if (p) {
      const here = p.col === s.hereCol && p.row === s.hereRow;
      if (p.founded) {
        side = `<h3>${esc(p.name)}</h3>
          <table class="kv">
            <tr><td>Mayor</td><td>${esc(p.mayor || 'nobody')}</td></tr>
            <tr><td>Population</td><td>${p.population.toLocaleString('en-US')}</td></tr>
            <tr><td>Treasury</td><td>${money(p.funds)}</td></tr>
            <tr><td>Here now</td><td>${p.players.length ? p.players.map(esc).join(', ') : 'nobody'}</td></tr>
          </table>
          <div class="actions">
            ${here ? '<span class="muted">You are here.</span> <button id="rg-back" class="active">Back to the city</button>'
                   : `<button id="rg-go" class="active">Open ${esc(p.name)}</button>`}
            <button id="rg-window" title="Open this city in a window of its own, next to this one">⧉ Own window</button>
          </div>`;
      } else {
        side = `<h3>Open land</h3>
          <p class="muted">Tile ${p.col + 1}, ${p.row + 1}. Nobody has built here yet. Found a city and you'll be its mayor.</p>
          <label>Name</label><input type="text" id="rg-name" value="New City" maxlength="40">
          <label><input type="checkbox" id="rg-starter" checked> Start with a small working town</label>
          <div class="actions"><button id="rg-found" class="active">Found a city here</button></div>`;
      }
    }
    if (this.pending) side += `<p class="rg-pending">⏳ ${esc(this.pending)}</p>`;
    $('#rg-body').innerHTML = `
      <div class="rg-map" style="grid-template-columns:repeat(${s.cols}, minmax(0, 1fr));grid-template-rows:repeat(${s.rows}, auto)">${cells}</div>
      <aside class="rg-side">
        <p class="muted">${cities} ${cities === 1 ? 'city' : 'cities'} · ${pop.toLocaleString('en-US')} people in the region</p>
        ${side}
      </aside>`;
    // Pictures: RGB triples, north up.
    $('#rg-body').querySelectorAll<HTMLButtonElement>('.rg-tile').forEach((b) => {
      const t = this.tile(Number(b.dataset.col), Number(b.dataset.row))!;
      const ctx = b.querySelector('canvas')!.getContext('2d')!;
      const img = ctx.createImageData(PICTURE, PICTURE);
      const px = t.picture as Uint8Array;
      for (let i = 0; i < PICTURE * PICTURE; i++) {
        img.data[i * 4] = px[i * 3]; img.data[i * 4 + 1] = px[i * 3 + 1]; img.data[i * 4 + 2] = px[i * 3 + 2]; img.data[i * 4 + 3] = 255;
      }
      ctx.putImageData(img, 0, 0);
      b.addEventListener('click', () => { this.picked = { col: t.col, row: t.row }; this.render(); });
      b.addEventListener('dblclick', () => { if (t.founded && !(t.col === s.hereCol && t.row === s.hereRow)) this.goTo(t); });
    });
    if (p) {
      $('#rg-go')?.addEventListener('click', () => this.goTo(p));
      $('#rg-back')?.addEventListener('click', () => this.toggle(false));
      $('#rg-window')?.addEventListener('click', () => this.ownWindow(p));
      $('#rg-found')?.addEventListener('click', () => this.found(p));
    }
  }
}
