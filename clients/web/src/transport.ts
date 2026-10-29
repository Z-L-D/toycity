// The bus company's own view. Switching to it swaps the city's top bar, tools and panel for the company's: its
// cash and buses, tools for leasing lots and drawing lines, and tabs for lines, lots, money, standing and the log.
// The map stays, dimmed, with the company's lots and routes on top. It can also open in a window of its own
// (?view=company), next to a window on the city.
import { decodeTransportView, encodeTransportCommand, type Connection, type Msg } from './net';
import type { CompanyLot, CompanyMap } from './render';
import type { City } from './state';

export interface TransportHost {
  conn: Connection;
  city: City;
  jumpTo(x: number, y: number): void;
  toast(text: string, severity?: string): void;
  /** Lines to draw on the map, with their buses. */
  setLines(lines: { color: string; cells: number[]; buses: number; running: boolean }[]): void;
  /** What the company view draws on the map; null for the city view. */
  setCompanyMap(m: CompanyMap | null): void;
  /** The view changed between city and company. */
  onModeChange(company: boolean): void;
}

type CoTool = 'look' | 'lease' | 'line' | 'signal';

/** Each mode's stop and depot lot kinds, and how a lot of each kind is named on a card. */
const STOP_KIND = ['bus-stop', 'tram-stop', 'rail-station'];
const MODE_ICON = ['🚌', '🚋', '🚆'];
const LOT_NAME: Record<string, string> = {
  'bus-stop': 'Stop', 'bus-depot': 'Depot', 'tram-stop': 'Tram stop', 'tram-depot': 'Tram depot', 'rail-station': 'Station', 'rail-depot': 'Rail yard', 'freight-depot': 'Freight depot',
};
const modeOfStop = (kind: string) => STOP_KIND.indexOf(kind);
const isStop = (kind: string) => modeOfStop(kind) >= 0 || kind === 'freight-depot';
const FREIGHT_ICON: Record<number, string> = { 0: '🚚', 2: '🚂' };
type CoTab = 'lines' | 'lots' | 'money' | 'standing' | 'log';

