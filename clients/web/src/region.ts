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
const KINDS = ['Power', 'Water', 'Garbage', 'Shuttle'];
const NOUNS = ['power', 'water', 'garbage collection', 'a shuttle'];
const unit = (k: number, n: number) => `${n.toLocaleString('en-US')} ${['kW', 'water', 'garbage', 'vehicles/h'][k]}`;
const AMOUNTS = [500, 500, 300, 200];

/** One deal in a sentence: "Westbrook sells Eastfield 500 kW for $100 a month". */
export function describeDeal(d: Msg): string {
  if (d.kind === 2) return `${d.seller} takes ${unit(2, d.amount)} from ${d.buyer} for ${money(d.price)} a month`;
  if (d.kind === 3) return `${d.seller} runs a shuttle (${unit(3, d.amount)}) for ${d.buyer} at ${money(d.price)} a month`;
  return `${d.seller} sells ${d.buyer} ${unit(d.kind, d.amount)} for ${money(d.price)} a month`;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

export class Region {
  state: Msg | null = null;
  open = false;
  /** What the player asked for and is waiting on, shown at once (moving city, founding one). */
  private pending = '';
  /** The tile picked on the map. */
  private picked: { col: number; row: number } | null = null;
  /** The deal being drawn up for the picked neighbour. */
  private offer = { kind: 0, selling: true, amount: 500, price: 100 };
  /** The offer being answered with other terms, or 0 for a new offer. */
  private counterId = 0;

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
    if (/^(Deal|Taking over|Handing)/.test(this.pending)) this.pending = '';
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
    const computer = ($('#rg-founder') as HTMLSelectElement).value;
    this.pending = `Founding ${name}…`;
    this.render();
    this.host.conn.send({ foundCity: { col: t.col, row: t.row, name, starterTown: starter, computer } });
  }

  /** The side of this city a tile lies on (0 north, 1 east, 2 south, 3 west), or -1 when it isn't next door. */
  private sideOf(t: Msg): number {
    const s = this.state!;
    const dc = t.col - s.hereCol, dr = t.row - s.hereRow;
    return dc === 0 && dr === -1 ? 0 : dc === 1 && dr === 0 ? 1 : dc === 0 && dr === 1 ? 2 : dc === -1 && dr === 0 ? 3 : -1;
  }

  private deal(op: number, id: number, what: string) {
    this.pending = `Deal: ${what}…`;
    this.render();
    this.host.conn.send({ deal: { op, id } });
  }

  /** The form takes an offer's terms, from this side, to change and send back. */
  private counter(id: number) {
    const s = this.state!;
    const d = s.deals.find((x: Msg) => x.id === id);
    if (!d) return;
    const sellerHere = d.sellerCol === s.hereCol && d.sellerRow === s.hereRow;
    this.counterId = id;
    this.offer = { kind: d.kind, selling: sellerHere, amount: d.amount, price: d.price };
    this.picked = sellerHere ? { col: d.buyerCol, row: d.buyerRow } : { col: d.sellerCol, row: d.sellerRow };
    this.render();
  }

  private sendOffer(t: Msg) {
    const o = this.offer;
    if (this.counterId) {
      this.pending = 'Deal: sending your terms…';
      this.host.conn.send({ deal: { op: 4, id: this.counterId, amount: o.amount, price: o.price } });
      this.counterId = 0;
      this.render();
      return;
    }
    this.pending = `Deal: offering ${t.name} ${NOUNS[o.kind]}…`;
    this.render();
    this.host.conn.send({ deal: { op: 0, side: this.sideOf(t), kind: o.kind, amount: o.amount, price: o.price, selling: o.selling } });
  }

  /** This city's deals, with what its mayor can do about each; with a neighbour picked, the offer being drawn up. */
  private dealsHtml(p: Msg | undefined): string {
    const s = this.state!;
    const here = this.tile(s.hereCol, s.hereRow);
    const mayor = !!here && here.mayor === this.host.conn.player;
    let h = '';
    if (s.deals?.length) {
      h += '<h3>Deals with neighbours</h3>';
      for (const d of s.deals) {
        const sellerHere = d.sellerCol === s.hereCol && d.sellerRow === s.hereRow;
        const mine = sellerHere === d.offeredBySeller;
        let act = '';
        if (d.active) act = `<span class="good">running</span>${mayor ? ` <button data-deal-end="${d.id}">End it</button>` : ''}`;
        else if (mine) act = `<span class="muted">offered, waiting for an answer</span>${mayor ? ` <button data-deal-end="${d.id}">Withdraw</button>` : ''}`;
        else act = `<span class="warn">offered to you</span>${mayor ? ` <button class="active" data-deal-accept="${d.id}">Agree</button> <button data-deal-counter="${d.id}">Counter</button> <button data-deal-decline="${d.id}">Decline</button>` : ''}`;
        h += `<div class="rg-deal">${esc(describeDeal(d))}<div class="actions">${act}</div></div>`;
      }
    }
    if (!p?.founded || this.sideOf(p) < 0 || !mayor) return h;
    const o = this.offer;
    const verb = o.kind === 2 ? (o.selling ? 'Take their garbage' : 'Send them our garbage')
      : o.kind === 3 ? (o.selling ? 'Run a shuttle for them' : 'Have them run a shuttle for us')
      : o.selling ? `Sell them ${NOUNS[o.kind]}` : `Buy ${NOUNS[o.kind]} from them`;
    const needs = o.kind === 0 ? 'a power line' : o.kind === 1 ? 'a pipe' : 'a road';
    const c = this.counterId > 0;
    h += `<h3>${c ? 'Your terms for' : 'A deal with'} ${esc(p.name)}</h3>
      <div class="rg-kinds${c ? ' hidden' : ''}">${KINDS.map((k, i) => `<button data-kind="${i}" class="${i === o.kind ? 'active' : ''}">${k}</button>`).join('')}</div>
      <div class="actions">${c ? '' : '<button id="rg-sell" title="Sell or buy">⇄</button> '}${verb}</div>
      <div class="actions rg-steps">
        <button data-amt="-1">−</button> <b>${unit(o.kind, o.amount)}</b> <button data-amt="1">+</button>
        <button data-price="-1">−</button> <b>${money(o.price)} a month</b> <button data-price="1">+</button>
      </div>
      <p class="muted">Needs ${needs} of each city to meet at the border.</p>
      <div class="actions"><button id="rg-offer" class="active">${c ? 'Send these terms back' : `Offer it to ${esc(p.name)}`}</button></div>`;
    return h;
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
        <span class="rg-label">${t.computer ? '🤖 ' : ''}${esc(t.founded ? t.name : '')}${t.players.length ? ` <span class="rg-who">👤${t.players.length}</span>` : ''}</span>
      </button>`;
    }).join('');
    const p = this.picked ? this.tile(this.picked.col, this.picked.row) : undefined;
    let side = '';
    if (p) {
      const here = p.col === s.hereCol && p.row === s.hereRow;
      if (p.founded) {
        side = `<h3>${esc(p.name)}</h3>
          <table class="kv">
            <tr><td>Mayor</td><td>${esc(p.mayor || 'nobody')}${p.computer ? ` <span class="muted">· ${esc(p.computer)}</span>` : ''}</td></tr>
            <tr><td>Population</td><td>${p.population.toLocaleString('en-US')}</td></tr>
            <tr><td>Treasury</td><td>${money(p.funds)}</td></tr>
            <tr><td>Here now</td><td>${p.players.length ? p.players.map(esc).join(', ') : 'nobody'}</td></tr>
          </table>
          <div class="actions">
            ${here ? '<span class="muted">You are here.</span> <button id="rg-back" class="active">Back to the city</button>'
                   : `<button id="rg-go" class="active">Open ${esc(p.name)}</button>`}
            <button id="rg-window" title="Open this city in a window of its own, next to this one">⧉ Own window</button>
          </div>
          ${p.computer ? '<div class="actions"><button id="rg-takeover">Take over this city</button> <span class="muted">and play it yourself</span></div>'
            : p.mayor === this.host.conn.player ? `<div class="actions"><span class="muted">Hand it to a computer mayor:</span>
                <button data-hand="growth">growth-hungry</button> <button data-hand="green">green</button> <button data-hand="careful">careful</button></div>` : ''}`;
      } else {
        side = `<h3>Open land</h3>
          <p class="muted">Tile ${p.col + 1}, ${p.row + 1}. Nobody has built here yet. Found a city and you'll be its mayor.</p>
          <label>Name</label><input type="text" id="rg-name" value="New City" maxlength="40">
          <label><input type="checkbox" id="rg-starter" checked> Start with a small working town</label>
          <label>Mayor</label><select id="rg-founder">
            <option value="">You</option><option value="growth">A growth-hungry computer</option>
            <option value="green">A green computer</option><option value="careful">A careful computer</option>
          </select>
          <p class="muted">A computer mayor plays its city on autopilot and answers deals by its temperament. Admins only.</p>
          <div class="actions"><button id="rg-found" class="active">Found a city here</button></div>`;
      }
    }
    side += this.dealsHtml(p);
    if (this.pending) side += `<p class="rg-pending">⏳ ${esc(this.pending.replace(/^Deal: /, ''))}</p>`;
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
      b.addEventListener('click', () => { this.picked = { col: t.col, row: t.row }; this.counterId = 0; this.render(); });
      b.addEventListener('dblclick', () => { if (t.founded && !(t.col === s.hereCol && t.row === s.hereRow)) this.goTo(t); });
    });
    if (p) {
      $('#rg-go')?.addEventListener('click', () => this.goTo(p));
      $('#rg-back')?.addEventListener('click', () => this.toggle(false));
      $('#rg-window')?.addEventListener('click', () => this.ownWindow(p));
      $('#rg-found')?.addEventListener('click', () => this.found(p));
      $('#rg-offer')?.addEventListener('click', () => this.sendOffer(p));
      $('#rg-takeover')?.addEventListener('click', () => {
        this.pending = `Taking over ${p.name}…`;
        this.render();
        this.host.conn.send({ mayor: { col: p.col, row: p.row, takeOver: true } });
      });
    }
    const body = $('#rg-body');
    const on = (sel: string, f: (b: HTMLButtonElement) => void) => body.querySelectorAll<HTMLButtonElement>(sel).forEach((b) => b.addEventListener('click', () => f(b)));
    on('[data-kind]', (b) => { this.offer.kind = Number(b.dataset.kind); this.offer.amount = AMOUNTS[this.offer.kind]; this.render(); });
    on('#rg-sell', () => { this.offer.selling = !this.offer.selling; this.render(); });
    on('[data-amt]', (b) => { this.offer.amount = Math.max(100, this.offer.amount + 100 * Number(b.dataset.amt)); this.render(); });
    on('[data-price]', (b) => { this.offer.price = Math.max(0, this.offer.price + 25 * Number(b.dataset.price)); this.render(); });
    on('[data-deal-accept]', (b) => this.deal(1, Number(b.dataset.dealAccept), 'agreeing'));
    on('[data-deal-decline]', (b) => this.deal(2, Number(b.dataset.dealDecline), 'declining'));
    on('[data-hand]', (b) => {
      this.pending = `Handing ${p!.name} to the computer…`;
      this.render();
      this.host.conn.send({ mayor: { col: p!.col, row: p!.row, computer: b.dataset.hand } });
    });
    on('[data-deal-counter]', (b) => this.counter(Number(b.dataset.dealCounter)));
    on('[data-deal-end]', (b) => this.deal(3, Number(b.dataset.dealEnd), 'ending'));
  }
}
