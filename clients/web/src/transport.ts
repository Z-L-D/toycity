// The bus company's panel: its lines, buses, fares and books, the lots it leases, and drawing new lines on the map.
import { decodeTransportView, encodeTransportCommand, type Connection, type Msg } from './net';
import type { City } from './state';

export interface TransportHost {
  conn: Connection;
  city: City;
  jumpTo(x: number, y: number): void;
  showTab(name: string): void;
  toast(text: string, severity?: string): void;
  /** Lines to draw on the map, with their buses. */
  setLines(lines: { color: string; cells: number[]; buses: number }[]): void;
  /** Hands the next map clicks to the line editor (true), or back to the tools. */
  setPicking(on: boolean): void;
}

const GAME = 'transport';
const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector(sel) as T;
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
const money = (n: number) => (n < 0 ? '-$' : '$') + Math.abs(Math.round(n)).toLocaleString('en-US');
const fare = (c: number) => (c === 0 ? 'free' : `$${(c / 100).toFixed(2)}`);
const OPS: Record<string, string> = { manual: 'run by you', caretaker: 'run by the computer (caretaker)', autopilot: 'run by the computer (autopilot)' };

export class Transport {
  view: Msg | null = null;
  /** The line being drawn: its stops (lease ids) in order. */
  private draft: { lineId: number; name: string; stops: number[]; fare: number } | null = null;

  constructor(private readonly host: TransportHost) {}

  onView(body: Msg) {
    if (body.game !== GAME) return;
    this.view = decodeTransportView(body.payload as Uint8Array);
    const tab = $('#tabs button[data-tab="buses"]');
    tab.textContent = this.view.lines.length ? `Buses (${this.view.lines.length})` : 'Buses';
    this.host.setLines((this.view.lines as Msg[]).filter((l) => l.headwayMinutes > 0).map((l) => ({ color: l.color, cells: l.routeCells, buses: l.buses - l.broken })));
    this.render();
  }

  private command(cmd: Msg) {
    this.host.conn.action({ sideGame: { game: GAME, seat: 'operator', payload: encodeTransportCommand(cmd) } });
  }

  private lot(leaseId: number): Msg | undefined {
    return (this.view?.lots as Msg[] | undefined)?.find((l) => l.leaseId === leaseId);
  }

  /** A click on the map while drawing a line: a leased stop joins the line (or leaves it, if it's the last one). */
  pick(x: number, y: number): boolean {
    if (!this.draft || !this.view) return false;
    const id = this.host.city.buildingAt[y * this.host.city.width + x];
    const lot = (this.view.lots as Msg[]).find((l) => l.kind === 'bus-stop' && l.buildingId === id - 1);
    if (!lot) {
      this.host.toast('Click a bus stop the company leases. Lease more under Buses → Lots.', 'warning');
      return true;
    }
    const s = this.draft.stops;
    if (s[s.length - 1] === lot.leaseId) s.pop();
    else if (s.includes(lot.leaseId)) this.host.toast('A line visits each stop once.', 'warning');
    else s.push(lot.leaseId);
    this.render();
    return true;
  }

  private startDraft(line?: Msg) {
    this.draft = line
      ? { lineId: line.id, name: line.name, stops: [...line.stopLeaseIds], fare: line.fareCents }
      : { lineId: 0, name: '', stops: [], fare: 150 };
    this.host.setPicking(true);
    this.host.showTab('buses');
    this.render();
  }

  private endDraft() {
    this.draft = null;
    this.host.setPicking(false);
    this.render();
  }

