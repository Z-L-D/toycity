// Seats: who plays the city (and each side game), handing it to the computer, and the "while you were away" report.
import type { Connection, Msg } from './net';

export interface SeatsHost {
  conn: Connection;
  jumpTo(x: number, y: number): void;
}

const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector(sel) as T;
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);

export const LEVELS = ['Manual', 'Stasis', 'Caretaker', 'Autopilot'];
const LEVEL_ICON = ['🧑', '⏸', '🛠', '🤖'];
const LEVEL_HELP = [
  'You play it.',
  'The city holds still: no growth, decay, money or new builds. People keep moving and side games keep running.',
  'Keeps things running: pays the bills (with a loan if it must), rebuilds what is lost like for like, nudges taxes a point either way, turns down deals. Never zones, builds roads or expands.',
  'Plays the city like a player would, in a style: zones blocks, lays new streets, builds plants and services, answers petitions and deals. It never demolishes anything and stays under the monthly spending cap.',
];
export const STYLES = ['Growth', 'Green', 'Profit', 'Chaos'];
const STYLE_HELP = [
  'Taxes 7–10%, the cheapest power per kW, takes deals that bring money or jobs.',
  'Taxes 8–11%, clean power only, parks in leftover land, no polluting deals, industry kept away from homes.',
  'Taxes 10–13%, takes any deal that pays.',
  'Taxes anywhere from 5% to 15%, zones on a whim, flips a coin on deals.',
];
const SEEN_KEY = 'toycity.seenReport';

const money = (n: number) => (n < 0 ? '-$' : '$') + Math.abs(Math.round(n)).toLocaleString('en-US');
const fare = (cents: number) => (cents === 0 ? 'free' : `$${(cents / 100).toFixed(2)}`);

/** The city's standard offer, as the sim defines it: a business lease, rent by size, a 10% fare tax, three months' notice. */
const STANDARD_RENT: Record<string, number> = { 'bus-depot': 300, 'tram-depot': 500, 'rail-depot': 1200, 'tram-stop': 60, 'rail-station': 150, 'freight-depot': 400 };
// As the sim's World.StandardTerms: stations are public services, the city paying toward the crews.
const STATION_SUBSIDY: Record<string, number> = { 'fire-station': 400, 'police-station': 400, clinic: 300, hospital: 800 };
const standardTerms = (kind: string) => kind in STATION_SUBSIDY
  ? { mode: 1, rentPerMonth: 0, taxTenths: 0, subsidyPerMonth: STATION_SUBSIDY[kind], fareCapCents: 0, noticeMonths: 3 }
  : { mode: 0, rentPerMonth: STANDARD_RENT[kind] ?? 40, taxTenths: 100, subsidyPerMonth: 0, fareCapCents: 0, noticeMonths: 3 };

export function termsText(t: Msg) {
  return t.mode === 0
    ? `Business lease: ${money(t.rentPerMonth)} a month and ${t.taxTenths / 10}% of fares`
    : `Public service: the city pays ${money(t.subsidyPerMonth)} a month, fares capped at ${fare(t.fareCapCents)}`;
}

export class Seats {
  private seats: Msg[] = [];
  leases: Msg[] = [];
  sideGames: Msg[] = [];
  consent: Msg = { owners: [], rows: [] };
  private civic: Msg = {};
  /** Everyone the server knows of in this city, from its presence list, for picking deputies. */
  players: string[] = [];
  /** The building whose lease panel is showing, so it can redraw when the lease changes. */
  private leaseView: { b: Msg; slot: HTMLElement } | null = null;
  private shownReport = -1;

  /** Who this browser plays as: "player" in solo play, the account name on a dedicated server. */
  get me(): string {
    return this.host.conn.player || 'player';
  }

  /** A seat's display name: the side game's name, or the city for the mayor's seat. */
  seatName(id: string): string {
    return this.seats.find((s) => s.id === id)?.name ?? id;
  }

  constructor(private readonly host: SeatsHost) {
    try { this.shownReport = Number(localStorage.getItem(SEEN_KEY) ?? -1); } catch { /* no storage */ }
    $('#btn-seat').addEventListener('click', () => this.openHandOff());
    $('#btn-council').addEventListener('click', () => this.openCouncil());
    $('#away-back').addEventListener('click', () => this.takeBack());
  }

  /** Whether a mechanic is on in this city: every seat owner allows it. */
  allowed(mechanic: number) { return !!(this.consent.rows as Msg[]).find((r) => r.mechanic === mechanic)?.effective; }

  get mayor(): Msg | undefined { return this.seats.find((s) => s.id === 'mayor'); }

