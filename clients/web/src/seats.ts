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
const standardTerms = (kind: string) => ({ mode: 0, rentPerMonth: kind === 'bus-depot' ? 300 : 40, taxTenths: 100, subsidyPerMonth: 0, fareCapCents: 0, noticeMonths: 3 });

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
    if (this.consentOpen) this.openConsent();
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
      <div class="actions"><button value="consent" style="margin-right:auto">House rules…</button><button value="cancel">Cancel</button><button value="ok" class="active">Apply</button></div>`;
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
    form.innerHTML = `
      <h3>House rules</h3>
      <p class="muted small">Each player switches these on or off for themselves. One is on in this city only while every player who owns a seat allows it.</p>
      <table class="consent">
        <tr><th></th>${cols.map((o) => `<th>${esc(o === me ? 'You' : o)}</th>`).join('')}<th>In this city</th></tr>
        ${(this.consent.rows as Msg[]).map((r) => `
          <tr><td><b>${esc(r.name)}</b><div class="muted small">${esc(r.note)}</div></td>
            ${cols.map((o) => {
              const i = owners.indexOf(o);
              const on = i >= 0 ? r.byOwner[i] : r.mechanic === 4;
              return o === me
                ? `<td><input type="checkbox" data-mech="${r.mechanic}" ${on ? 'checked' : ''}></td>`
                : `<td>${on ? '✓' : '✗'}</td>`;
            }).join('')}
            <td class="${r.effective ? 'pos' : 'neg'}">${r.effective ? 'On' : `Off${r.blockers.length ? ` <span class="muted small">(${esc((r.blockers as string[]).map((b) => (b === me ? 'you' : b)).join(', '))})</span>` : ''}`}</td></tr>`).join('')}
      </table>
      <div class="actions"><button value="ok" class="active">Done</button></div>`;
    form.querySelectorAll<HTMLInputElement>('input[data-mech]').forEach((box) =>
      box.addEventListener('change', () => this.host.conn.action({ consent: { owner: me, mechanic: Number(box.dataset.mech), allowed: box.checked } })));
    form.onchange = null;
    this.consentOpen = true;
    dlg.onclose = () => { this.consentOpen = false; };
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
      </div>`;
    slot.querySelector('#lease-change')!.addEventListener('click', () =>
      this.termsDialog(`New terms for ${lease.gameName}`, lease.pendingTerms ?? lease.terms, 'Give notice', (terms) =>
        this.host.conn.action({ leaseTerms: { leaseId: lease.id, terms } }),
        `The new terms start after ${lease.terms.noticeMonths} months' notice, so ${lease.gameName} isn't caught out.`));
    slot.querySelector('#lease-end')!.addEventListener('click', () => {
      if (evict && !confirm(`Evict ${lease.gameName}? It gets ${lease.terms.noticeMonths} months' notice, and the city pays it compensation when it goes.`)) return;
      this.host.conn.action({ endLease: { leaseId: lease.id, byOperator: false } });
    });
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
