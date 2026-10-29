// The bus company's own view. Switching to it swaps the city's top bar, tools and panel for the company's: its
// cash and buses, tools for leasing lots and drawing lines, and tabs for lines, lots, money, standing and the log.
// The map stays, dimmed, with the company's lots and routes on top. It can also open in a window of its own
// (?view=company), next to a window on the city.
import { decodeTransportView, encodeTransportCommand, type Connection, type Msg } from './net';
import type { CompanyLot } from './render';
import type { City } from './state';

export interface TransportHost {
  conn: Connection;
  city: City;
  jumpTo(x: number, y: number): void;
  toast(text: string, severity?: string): void;
  /** Lines to draw on the map, with their buses. */
  setLines(lines: { color: string; cells: number[]; buses: number; running: boolean }[]): void;
  /** What the company view draws on the map; null for the city view. */
  setCompanyMap(m: { lots: CompanyLot[]; catchment: number; showCatchment: boolean; selected: number | null } | null): void;
  /** The view changed between city and company. */
  onModeChange(company: boolean): void;
}

type CoTool = 'look' | 'lease' | 'line';

/** Each mode's stop and depot lot kinds, and how a lot of each kind is named on a card. */
const STOP_KIND = ['bus-stop', 'tram-stop', 'rail-station'];
const MODE_ICON = ['🚌', '🚋', '🚆'];
const LOT_NAME: Record<string, string> = {
  'bus-stop': 'Stop', 'bus-depot': 'Depot', 'tram-stop': 'Tram stop', 'tram-depot': 'Tram depot', 'rail-station': 'Station', 'rail-depot': 'Rail yard',
};
const modeOfStop = (kind: string) => STOP_KIND.indexOf(kind);
type CoTab = 'lines' | 'lots' | 'money' | 'standing' | 'log';

const GAME = 'transport';
const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector(sel) as T;
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
const money = (n: number) => (n < 0 ? '-$' : '$') + Math.abs(Math.round(n)).toLocaleString('en-US');
const fare = (c: number) => (c === 0 ? 'free' : `$${(c / 100).toFixed(2)}`);
const plural = (n: number, one: string, many = `${one}s`) => `${n.toLocaleString('en-US')} ${n === 1 ? one : many}`;
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
// Name this client's player as "you" in the city's reasons.
const yours = (s: string, me: string) => {
  const name = me.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return s.replace(new RegExp(`\\b${name} won't allow it`, 'g'), "you haven't allowed it").replace(new RegExp(`\\b${name}\\b`, 'g'), 'you');
};

export class Transport {
  view: Msg | null = null;
  /** True while the company's view is up instead of the city's. */
  company = false;
  private tool: CoTool = 'look';
  private tab: CoTab = 'lines';
  private showCatchment = true;
  /** The lot picked with the look tool, by building id. */
  private selected: number | null = null;
  /** The line being drawn: its stops (lease ids) in order. */
  private draft: { lineId: number; name: string; stops: number[]; fare: number; mode: number } | null = null;

  constructor(private readonly host: TransportHost) {
    $('#btn-company').addEventListener('click', () => this.setMode(true));
    $('#co-city').addEventListener('click', () => this.setMode(false));
    $('#co-window').addEventListener('click', () => window.open(`${location.pathname}?view=company`, 'toycity-company'));
    document.querySelectorAll<HTMLButtonElement>('#co-speed button').forEach((b) =>
      b.addEventListener('click', () => this.host.conn.send({ speed: { speed: Number(b.dataset.speed) } })));
    document.querySelectorAll<HTMLButtonElement>('#co-tabs button').forEach((b) =>
      b.addEventListener('click', () => this.showTab(b.dataset.cotab as CoTab)));
    if (new URLSearchParams(location.search).get('view') === 'company') this.setMode(true);
  }

  // -------------------------------------------------------------------------
  // Switching views
  // -------------------------------------------------------------------------

  setMode(company: boolean) {
    this.company = company;
    document.body.classList.toggle('company', company);
    if (!company) { this.draft = null; this.tool = 'look'; }
    document.title = company ? `${this.view?.name ?? 'Bus company'} · ToyCity` : 'ToyCity';
    this.host.onModeChange(company);
    this.publishMap();
    this.render();
  }