  onCivic(c: Msg) {
    this.seats = c.seats ?? [];
    this.leases = c.leases ?? [];
    this.sideGames = c.sideGames ?? [];
    this.consent = c.consent ?? { owners: [], rows: [] };
    this.civic = c;
    if (this.consentOpen) this.openConsent();
    if (this.councilOpen) this.openCouncil();
    const open = (c.proposals as Msg[] ?? []).filter((p) => p.status === 'open' && !(p.yes as string[]).includes(this.me) && !(p.no as string[]).includes(this.me));
    const cb = document.querySelector('#btn-council');
    if (cb) {
      cb.classList.toggle('hidden', !c.councilOn && !(c.deputies as Msg[] ?? []).length);
      cb.textContent = open.length && (c.council as string[]).includes(this.me) ? `🏛 Council (${open.length})` : '🏛 Council';
    }
    if (this.leaseView && this.leaseView.slot.isConnected) this.renderLease(this.leaseView.b, this.leaseView.slot);
    const m = this.mayor;
    if (!m) return;
    const btn = $('#btn-seat');
    btn.textContent = `${LEVEL_ICON[m.level]} ${m.level === 0 ? 'Mayor' : LEVELS[m.level]}${m.level === 3 ? ` (${STYLES[m.style].toLowerCase()})` : ''}`;
    btn.classList.toggle('away', m.level !== 0);
    const banner = $('#away');
    banner.classList.toggle('hidden', m.level === 0);
    if (m.level !== 0)
      $('#away-text').textContent = m.level === 1 ? 'The city is in stasis.'
        : m.level === 2 ? 'The computer is minding the city (caretaker).'
        : `The computer is playing the city (autopilot, ${STYLES[m.style].toLowerCase()}).`;
    // The report appears once, when the owner takes the city back.
    if (m.level === 0 && m.report && m.report.toTick !== this.shownReport && m.report.toTick > 0) {
      this.shownReport = m.report.toTick;
      try { localStorage.setItem(SEEN_KEY, String(this.shownReport)); } catch { /* no storage */ }
      this.showReport(m.report);
    }
  }

  private takeBack() {
    const m = this.mayor;
    this.host.conn.action({ seat: { seat: 'mayor', owner: m?.owner || this.me, level: 0, style: m?.style ?? 0, spendCap: m?.spendCap ?? 25000 } });
  }

  openHandOff() {
    const m = this.mayor;
    if (!m) return;
    const dlg = $<HTMLDialogElement>('#dlg');
    const form = $<HTMLFormElement>('#dlg-form');
    const others = this.seats.filter((s) => s.id !== 'mayor');
    form.innerHTML = `
      <h3>Who runs the city</h3>
      <p class="muted small">Hand the city to the computer when you step away. Take it back any time and you get a report of what happened.</p>
      <div class="levels">
        ${LEVELS.map((l, i) => `
          <label class="level-opt"><input type="radio" name="level" value="${i}" ${m.level === i ? 'checked' : ''}>
            <b>${LEVEL_ICON[i]} ${l}</b><span class="muted small">${LEVEL_HELP[i]}</span></label>`).join('')}
      </div>
      <div id="style-opts">
        <label>Autopilot style</label>
        <select name="style">${STYLES.map((s, i) => `<option value="${i}" ${m.style === i ? 'selected' : ''}>${s}</option>`).join('')}</select>
        <div class="muted small" id="style-help">${STYLE_HELP[m.style]}</div>
      </div>
      <div id="cap-opts">
        <label>Most the computer may spend in a month</label>
        <input type="number" name="cap" min="0" step="1000" value="${m.spendCap}">
      </div>
      ${this.host.conn.dedicated && m.owner === this.me ? `
        <label>While you're away from this server</label>
        <select name="away">${LEVELS.map((l, i) => (i === 0 ? '' : `<option value="${i}" ${m.awayLevel === i ? 'selected' : ''}>${l}</option>`)).join('')}</select>
        <div class="muted small">When your last window closes, the computer plays the city this way after a short wait, and hands it back when you return.</div>` : ''}
      ${others.length ? `<h4>Side games</h4>${others.map((s) => `
        <div class="seat-row"><b>${esc(s.name)}</b> <span class="muted small">${s.owner ? `run by ${esc(s.owner)}` : 'nobody runs it'} · ${LEVELS[s.level].toLowerCase()}</span>
          ${s.owner === this.me && s.level === 0
            ? `<button value="leave:${esc(s.id)}">Hand to the computer</button>`
            : `<button value="claim:${esc(s.id)}">Run it yourself</button>`}</div>`).join('')}` : ''}
      ${this.transitHtml(m)}
      ${this.partingHtml(m)}
      <div class="actions"><button value="consent">House rules…</button><button value="council" style="margin-right:auto">Deputies and council…</button><button value="cancel">Cancel</button><button value="ok" class="active">Apply</button></div>`;
    const sync = () => {
      const level = Number(new FormData(form).get('level'));
      $('#style-opts').style.display = level === 3 ? '' : 'none';
      $('#cap-opts').style.display = level === 2 || level === 3 ? '' : 'none';
      $('#style-help').textContent = STYLE_HELP[Number(new FormData(form).get('style'))];
    };
    form.onchange = sync;
    sync();
    dlg.onclose = () => {
      const v = dlg.returnValue;
      if (v === 'consent') { this.openConsent(); return; }
      if (v === 'council') { this.openCouncil(); return; }
      if (v.startsWith('delink:')) { this.delink(v.slice(7)); return; }
      if (v.startsWith('invite:')) { this.host.conn.action({ attach: { game: v.slice(7) } }); return; }
      if (v === 'tender') {
        const fd = new FormData(form);
        this.host.conn.action({ openTender: { district: Number(fd.get('tender-district')), months: Number(fd.get('tender-months')) } });
        return;
      }
      if (v.startsWith('claim:') || v.startsWith('leave:')) {
        const claim = v.startsWith('claim:');
        this.host.conn.action({ seat: { seat: v.slice(6), owner: claim ? this.me : '', level: claim ? 0 : 2, style: 0, spendCap: 25000 } });
        return;
      }
      if (v !== 'ok') return;
      const fd = new FormData(form);
      if (fd.get('away') && Number(fd.get('away')) !== m.awayLevel)
        this.host.conn.action({ awayLevel: { seat: 'mayor', level: Number(fd.get('away')), style: Number(fd.get('style')) } });
      this.host.conn.action({ seat: { seat: 'mayor', owner: m.owner || this.me, level: Number(fd.get('level')), style: Number(fd.get('style')), spendCap: Number(fd.get('cap') || 0) } });
    };
    dlg.showModal();
  }

