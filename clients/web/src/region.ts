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

const RULES = ['admins only', 'a vote of the mayors', 'any mayor'];
const sideText = (n: number) => (n === 1 ? 'one tile' : n > 0 ? `${n} × ${n} tiles` : 'as big as the server allows');

/**
 * Where a tile is, in words: by column and row for a fixed region; for a growing one, from the tile it began with
 * (its frame is so wide the numbers would mean nothing).
 */
export function where(s: Msg, t: Msg): string {
  if (!s.grows) return `Tile ${t.col + 1}, ${t.row + 1}`;
  const c = (s.cols - 1) >> 1, dx = t.col - c, dy = t.row - c;
  if (dx === 0 && dy === 0) return 'The tile the region began with';
  const parts: string[] = [];
  if (dy) parts.push(`${Math.abs(dy)} ${dy < 0 ? 'north' : 'south'}`);
  if (dx) parts.push(`${Math.abs(dx)} ${dx < 0 ? 'west' : 'east'}`);
  return `The tile ${parts.join(' and ')} of where the region began`;
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
  /** The price on the table for taking in a neighbour, and whose it is (its top-left tile). */
  private bid = { col: -1, row: -1, price: 0 };
  /** The offer being answered with other terms, or 0 for a new offer. */
  private counterId = 0;
  /** A new charter being drawn up (Beta 4, part 6), or null. */
  private draft: { grow: number; annex: number; maxCitySide: number } | null = null;
  /** True when this player is one of the server's admins. */
  admin = () => false;

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
    if (this.pending.startsWith('Adding') && (this.tile(this.picked.col, this.picked.row)?.wild === false
      || body.votes.some((v: Msg) => v.kind === 1 && v.col === this.picked!.col && v.row === this.picked!.row))) this.pending = '';
    if (/^(Charter|Voting|Takeover)/.test(this.pending)) this.pending = '';
    // A tile a city has annexed: the city is picked whole, from its top-left tile.
    const pt = this.tile(this.picked.col, this.picked.row);
    if (pt?.founded && (pt.cityCols ?? 0) * (pt.cityRows ?? 0) > 1) this.picked = { col: pt.cityCol, row: pt.cityRow };
    if (this.pending.startsWith('Anne') && (pt?.founded || body.votes.some((v: Msg) => v.kind === 2 && v.toCol === pt?.col && v.toRow === pt?.row))) this.pending = '';
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

  /** A growing region's wild tile that can be added: one on a side of a tile already in the region (G4). */
  private canGrow(t: Msg): boolean {
    if (!t.wild) return false;
    return [[0, -1], [1, 0], [0, 1], [-1, 0]].some(([dc, dr]) => this.tile(t.col + dc, t.row + dr)?.wild === false);
  }

  /**
   * The tiles to show: the whole grid for a fixed region; for a growing one, the tiles in it and a ring of wild land
   * around them, where it can grow.
   */
  private shown(): { c0: number; r0: number; c1: number; r1: number } {
    const s = this.state!;
    if (!s.grows) return { c0: 0, r0: 0, c1: s.cols - 1, r1: s.rows - 1 };
    const inRegion = s.tiles.filter((t: Msg) => !t.wild);
    const cs = inRegion.map((t: Msg) => t.col), rs = inRegion.map((t: Msg) => t.row);
    return {
      c0: Math.max(0, Math.min(...cs) - 1), r0: Math.max(0, Math.min(...rs) - 1),
      c1: Math.min(s.cols - 1, Math.max(...cs) + 1), r1: Math.min(s.rows - 1, Math.max(...rs) + 1),
    };
  }

  /** The tiles a city spans, from its top-left tile; one tile for open land. */
  private span(t: Msg): { anchor: boolean; cols: number; rows: number } {
    if (!t.founded || (t.cityCols ?? 0) <= 1 && (t.cityRows ?? 0) <= 1) return { anchor: true, cols: 1, rows: 1 };
    return { anchor: t.col === t.cityCol && t.row === t.cityRow, cols: Math.max(1, t.cityCols), rows: Math.max(1, t.cityRows) };
  }

  private annex(t: Msg, vote: boolean) {
    this.pending = vote ? 'Annex: asking the mayors…' : 'Annexing the land: moving the city onto its bigger map…';
    this.render();
    this.host.conn.send({ annex: { col: t.col, row: t.row } });
  }

  /** The open land a city could annex, as the side panel shows it for a picked tile of it. */
  private annexHtml(p: Msg): string {
    const s = this.state!;
    const a = (s.annex ?? []).find((x: Msg) => x.col === p.col && x.row === p.row);
    const home = s.tiles.find((h: Msg) => h.col === s.hereCol && h.row === s.hereRow);
    if (!a || !home?.founded) return '';
    const rule = s.charter?.annex ?? 1;
    const mine = home.mayor === this.host.conn.player;
    const asked = s.votes.some((v: Msg) => v.kind === 2 && v.toCol === p.col && v.toRow === p.row);
    const ask = rule === 1 && !this.admin() && s.voters.length > 1;
    const may = this.admin() || (mine && rule !== 0);
    const takes = a.toCols * a.toRows - Math.max(1, home.cityCols || 1) * Math.max(1, home.cityRows || 1);
    const ruleText = ['By the region\'s charter only the server\'s admins annex land.', 'By the region\'s charter, annexing goes to a vote of the mayors: more than half must agree.', `By the region's charter, ${esc(home.name)}'s mayor can annex land beside it.`][rule];
    return `<h4>Annex for ${esc(home.name)}</h4>
      <p class="muted">${esc(home.name)} would take ${takes === 1 ? 'this tile' : `${takes} tiles (a city is always a rectangle)`} and be
        <b>${a.toCols} × ${a.toRows} tiles</b>. Everything in it moves onto the bigger map as it stands; the new land comes with its ground, rivers and rock.</p>
      <table class="kv"><tr><td>Price</td><td>${money(a.price)}</td></tr><tr><td>Treasury</td><td>${money(home.funds)}</td></tr></table>
      <p class="muted">${ruleText}</p>
      ${a.why ? `<p class="rg-why">${esc(a.why)}</p>`
        : asked ? '<p class="muted">The mayors are voting on it now (below).</p>'
        : may ? `<div class="actions"><button id="rg-annex" class="active">${ask ? 'Ask the mayors to let it annex' : `Annex for ${money(a.price)}`}</button></div>` : ''}`;
  }

  private grow(t: Msg) {
    this.pending = 'Adding a tile to the region: making its ground…';
    this.render();
    this.host.conn.send({ grow: { col: t.col, row: t.row } });
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
    const home = this.tile(s.hereCol, s.hereRow);
    if (!home) return -1;
    // Cities of several tiles: the sides meet where their rectangles touch.
    const box = (x: Msg) => { const sp = this.span(x); return { c: x.founded && sp.cols * sp.rows > 1 ? x.cityCol : x.col, r: x.founded && sp.cols * sp.rows > 1 ? x.cityRow : x.row, w: sp.cols, h: sp.rows }; };
    const a = box(home), b = box(t);
    const cols = b.c < a.c + a.w && b.c + b.w > a.c, rows = b.r < a.r + a.h && b.r + b.h > a.r;
    return cols && b.r + b.h === a.r ? 0 : rows && b.c === a.c + a.w ? 1 : cols && b.r === a.r + a.h ? 2 : rows && b.c + b.w === a.c ? 3 : -1;
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
  /**
   * Taking in a neighbouring city by a deal (Beta 4, part 6): the offers open now, both ways, and, for a picked
   * neighbour, what the two would make and an offer to send.
   */
  private takeoverHtml(p: Msg | undefined): string {
    const s = this.state!;
    const home = this.tile(s.hereCol, s.hereRow);
    const me = this.host.conn.player;
    let h = '';
    for (const o of s.takeovers ?? []) {
      const theirs = o.sellerCol === s.hereCol && o.sellerRow === s.hereRow;
      h += `<div class="rg-offer"><b>${esc(o.buyer)}</b> would take in <b>${esc(o.seller)}</b> for ${money(o.price)}
        <span class="muted">(${o.toCols} × ${o.toRows} tiles; offered by ${esc(o.by)})</span>
        ${theirs && home?.mayor === me ? `<div class="actions"><button data-to-accept="${o.id}" class="active">Agree: ${esc(o.seller)} joins ${esc(o.buyer)}</button> <button data-to-decline="${o.id}">Decline</button></div>`
          : !theirs && (home?.mayor === me || o.by === me) ? `<div class="actions"><span class="muted">Waiting for ${esc(o.seller)}'s mayor.</span> <button data-to-withdraw="${o.id}">Withdraw</button></div>` : ''}</div>`;
    }
    if (h) h = `<h3>Taking in a neighbour</h3>${h}`;
    const opt = p && (s.takeoverOptions ?? []).find((x: Msg) => x.col === p.col && x.row === p.row);
    if (!opt || !home) return h;
    if (this.bid.col !== opt.col || this.bid.row !== opt.row) this.bid = { col: opt.col, row: opt.row, price: opt.floor };
    // A computer mayor takes no less than its price, which moves with its city.
    if (opt.computer && this.bid.price < opt.floor) this.bid.price = opt.floor;
    const may = home.mayor === me || this.admin();
    h += `<h4>Take in ${esc(p.name)}</h4>
      <p class="muted">${esc(home.name)} and ${esc(p.name)} would be one city of <b>${opt.toCols} × ${opt.toRows} tiles</b>, named ${esc(home.name)}.
        ${esc(p.name)}'s buildings, roads and people come with it, and its treasury and debts; its bus lines and dispatch close.
        ${opt.computer ? `${esc(p.mayor)} sells for ${money(opt.floor)} or more, by its temperament.` : `Its mayor, ${esc(p.mayor)}, has to agree, and stays on as a deputy.`}</p>
      ${opt.why ? `<p class="rg-why">${esc(opt.why)}</p>` : may ? `<div class="actions"><span class="muted">Price</span>
          <button data-bid="-1">−</button> <b>${money(this.bid.price)}</b> <button data-bid="1">＋</button>
          <button id="rg-takeover-offer" class="active">${opt.computer ? 'Buy it' : 'Offer it'}</button></div>` : ''}`;
    return h;
  }

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

  /**
   * The region's charter (Beta 4, part 6): who grows it, who annexes, how big a city may be; the votes open; and, for
   * a mayor or an admin, a new charter to set or ask for.
   */
  private charterHtml(): string {
    const s = this.state!;
    const c = s.charter ?? { grow: 2, annex: 1, maxCitySide: 0 };
    const me = this.host.conn.player;
    let h = `<h3>Region charter</h3><table class="kv">
      <tr><td>Adding land</td><td>${s.grows ? RULES[c.grow] : '<span class="muted">this map was made all at once</span>'}</td></tr>
      <tr><td>Annexing</td><td>${RULES[c.annex]}</td></tr>
      <tr><td>Cities up to</td><td>${s.maxCitySide} × ${s.maxCitySide} tiles${c.maxCitySide ? '' : ' <span class="muted">(the server\'s limit)</span>'}</td></tr>
    </table>`;
    for (const v of s.votes) {
      const yes = v.yes.includes(me), no = v.no.includes(me);
      h += `<div class="rg-vote"><p>🗳 ${esc(v.by)} asks for ${esc(v.text)}.
        <span class="muted">${v.yes.length} yes, ${v.no.length} no, of ${s.voters.length} mayors; open until day ${v.day + 30}.</span></p>
        ${s.voters.includes(me) ? `<div class="actions"><button data-vote="${v.id}" data-yes="1"${yes ? ' class="active"' : ''}>Yes</button>
          <button data-vote="${v.id}" data-yes="0"${no ? ' class="active"' : ''}>No</button></div>` : ''}</div>`;
    }
    const mayor = s.voters.includes(me), direct = this.admin() || s.voters.length <= 1;
    if (!mayor && !this.admin()) return h;
    const d = this.draft;
    if (!d) return h + `<div class="actions"><button id="rg-charter">${direct ? 'Change the charter' : 'Ask the mayors for a new charter'}</button></div>`;
    const rules = (key: string, label: string, now: number) =>
      `<label>${label}</label><div class="seg">${RULES.map((r, k) => `<button data-ch-${key}="${k}"${k === now ? ' class="active"' : ''}>${r}</button>`).join('')}</div>`;
    const sides = [1, 2, 3, 4, 0].filter((n) => n <= Math.max(1, s.serverMaxCitySide));
    return h + `<h4>A new charter</h4>
      ${s.grows ? rules('grow', 'Adding land', d.grow) : ''}
      ${rules('annex', 'Annexing', d.annex)}
      <label>Cities up to</label><div class="seg">${sides.map((n) => `<button data-ch-max="${n}"${n === d.maxCitySide ? ' class="active"' : ''}>${sideText(n)}</button>`).join('')}</div>
      <div class="actions"><button id="ch-cancel">Cancel</button><button id="ch-send" class="active">${direct ? 'Set it' : 'Put it to a vote'}</button></div>`;
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
    const inRegion = s.tiles.filter((t: Msg) => !t.wild).length;
    const size = s.grows ? `${inRegion} ${inRegion === 1 ? 'tile' : 'tiles'}, growing` : `${s.cols} × ${s.rows} tiles`;
    $('#rg-title').innerHTML = `🗺 <b>${esc(s.name)}</b> <span class="muted">${MONTHS[month]} ${year} · ${size}${speedNote}</span>`;
    // A city of several tiles counts once, by its top-left tile.
    const towns = s.tiles.filter((t: Msg) => t.founded && this.span(t).anchor);
    const pop = towns.reduce((n: number, t: Msg) => n + t.population, 0);
    const cities = towns.length;
    const box = this.shown();
    const annexAt = (t: Msg) => (s.annex ?? []).find((a: Msg) => a.col === t.col && a.row === t.row);
    const cells = s.tiles.filter((t: Msg) => t.col >= box.c0 && t.col <= box.c1 && t.row >= box.r0 && t.row <= box.r1)
      // A city of several tiles is drawn once, from its top-left tile, across all of them.
      .filter((t: Msg) => !t.founded || this.span(t).anchor).map((t: Msg) => {
      const here = t.col === s.hereCol && t.row === s.hereRow;
      const picked = this.picked?.col === t.col && this.picked?.row === t.row;
      const sp = this.span(t);
      const at = `grid-column:${t.col - box.c0 + 1} / span ${sp.cols};grid-row:${t.row - box.r0 + 1} / span ${sp.rows}${sp.cols * sp.rows > 1 ? `;aspect-ratio:${sp.cols}/${sp.rows}` : ''}`;
      if (t.wild) {
        const can = this.canGrow(t);
        return `<button class="rg-tile wild${can ? ' can' : ''}${picked ? ' picked' : ''}" data-col="${t.col}" data-row="${t.row}" style="${at}"
          title="${can ? 'Wild land: add it to the region' : 'Wild land'}">${can ? '<span class="rg-plus">＋</span>' : ''}</button>`;
      }
      const ax = !t.founded && annexAt(t);
      return `<button class="rg-tile${t.founded ? '' : ' open'}${here ? ' here' : ''}${picked ? ' picked' : ''}${ax && !ax.why ? ' annex' : ''}" data-col="${t.col}" data-row="${t.row}"
        style="${at}" title="${esc(t.founded ? t.name : ax ? (ax.why ? 'Open land' : `Open land: ${s.tiles.find((h: Msg) => h.col === s.hereCol && h.row === s.hereRow)?.name ?? 'your city'} can annex it`) : 'Open land')}">
        <canvas width="${PICTURE * sp.cols}" height="${PICTURE * sp.rows}"></canvas>
        ${ax && !ax.why ? '<span class="rg-plus">⊕</span>' : ''}
        ${t.weather ? `<span class="rg-weather" title="${esc(t.weather)}">${esc(t.weather.split(' ')[0])}</span>` : ''}
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
            ${this.span(p).cols * this.span(p).rows > 1 ? `<tr><td>Size</td><td>${this.span(p).cols} × ${this.span(p).rows} tiles</td></tr>` : ''}
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
                <button data-hand="growth">growth-hungry</button> <button data-hand="green">green</button> <button data-hand="careful">careful</button></div>` : ''}
          ${here && (s.annex ?? []).some((a: Msg) => !a.why) ? '<p class="muted">Room to grow: pick the open land beside it, marked ⊕, to annex it.</p>' : ''}`;
      } else if (p.wild) {
        const rule = s.charter?.grow ?? 2;
        const asked = s.votes.some((v: Msg) => v.kind === 1 && v.col === p.col && v.row === p.row);
        const ask = rule === 1 && !this.admin() && s.voters.length > 1;
        side = this.canGrow(p)
          ? `<h3>Wild land</h3>
            <p class="muted">${where(s, p)} isn't part of the region yet. Add it and its ground, rivers and rock are
              made now, carrying on from the tiles beside it. It joins as open land: you or any other player can found a city on it.</p>
            <p class="muted">${['The region\'s charter lets only the server\'s admins add land.', 'By the region\'s charter, adding land goes to a vote of the mayors: more than half must agree.', 'By the region\'s charter, any mayor here can add land.'][rule]}</p>
            ${asked ? '<p class="muted">The mayors are voting on it now (below).</p>'
              : `<div class="actions"><button id="rg-grow" class="active">${ask ? 'Ask the mayors to add this tile' : 'Add this tile to the region'}</button></div>`}`
          : `<h3>Wild land</h3><p class="muted">Not part of the region yet. Land joins on a side of a tile already in it.</p>`;
      } else {
        side = `<h3>Open land</h3>
          <p class="muted">${where(s, p)}. Nobody has built here yet. Found a city and you'll be its mayor.${p.unsurveyed ? ' The picture is a rough look: its ground, rivers and rock are made when a city is founded.' : ''}</p>
          <label>Name</label><input type="text" id="rg-name" value="New City" maxlength="40">
          <label><input type="checkbox" id="rg-starter" checked> Start with a small working town</label>
          <label>Mayor</label><select id="rg-founder">
            <option value="">You</option><option value="growth">A growth-hungry computer</option>
            <option value="green">A green computer</option><option value="careful">A careful computer</option>
          </select>
          <p class="muted">A computer mayor plays its city on autopilot and answers deals by its temperament. Admins only.</p>
          <div class="actions"><button id="rg-found" class="active">Found a city here</button></div>
          ${this.annexHtml(p)}`;
      }
    }
    side += this.dealsHtml(p);
    side += this.takeoverHtml(p && p.founded && !(p.col === s.hereCol && p.row === s.hereRow) ? p : undefined);
    side += this.charterHtml();
    if (this.pending) side += `<p class="rg-pending">⏳ ${esc(this.pending.replace(/^Deal: /, ''))}</p>`;
    $('#rg-body').innerHTML = `
      <div class="rg-map${box.c1 - box.c0 + 1 > 12 ? ' big' : ''}" style="grid-template-columns:repeat(${box.c1 - box.c0 + 1}, minmax(0, 1fr));grid-template-rows:repeat(${box.r1 - box.r0 + 1}, auto)">${cells}</div>
      <aside class="rg-side">
        <p class="muted">${cities} ${cities === 1 ? 'city' : 'cities'} · ${pop.toLocaleString('en-US')} people in the region</p>
        ${s.cols * s.rows > 1 ? `<label title="Whether what one city puts in a river reaches the cities downstream. An admin's, or the sole mayor's, to set."><input type="checkbox" id="rg-water" ${s.waterStays ? '' : 'checked'}> Rivers carry foul water on into the next city</label>` : ''}
        ${side}
      </aside>`;
    // Pictures: RGB triples, north up.
    $('#rg-body').querySelectorAll<HTMLButtonElement>('.rg-tile').forEach((b) => {
      const t = this.tile(Number(b.dataset.col), Number(b.dataset.row))!;
      b.addEventListener('click', () => { this.picked = { col: t.col, row: t.row }; this.counterId = 0; this.render(); });
      if (t.wild) return;
      const ctx = b.querySelector('canvas')!.getContext('2d')!;
      const sp = this.span(t);
      // Each of the city's tiles brings its own part of the picture.
      for (let dr = 0; dr < sp.rows; dr++) for (let dc = 0; dc < sp.cols; dc++) {
        const part = this.tile(t.col + dc, t.row + dr);
        const px = part?.picture as Uint8Array | undefined;
        if (!px || px.length < PICTURE * PICTURE * 3) continue;
        const img = ctx.createImageData(PICTURE, PICTURE);
        for (let i = 0; i < PICTURE * PICTURE; i++) {
          img.data[i * 4] = px[i * 3]; img.data[i * 4 + 1] = px[i * 3 + 1]; img.data[i * 4 + 2] = px[i * 3 + 2]; img.data[i * 4 + 3] = 255;
        }
        ctx.putImageData(img, dc * PICTURE, dr * PICTURE);
      }
      b.addEventListener('dblclick', () => { if (t.founded && !(t.col === s.hereCol && t.row === s.hereRow)) this.goTo(t); });
    });
    if (p) {
      $('#rg-go')?.addEventListener('click', () => this.goTo(p));
      $('#rg-back')?.addEventListener('click', () => this.toggle(false));
      $('#rg-window')?.addEventListener('click', () => this.ownWindow(p));
      $('#rg-found')?.addEventListener('click', () => this.found(p));
      $('#rg-grow')?.addEventListener('click', () => this.grow(p));
      $('#rg-annex')?.addEventListener('click', () => this.annex(p, (s.charter?.annex ?? 1) === 1 && !this.admin() && s.voters.length > 1));
      $('#rg-offer')?.addEventListener('click', () => this.sendOffer(p));
      $('#rg-takeover')?.addEventListener('click', () => {
        this.pending = `Taking over ${p.name}…`;
        this.render();
        this.host.conn.send({ mayor: { col: p.col, row: p.row, takeOver: true } });
      });
    }
    $('#rg-water')?.addEventListener('change', () => this.host.conn.send({ regionWater: { stays: !(s.waterStays ?? false) } }));
    const body = $('#rg-body');
    const on = (sel: string, f: (b: HTMLButtonElement) => void) => body.querySelectorAll<HTMLButtonElement>(sel).forEach((b) => b.addEventListener('click', () => f(b)));
    const charter = (k: 'grow' | 'annex' | 'maxCitySide', v: number) => { if (this.draft) { this.draft[k] = v; this.render(); } };
    on('[data-ch-grow]', (b) => charter('grow', Number(b.dataset.chGrow)));
    on('[data-ch-annex]', (b) => charter('annex', Number(b.dataset.chAnnex)));
    on('[data-ch-max]', (b) => charter('maxCitySide', Number(b.dataset.chMax)));
    on('#rg-charter', () => { const c = s.charter ?? {}; this.draft = { grow: c.grow ?? 2, annex: c.annex ?? 1, maxCitySide: c.maxCitySide ?? 0 }; this.render(); });
    on('#ch-cancel', () => { this.draft = null; this.render(); });
    on('#ch-send', () => {
      this.pending = this.admin() || s.voters.length <= 1 ? 'Charter: setting it…' : 'Charter: asking the mayors…';
      this.host.conn.send({ charter: { charter: this.draft } });
      this.draft = null;
      this.render();
    });
    on('[data-vote]', (b) => {
      this.pending = 'Voting…';
      this.render();
      this.host.conn.send({ vote: { id: Number(b.dataset.vote), yes: b.dataset.yes === '1' } });
    });
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
    on('[data-bid]', (b) => { this.bid.price = Math.max(0, this.bid.price + 5000 * Number(b.dataset.bid)); this.render(); });
    on('#rg-takeover-offer', () => {
      this.pending = `Takeover: offering ${money(this.bid.price)}…`;
      this.render();
      this.host.conn.send({ takeover: { op: 0, col: this.bid.col, row: this.bid.row, price: this.bid.price } });
    });
    const answer = (op: number, id: number, what: string) => { this.pending = `Takeover: ${what}…`; this.render(); this.host.conn.send({ takeover: { op, id } }); };
    on('[data-to-accept]', (b) => answer(1, Number(b.dataset.toAccept), 'agreeing: the two cities become one'));
    on('[data-to-decline]', (b) => answer(2, Number(b.dataset.toDecline), 'declining'));
    on('[data-to-withdraw]', (b) => answer(3, Number(b.dataset.toWithdraw), 'withdrawing'));
    on('[data-deal-end]', (b) => this.deal(3, Number(b.dataset.dealEnd), 'ending'));
  }
}