  private showTab(tab: CoTab) {
    this.tab = tab;
    this.renderPanel();
  }

  private setTool(t: CoTool) {
    this.tool = t;
    if (t === 'line' && !this.draft) this.draft = { lineId: 0, name: '', stops: [], fare: 150, mode: -1 };
    if (t !== 'line') this.draft = null;
    if (t === 'line') this.tab = 'lines';
    this.render();
  }

  // -------------------------------------------------------------------------
  // From the server
  // -------------------------------------------------------------------------

  onView(body: Msg) {
    if (body.game !== GAME) return;
    this.view = decodeTransportView(body.payload as Uint8Array);
    this.public = body.seat === 'public';
    $('#btn-company').classList.remove('hidden');
    $('#btn-company').textContent = `🚌 ${this.view.name}`;
    this.host.setLines((this.view.lines as Msg[]).filter((l) => l.routeCells.length > 1).map((l) => ({
      color: l.color, cells: l.routeCells, buses: l.buses - l.broken, running: l.headwayMinutes > 0,
    })));
    this.publishMap();
    this.render();
  }

  /** The city's top-bar figures changed (the date, the connection): the company's bar shows them too. */
  onCityTick() {
    if (!this.company) return;
    this.renderTop();
  }

  private command(cmd: Msg) {
    this.host.conn.action({ sideGame: { game: GAME, seat: 'operator', payload: encodeTransportCommand(cmd) } });
  }

  private get mine() { return this.view?.operator === 'manual' && this.view.owner === (this.host.conn.player || 'player'); }
  /** Another player runs the company: this is the public view, without its books, standing or log. */
  private public = false;

  private lot(leaseId: number): Msg | undefined {
    return (this.view?.lots as Msg[] | undefined)?.find((l) => l.leaseId === leaseId);
  }

  private allLots(): Msg[] {
    return this.view ? [...(this.view.lots as Msg[]), ...(this.view.openLots as Msg[])] : [];
  }

  private publishMap() {
    if (!this.company || !this.view) { this.host.setCompanyMap(null); return; }
    const city = this.host.city;
    const lots: CompanyLot[] = this.allLots().map((l) => {
      const b = city.buildings.get(l.buildingId);
      const t = b ? city.typeById.get(b.typeId) : undefined;
      return {
        buildingId: l.buildingId, kind: l.kind, x: l.x, y: l.y, w: b?.width ?? 1, h: b?.height ?? 1, leased: l.leaseId > 0, closed: l.closedDays > 0,
        reach: t?.catchment ?? 0, side: t?.roadside && b ? b.side : -1,
      };
    });
    this.host.setCompanyMap({ lots, catchment: this.catchment(), showCatchment: this.showCatchment, selected: this.selected });
  }

  private catchment() { return this.host.city.typeById.get('bus-stop')?.catchment ?? 8; }

  /** Homes and workplaces within walking reach of a stop, from the city's map. */
  private reach(l: Msg) {
    const city = this.host.city;
    const stop = city.buildings.get(l.buildingId);
    const r = (stop && city.typeById.get(stop.typeId)?.catchment) || this.catchment();
    const sw = stop?.width ?? 1, sh = stop?.height ?? 1;
    let homes = 0, people = 0, sites = 0, jobs = 0;
    for (const b of city.buildings.values()) {
      const dx = Math.max(0, b.x - (l.x + sw - 1), l.x - (b.x + b.width - 1));
      const dy = Math.max(0, b.y - (l.y + sh - 1), l.y - (b.y + b.height - 1));
      if (dx + dy > r) continue;
      if (b.residents > 0) { homes++; people += b.residents; }
      if (b.jobs > 0 && b.id !== l.buildingId) { sites++; jobs += b.jobs; }
    }
    return { homes, people, sites, jobs };
  }

  // -------------------------------------------------------------------------
  // The map
  // -------------------------------------------------------------------------

