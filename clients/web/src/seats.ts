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

export class Seats {
  private seats: Msg[] = [];
  private shownReport = -1;

  constructor(private readonly host: SeatsHost) {
    try { this.shownReport = Number(localStorage.getItem(SEEN_KEY) ?? -1); } catch { /* no storage */ }
    $('#btn-seat').addEventListener('click', () => this.openHandOff());
    $('#away-back').addEventListener('click', () => this.takeBack());
  }

  get mayor(): Msg | undefined { return this.seats.find((s) => s.id === 'mayor'); }

  onCivic(c: Msg) {
    this.seats = c.seats ?? [];
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
    this.host.conn.action({ seat: { seat: 'mayor', owner: m?.owner || 'player', level: 0, style: m?.style ?? 0, spendCap: m?.spendCap ?? 25000 } });
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
      ${others.length ? `<h4>Side games</h4>${others.map((s) => `
        <div class="seat-row"><b>${esc(s.name)}</b> <span class="muted small">${s.owner ? `run by ${esc(s.owner)}` : 'nobody runs it'} · ${LEVELS[s.level].toLowerCase()}</span>
          ${s.owner === 'player' && s.level === 0
            ? `<button value="leave:${esc(s.id)}">Hand to the computer</button>`
            : `<button value="claim:${esc(s.id)}">Run it yourself</button>`}</div>`).join('')}` : ''}
      <div class="actions"><button value="cancel">Cancel</button><button value="ok" class="active">Apply</button></div>`;
    const sync = () => {
      const level = Number(new FormData(form).get('level'));
      $('#style-opts').style.display = level === 3 ? '' : 'none';
      $('#cap-opts').style.display = level === 2 || level === 3 ? '' : 'none';
      $('#style-help').textContent = STYLE_HELP[Number(new FormData(form).get('style'))];
    };
    form.addEventListener('change', sync);
    sync();
    dlg.onclose = () => {
      const v = dlg.returnValue;
      if (v.startsWith('claim:') || v.startsWith('leave:')) {
        const claim = v.startsWith('claim:');
        this.host.conn.action({ seat: { seat: v.slice(6), owner: claim ? 'player' : '', level: claim ? 0 : 2, style: 0, spendCap: 25000 } });
        return;
      }
      if (v !== 'ok') return;
      const fd = new FormData(form);
      this.host.conn.action({ seat: { seat: 'mayor', owner: m.owner || 'player', level: Number(fd.get('level')), style: Number(fd.get('style')), spendCap: Number(fd.get('cap') || 0) } });
    };
    dlg.showModal();
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
}