  /**
   * Transit competition: rival companies the mayor can invite in, and district franchises put out to tender. The
   * highest sealed bid wins and pays the city; the winner alone may lease new transit lots in the district, and
   * with exclusive rights allowed by everyone, only its lines stop there.
   */
  private transitHtml(m: Msg) {
    const c = this.civic;
    const isMayor = m.owner === this.me || (!m.owner && !this.host.conn.dedicated);
    const invitable = (c.invitable ?? []) as Msg[];
    const rows = ((c.franchises ?? []) as Msg[]);
    const companies = this.sideGames.filter((g) => (g.lotKinds as string[]).some((k) => k.startsWith('bus-') || k.startsWith('rail-')));
    if (!companies.length && !invitable.length) return '';
    const live = rows.filter((f) => f.status !== 'none').map((f) => `<div class="seat-row"><b>${esc(f.name)}</b> <span class="muted small">${f.status === 'open'
      ? `out to tender for ${f.months} months · ${f.bids} bid${f.bids === 1 ? '' : 's'} in · closes in ${f.daysLeft} day${f.daysLeft === 1 ? '' : 's'}`
      : `${esc(f.holderName)} holds it${f.exclusive ? ' exclusively' : ''} · paid ${money(f.amount)} · ${Math.ceil(f.daysLeft / 30)} months left`}</span></div>`).join('');
    const free = rows.filter((f) => f.status === 'none');
    const tender = isMayor && free.length && companies.length ? `<div class="row-flex" style="margin-top:6px;flex-wrap:wrap">
        <select name="tender-district">${free.map((f) => `<option value="${f.district}">${esc(f.name)}${f.last ? ' (no bids last time)' : ''}</option>`).join('')}</select>
        <select name="tender-months">${[6, 12, 24, 36].map((n) => `<option value="${n}" ${n === 12 ? 'selected' : ''}>${n} months</option>`).join('')}</select>
        <button value="tender">Put the franchise out to tender</button></div>
      <div class="muted small">Companies have ${14} days to bid; the highest bid wins and is paid to the city. ${this.allowed(1) ? 'Everyone allows exclusive rights, so the winner alone stops in the district.' : 'The winner alone may lease new transit lots there; rivals keep the stops they have.'}</div>` : '';
    const invite = isMayor ? invitable.map((g) => `<div class="seat-row"><b>${esc(g.name)}</b> <span class="muted small">${esc(g.description)}</span><button value="invite:${esc(g.id)}">Invite it in</button></div>`).join('') : '';
    const freight = c.freightExports || c.freightDelivered ? `<p class="muted small">Company freight last month: ${Number(c.freightDelivered).toLocaleString('en-US')} tons a day to shops, ${Number(c.freightExports).toLocaleString('en-US')} tons a day exported.</p>` : '';
    return `<h4>Transit competition</h4>${invite}${live}${tender}${freight}`;
  }

  /** Parting ways: the mayor may cut players loose on the rules' grounds; anyone else may leave with a copy. */
  private partingHtml(m: Msg) {
    const options = (this.civic.delinkOptions ?? []) as Msg[];
    const past = (this.civic.delinks ?? []) as Msg[];
    const isMayor = m.owner === this.me;
    const mine = options.find((o) => o.player === this.me);
    if (!isMayor && !mine && !past.length) return '';
    const rows = isMayor
      ? options.map((o) => `<div class="seat-row"><b>${esc(o.player)}</b> <span class="muted small">${esc(o.rule)}</span>
          <button value="delink:${esc(o.player)}" ${o.open ? '' : 'disabled'}>De-link…</button></div>`).join('')
      : mine ? `<div class="seat-row"><span class="muted small">Leave for good: you take a copy of the city as it is now, with your seats and lots, and become its mayor. This city carries on without you.</span>
          <button value="delink:${esc(this.me)}">Leave with a copy…</button></div>` : '';
    const history = past.slice(0, 4).map((d) => `<p class="small muted">Day ${d.day}: ${esc(d.player)} ${d.by === d.player ? 'left' : `was de-linked by ${esc(d.by)}`}${d.leasesEnded ? `, ${d.leasesEnded} lease${d.leasesEnded === 1 ? '' : 's'} ended` : ''}${d.writtenOff ? `, ${money(d.writtenOff)} written off` : ''}${d.paid ? `, ${money(d.paid)} paid` : ''}.${d.cooldownDays ? ` They can share a city with ${esc(d.mayor)} again in ${d.cooldownDays} days.` : ''}</p>`).join('');
    return `<h4>Parting ways</h4>${rows}${history}`;
  }