  /** A click on the map in the company's view. */
  mapClick(x: number, y: number) {
    if (!this.view) return;
    const city = this.host.city, lots = this.allLots();
    const near = [city.buildingAtCell(x, y), ...city.stopsAt(x, y), ...city.stopsFacing(x, y)].filter(Boolean).map((b) => b!.id);
    const lot = near.map((id) => lots.find((l) => l.buildingId === id)).find(Boolean);
    if (this.tool === 'line' && this.draft) {
      const d = this.draft;
      if (!lot || lot.leaseId === 0 || modeOfStop(lot.kind) < 0) {
        this.host.toast('Click a stop or station the company leases (solid yellow). Lease free ones (dashed) with the Lease tool.', 'warning');
        return;
      }
      // The first stop picks the mode: a line is all bus stops, all tram stops or all stations.
      if (d.mode >= 0 && d.stops.length && modeOfStop(lot.kind) !== d.mode) {
        this.host.toast(`This is a ${['bus', 'tram', 'rail'][d.mode]} line: click its own kind of stop.`, 'warning');
        return;
      }
      if (d.mode < 0 || (!d.stops.length && !d.lineId)) d.mode = modeOfStop(lot.kind);
      const s = d.stops;
      if (s[s.length - 1] === lot.leaseId) s.pop();
      else if (s.includes(lot.leaseId)) this.host.toast('A line visits each stop once.', 'warning');
      else s.push(lot.leaseId);
      this.renderPanel();
      return;
    }
    if (this.tool === 'lease') {
      if (!lot) { this.host.toast('Click a free stop, station or depot (dashed outline).', 'warning'); return; }
      if (lot.leaseId > 0) { this.host.toast('The company already leases that.', 'info'); return; }
      if (!this.mine) { this.host.toast('Run the company yourself to lease lots.', 'warning'); return; }
      this.host.conn.action({ lease: { game: GAME, buildingId: lot.buildingId } });
      return;
    }
    this.selected = lot ? lot.buildingId : null;
    if (lot) this.tab = 'lots';
    else this.host.toast('Click a stop, station or depot to see it.', 'info');
    this.publishMap();
    this.renderPanel();
  }

  // -------------------------------------------------------------------------
  // Drawing the view
  // -------------------------------------------------------------------------

  render() {
    this.renderCityTab();
    if (!this.company) return;
    this.renderTop();
    this.renderTools();
    this.renderPanel();
  }

  /** The city's Buses tab: a summary and the way into the company's view. */
  private renderCityTab() {
    const el = $('#tab-buses');
    const v = this.view;
    const tab = $('#tabs button[data-tab="buses"]');
    tab.textContent = v && v.lines.length ? `Buses (${v.lines.length})` : 'Buses';
    if (!v) { el.innerHTML = '<p class="muted">No bus company in this city.</p>'; return; }
    const riders = (v.lines as Msg[]).reduce((n, l) => n + l.riders, 0);
    el.innerHTML = `
      <h3>${esc(v.name)}</h3>
      <p class="muted small">${this.mine ? 'You run it.' : `The computer runs it (${esc(v.operator)}).`} ${v.lines.length} line${v.lines.length === 1 ? '' : 's'}, ${v.buses} buses, ${riders.toLocaleString('en-US')} riders yesterday, ${v.lots.length} lots leased.</p>
      <div class="row-flex" style="margin:8px 0"><button class="active" id="tc-open">🚌 Switch to the company</button><button id="tc-window">⧉ Own window</button></div>
      <p class="muted small">The company has its own view: its lots and routes on the map, and tools for leasing lots and drawing lines. You can run it in a second window while this one shows the city.</p>
      <p class="muted small">Its routes show on the city map under Layers → Bus routes.</p>`;
    $('#tc-open').addEventListener('click', () => this.setMode(true));
    $('#tc-window').addEventListener('click', () => window.open(`${location.pathname}?view=company`, 'toycity-company'));
  }