/** The transit companies: the city's first, and rivals the mayor invites in. */
const isTransport = (game: string) => game === 'transport' || game.startsWith('transport-');
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
  private draft: { lineId: number; name: string; stops: number[]; fare: number; mode: number; freight: boolean; toEdge: boolean } | null = null;
  /** Every transit company's view, by game id, and the one shown. */
  private views = new Map<string, { view: Msg; public: boolean }>();
  private game = 'transport';
  /** The player picked the company to show: don't switch to their own when it arrives. */
  private picked = false;

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
    if (t === 'line' && !this.draft) this.draft = { lineId: 0, name: '', stops: [], fare: 150, mode: -1, freight: false, toEdge: false };
    if (t !== 'line') this.draft = null;
    if (t === 'line') this.tab = 'lines';
    this.render();
  }

  // -------------------------------------------------------------------------
  // From the server
  // -------------------------------------------------------------------------

  onView(body: Msg) {
    if (!isTransport(body.game)) return;
    this.views.set(body.game, { view: decodeTransportView(body.payload as Uint8Array), public: body.seat === 'public' });
    // Show the company this player runs, when they run one.
    const me = this.host.conn.player || 'player';
    const mine = [...this.views].find(([, x]) => x.view.operator === 'manual' && x.view.owner === me)?.[0];
    if (!this.views.has(this.game) || (mine && !this.picked)) this.game = mine ?? (this.views.has('transport') ? 'transport' : body.game);
    this.show();
  }

  /** Shows the chosen company, and every company's routes on the map. */
  private show() {
    const cur = this.views.get(this.game);
    if (!cur) return;
    this.view = cur.view;
    this.public = cur.public;
    $('#btn-company').classList.remove('hidden');
    $('#btn-company').textContent = `🚌 ${this.view.name}`;
    this.host.setLines([...this.views.values()].flatMap((x) => (x.view.lines as Msg[]).filter((l) => l.routeCells.length > 1).map((l) => ({
      color: l.color, cells: l.routeCells, buses: l.buses - l.broken, running: l.headwayMinutes > 0,
    }))));
    if (this.company) document.title = `${this.view.name} · ToyCity`;
    this.publishMap();
    this.render();
  }

  /** Switches to another transit company. */
  private pick(game: string) {
    if (!this.views.has(game)) return;
    this.game = game;
    this.picked = true;
    this.draft = null;
    this.tool = 'look';
    this.selected = null;
    this.show();
  }

  /** The city's top-bar figures changed (the date, the connection): the company's bar shows them too. */
  onCityTick() {
    if (!this.company) return;
    this.renderTop();
  }

  private command(cmd: Msg) {
    this.host.conn.action({ sideGame: { game: this.game, seat: 'operator', payload: encodeTransportCommand(cmd) } });
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
    const w = city.width || 1;
    const v = this.view;
    this.host.setCompanyMap({
      lots, catchment: this.catchment(), showCatchment: this.showCatchment, selected: this.selected,
      signals: (v.signals as Msg[]).map((sg) => ({ x: sg.cell % w, y: Math.floor(sg.cell / w), kind: sg.kind })),
      reserved: (v.reserved as Msg[]).map((c) => ({ x: c.cell % w, y: Math.floor(c.cell / w), percent: c.percent })),
    });
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
    if (this.tool === 'signal') {
      if (!this.mine) { this.host.toast('Run the company yourself to put up signals.', 'warning'); return; }
      const cell = y * city.width + x;
      const on = (this.view.lines as Msg[]).some((l) => l.mode === 2 && (l.routeCells as number[]).includes(cell));
      const now = (this.view.signals as Msg[]).find((sg) => sg.cell === cell)?.kind ?? 0;
      if (!on && !now) { this.host.toast('Signals go on the company’s own rail lines: click a cell one of its trains runs over.', 'warning'); return; }
      // Each click goes round: a block signal, a path signal, none.
      this.command({ signal: { cell, kind: (now + 1) % 3 } });
      return;
    }
    if (this.tool === 'line' && this.draft) {
      const d = this.draft;
      if (!lot || lot.leaseId === 0 || !isStop(lot.kind)) {
        this.host.toast('Click a stop, station or freight depot the company leases (solid yellow). Lease free ones (dashed) with the Lease tool.', 'warning');
        return;
      }
      const freight = lot.kind === 'freight-depot';
      // The first stop picks what runs: a line is all bus stops, all tram stops, all stations, or all freight depots.
      if (d.stops.length && (freight !== d.freight || (!freight && modeOfStop(lot.kind) !== d.mode))) {
        this.host.toast(d.freight ? 'This is a freight line: click the company’s freight depots.' : `This is a ${['bus', 'tram', 'rail'][d.mode]} line: click its own kind of stop.`, 'warning');
        return;
      }
      if (!d.stops.length && !d.lineId) { d.freight = freight; d.mode = freight ? (d.mode === 2 ? 2 : 0) : modeOfStop(lot.kind); }
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
      this.host.conn.action({ lease: { game: this.game, buildingId: lot.buildingId } });
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
    const me = this.host.conn.player || 'player';
    const companies = [...this.views].map(([id, x]) => {
      const cv = x.view;
      const riders = (cv.lines as Msg[]).reduce((n, l) => n + l.riders, 0);
      const runs = cv.operator === 'manual' ? (cv.owner === me ? 'You run it.' : `${esc(cv.owner)} runs it.`) : `The computer runs it (${esc(cv.operator)}).`;
      return `<h3>${esc(cv.name)}${this.views.size > 1 && id === this.game ? ' <span class="muted small">(shown)</span>' : ''}</h3>
      <p class="muted small">${runs} ${cv.lines.length} line${cv.lines.length === 1 ? '' : 's'}, ${riders.toLocaleString('en-US')} riders yesterday, ${cv.lots.length} lots leased.</p>
      ${this.views.size > 1 && id !== this.game ? `<button class="mini" data-cogame="${esc(id)}">Show this company</button>` : ''}`;
    }).join('');
    el.innerHTML = `
      ${companies}
      <div class="row-flex" style="margin:8px 0"><button class="active" id="tc-open">🚌 Switch to the company</button><button id="tc-window">⧉ Own window</button></div>
      <p class="muted small">The company has its own view: its lots and routes on the map, and tools for leasing lots and drawing lines. You can run it in a second window while this one shows the city.</p>
      <p class="muted small">Its routes show on the city map under Layers → Bus routes.</p>`;
    $('#tc-open').addEventListener('click', () => this.setMode(true));
    el.querySelectorAll<HTMLButtonElement>('[data-cogame]').forEach((b) => b.addEventListener('click', () => this.pick(b.dataset.cogame!)));
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
    if (this.views.size > 1)
      seat.innerHTML += [...this.views].filter(([id]) => id !== this.game).map(([id, x]) => `<button class="mini" data-cogame="${esc(id)}" title="Show the other company">⇄ ${esc(x.view.name)}</button>`).join('');
    seat.querySelector('#co-claim')?.addEventListener('click', () => this.host.conn.action({ seat: { seat: this.game, owner: this.host.conn.player || 'player', level: 0, style: 0, spendCap: 25000 } }));
    seat.querySelector('#co-handoff')?.addEventListener('click', () => this.host.conn.action({ seat: { seat: this.game, owner: '', level: 3, style: 0, spendCap: 25000 } }));
    seat.querySelectorAll<HTMLButtonElement>('[data-cogame]').forEach((b) => b.addEventListener('click', () => this.pick(b.dataset.cogame!)));
  }

  private renderTools() {
    const el = $('#co-tools');
    const btn = (t: CoTool, label: string, help: string) =>
      `<button data-cotool="${t}" class="${this.tool === t ? 'active' : ''}" title="${esc(help)}"><span>${label}</span></button>`;
    const tips: Record<CoTool, string> = {
      look: 'Click a stop or depot to see who it reaches and how it does.',
      lease: 'Click a free stop, station or depot (dashed) to lease it on the city’s standard terms.',
      line: 'Click the company’s stops (solid yellow) in the order the line visits them. Click the last one again to take it off. Freight lines run between freight depots.',
      signal: 'Click a cell on one of the company’s rail lines: a block signal, click again for a path signal, again to take it down. Without signals only one train can use a line.',
    };
    el.innerHTML = `
      <h4>Tools</h4>
      ${btn('look', '🔍 Look', 'See a stop or depot')}
      ${this.mine ? btn('lease', '📝 Lease lots', 'Lease free stops and depots') : ''}
      ${this.mine ? btn('line', '✏️ Draw a line', 'Draw a new bus, tram, train or freight line through the company’s stops') : ''}
      ${this.mine && (this.view?.lines as Msg[] | undefined)?.some((l) => l.mode === 2) ? btn('signal', '🚦 Signals', `Put up rail signals (${money(this.view?.signalPrice ?? 250)} each)`) : ''}
      <div class="tip">${tips[this.tool]}</div>
      <h4>Map</h4>
      <button id="co-catch" class="${this.showCatchment ? 'active' : ''}" title="Shade the cells within walking reach of each stop"><span>Walking reach</span></button>
      <div class="tip">People ride if they live in reach of one of the company's stops and work in reach of another, and the bus beats driving or cycling.</div>
      <h4>Key</h4>
      <div class="tip">🟨 the company's lots<br>⬚ free to lease<br>— a running line, ┅ one that isn't${(this.view?.signals as Msg[] | undefined)?.length || (this.view?.reserved as Msg[] | undefined)?.length ? '<br>🔴 block signal, 🔵 path signal<br>track held: green has room, amber nearly full, red over-asked' : ''}</div>`;
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
    const freightModes = (v.freightModes ?? []) as Msg[];
    const specOf = (l: Msg) => (l.freight ? freightModes.find((f) => f.mode === l.mode) : modes[l.mode]);
    const word = (m: number, n = 1, freight = false) => {
      const sp = specOf({ mode: m, freight });
      return sp ? (n === 1 ? sp.word : sp.plural) : n === 1 ? 'bus' : 'buses';
    };
    const icon = (l: Msg) => (l.freight ? FREIGHT_ICON[l.mode] : MODE_ICON[l.mode]) ?? '';
    const need = d && d.freight && d.toEdge ? 1 : 2;
    const draft = d ? `
      <div class="bus-line draft">
        <b>${d.lineId ? 'Editing a line' : 'New line'}${d.mode >= 0 ? ` ${icon(d)}` : ''}</b>
        <p class="small">Click the company's stops on the map in the order the line visits them. It runs both ways. The first stop decides what runs: buses from bus stops, trams from tram stops, trains from stations, freight from freight depots.</p>
        <div class="small">${d.stops.length ? d.stops.map((s) => this.stopName(s)).join(' → ') : '<span class="muted">No stops yet.</span>'}${d.freight && d.toEdge ? ' → map edge' : ''}</div>
        ${d.freight ? `
        <div class="row-flex small" style="margin-top:4px">${d.lineId ? `<span>${word(d.mode, 2, true)}</span>` : `
          <label><input type="radio" name="draft-fmode" value="0" ${d.mode !== 2 ? 'checked' : ''}> 🚚 Trucks</label>
          <label><input type="radio" name="draft-fmode" value="2" ${d.mode === 2 ? 'checked' : ''}> 🚂 Freight trains</label>`}
          <label title="After the last depot, carry what's left to the map edge: exports, which raise industry's demand"><input type="checkbox" id="draft-edge" ${d.toEdge ? 'checked' : ''}> On to the map edge</label></div>
        <p class="small muted">Goods made by factories near a depot go to the shops near the line's other depots${d.toEdge ? ', and the rest out of the city' : ''}. Each ton pays more the longer the run.</p>`
        : `<label>Fare ($)</label><input type="number" id="draft-fare" min="0" max="10" step="0.25" value="${d.fare / 100}">`}
        <label>Name</label><input type="text" id="draft-name" maxlength="30" value="${esc(d.name)}" placeholder="${d.freight ? 'Freight' : 'Route'} ${(v.lines as Msg[]).length + 1}">
        <div class="row-flex" style="margin-top:6px"><button class="active" id="draft-save" ${d.stops.length < need ? 'disabled' : ''}>Save line</button><button id="draft-cancel">Cancel</button></div>
      </div>` : '';
    const lines = (v.lines as Msg[]).map((l) => {
      const reach = (l.stopLeaseIds as number[]).map((s) => this.lot(s)).filter(Boolean).map((s) => this.reach(s!));
      const homes = reach.reduce((n, r) => n + r.homes, 0), jobs = reach.reduce((n, r) => n + r.jobs, 0);
      const m = l.mode as number;
      const fr = !!l.freight;
      const w = (n = 1) => word(m, n, fr);
      const why = l.problem ? `<div class="co-warn">Not running: ${esc(l.problem)}</div>`
        : fr ? (l.tons === 0 ? `<div class="co-hint">Nothing to carry today. Goods come from factories near its depots and go to shops near another of its depots${l.toEdge ? ' or out to the edge' : ''}. Lease a freight depot by the factories and one by the shops, or run it on to the edge.</div>` : '')
        : l.riders === 0 ? `<div class="co-hint">No riders yet. Riders live near one of its stops and work near another (${plural(homes, 'home')} and ${plural(jobs, 'job')} in reach of its stops). People re-think their commute about once a month, so a new line takes a month to fill. More stops where homes and jobs are, a lower fare or more ${word(m, 2)} all help.</div>` : '';
      const veh = l.vehicles as Msg[];
      const count = (st: string) => veh.filter((x) => x.state === st).length;
      const fleet = `${count('running')} running${count('spare') ? `, ${count('spare')} spare` : ''}${count('workshop') ? `, ${count('workshop')} being serviced` : ''}${count('broken') ? `, <span class="neg">${count('broken')} broken down</span>` : ''}`;
      const oldest = veh.reduce((a, x) => Math.max(a, x.ageDays), 0);
      const cond = l.condition as number;
      return `
      <div class="bus-line" style="border-left-color:${esc(l.color)}">
        <div class="row-flex" style="justify-content:space-between"><b>${icon(l)} ${esc(l.name)}</b><span class="muted small">${fr ? `${plural(l.stopLeaseIds.length, 'depot')}${l.toEdge ? ' + edge' : ''} · freight` : `${l.stopLeaseIds.length} stops · ${fare(l.fareCents)}${l.fareCapCents >= 0 && l.fareCapCents < l.fareCents ? ` (capped at ${fare(l.fareCapCents)})` : ''}`}</span></div>
        ${l.problem ? '' : fr ? `<div class="small">A ${w()} every ${l.headwayMinutes} min (${l.roundTripMinutes} min round trip) · <b>${l.tons.toLocaleString('en-US')}</b> tons carried today</div>`
          : `<div class="small">A ${w()} every ${l.headwayMinutes} min (${l.roundTripMinutes} min round trip) · <b>${l.riders.toLocaleString('en-US')}</b> riders yesterday · ${l.loadPercent}% full at the peak</div>`}
        ${l.signalLimited ? `<div class="co-warn">The signals hold it to a train every ${l.headwayMinutes} min: the track ahead is still held by the last one. Put up signals (🚦) along the line so more trains can share it; path signals keep trains on different lines out of each other's way at junctions.</div>` : ''}
        ${why}
        ${fr ? `<div class="small muted">Depots: ${(l.stopLeaseIds as number[]).map((s) => `<a href="#" data-jumplot="${s}">${this.stopName(s)}</a>`).join(' → ')}${l.toEdge ? ' → edge' : ''}</div>`
          : `<div class="small muted">Boardings: ${(l.stopLeaseIds as number[]).map((s, i) => `<a href="#" data-jumplot="${s}">${this.stopName(s)}</a> ${l.boardings[i] ?? 0}`).join(' → ')}</div>`}
        <div class="small">Last month: ${fr ? 'freight' : 'fares'} ${money(l.lastMonth.fares)}, costs ${money(l.lastMonth.wages + l.lastMonth.upkeep + l.lastMonth.rent)}, <b class="${l.lastMonth.profit >= 0 ? 'pos' : 'neg'}">${money(l.lastMonth.profit)}</b></div>
        ${veh.length ? `<div class="small">Fleet: ${fleet} · condition <b class="${cond < 40 ? 'neg' : cond < 70 ? '' : 'pos'}">${cond}%</b> · oldest ${Math.floor(oldest / 360)}y ${Math.floor((oldest % 360) / 30)}m${l.timetableMinutes ? ` · on time ${l.onTimePercent}% last month` : ''}</div>` : ''}
        ${this.mine ? `<div class="row-flex" style="margin-top:4px">
          <span>${w(2).replace(/^./, (c: string) => c.toUpperCase())}</span><button class="mini" data-buses="${l.id}" data-n="${l.buses - 1}" ${l.buses === 0 ? 'disabled' : ''}>−</button><b>${l.buses}</b><button class="mini" data-buses="${l.id}" data-n="${l.buses + 1}" title="${money(specOf(l)?.price ?? v.busPrice)} each">+</button>
          <button class="mini" data-renew="${l.id}" ${l.buses ? '' : 'disabled'} title="Sell the oldest and buy a new one">Replace oldest</button>
          <button class="mini" data-edit="${l.id}">Edit</button><button class="mini" data-close="${l.id}">Close</button></div>
        <div class="row-flex small" style="margin-top:4px">
          <span title="Run a ${w()} this often and keep the rest as spares, or run them all">Timetable</span>
          <button class="mini" data-tt="${l.id}" data-n="${l.timetableMinutes <= 5 ? 0 : l.timetableMinutes - 5}">−</button><b>${l.timetableMinutes ? `every ${l.timetableMinutes} min (${l.needed} needed)` : `all ${w(2)}`}</b>
          <button class="mini" data-tt="${l.id}" data-n="${Math.min(120, l.timetableMinutes ? l.timetableMinutes + 5 : 5)}">+</button>
          <span title="Each ${w()} goes to the depot this often; neglected ones wear out and break down" style="margin-left:8px">Service</span>
          <button class="mini" data-svc="${l.id}" data-n="${l.serviceDays <= 7 ? 0 : l.serviceDays - 7}">−</button><b>${l.serviceDays ? `every ${l.serviceDays} days` : '<span class="neg">never</span>'}</b>
          <button class="mini" data-svc="${l.id}" data-n="${Math.min(180, l.serviceDays ? l.serviceDays + 7 : 7)}">+</button></div>`
          : `<div class="small muted">${l.buses} ${w(l.buses)}</div>`}
      </div>`;
    }).join('');
    const has = (kind: string) => (v.lots as Msg[]).some((l) => l.kind === kind);
    const stops = (v.lots as Msg[]).filter((l) => isStop(l.kind)).length;
    const start = !v.lines.length && !d
      ? `<div class="co-hint">To start: lease a depot and two or more stops of the same kind with <b>Lease lots</b> (bus stops and a bus depot, tram stops and a tram depot, stations and a rail yard, or two freight depots for freight), then <b>Draw a line</b> through the stops and give it vehicles. ${has('bus-depot') || has('tram-depot') || has('rail-depot') || has('freight-depot') ? '✓' : '○'} depot · ${stops >= 2 ? '✓' : '○'} two stops${!this.mine ? ' · Run the company yourself first (top bar).' : ''}</div>` : '';
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
        ${l.kind === 'freight-depot' && l.leaseId ? `<div class="small">Goods today: ${l.tonsReady.toLocaleString('en-US')} tons ready from factories in reach, ${l.tonsWanted.toLocaleString('en-US')} tons wanted by shops</div>` : ''}
        ${l.closedDays ? `<div class="small neg">Closed for ${l.closedDays} more days (fire or disaster nearby).</div>` : ''}
        ${l.barred ? '<div class="small neg">Another company holds exclusive rights in this district: no line stops here.</div>' : ''}
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
    const books = (mm: Msg) => `<tr><td>Fares and freight</td><td class="num pos">${money(mm.fares)}</td></tr>
      ${mm.subsidy ? `<tr><td>From the city</td><td class="num pos">${money(mm.subsidy)}</td></tr>` : ''}
      <tr><td>Drivers' wages</td><td class="num neg">-${money(mm.wages)}</td></tr>
      <tr><td>Bus upkeep</td><td class="num neg">-${money(mm.upkeep)}</td></tr>
      <tr><td>Rent and fare tax</td><td class="num neg">-${money(mm.rent)}</td></tr>
      ${mm.standing ? `<tr><td>Standing (ads, statues, rights)</td><td class="num neg">-${money(mm.standing)}</td></tr>` : ''}
      <tr class="total"><td>Profit</td><td class="num ${mm.profit >= 0 ? 'pos' : 'neg'}">${money(mm.profit)}</td></tr>`;
    return `<p>Cash <b class="${v.cash < 0 ? 'neg' : ''}">${money(v.cash)}</b>. ${[...(v.modes as Msg[]), ...(v.freightModes as Msg[])].map((m) => `A ${m.word} costs ${money(m.price)}`).join(', ')}; they sell for less as they age.</p>
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
    const tenders = (v.tenders as Msg[]).map((t) => `
      <div class="standing">
        <div class="row-flex" style="justify-content:space-between"><b>${esc(t.name)} franchise</b><span class="small">${t.months} months${t.exclusive ? ', exclusive' : ''} · closes in ${plural(t.daysLeft, 'day')}</span></div>
        <div class="small muted">The city wants at least ${money(t.minBid)}; bids are sealed and the highest wins when it closes, paying its bid then. ${t.exclusive ? 'The winner alone stops in the district.' : 'The winner alone may lease new transit lots in the district.'} The company has ${plural(t.lots, 'lot')} there.${t.yourBid ? ` <b>Your bid: ${money(t.yourBid)}</b>.` : ''}</div>
        ${this.mine ? `<div class="row-flex" style="margin-top:4px"><input type="number" id="bid-${t.district}" min="${t.minBid}" step="500" value="${t.yourBid || t.minBid}" style="width:110px"><button class="mini active" data-bid="${t.district}">${t.yourBid ? 'Change bid' : 'Bid'}</button></div>` : ''}
      </div>`).join('');
    const held = (v.franchises as Msg[]).map((f) => `<div class="small">${esc(f.name)}: ${f.mine ? '<b class="pos">yours</b>' : `held by ${esc(this.views.get(f.holder)?.view.name ?? f.holder)}`}${f.exclusive ? ', exclusive' : ''}, ${Math.ceil(f.daysLeft / 30)} months left</div>`).join('');
    return `${tenders ? `<h4>Out to tender</h4>${tenders}` : ''}${held ? `<h4>Franchises in force</h4>${held}` : ''}
      ${standing || '<p class="muted small">No lots yet, so no standing anywhere.</p>'}
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
      this.draft = { lineId: line.id, name: line.name, stops: [...line.stopLeaseIds], fare: line.fareCents, mode: line.mode, freight: !!line.freight, toEdge: !!line.toEdge };
      this.tool = 'line';
      this.render();
    });
    on('[data-close]', (b) => this.command({ deleteLine: { lineId: Number(b.dataset.close) } }));
    on('[data-buses]', (b) => this.command({ buses: { lineId: Number(b.dataset.buses), buses: Number(b.dataset.n) } }));
    on('[data-tt]', (b) => this.command({ timetable: { lineId: Number(b.dataset.tt), headwayMinutes: Number(b.dataset.n) } }));
    on('[data-svc]', (b) => this.command({ service: { lineId: Number(b.dataset.svc), everyDays: Number(b.dataset.n) } }));
    on('[data-renew]', (b) => this.command({ renew: { lineId: Number(b.dataset.renew) } }));
    on('[data-buy]', (b) => this.command({ standing: { kind: b.dataset.buy, district: Number(b.dataset.district) } }));
    on('[data-bid]', (b) => {
      const amount = Number($<HTMLInputElement>(`#bid-${b.dataset.bid}`).value || 0);
      this.command({ bid: { district: Number(b.dataset.bid), amount } });
    });
    on('[data-lease]', (b) => this.host.conn.action({ lease: { game: this.game, buildingId: Number(b.dataset.lease) } }));
    on('[data-release]', (b) => this.host.conn.action({ endLease: { leaseId: Number(b.dataset.release), byOperator: true } }));
    on('[data-jx]', (b) => this.host.jumpTo(Number(b.dataset.jx), Number(b.dataset.jy)));
    on('[data-jumplot]', (b) => { const l = this.lot(Number(b.dataset.jumplot)); if (l) this.host.jumpTo(l.x, l.y); });
    on('#draft-cancel', () => this.setTool('look'));
    on('#draft-save', () => {
      if (!this.draft) return;
      const name = ($<HTMLInputElement>('#draft-name').value || '').trim();
      const fareCents = this.draft.freight ? 0 : Math.round(Number($<HTMLInputElement>('#draft-fare').value || 0) * 100);
      const d = this.draft;
      this.command({ saveLine: { lineId: d.lineId, name, stopLeaseIds: d.stops, fareCents, mode: Math.max(0, d.mode), freight: d.freight, toEdge: d.toEdge } });
      this.setTool('look');
    });
    el.querySelector('#draft-name')?.addEventListener('input', (e) => { if (this.draft) this.draft.name = (e.target as HTMLInputElement).value; });
    el.querySelectorAll<HTMLInputElement>('input[name="draft-fmode"]').forEach((r) => r.addEventListener('change', () => { if (this.draft) this.draft.mode = Number(r.value); this.renderPanel(); }));
    el.querySelector('#draft-edge')?.addEventListener('change', (e) => { if (this.draft) this.draft.toEdge = (e.target as HTMLInputElement).checked; this.renderPanel(); });
    el.querySelector('#draft-fare')?.addEventListener('input', (e) => { if (this.draft) this.draft.fare = Math.round(Number((e.target as HTMLInputElement).value) * 100); });
  }
}