  /** De-links a player (or this one) after saying what happens. */
  delink(player: string) {
    const self = player === this.me;
    if (!confirm(self
      ? 'Leave this city for good? You get a copy of it as it is now, with your seats and lots, to play on your own. Your leases here end and you can\'t rejoin for 90 days.'
      : `De-link ${player}? They take a copy of the city as it is now and leave. Their leases here end, what they owe is written off, and they can't come back for 90 days.`)) return;
    this.host.conn.action({ delink: { player } });
  }

  private councilOpen = false;

  /**
   * Deputies and the council. The mayor names deputies, each with the portfolios they may act in, and can seat a
   * council that votes on taxes, laws and loans. Members vote here; ties go the way the mayor voted.
   */
  openCouncil() {
    const c = this.civic;
    const m = this.mayor;
    if (!m) return;
    const dlg = $<HTMLDialogElement>('#dlg');
    const form = $<HTMLFormElement>('#dlg-form');
    const isMayor = m.owner === this.me || (!m.owner && !this.host.conn.dedicated);
    const deputies = (c.deputies as Msg[] ?? []);
    const bits = (name: string) => deputies.find((d) => d.player === name)?.portfolios ?? 0;
    const people = [...new Set([...this.players, ...deputies.map((d) => d.player as string)])].filter((n) => n !== m.owner);
    const PORTFOLIOS = ['Planning', 'Budget', 'Services', 'Transit'];
    const members = c.council as string[] ?? [];
    const proposals = c.proposals as Msg[] ?? [];
    form.innerHTML = `
      <h3>Deputies and council</h3>
      <p class="muted small">Deputies act for the mayor in their portfolios: planning (roads, zones, utilities), the budget (taxes, funding, loans), services (civic buildings) and transit (stops, depots and leases).</p>
      <table class="consent"><tr><th></th>${PORTFOLIOS.map((p) => `<th>${p}</th>`).join('')}</tr>
        ${people.length ? people.map((n) => `<tr><td>${esc(n)}</td>${PORTFOLIOS.map((_, i) => `<td><input type="checkbox" data-dep="${esc(n)}" data-bit="${1 << i}" ${bits(n) & (1 << i) ? 'checked' : ''} ${isMayor ? '' : 'disabled'}></td>`).join('')}</tr>`).join('')
          : `<tr><td colspan="5" class="muted small">Nobody else is here to make a deputy.</td></tr>`}
      </table>
      <h4>Council</h4>
      <label><input type="checkbox" id="council-on" ${c.councilOn ? 'checked' : ''} ${isMayor ? '' : 'disabled'}> The council sits</label>
      <div class="muted small">${c.councilOn ? `Members: ${members.map(esc).join(', ')}. Tax changes, laws and loans go to a vote for ${7} days; ties go to the mayor.` : 'Off: the mayor decides alone.'}</div>
      ${proposals.length ? `<div class="proposals">${proposals.map((p) => {
        const voted = (p.yes as string[]).includes(this.me) ? 'yes' : (p.no as string[]).includes(this.me) ? 'no' : '';
        const tally = `${(p.yes as string[]).length} for, ${(p.no as string[]).length} against`;
        return `<div class="seat-row"><b>${esc(p.text)}</b> <span class="muted small">by ${esc(p.proposer)} · ${p.status === 'open' ? `${p.daysLeft} days left · ${tally}` : `${p.status} (${tally})`}</span>
          ${p.status === 'open' && members.includes(this.me) ? `<button value="vote:${p.id}:1" class="${voted === 'yes' ? 'active' : ''}">Yes</button><button value="vote:${p.id}:0" class="${voted === 'no' ? 'active' : ''}">No</button>` : ''}</div>`;
      }).join('')}</div>` : ''}
      <div class="actions"><button value="cancel">Close</button>${isMayor ? '<button value="ok" class="active">Apply</button>' : ''}</div>`;
    this.councilOpen = true;
    dlg.onclose = () => {
      this.councilOpen = false;
      const v = dlg.returnValue;
      if (v.startsWith('vote:')) {
        const [, id, yes] = v.split(':');
        this.host.conn.action({ vote: { proposal: Number(id), yes: yes === '1' } });
        return;
      }
      if (v !== 'ok') return;
      for (const n of people) {
        let b = 0;
        form.querySelectorAll<HTMLInputElement>('input[data-dep]').forEach((i) => { if (i.dataset.dep === n && i.checked) b |= Number(i.dataset.bit); });
        if (b !== bits(n)) this.host.conn.action({ deputy: { player: n, portfolios: b } });
      }
      const on = (form.querySelector('#council-on') as HTMLInputElement).checked;
      if (on !== !!c.councilOn) this.host.conn.action({ council: { on } });
    };
    if (!dlg.open) dlg.showModal();
  }