  private renderTop() {
    const v = this.view;
    const s = this.host.city.stats;
    $('#co-date').textContent = s.month ? `${MONTHS[s.month - 1]} ${s.day}, ${s.year}` : '…';
    document.querySelectorAll<HTMLButtonElement>('#co-speed button').forEach((b) => b.classList.toggle('active', Number(b.dataset.speed) === s.speed));
    $('#co-conn').className = $('#conn').className;
    if (!v) { $('#co-name').textContent = 'No bus company'; return; }
    $('#co-name').textContent = v.name;
    const cash = $('#co-cash');
    cash.textContent = this.public ? 'books private' : money(v.cash);
    cash.classList.toggle('neg', !this.public && v.cash < 0);
    $('#co-buses').textContent = `${v.buses} of ${v.busCapacity} buses`;
    $('#co-quality').textContent = `service ${v.serviceQuality}/100`;
    const seat = $('#co-seat');
    seat.innerHTML = this.mine
      ? `<span class="note">You run it</span><button class="mini" id="co-handoff">Hand to the computer</button>`
      : this.public ? `<span class="note">${esc(v.owner)} runs it</span>`
      : `<span class="note">The computer runs it (${esc(v.operator)})</span><button class="mini active" id="co-claim">Run it yourself</button>`;
    seat.querySelector('#co-claim')?.addEventListener('click', () => this.host.conn.action({ seat: { seat: GAME, owner: this.host.conn.player || 'player', level: 0, style: 0, spendCap: 25000 } }));
    seat.querySelector('#co-handoff')?.addEventListener('click', () => this.host.conn.action({ seat: { seat: GAME, owner: '', level: 3, style: 0, spendCap: 25000 } }));
  }

  private renderTools() {
    const el = $('#co-tools');
    const btn = (t: CoTool, label: string, help: string) =>
      `<button data-cotool="${t}" class="${this.tool === t ? 'active' : ''}" title="${esc(help)}"><span>${label}</span></button>`;
    const tips: Record<CoTool, string> = {
      look: 'Click a stop or depot to see who it reaches and how it does.',
      lease: 'Click a free stop, station or depot (dashed) to lease it on the city’s standard terms.',
      line: 'Click the company’s stops (solid yellow) in the order the line visits them. Click the last one again to take it off.',
    };
    el.innerHTML = `
      <h4>Tools</h4>
      ${btn('look', '🔍 Look', 'See a stop or depot')}
      ${this.mine ? btn('lease', '📝 Lease lots', 'Lease free stops and depots') : ''}
      ${this.mine ? btn('line', '✏️ Draw a line', 'Draw a new bus, tram or train line through the company’s stops') : ''}
      <div class="tip">${tips[this.tool]}</div>
      <h4>Map</h4>
      <button id="co-catch" class="${this.showCatchment ? 'active' : ''}" title="Shade the cells within walking reach of each stop"><span>Walking reach</span></button>
      <div class="tip">People ride if they live in reach of one of the company's stops and work in reach of another, and the bus beats driving or cycling.</div>
      <h4>Key</h4>
      <div class="tip">🟨 the company's lots<br>⬚ free to lease<br>— a running line, ┅ one that isn't</div>`;
    el.querySelectorAll<HTMLButtonElement>('button[data-cotool]').forEach((b) => b.addEventListener('click', () => this.setTool(b.dataset.cotool as CoTool)));
    $('#co-catch').addEventListener('click', () => { this.showCatchment = !this.showCatchment; this.publishMap(); this.renderTools(); });
  }

  private renderPanel() {
    if (!this.company) return;
    document.querySelectorAll<HTMLButtonElement>('#co-tabs button').forEach((b) => b.classList.toggle('active', b.dataset.cotab === this.tab));
    const el = $('#co-body');
    // Don't rebuild the panel under someone typing in it; catch up when they leave the field.
    const focused = document.activeElement;
    if (focused instanceof HTMLInputElement && el.contains(focused)) {
      // A moment's delay, so a click that moved focus (on Save, say) lands before the panel is rebuilt.
      focused.addEventListener('blur', () => setTimeout(() => this.renderPanel(), 250), { once: true });
      return;
    }
    const v = this.view;
    if (!v) { el.innerHTML = '<p class="muted">No bus company in this city.</p>'; return; }
    if (this.public && ['money', 'standing', 'log'].includes(this.tab)) {
      el.innerHTML = `<p class="muted">${esc(v.owner)} runs this company. Its books, its standing with the city and its log are theirs to see.</p>`;
      return;
    }
    const paused = this.host.city.stats.speed === 0 ? '<div class="co-hint">Paused: the company hears about new lots, lines and purchases when the day turns.</div>' : '';
    el.innerHTML = paused + ({ lines: () => this.linesHtml(v), lots: () => this.lotsHtml(v), money: () => this.moneyHtml(v), standing: () => this.standingHtml(v), log: () => this.logHtml(v) })[this.tab]();
    this.wirePanel(el, v);
  }