  render() {
    const el = $('#tab-buses');
    // Don't rebuild the panel under someone typing in it; catch up when they leave the field.
    const focused = document.activeElement;
    if (focused instanceof HTMLInputElement && el.contains(focused)) {
      // A moment's delay, so a click that moved focus (on Save, say) lands before the panel is rebuilt.
      focused.addEventListener('blur', () => setTimeout(() => this.render(), 250), { once: true });
      return;
    }
    const v = this.view;
    if (!v) { el.innerHTML = '<p class="muted">No bus company in this city.</p>'; return; }
    const mine = v.operator === 'manual';
    const m = v.lastMonth;
    const books = (mm: Msg) => `<tr><td>Fares</td><td class="num pos">${money(mm.fares)}</td></tr>
      ${mm.subsidy ? `<tr><td>City subsidy</td><td class="num pos">${money(mm.subsidy)}</td></tr>` : ''}
      <tr><td>Drivers' wages</td><td class="num neg">-${money(mm.wages)}</td></tr>
      <tr><td>Bus upkeep</td><td class="num neg">-${money(mm.upkeep)}</td></tr>
      <tr><td>Rent and fare tax</td><td class="num neg">-${money(mm.rent)}</td></tr>
      ${mm.standing ? `<tr><td>Standing (ads, statues, rights)</td><td class="num neg">-${money(mm.standing)}</td></tr>` : ''}
      <tr class="total"><td>Profit</td><td class="num ${mm.profit >= 0 ? 'pos' : 'neg'}">${money(mm.profit)}</td></tr>`;
    const stopName = (leaseId: number) => {
      const l = this.lot(leaseId);
      return l ? `${l.x},${l.y}` : '?';
    };
    const lines = (v.lines as Msg[]).map((l) => `
      <div class="bus-line" style="border-left-color:${esc(l.color)}">
        <div class="row-flex" style="justify-content:space-between"><b>${esc(l.name)}</b><span class="muted small">${l.stopLeaseIds.length} stops · ${fare(l.fareCents)}${l.fareCapCents >= 0 && l.fareCapCents < l.fareCents ? ` (capped at ${fare(l.fareCapCents)})` : ''}</span></div>
        ${l.problem ? `<div class="small neg">${esc(l.problem)}</div>`
          : `<div class="small">A bus every ${l.headwayMinutes} min (${l.roundTripMinutes} min round trip) · ${l.riders.toLocaleString('en-US')} riders yesterday · ${l.loadPercent}% full at the peak</div>`}
        <div class="small muted">Boardings: ${(l.stopLeaseIds as number[]).map((s, i) => `<a href="#" data-jumplot="${s}">${stopName(s)}</a> ${l.boardings[i] ?? 0}`).join(' → ')}</div>
        <div class="small">Last month: fares ${money(l.lastMonth.fares)}, costs ${money(l.lastMonth.wages + l.lastMonth.upkeep + l.lastMonth.rent)}, <b class="${l.lastMonth.profit >= 0 ? 'pos' : 'neg'}">${money(l.lastMonth.profit)}</b></div>
        ${mine ? `<div class="row-flex" style="margin-top:4px">
          <span>Buses</span><button class="mini" data-buses="${l.id}" data-n="${l.buses - 1}" ${l.buses === 0 ? 'disabled' : ''}>−</button><b>${l.buses}</b>${l.broken ? `<span class="small neg">(${l.broken} in the workshop)</span>` : ''}<button class="mini" data-buses="${l.id}" data-n="${l.buses + 1}" title="${money(v.busPrice)} each">+</button>
          <button class="mini" data-edit="${l.id}">Edit</button><button class="mini" data-close="${l.id}">Close</button></div>` : `<div class="small muted">${l.buses} buses</div>`}
      </div>`).join('');
    const d = this.draft;
    const draft = d ? `
      <div class="bus-line draft">
        <b>${d.lineId ? 'Editing a line' : 'New line'}</b>
        <p class="small">Click leased bus stops on the map in the order the line visits them; click the last one again to take it off. It runs both ways.</p>
        <div class="small">${d.stops.length ? d.stops.map(stopName).join(' → ') : '<span class="muted">No stops yet.</span>'}</div>
        <label>Name</label><input type="text" id="draft-name" maxlength="30" value="${esc(d.name)}" placeholder="Route ${(v.lines as Msg[]).length + 1}">
        <label>Fare ($)</label><input type="number" id="draft-fare" min="0" max="10" step="0.25" value="${d.fare / 100}">
        <div class="row-flex" style="margin-top:6px"><button class="active" id="draft-save" ${d.stops.length < 2 ? 'disabled' : ''}>Save line</button><button id="draft-cancel">Cancel</button></div>
      </div>` : '';
    const lotRow = (l: Msg, open: boolean) => `<div class="lot-row ${l.badStanding ? 'bad' : ''}">
        <a href="#" data-jx="${l.x}" data-jy="${l.y}">${l.kind === 'bus-depot' ? 'Depot' : 'Stop'} at ${l.x},${l.y}</a>
        <span class="muted small">${esc(l.terms)}${l.closedDays ? ` · closed ${l.closedDays} days` : ''}${l.badStanding ? ' · bad standing' : ''}</span>
        ${open && mine ? `<button class="mini" data-lease="${l.buildingId}">Lease</button>` : ''}</div>`;
    // The authority screen: the company's rating in each district it has lots in, and what it can buy there.
    const offers = v.offers as Msg[];
    // This client plays as "player": name it as "you" in the city's reasons.
    const yours = (s: string) => s.replace(/\bplayer won't allow it/g, "you haven't allowed it").replace(/\bplayer\b/g, 'you');
    const standing = (v.districts as Msg[]).map((dd) => `
      <div class="standing">
        <div class="row-flex" style="justify-content:space-between"><b>${esc(dd.name)}</b>
          <span class="small">${dd.statue ? '🗿 statue · ' : ''}${dd.exclusiveDays ? `exclusive rights, ${Math.ceil(dd.exclusiveDays / 30)} months left · ` : ''}rating <b class="${dd.rating < 0 ? 'neg' : 'pos'}">${dd.rating}</b></span></div>
        <div class="rating-bar"><span style="left:50%;width:${Math.abs(dd.rating) / 20}%;${dd.rating < 0 ? `transform:translateX(-100%);background:var(--bad)` : ''}"></span></div>
        ${mine ? `<div class="row-flex" style="flex-wrap:wrap;margin-top:4px">${offers.map((o) => `<button class="mini" data-buy="${esc(o.kind)}" data-district="${dd.id}"
          ${o.allowed ? `title="${money(o.cost)}"` : `disabled title="${esc(yours(o.whyNot))}"`}>${esc(o.name)} · ${money(o.cost)}</button>`).join('')}</div>` : ''}
      </div>`).join('');
    el.innerHTML = `
      <h3>${esc(v.name)}</h3>
      ${this.host.city.stats.speed === 0 ? '<p class="muted small">Paused: the company hears about new lots and hand-offs when the day turns.</p>' : ''}
      <div class="muted small">${OPS[v.operator] ?? v.operator} · cash <b>${money(v.cash)}</b> · ${v.buses} of ${v.busCapacity} buses · service ${v.serviceQuality}/100</div>
      <div class="row-flex" style="margin:6px 0">
        ${mine ? '<button id="tc-handoff">Hand to the computer</button>' : '<button class="active" id="tc-claim">Run it yourself</button>'}
        ${mine && !d ? '<button id="tc-new">New line…</button>' : ''}
      </div>
      ${draft}
      <h4>Lines</h4>
      ${lines || '<p class="muted">No lines yet. Lease a depot and two or more bus stops, then draw a line through them.</p>'}
      <h4>Last month</h4><table class="kv">${books(m)}</table>
      <h4>Standing in the districts</h4>
      ${standing || '<p class="muted small">No lots yet, so no standing anywhere.</p>'}
      <p class="muted small">Good service, riders and jobs raise a rating; bad standing and breakdowns lower it. Below ${-400} the district turns down requests for more lots.${offers.some((o) => !o.allowed) ? ` ${esc(offers.filter((o) => !o.allowed).map((o) => yours(o.whyNot)).join(' '))} Players set these under Mayor → House rules.` : ''}</p>
      <h4>Lots</h4>
      ${(v.lots as Msg[]).map((l) => lotRow(l, false)).join('') || '<p class="muted small">None leased yet.</p>'}
      ${(v.openLots as Msg[]).length ? `<div class="muted small" style="margin-top:6px">Free to lease on the city's standard terms:</div>${(v.openLots as Msg[]).map((l) => lotRow(l, true)).join('')}`
        : '<p class="muted small">No free stops or depots. The mayor builds them (Place building → Transit).</p>'}
      <h4>Company log</h4>
      <div class="report-notes">${(v.log as string[]).slice().reverse().map((s) => `<div class="note">${esc(s)}</div>`).join('')}</div>`;
    const on = (sel: string, f: (b: HTMLElement) => void) => el.querySelectorAll<HTMLElement>(sel).forEach((b) => b.addEventListener('click', (e) => { e.preventDefault(); f(b); }));
    on('#tc-claim', () => this.host.conn.action({ seat: { seat: GAME, owner: 'player', level: 0, style: 0, spendCap: 25000 } }));
    on('#tc-handoff', () => this.host.conn.action({ seat: { seat: GAME, owner: '', level: 3, style: 0, spendCap: 25000 } }));
    on('#tc-new', () => this.startDraft());
    on('[data-buy]', (b) => this.command({ standing: { kind: b.dataset.buy, district: Number(b.dataset.district) } }));
    on('[data-edit]', (b) => this.startDraft((v.lines as Msg[]).find((l) => l.id === Number(b.dataset.edit))));
    on('[data-close]', (b) => this.command({ deleteLine: { lineId: Number(b.dataset.close) } }));
    on('[data-buses]', (b) => this.command({ buses: { lineId: Number(b.dataset.buses), buses: Number(b.dataset.n) } }));
    on('[data-lease]', (b) => this.host.conn.action({ lease: { game: GAME, buildingId: Number(b.dataset.lease) } }));
    on('[data-jx]', (b) => this.host.jumpTo(Number(b.dataset.jx), Number(b.dataset.jy)));
    on('[data-jumplot]', (b) => { const l = this.lot(Number(b.dataset.jumplot)); if (l) this.host.jumpTo(l.x, l.y); });
    on('#draft-cancel', () => this.endDraft());
    on('#draft-save', () => {
      if (!this.draft) return;
      const name = ($<HTMLInputElement>('#draft-name').value || '').trim();
      const fareCents = Math.round(Number($<HTMLInputElement>('#draft-fare').value || 0) * 100);
      this.command({ saveLine: { lineId: this.draft.lineId, name, stopLeaseIds: this.draft.stops, fareCents } });
      this.endDraft();
    });
    el.querySelector('#draft-name')?.addEventListener('input', (e) => { if (this.draft) this.draft.name = (e.target as HTMLInputElement).value; });
    el.querySelector('#draft-fare')?.addEventListener('input', (e) => { if (this.draft) this.draft.fare = Math.round(Number((e.target as HTMLInputElement).value) * 100); });
  }
}