  private consentOpen = false;

  /**
   * House rules: every seat owner against every mechanic. A mechanic is on only while everyone who owns a seat
   * allows it, so the table names who is blocking each one. You can only change your own column.
   */
  openConsent() {
    const dlg = $<HTMLDialogElement>('#dlg');
    const form = $<HTMLFormElement>('#dlg-form');
    const owners = this.consent.owners as string[];
    const me = this.me;
    const cols = owners.includes(me) ? owners : [...owners, me];
    const mayor = this.mayor?.owner === me;
    const rows = this.consent.rows as Msg[];
    const pacts = (this.consent.pacts ?? []) as Msg[];
    const mechName = (m: number) => rows.find((r) => r.mechanic === m)?.name ?? '';
    const others = owners.filter((o) => o !== me);
    const result = (r: Msg) => {
      if (r.hostForbids) return '<td class="neg">Forbidden <span class="muted small">by the host</span></td>';
      if (r.cityForbids) return '<td class="neg">Forbidden <span class="muted small">by the city</span></td>';
      const who = (r.blockers as string[]).map((b) => (b === me ? 'you' : b)).join(', ');
      if (r.pairwise) return `<td>Between two <span class="muted small">${who ? `(not with ${esc(who)})` : '(everyone allows it)'}</span></td>`;
      return `<td class="${r.effective ? 'pos' : 'neg'}">${r.effective ? 'On' : `Off${who ? ` <span class="muted small">(${esc(who)})</span>` : ''}`}</td>`;
    };
    const pactRows = pacts.filter((p) => p.a === me || p.b === me).map((p) => {
      const other = p.a === me ? p.b : p.a;
      const name = esc(mechName(p.mechanic).toLowerCase());
      if (!p.offer) return `<div class="seat-row">Pact with <b>${esc(other)}</b>: ${name} <button value="pact:2:${esc(other)}:${p.mechanic}">End it</button></div>`;
      if (p.b === me) return `<div class="seat-row"><b>${esc(other)}</b> offers a pact: ${name} <button value="pact:1:${esc(other)}:${p.mechanic}">Accept</button><button value="pact:2:${esc(other)}:${p.mechanic}">Turn down</button></div>`;
      return `<div class="seat-row">Offered <b>${esc(other)}</b>: ${name} <button value="pact:2:${esc(other)}:${p.mechanic}">Withdraw</button></div>`;
    }).join('');
    const offerable = rows.filter((r) => r.pairwise && !r.hostForbids && !r.cityForbids);
    form.innerHTML = `
      <h3>House rules</h3>
      <p class="muted small">Each player switches these on or off for themselves. One that reaches everyone is on only while every player who owns a seat allows it. One <i>between two</i> players needs only those two to allow it, or a pact between them. The host and the city can forbid any of them outright.</p>
      <table class="consent">
        <tr><th></th>${cols.map((o) => `<th>${esc(o === me ? 'You' : o)}</th>`).join('')}${mayor ? '<th>City forbids</th>' : ''}<th>In this city</th></tr>
        ${rows.map((r) => `
          <tr><td><b>${esc(r.name)}</b><div class="muted small">${esc(r.note)}</div></td>
            ${cols.map((o) => {
              const i = owners.indexOf(o);
              const on = i >= 0 ? r.byOwner[i] : r.mechanic === 4;
              return o === me
                ? `<td><input type="checkbox" data-mech="${r.mechanic}" ${on ? 'checked' : ''}></td>`
                : `<td>${on ? '✓' : '✗'}</td>`;
            }).join('')}
            ${mayor ? `<td><input type="checkbox" data-rule="${r.mechanic}" ${r.cityForbids ? 'checked' : ''}></td>` : ''}
            ${result(r)}</tr>`).join('')}
      </table>
      <h4>Pacts</h4>
      ${pactRows || '<p class="muted small">None yet. A pact lets something between two players happen between just you two, whatever either of you allows in general.</p>'}
      ${others.length && offerable.length ? `<div class="seat-row">Offer
        <select name="pact-with">${others.map((o) => `<option>${esc(o)}</option>`).join('')}</select>
        a pact on <select name="pact-mech">${offerable.map((r) => `<option value="${r.mechanic}">${esc(r.name.toLowerCase())}</option>`).join('')}</select>
        <button value="offer">Offer</button></div>` : ''}
      <div class="actions"><button value="ok" class="active">Done</button></div>`;
    form.querySelectorAll<HTMLInputElement>('input[data-mech]').forEach((box) =>
      box.addEventListener('change', () => this.host.conn.action({ consent: { owner: me, mechanic: Number(box.dataset.mech), allowed: box.checked } })));
    form.querySelectorAll<HTMLInputElement>('input[data-rule]').forEach((box) =>
      box.addEventListener('change', () => this.host.conn.action({ cityRule: { mechanic: Number(box.dataset.rule), forbid: box.checked } })));
    form.onchange = null;
    this.consentOpen = true;
    dlg.onclose = () => {
      this.consentOpen = false;
      const v = dlg.returnValue;
      if (v.startsWith('pact:')) {
        const [, step, withName, mech] = v.split(':');
        this.host.conn.action({ pact: { step: Number(step), with: withName, mechanic: Number(mech) } });
      } else if (v === 'offer') {
        const fd = new FormData(form);
        this.host.conn.action({ pact: { step: 0, with: String(fd.get('pact-with')), mechanic: Number(fd.get('pact-mech')) } });
      }
    };
    if (!dlg.open) dlg.showModal();
  }