  private stopName(leaseId: number) {
    const l = this.lot(leaseId);
    return l ? `${l.x},${l.y}` : '?';
  }

  private linesHtml(v: Msg) {
    const d = this.draft;
    const modes = (v.modes ?? []) as Msg[];
    const word = (m: number, n = 1) => (modes[m] ? (n === 1 ? modes[m].word : modes[m].plural) : n === 1 ? 'bus' : 'buses');
    const draft = d ? `
      <div class="bus-line draft">
        <b>${d.lineId ? 'Editing a line' : 'New line'}${d.mode >= 0 ? ` ${MODE_ICON[d.mode]}` : ''}</b>
        <p class="small">Click the company's stops on the map in the order the line visits them. It runs both ways. The first stop decides what runs: buses from bus stops, trams from tram stops, trains from stations.</p>
        <div class="small">${d.stops.length ? d.stops.map((s) => this.stopName(s)).join(' → ') : '<span class="muted">No stops yet.</span>'}</div>
        <label>Name</label><input type="text" id="draft-name" maxlength="30" value="${esc(d.name)}" placeholder="Route ${(v.lines as Msg[]).length + 1}">
        <label>Fare ($)</label><input type="number" id="draft-fare" min="0" max="10" step="0.25" value="${d.fare / 100}">
        <div class="row-flex" style="margin-top:6px"><button class="active" id="draft-save" ${d.stops.length < 2 ? 'disabled' : ''}>Save line</button><button id="draft-cancel">Cancel</button></div>
      </div>` : '';
    const lines = (v.lines as Msg[]).map((l) => {
      const reach = (l.stopLeaseIds as number[]).map((s) => this.lot(s)).filter(Boolean).map((s) => this.reach(s!));
      const homes = reach.reduce((n, r) => n + r.homes, 0), jobs = reach.reduce((n, r) => n + r.jobs, 0);
      const m = l.mode as number;
      const why = l.problem ? `<div class="co-warn">Not running: ${esc(l.problem)}</div>`
        : l.riders === 0 ? `<div class="co-hint">No riders yet. Riders live near one of its stops and work near another (${plural(homes, 'home')} and ${plural(jobs, 'job')} in reach of its stops). People re-think their commute about once a month, so a new line takes a month to fill. More stops where homes and jobs are, a lower fare or more ${word(m, 2)} all help.</div>` : '';
      const veh = l.vehicles as Msg[];
      const count = (st: string) => veh.filter((x) => x.state === st).length;
      const fleet = `${count('running')} running${count('spare') ? `, ${count('spare')} spare` : ''}${count('workshop') ? `, ${count('workshop')} being serviced` : ''}${count('broken') ? `, <span class="neg">${count('broken')} broken down</span>` : ''}`;
      const oldest = veh.reduce((a, x) => Math.max(a, x.ageDays), 0);
      const cond = l.condition as number;
      return `
      <div class="bus-line" style="border-left-color:${esc(l.color)}">
        <div class="row-flex" style="justify-content:space-between"><b>${MODE_ICON[m] ?? ''} ${esc(l.name)}</b><span class="muted small">${l.stopLeaseIds.length} stops · ${fare(l.fareCents)}${l.fareCapCents >= 0 && l.fareCapCents < l.fareCents ? ` (capped at ${fare(l.fareCapCents)})` : ''}</span></div>
        ${l.problem ? '' : `<div class="small">A ${word(m)} every ${l.headwayMinutes} min (${l.roundTripMinutes} min round trip) · <b>${l.riders.toLocaleString('en-US')}</b> riders yesterday · ${l.loadPercent}% full at the peak</div>`}
        ${why}
        <div class="small muted">Boardings: ${(l.stopLeaseIds as number[]).map((s, i) => `<a href="#" data-jumplot="${s}">${this.stopName(s)}</a> ${l.boardings[i] ?? 0}`).join(' → ')}</div>
        <div class="small">Last month: fares ${money(l.lastMonth.fares)}, costs ${money(l.lastMonth.wages + l.lastMonth.upkeep + l.lastMonth.rent)}, <b class="${l.lastMonth.profit >= 0 ? 'pos' : 'neg'}">${money(l.lastMonth.profit)}</b></div>
        ${veh.length ? `<div class="small">Fleet: ${fleet} · condition <b class="${cond < 40 ? 'neg' : cond < 70 ? '' : 'pos'}">${cond}%</b> · oldest ${Math.floor(oldest / 360)}y ${Math.floor((oldest % 360) / 30)}m${l.timetableMinutes ? ` · on time ${l.onTimePercent}% last month` : ''}</div>` : ''}
        ${this.mine ? `<div class="row-flex" style="margin-top:4px">
          <span>${word(m, 2).replace(/^./, (c: string) => c.toUpperCase())}</span><button class="mini" data-buses="${l.id}" data-n="${l.buses - 1}" ${l.buses === 0 ? 'disabled' : ''}>−</button><b>${l.buses}</b><button class="mini" data-buses="${l.id}" data-n="${l.buses + 1}" title="${money(modes[m]?.price ?? v.busPrice)} each">+</button>
          <button class="mini" data-renew="${l.id}" ${l.buses ? '' : 'disabled'} title="Sell the oldest and buy a new one">Replace oldest</button>
          <button class="mini" data-edit="${l.id}">Edit</button><button class="mini" data-close="${l.id}">Close</button></div>
        <div class="row-flex small" style="margin-top:4px">
          <span title="Run a ${word(m)} this often and keep the rest as spares, or run them all">Timetable</span>
          <button class="mini" data-tt="${l.id}" data-n="${l.timetableMinutes <= 5 ? 0 : l.timetableMinutes - 5}">−</button><b>${l.timetableMinutes ? `every ${l.timetableMinutes} min (${l.needed} needed)` : `all ${word(m, 2)}`}</b>
          <button class="mini" data-tt="${l.id}" data-n="${Math.min(120, l.timetableMinutes ? l.timetableMinutes + 5 : 5)}">+</button>
          <span title="Each ${word(m)} goes to the depot this often; neglected ones wear out and break down" style="margin-left:8px">Service</span>
          <button class="mini" data-svc="${l.id}" data-n="${l.serviceDays <= 7 ? 0 : l.serviceDays - 7}">−</button><b>${l.serviceDays ? `every ${l.serviceDays} days` : '<span class="neg">never</span>'}</b>
          <button class="mini" data-svc="${l.id}" data-n="${Math.min(180, l.serviceDays ? l.serviceDays + 7 : 7)}">+</button></div>`
          : `<div class="small muted">${l.buses} ${word(m, l.buses)}</div>`}
      </div>`;
    }).join('');
    const has = (kind: string) => (v.lots as Msg[]).some((l) => l.kind === kind);
    const stops = (v.lots as Msg[]).filter((l) => modeOfStop(l.kind) >= 0).length;
    const start = !v.lines.length && !d
      ? `<div class="co-hint">To start: lease a depot and two or more stops of the same kind with <b>Lease lots</b> (bus stops and a bus depot, tram stops and a tram depot, or stations and a rail yard), then <b>Draw a line</b> through the stops and give it vehicles. ${has('bus-depot') || has('tram-depot') || has('rail-depot') ? '✓' : '○'} depot · ${stops >= 2 ? '✓' : '○'} two stops${!this.mine ? ' · Run the company yourself first (top bar).' : ''}</div>` : '';
    return `${draft}${start}${lines || (d ? '' : '<p class="muted">No lines yet.</p>')}
      ${this.mine && !d ? '<button id="tc-new" style="margin-top:8px">✏️ New line…</button>' : ''}`;
  }