  private showReport(r: Msg) {
    const dlg = $<HTMLDialogElement>('#dlg');
    const form = $<HTMLFormElement>('#dlg-form');
    const notes = r.notes as Msg[];
    form.innerHTML = `
      <h3>While you were away</h3>
      <ul class="report">${(r.changes as string[]).map((c) => `<li>${esc(c)}</li>`).join('')}</ul>
      ${notes.length ? `<h4>What the computer did (${notes.length})</h4>
        <div class="report-notes">${notes.slice(-40).map((n) => `<div class="note" ${n.x >= 0 ? `data-jump="${n.x},${n.y}"` : ''}>${esc(n.text)}${n.x >= 0 ? ' <span class="go">Go →</span>' : ''}</div>`).join('')}</div>` : ''}
      ${r.news.length ? `<h4>In the news</h4><div class="report-notes">${(r.news as string[]).slice(-12).map((t) => `<div class="news">${esc(t)}</div>`).join('')}</div>` : ''}
      <div class="actions"><button value="ok" class="active">Back to it</button></div>`;
    form.querySelectorAll<HTMLElement>('[data-jump]').forEach((el) =>
      el.addEventListener('click', () => {
        const [x, y] = el.dataset.jump!.split(',').map(Number);
        dlg.close();
        this.host.jumpTo(x, y);
      }),
    );
    dlg.onclose = null;
    dlg.showModal();
  }

  // -------------------------------------------------------------------------
  // Leases on side-game lots
  // -------------------------------------------------------------------------

  /** The lease part of a building's panel: who leases it and on what terms, or who could. */
  renderLease(b: Msg, slot: HTMLElement) {
    this.leaseView = { b, slot };
    const lease = this.leases.find((l) => l.buildingId === b.id);
    const takers = this.sideGames.filter((g) => (g.lotKinds as string[]).includes(b.typeId));
    if (!lease && !takers.length) { slot.innerHTML = ''; return; }
    if (!lease) {
      slot.innerHTML = `<h4>Lease</h4><p class="muted small">Not leased. Unleased stops keep the city's own bus service.</p>
        ${takers.map((g) => `<button data-lease-to="${esc(g.id)}">Lease to ${esc(g.name)}…</button>`).join(' ')}`;
      slot.querySelectorAll<HTMLButtonElement>('button[data-lease-to]').forEach((btn) =>
        btn.addEventListener('click', () => {
          const g = this.sideGames.find((x) => x.id === btn.dataset.leaseTo)!;
          this.termsDialog(`Lease to ${g.name}`, standardTerms(b.typeId), 'Lease', (terms) =>
            this.host.conn.action({ lease: { game: g.id, buildingId: b.id, terms } }));
        }),
      );
      return;
    }
    const evict = !lease.terminable && !lease.badStanding && lease.evictMonths === 0 && this.allowed(3);
    const pending = lease.pendingTerms ? `<p class="small">From ${lease.pendingMonths} month${lease.pendingMonths === 1 ? '' : 's'} on: ${esc(termsText(lease.pendingTerms))}.</p>` : '';
    slot.innerHTML = `<h4>Lease</h4>
      <p>Leased to <b>${esc(lease.gameName)}</b>. ${esc(termsText(lease.terms))}, ${lease.terms.noticeMonths} months' notice.</p>
      ${pending}
      <p class="small">${lease.jobs} jobs here · ${money(lease.lastMonthFares)} in fares last month${lease.arrears > 0 ? ` · <span class="neg">${money(lease.arrears)} owed</span>` : ''}</p>
      <div class="req ${lease.badStanding ? 'unmet' : 'met'}"><span class="mark">${lease.badStanding ? '✗' : '✓'}</span><span class="label">Standing</span>
        <span class="detail">${lease.badStanding ? esc((lease.targets as string[]).join(' ')) : 'Good: paid up and well regarded.'}</span></div>
      <p class="small">Rating in the district: <b class="${lease.rating < 0 ? 'neg' : 'pos'}">${lease.rating}</b> <span class="muted">(−1000 to 1000)</span></p>
      ${lease.evictMonths > 0 ? `<p class="small neg">Evicted: it leaves in ${lease.evictMonths} month${lease.evictMonths === 1 ? '' : 's'}, with compensation.</p>` : ''}
      <div class="row-flex" style="margin-top:6px">
        <button id="lease-change">Change terms…</button>
        ${evict
          ? `<button id="lease-end" title="Hostile evictions are on: the lease ends after notice and the city pays six months' rent">Evict…</button>`
          : `<button id="lease-end" ${lease.terminable ? '' : `disabled title="Only a lease in bad standing past its cure period can be ended by the city${this.allowed(3) ? '' : ', unless every player allows hostile evictions'}"`}>End lease</button>`}
      </div>
      ${this.disputeHtml(lease)}`;
    this.wireDispute(lease, slot);
    slot.querySelector('#lease-change')!.addEventListener('click', () =>
      this.termsDialog(`New terms for ${lease.gameName}`, lease.pendingTerms ?? lease.terms, 'Give notice', (terms) =>
        this.host.conn.action({ leaseTerms: { leaseId: lease.id, terms } }),
        `The new terms start after ${lease.terms.noticeMonths} months' notice, so ${lease.gameName} isn't caught out.`));
    slot.querySelector('#lease-end')!.addEventListener('click', () => {
      if (evict && !confirm(`Evict ${lease.gameName}? It gets ${lease.terms.noticeMonths} months' notice, and the city pays it compensation when it goes.`)) return;
      this.host.conn.action({ endLease: { leaseId: lease.id, byOperator: false } });
    });
  }

  /** Whether this player speaks for the side game on a lease (its seat's owner); otherwise they speak for the city. */
  private operatorOf(lease: Msg) { return this.seats.find((s) => s.id === lease.game)?.owner === this.me; }

  /** The dispute ladder: each rung with the rule behind it, the offer on the table, and the last few decided. */
  private disputeHtml(lease: Msg) {
    const ladder = (lease.ladder ?? []) as Msg[];
    const offers = (lease.offers ?? []) as Msg[];
    const open = offers.find((o) => o.status === 'open');
    const mine = open && open.by === this.me;
    const side = this.operatorOf(lease);
    const who = (o: Msg) => (o.fromOperator ? lease.gameName : 'The city') + (o.by ? ` (${o.by})` : '');
    return `<h4>Dispute ladder</h4>
      ${ladder.map((r) => `<div class="req ${r.open ? 'met' : 'unmet'}"><span class="mark">${r.open ? '✓' : '✗'}</span><span class="label">${esc(r.step)}</span><span class="detail">${esc(r.rule)}</span></div>`).join('')}
      ${this.delinkButton(lease)}
      ${open
        ? `<div class="offer"><p><b>On the table:</b> ${esc(who(open))} offers ${esc(open.text)} <span class="muted small">(${open.daysLeft} day${open.daysLeft === 1 ? '' : 's'} left)</span></p>
          <div class="row-flex">${mine
            ? `<button data-answer="0">Withdraw</button>`
            : `<button data-answer="1" class="active">Accept</button> <button data-answer="0">Decline</button> <button data-counter="1">Counter…</button>`}</div></div>`
        : `<div class="row-flex" style="margin-top:6px">
            <button data-offer="0">Offer terms…</button>
            <button data-offer="1">${side ? 'Ask for a buyout…' : 'Offer a buyout…'}</button>
            <button data-offer="2" ${(lease.moveTo ?? []).length ? '' : 'disabled title="There\'s no free lot of the same kind to move to."'}>Propose a move…</button>
          </div>`}
      ${offers.filter((o) => o.status !== 'open').slice(0, 4).map((o) =>
        `<p class="small muted">${esc(who(o))}: ${esc(o.text)} — <b>${esc(o.status)}</b>${o.note ? `. ${esc(o.note)}` : ''}</p>`).join('')}`;
  }

  /** The last rung as a button: the operator may leave, the mayor may cut them loose when the rules allow. */
  private delinkButton(lease: Msg) {
    const op = this.seats.find((s) => s.id === lease.game)?.owner as string | undefined;
    if (!op) return '';
    if (op === this.me) return `<button data-delink="${esc(op)}">Leave with a copy…</button>`;
    if (this.mayor?.owner !== this.me) return '';
    const o = ((this.civic.delinkOptions ?? []) as Msg[]).find((x) => x.player === op);
    return `<button data-delink="${esc(op)}" ${o?.open ? '' : `disabled title="${esc(o?.rule ?? '')}"`}>De-link ${esc(op)}…</button>`;
  }