  private lotsHtml(v: Msg) {
    const card = (l: Msg) => {
      const r = this.reach(l);
      const sel = l.buildingId === this.selected;
      const boards = (v.lines as Msg[]).reduce((n, line) => {
        const i = (line.stopLeaseIds as number[]).indexOf(l.leaseId);
        return n + (i >= 0 ? line.boardings[i] ?? 0 : 0);
      }, 0);
      const district = (v.districts as Msg[]).find((dd) => dd.id === l.district);
      return `<div class="lot-card ${sel ? 'sel' : ''}">
        <div class="row-flex" style="justify-content:space-between"><b><a href="#" data-jx="${l.x}" data-jy="${l.y}">${LOT_NAME[l.kind] ?? 'Lot'} at ${l.x},${l.y}</a></b>
          <span class="small ${l.leaseId ? 'pos' : 'muted'}">${l.leaseId ? 'leased' : 'free to lease'}</span></div>
        <div class="small muted">${esc(l.terms)}${district ? ` · ${esc(district.name)}, rating ${district.rating}` : ''}</div>
        ${modeOfStop(l.kind) >= 0 ? `<div class="small">In walking reach: ${plural(r.homes, 'home')} (${plural(r.people, 'person', 'people')}), ${plural(r.sites, 'workplace')} (${plural(r.jobs, 'job')})${l.leaseId ? ` · ${plural(boards, 'boarding')} yesterday` : ''}</div>` : ''}
        ${l.closedDays ? `<div class="small neg">Closed for ${l.closedDays} more days (fire or disaster nearby).</div>` : ''}
        ${l.badStanding ? '<div class="small neg">Bad standing with the city.</div>' : ''}
        ${this.mine ? (l.leaseId ? `<button class="mini" data-release="${l.leaseId}" style="margin-top:4px">Give up the lease</button>` : `<button class="mini active" data-lease="${l.buildingId}" style="margin-top:4px">Lease it</button>`) : ''}
      </div>`;
    };
    const leased = v.lots as Msg[], open = v.openLots as Msg[];
    const sorted = (list: Msg[]) => [...list].sort((a, b) => Number(b.buildingId === this.selected) - Number(a.buildingId === this.selected));
    return `<h4>The company's lots</h4>${sorted(leased).map(card).join('') || '<p class="muted small">None leased yet.</p>'}
      <h4>Free to lease</h4>${sorted(open).map(card).join('') || '<p class="muted small">No free stops or depots. The mayor builds them (Place building → Transit).</p>'}`;
  }