  private wireDispute(lease: Msg, slot: HTMLElement) {
    slot.querySelector<HTMLButtonElement>('button[data-delink]')?.addEventListener('click', (e) => this.delink((e.target as HTMLElement).dataset.delink!));
    const open = ((lease.offers ?? []) as Msg[]).find((o) => o.status === 'open');
    const fromOperator = this.operatorOf(lease);
    const send = (kind: number, extra: Msg) =>
      this.host.conn.action({ offer: { leaseId: lease.id, fromOperator, kind, terms: null, amount: 0, toBuildingId: -1, counterOf: open?.id ?? 0, ...extra } });
    const make = (kind: number) => {
      if (kind === 0)
        this.termsDialog(`Offer terms to ${fromOperator ? 'the city' : lease.gameName}`, open?.terms ?? lease.terms, 'Offer', (terms) => send(0, { terms }),
          'If the other side accepts, the new terms start at once.');
      else if (kind === 1) {
        const v = prompt(fromOperator ? 'How much should the city pay to end the lease? (it ends the day it\'s accepted)'
          : `Offer ${lease.gameName} how much to give up the lot? (the lease ends the day it's accepted)`, String(open?.kind === 1 ? open.amount : 0));
        if (v !== null && Number(v) >= 0) send(1, { amount: Math.round(Number(v)) });
      } else this.moveDialog(lease, open, (to, amount) => send(2, { toBuildingId: to, amount }));
    };
    slot.querySelectorAll<HTMLButtonElement>('button[data-offer]').forEach((b) => b.addEventListener('click', () => make(Number(b.dataset.offer))));
    slot.querySelectorAll<HTMLButtonElement>('button[data-answer]').forEach((b) =>
      b.addEventListener('click', () => this.host.conn.action({ answerOffer: { offerId: open!.id, accept: b.dataset.answer === '1' } })));
    // A counter is the same kind of offer, from the other side.
    slot.querySelector('button[data-counter]')?.addEventListener('click', () => make(open!.kind));
  }

  private moveDialog(lease: Msg, open: Msg | undefined, done: (to: number, amount: number) => void) {
    const dlg = $<HTMLDialogElement>('#dlg');
    const form = $<HTMLFormElement>('#dlg-form');
    const lots = (lease.moveTo ?? []) as Msg[];
    form.innerHTML = `
      <h3>Move ${esc(lease.gameName)}</h3>
      <p class="muted small">The lease moves with its terms and jobs. The city can offer to pay the moving costs.</p>
      ${lots.map((l, i) => `<label><input type="radio" name="to" value="${l.buildingId}" ${(open?.toBuildingId ?? lots[0].buildingId) === l.buildingId || (!open && i === 0) ? 'checked' : ''}> ${esc(l.name)}</label>`).join('')}
      <label>Moving costs the city pays ($)</label><input type="number" name="amount" min="0" step="100" value="${open?.amount ?? 1000}">
      <div class="actions"><button value="cancel">Cancel</button><button value="ok" class="active">Propose</button></div>`;
    form.onchange = null;
    dlg.onclose = () => {
      if (dlg.returnValue !== 'ok') return;
      const fd = new FormData(form);
      done(Number(fd.get('to')), Math.round(Number(fd.get('amount') || 0)));
    };
    dlg.showModal();
  }

  private termsDialog(title: string, t: Msg, okLabel: string, done: (terms: Msg) => void, note = '') {
    const dlg = $<HTMLDialogElement>('#dlg');
    const form = $<HTMLFormElement>('#dlg-form');
    form.innerHTML = `
      <h3>${esc(title)}</h3>
      ${note ? `<p class="muted small">${esc(note)}</p>` : ''}
      <label><input type="radio" name="mode" value="0" ${t.mode === 0 ? 'checked' : ''}> <b>Business lease</b> <span class="muted small">the operator pays rent and a fare tax and keeps the profit</span></label>
      <label><input type="radio" name="mode" value="1" ${t.mode === 1 ? 'checked' : ''}> <b>Public service</b> <span class="muted small">the city pays a subsidy and caps fares</span></label>
      <div id="biz-opts">
        <label>Rent a month ($)</label><input type="number" name="rent" min="0" step="10" value="${t.rentPerMonth}">
        <label>Fare tax (%)</label><input type="number" name="tax" min="0" max="50" step="0.5" value="${t.taxTenths / 10}">
      </div>
      <div id="pub-opts">
        <label>Subsidy a month ($)</label><input type="number" name="subsidy" min="0" step="10" value="${t.subsidyPerMonth}">
        <label>Fare cap ($, 0 for free)</label><input type="number" name="cap" min="0" step="0.25" value="${t.fareCapCents / 100}">
      </div>
      <label>Notice for future changes (months)</label><input type="number" name="notice" min="1" max="12" value="${t.noticeMonths}">
      <div class="actions"><button value="cancel">Cancel</button><button value="ok" class="active">${esc(okLabel)}</button></div>`;
    const sync = () => {
      const pub = new FormData(form).get('mode') === '1';
      $('#biz-opts').style.display = pub ? 'none' : '';
      $('#pub-opts').style.display = pub ? '' : 'none';
    };
    form.onchange = sync;
    sync();
    dlg.onclose = () => {
      if (dlg.returnValue !== 'ok') return;
      const fd = new FormData(form);
      const pub = fd.get('mode') === '1';
      done({
        mode: pub ? 1 : 0,
        rentPerMonth: pub ? 0 : Number(fd.get('rent') || 0),
        taxTenths: pub ? 0 : Math.round(Number(fd.get('tax') || 0) * 10),
        subsidyPerMonth: pub ? Number(fd.get('subsidy') || 0) : 0,
        fareCapCents: pub ? Math.round(Number(fd.get('cap') || 0) * 100) : 0,
        noticeMonths: Number(fd.get('notice') || 3),
      });
    };
    dlg.showModal();
  }
}