  private moneyHtml(v: Msg) {
    const books = (mm: Msg) => `<tr><td>Fares</td><td class="num pos">${money(mm.fares)}</td></tr>
      ${mm.subsidy ? `<tr><td>From the city</td><td class="num pos">${money(mm.subsidy)}</td></tr>` : ''}
      <tr><td>Drivers' wages</td><td class="num neg">-${money(mm.wages)}</td></tr>
      <tr><td>Bus upkeep</td><td class="num neg">-${money(mm.upkeep)}</td></tr>
      <tr><td>Rent and fare tax</td><td class="num neg">-${money(mm.rent)}</td></tr>
      ${mm.standing ? `<tr><td>Standing (ads, statues, rights)</td><td class="num neg">-${money(mm.standing)}</td></tr>` : ''}
      <tr class="total"><td>Profit</td><td class="num ${mm.profit >= 0 ? 'pos' : 'neg'}">${money(mm.profit)}</td></tr>`;
    return `<p>Cash <b class="${v.cash < 0 ? 'neg' : ''}">${money(v.cash)}</b>. A bus costs ${money(v.busPrice)} and sells for half that.</p>
      <h4>Last month</h4><table class="kv">${books(v.lastMonth)}</table>
      <h4>This month so far</h4><table class="kv">${books(v.thisMonth)}</table>
      <h4>By line, last month</h4>
      <table class="kv">${(v.lines as Msg[]).map((l) => `<tr><td>${esc(l.name)}</td><td class="num ${l.lastMonth.profit >= 0 ? 'pos' : 'neg'}">${money(l.lastMonth.profit)}</td></tr>`).join('') || '<tr><td class="muted">No lines.</td></tr>'}</table>`;
  }

  private standingHtml(v: Msg) {
    const offers = v.offers as Msg[];
    const standing = (v.districts as Msg[]).map((dd) => `
      <div class="standing">
        <div class="row-flex" style="justify-content:space-between"><b>${esc(dd.name)}</b>
          <span class="small">${dd.statue ? '🗿 statue · ' : ''}${dd.exclusiveDays ? `exclusive rights, ${Math.ceil(dd.exclusiveDays / 30)} months left · ` : ''}rating <b class="${dd.rating < 0 ? 'neg' : 'pos'}">${dd.rating}</b></span></div>
        <div class="rating-bar"><span style="left:50%;width:${Math.abs(dd.rating) / 20}%;${dd.rating < 0 ? 'transform:translateX(-100%);background:var(--bad)' : ''}"></span></div>
        ${this.mine ? `<div class="row-flex" style="flex-wrap:wrap;margin-top:4px">${offers.map((o) => `<button class="mini" data-buy="${esc(o.kind)}" data-district="${dd.id}"
          ${o.allowed ? `title="${money(o.cost)}"` : `disabled title="${esc(yours(o.whyNot, this.host.conn.player || 'player'))}"`}>${esc(o.name)} · ${money(o.cost)}</button>`).join('')}</div>` : ''}
      </div>`).join('');
    return `${standing || '<p class="muted small">No lots yet, so no standing anywhere.</p>'}
      <p class="muted small">Good service, riders and jobs raise a rating; bad standing and breakdowns lower it. Below -400 the district turns down requests for more lots.${offers.some((o) => !o.allowed) ? ` ${esc(offers.filter((o) => !o.allowed).map((o) => yours(o.whyNot, this.host.conn.player || 'player')).join(' '))} Players set these in the city view under Mayor → House rules.` : ''}</p>`;
  }

  private logHtml(v: Msg) {
    return `<div class="report-notes">${(v.log as string[]).slice().reverse().map((s) => `<div class="note">${esc(s)}</div>`).join('') || '<p class="muted small">Nothing yet.</p>'}</div>`;
  }

  private wirePanel(el: HTMLElement, v: Msg) {
    const on = (sel: string, f: (b: HTMLElement) => void) => el.querySelectorAll<HTMLElement>(sel).forEach((b) => b.addEventListener('click', (e) => { e.preventDefault(); f(b); }));
    on('#tc-new', () => this.setTool('line'));
    on('[data-edit]', (b) => {
      const line = (v.lines as Msg[]).find((l) => l.id === Number(b.dataset.edit))!;
      this.draft = { lineId: line.id, name: line.name, stops: [...line.stopLeaseIds], fare: line.fareCents, mode: line.mode };
      this.tool = 'line';
      this.render();
    });
    on('[data-close]', (b) => this.command({ deleteLine: { lineId: Number(b.dataset.close) } }));
    on('[data-buses]', (b) => this.command({ buses: { lineId: Number(b.dataset.buses), buses: Number(b.dataset.n) } }));
    on('[data-tt]', (b) => this.command({ timetable: { lineId: Number(b.dataset.tt), headwayMinutes: Number(b.dataset.n) } }));
    on('[data-svc]', (b) => this.command({ service: { lineId: Number(b.dataset.svc), everyDays: Number(b.dataset.n) } }));
    on('[data-renew]', (b) => this.command({ renew: { lineId: Number(b.dataset.renew) } }));
    on('[data-buy]', (b) => this.command({ standing: { kind: b.dataset.buy, district: Number(b.dataset.district) } }));
    on('[data-lease]', (b) => this.host.conn.action({ lease: { game: GAME, buildingId: Number(b.dataset.lease) } }));
    on('[data-release]', (b) => this.host.conn.action({ endLease: { leaseId: Number(b.dataset.release), byOperator: true } }));
    on('[data-jx]', (b) => this.host.jumpTo(Number(b.dataset.jx), Number(b.dataset.jy)));
    on('[data-jumplot]', (b) => { const l = this.lot(Number(b.dataset.jumplot)); if (l) this.host.jumpTo(l.x, l.y); });
    on('#draft-cancel', () => this.setTool('look'));
    on('#draft-save', () => {
      if (!this.draft) return;
      const name = ($<HTMLInputElement>('#draft-name').value || '').trim();
      const fareCents = Math.round(Number($<HTMLInputElement>('#draft-fare').value || 0) * 100);
      this.command({ saveLine: { lineId: this.draft.lineId, name, stopLeaseIds: this.draft.stops, fareCents, mode: Math.max(0, this.draft.mode) } });
      this.setTool('look');
    });
    el.querySelector('#draft-name')?.addEventListener('input', (e) => { if (this.draft) this.draft.name = (e.target as HTMLInputElement).value; });
    el.querySelector('#draft-fare')?.addEventListener('input', (e) => { if (this.draft) this.draft.fare = Math.round(Number((e.target as HTMLInputElement).value) * 100); });
  }
}
