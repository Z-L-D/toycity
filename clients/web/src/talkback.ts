// The panels that let the city explain itself: demand by source, alerts, the forecast and the encyclopedia.
import type { Connection, Msg } from './net';
import type { City } from './state';

export interface TalkBackHost {
  conn: Connection;
  city: City;
  /** Centres the map on a cell and opens the why panel for it. */
  jumpTo(x: number, y: number): void;
  showTab(name: string): void;
  /** Called when the set of buildable landmarks and reward lots changes. */
  onUnlocks(): void;
}

const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector(sel) as T;
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
const money = (n: number) => (n < 0 ? '-$' : '$') + Math.abs(Math.round(n)).toLocaleString('en-US');
const signed = (n: number) => (n > 0 ? `+${n}` : `${n}`);
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const ZONES = ['', 'Residential', 'Commercial', 'Industrial'];
const WEALTH_TAGS = [
  ['R$', 'R$$', 'R$$$'],
  ['C$', 'C$$', 'C$$$'],
  ['Dirty', 'Manuf.', 'High-tech'],
];

export class TalkBack {
  private alerts: Msg[] = [];
  private articles: Msg[] = [];
  private articleById = new Map<string, Msg>();
  private currentArticle = '';
  private search = '';
  private forecastMonths = 12;
  private forecastBusy = false;
  private lastForecast: Msg | null = null;
  private ordinances: Msg[] = [];
  private openLaw = '';
  /** A side-by-side forecast for one ordinance: the city with and without it. */
  private compare: { id: string; baseSeq: number; planSeq: number; base?: Msg; plan?: Msg } | null = null;
  /** "What if" mode: actions are collected here instead of being sent. */
  planning = false;
  plan: { action: Msg; label: string }[] = [];

  constructor(private readonly host: TalkBackHost) {
    this.renderAlerts();
    this.renderForecast();
    this.renderGuide();
    $('#rci').addEventListener('click', () => this.open('demand'));
    $('#btn-alerts').addEventListener('click', () => this.open('alerts'));
    $('#approval').addEventListener('click', () => this.open('people'));
    $('#ticker').addEventListener('click', () => {
      const h = this.headlines[this.tickerIndex];
      if (h && h.x >= 0) this.host.jumpTo(h.x, h.y);
      else this.open('people');
    });
    setInterval(() => this.rotateTicker(), 7000);
  }

  private civic: Msg | null = null;
  private headlines: Msg[] = [];
  private tickerIndex = -1;
  private lastSeq = 0;
  private advisors: Msg | null = null;

  private rotateTicker() {
    if (!this.headlines.length) return;
    this.tickerIndex = (this.tickerIndex + 1) % Math.min(6, this.headlines.length);
    this.showHeadline(this.headlines[this.tickerIndex]);
  }

  private showHeadline(h: Msg) {
    const el = $('#ticker-text');
    el.textContent = h.text;
    el.className = `sev-${h.severity}`;
    el.classList.remove('slide');
    void el.offsetWidth;
    el.classList.add('slide');
    $('#ticker').classList.toggle('located', h.x >= 0);
  }

  private onCivic(c: Msg) {
    this.civic = c;
    const before = [...this.host.city.unlocked].sort().join();
    this.host.city.unlocked = new Set(c.unlockedTypes);
    if ([...this.host.city.unlocked].sort().join() !== before) this.host.onUnlocks();
    // Newest first for the ticker.
    this.headlines = [...c.headlines].reverse();
    const newest = this.headlines[0];
    if (newest && newest.seq !== this.lastSeq) {
      this.lastSeq = newest.seq;
      this.tickerIndex = 0;
      this.showHeadline(newest);
    }
    const a = $('#approval');
    a.textContent = `${c.approval >= 50 ? '👍' : '👎'} ${c.approval}%`;
    a.classList.toggle('neg', c.approval < 40);
    const open = (c.prompts as Msg[]).filter((p) => p.status === 0);
    $('#tabs button[data-tab="people"]').textContent = open.length ? `People (${open.length})` : 'People';
    this.renderPeople();
  }

  /** Opens a tab and asks the server for fresh data when the tab needs it. */
  open(tab: string) {
    this.host.showTab(tab);
    this.refresh(tab);
  }

  refresh(tab: string) {
    if (tab === 'demand') this.host.conn.send({ demand: {} });
    if (tab === 'laws') this.host.conn.send({ ordinances: {} });
    if (tab === 'people') this.host.conn.send({ advisors: {} });
    if (tab === 'guide') this.host.conn.send({ encyclopedia: {} });
  }

  /** Called on the first day of each month so open panels stay live. */
  onMonth(activeTab: string) {
    if (['demand', 'guide', 'laws', 'people'].includes(activeTab)) this.refresh(activeTab);
  }

  onMessage(kind: string, body: Msg, replyTo = 0): boolean {
    if (kind === 'forecast' && this.compare && (replyTo === this.compare.baseSeq || replyTo === this.compare.planSeq)) {
      if (replyTo === this.compare.baseSeq) this.compare.base = body;
      else this.compare.plan = body;
      this.renderLaws();
      return true;
    }
    switch (kind) {
      case 'ordinances': this.ordinances = body.ordinances; this.renderLaws(); return true;
      case 'civic': this.onCivic(body); return true;
      case 'advisors': this.advisors = body; this.renderPeople(); return true;
      case 'demand': this.renderDemand(body); return true;
      case 'alerts': this.alerts = body.alerts; this.renderAlerts(); return true;
      case 'forecast': this.forecastBusy = false; this.lastForecast = body; this.renderForecast(); return true;
      case 'encyclopedia':
        this.articles = body.articles;
        this.articleById = new Map(this.articles.map((a) => [a.id, a]));
        this.renderGuide();
        return true;
    }
    return false;
  }

  /** Shows an encyclopedia article, fetching live data first. */
  article(id: string) {
    if (!id) return;
    this.currentArticle = id;
    this.search = '';
    this.open('guide');
    this.renderGuide();
  }

  private topicLink(topic: string, text = 'Learn more') {
    return topic ? `<a href="#" class="topic" data-topic="${esc(topic)}">${esc(text)}</a>` : '';
  }

  private wireLinks(root: HTMLElement) {
    root.querySelectorAll<HTMLAnchorElement>('a.topic').forEach((a) =>
      a.addEventListener('click', (e) => { e.preventDefault(); this.article(a.dataset.topic!); }),
    );
    root.querySelectorAll<HTMLElement>('[data-jump]').forEach((el) =>
      el.addEventListener('click', (e) => {
        if ((e.target as HTMLElement).closest('a.topic')) return;
        const [x, y] = el.dataset.jump!.split(',').map(Number);
        this.host.jumpTo(x, y);
      }),
    );
  }

  // -------------------------------------------------------------------------
  // Demand
  // -------------------------------------------------------------------------

  private renderDemand(r: Msg) {
    const bar = (v: number, cls: string) => {
      const w = Math.min(50, Math.abs(v) / 2);
      return `<span class="track"><span class="fill ${v >= 0 ? 'pos' : 'neg'} ${cls}" style="width:${w}%"></span></span>`;
    };
    const line = (d: Msg, k?: number) => {
      const v = d.points.length === 1 ? d.points[0] : d.points[k ?? 0];
      return `<div class="factor" title="${esc(d.detail)}"><span>${esc(d.label)}</span>${bar(v, '')}<span class="val">${signed(v)}</span></div>
        <div class="line-detail muted">${esc(d.detail)}</div>`;
    };
    const zones = (r.zones as Msg[]).map((z) => {
      const zi = z.zone - 1;
      const cls = 'rci'[zi];
      const levels = [0, 1, 2]
        .map((k) => {
          const capped = z.final[k] < z.raw[k];
          return `<div class="lvl"><span class="tag">${WEALTH_TAGS[zi][k]}</span><b class="${z.final[k] > 0 ? 'pos' : 'neg'}">${signed(z.final[k])}</b>${capped ? `<span class="muted" title="before limits"> of ${signed(z.raw[k])}</span>` : ''}</div>`;
        })
        .join('');
      const adjust = (z.adjustments as Msg[])
        .map((a) => `<tr><td>${esc(a.label)}</td>${[0, 1, 2].map((k) => `<td class="num">${signed(a.points[k] ?? a.points[0])}</td>`).join('')}</tr>`)
        .join('');
      const limits = (z.limits as Msg[])
        .map((l) => `<tr><td>${esc(l.label)}</td><td class="num">${l.key === 'power' ? (l.points[0] >= 100 ? 'no limit' : `caps at ${l.points[0]}`) : l.points[0].toLocaleString('en-US') + ' cells'}</td></tr><tr><td colspan="2" class="muted small">${esc(l.detail)}</td></tr>`)
        .join('');
      const located = z.x >= 0;
      return `<div class="zone-card ${cls}">
        <div class="zone-head"><h3>${ZONES[z.zone]}</h3><div class="levels">${levels}</div></div>
        <div class="binding ${z.bindingKey === 'growing' ? 'ok' : ''}" ${located ? `data-jump="${z.x},${z.y}" title="Click to go there"` : ''}>
          ${esc(z.binding)} ${located ? '<span class="go">Show me →</span>' : ''} ${this.topicLink(z.topic)}
        </div>
        <h4>Where demand comes from</h4>
        ${(z.sources as Msg[]).map((s) => line(s)).join('')}
        <h4>By wealth</h4>
        <table class="kv adj"><tr><td></td>${WEALTH_TAGS[zi].map((t) => `<td class="num muted">${t}</td>`).join('')}</tr>${adjust}</table>
        <h4>Limits</h4>
        <table class="kv">${limits}</table>
      </div>`;
    });
    const el = $('#tab-demand');
    el.innerHTML = `<p class="muted">Demand is what the city wants more of. Each zone lists its sources, then the one thing holding it back.</p>${zones.join('')}`;
    this.wireLinks(el);
  }

  // -------------------------------------------------------------------------
  // Alerts
  // -------------------------------------------------------------------------

  private renderAlerts() {
    const serious = this.alerts.filter((a) => a.severity !== 'info').length;
    const badge = $('#alert-count');
    badge.textContent = String(this.alerts.length);
    badge.className = `badge ${serious ? (this.alerts.some((a) => a.severity === 'danger') ? 'danger' : 'warning') : this.alerts.length ? 'info' : 'none'}`;
    const el = $('#tab-alerts');
    if (!this.alerts.length) {
      el.innerHTML = '<h3>Alerts</h3><p class="muted">Nothing needs your attention right now.</p>';
      return;
    }
    el.innerHTML = `<h3>Alerts</h3><p class="muted">Click an alert to go to its source.</p>` +
      this.alerts
        .map((a) => `<div class="alert ${a.severity}" ${a.x >= 0 ? `data-jump="${a.x},${a.y}"` : ''}>
          <div>${esc(a.text)}</div>
          <div class="alert-foot">${a.x >= 0 ? '<span class="go">Go there →</span>' : '<span></span>'}${this.topicLink(a.topic, 'Why?')}</div>
        </div>`)
        .join('');
    this.wireLinks(el);
  }

  // -------------------------------------------------------------------------
  // Forecast and what-if planning
  // -------------------------------------------------------------------------

  /** Called with every action the player makes. Returns true when it was captured into the plan. */
  capture(action: Msg, label: string): boolean {
    if (!this.planning) return false;
    this.plan.push({ action, label });
    this.renderForecast();
    return true;
  }

  private runForecast(withPlan: boolean) {
    this.forecastBusy = true;
    this.host.conn.send({ forecast: { months: this.forecastMonths, plan: withPlan ? this.plan.map((p) => p.action) : [] } });
    this.renderForecast();
  }

  private renderForecast() {
    const f = this.lastForecast;
    const opts = [3, 6, 12, 24, 60].map((m) => `<option value="${m}" ${m === this.forecastMonths ? 'selected' : ''}>${m < 12 ? `${m} months` : `${m / 12} year${m > 12 ? 's' : ''}`}</option>`).join('');
    let result = '';
    if (f) {
      const pts = f.points as Msg[];
      const label = (p: Msg) => `${MONTHS[p.month - 1]} ${p.year}`;
      result = `
        <h4>${f.withPlan ? 'With your plan' : 'If nothing changes'}, ${f.months} months ahead</h4>
        <ul class="summary-list">${(f.summary as string[]).map((s) => `<li>${esc(s)}</li>`).join('')}</ul>
        ${chart('Population', pts.map((p) => p.population), (v) => v.toLocaleString('en-US'), 'var(--r)')}
        ${chart('Treasury', pts.map((p) => p.funds), money, 'var(--accent)')}
        ${chart('Jobs', pts.map((p) => p.jobs), (v) => v.toLocaleString('en-US'), 'var(--c)')}
        <div class="muted small">${label(pts[0])} to ${label(pts[pts.length - 1])}</div>
        <h4>What happens</h4>
        ${(f.events as Msg[]).length ? (f.events as Msg[]).map((e) => `<div class="alert ${e.severity === 'good' ? 'good' : e.severity}" ${e.x >= 0 ? `data-jump="${e.x},${e.y}"` : ''}><span class="muted">${MONTHS[e.month - 1]} ${e.year}</span> ${esc(e.text)}</div>`).join('') : '<p class="muted">Nothing notable.</p>'}`;
    }
    const planList = this.plan.length
      ? `<ol class="plan">${this.plan.map((p) => `<li>${esc(p.label)}</li>`).join('')}</ol>`
      : '<p class="muted">Turn on planning, then use the tools as usual. Nothing is built until you say so.</p>';
    const el = $('#tab-forecast');
    el.innerHTML = `
      <h3>Forecast</h3>
      <p class="muted">Runs a copy of the city ahead. The simulation is deterministic, so this is exactly what happens if you change nothing. ${this.topicLink('forecast')}</p>
      <div class="row-flex"><select id="fc-months">${opts}</select><button id="fc-run" ${this.forecastBusy ? 'disabled' : ''}>${this.forecastBusy ? 'Running…' : 'Run forecast'}</button></div>
      <h4>What if…</h4>
      <div class="row-flex"><button id="fc-plan" class="${this.planning ? 'active' : ''}">${this.planning ? 'Planning: on' : 'Plan changes'}</button>
        ${this.plan.length ? `<button id="fc-plan-run" ${this.forecastBusy ? 'disabled' : ''}>Forecast plan</button><button id="fc-plan-build">Build it</button><button id="fc-plan-clear">Discard</button>` : ''}</div>
      ${planList}
      ${result}`;
    $<HTMLSelectElement>('#fc-months').addEventListener('change', (e) => (this.forecastMonths = Number((e.target as HTMLSelectElement).value)));
    $('#fc-run').addEventListener('click', () => this.runForecast(false));
    $('#fc-plan').addEventListener('click', () => { this.planning = !this.planning; this.renderForecast(); });
    document.getElementById('fc-plan-run')?.addEventListener('click', () => this.runForecast(true));
    document.getElementById('fc-plan-build')?.addEventListener('click', () => {
      for (const p of this.plan) this.host.conn.action(p.action);
      this.plan = [];
      this.planning = false;
      this.renderForecast();
    });
    document.getElementById('fc-plan-clear')?.addEventListener('click', () => { this.plan = []; this.renderForecast(); });
    this.wireLinks(el);
  }

  // -------------------------------------------------------------------------
  // People: approval, petitions and deals, advisors, news
  // -------------------------------------------------------------------------

  private renderPeople() {
    const el = $('#tab-people');
    const c = this.civic;
    if (!c) return;
    const prompts = c.prompts as Msg[];
    const open = prompts.filter((p) => p.status === 0);
    const done = prompts.filter((p) => p.status !== 0).slice(-6).reverse();
    const promptCard = (p: Msg) => `
      <div class="alert ${p.kind === 'deal' ? 'deal' : 'petition'}" ${p.x >= 0 ? `data-jump="${p.x},${p.y}"` : ''}>
        <div class="muted small">${p.kind === 'deal' ? 'Deal' : `Petition from ${esc(p.petitioner)}`} · ${p.monthsLeft} month${p.monthsLeft === 1 ? '' : 's'} left</div>
        <div>${esc(p.text)}</div>
        ${p.goal ? `<div class="small goal">Wants: ${esc(p.goal)}</div>` : ''}
        <div class="row-flex" style="margin-top:6px">
          ${p.kind === 'deal'
            ? `<button class="active" data-respond="${p.id}" data-accept="1">Accept</button><button data-respond="${p.id}" data-accept="0">Decline</button>`
            : `<button data-respond="${p.id}" data-accept="0" title="Turn them down now">Dismiss</button>`}
          ${p.x >= 0 ? '<span class="go">Go there →</span>' : ''}
        </div>
      </div>`;
    const MOOD: Record<string, string> = { happy: '🙂', worried: '😟', alarmed: '😱' };
    const adv = this.advisors
      ? (this.advisors.advisors as Msg[]).map((a) => `
        <div class="advisor ${a.mood}">
          <div class="adv-head"><span class="face">${MOOD[a.mood] ?? '🙂'}</span><b>${esc(a.name)}</b><span class="muted small">${esc(a.title)}</span></div>
          ${(a.messages as Msg[]).map((m) => `<div class="adv-msg ${m.severity}" ${m.x >= 0 ? `data-jump="${m.x},${m.y}"` : ''}>${esc(m.text)} ${this.topicLink(m.topic, 'Why?')}</div>`).join('')}
        </div>`).join('')
      : '<p class="muted">Asking the advisors…</p>';
    const factors = this.advisors
      ? (this.advisors.approvalFactors as Msg[]).map((f) => `<tr><td>${esc(f.label)}</td><td class="num ${f.value >= 0 ? 'pos' : 'neg'}">${f.value > 0 ? '+' : ''}${f.value}</td></tr>`).join('')
      : '';
    el.innerHTML = `
      <h3>People</h3>
      <div class="approval-meter"><span style="width:${c.approval}%"></span></div>
      <div class="row-flex" style="justify-content:space-between"><span>Mayor approval <b>${c.approval}%</b></span>${this.topicLink('approval')}</div>
      ${factors ? `<details><summary class="muted small">Why ${c.approval}%?</summary><table class="kv">${factors}</table></details>` : ''}
      <h4>Waiting for you (${open.length})</h4>
      ${open.length ? open.map(promptCard).join('') : '<p class="muted">No petitions or offers right now.</p>'}
      <h4>Advisors</h4>
      ${adv}
      ${done.length ? `<h4>Recently settled</h4>${done.map((p) => `<div class="alert ${p.status === 1 || p.status === 3 ? 'good' : 'warning'}" ${p.x >= 0 ? `data-jump="${p.x},${p.y}"` : ''}>${esc(p.resultText)}</div>`).join('')}` : ''}
      <h4>News</h4>
      ${this.headlines.slice(0, 12).map((h) => `<div class="news ${h.severity}" ${h.x >= 0 ? `data-jump="${h.x},${h.y}"` : ''}>${esc(h.text)}</div>`).join('')}`;
    el.querySelectorAll<HTMLButtonElement>('button[data-respond]').forEach((b) =>
      b.addEventListener('click', (e) => {
        e.stopPropagation();
        this.host.conn.action({ respond: { promptId: Number(b.dataset.respond), accept: b.dataset.accept === '1' } });
      }),
    );
    this.wireLinks(el);
  }

  // -------------------------------------------------------------------------
  // Laws (ordinances)
  // -------------------------------------------------------------------------

  private renderLaws() {
    const el = $('#tab-laws');
    const STATUS = ['Locked', 'Available', 'Offered', 'Enacted'];
    const groups = new Map<string, Msg[]>();
    for (const o of this.ordinances) groups.set(o.category, [...(groups.get(o.category) ?? []), o]);
    const card = (o: Msg) => {
      const open = this.openLaw === o.id;
      const status = STATUS[o.status];
      const left = o.monthsLeft > 0 ? ` · ${o.monthsLeft} month${o.monthsLeft > 1 ? 's' : ''} ${o.status === 2 ? 'to decide' : 'left'}` : '';
      const net = o.monthlyNet === 0 ? 'free' : `${o.monthlyNet > 0 ? '+' : '-'}$${Math.abs(o.monthlyNet).toLocaleString('en-US')}/mo`;
      const button = o.status === 3 ? `<button data-law="${esc(o.id)}" data-enact="0">Repeal</button>`
        : o.status === 0 ? '' : `<button class="active" data-law="${esc(o.id)}" data-enact="1">Enact</button>`;
      let detail = '';
      if (open) {
        const c = this.compare?.id === o.id ? this.compare : null;
        let cmp = '';
        if (c) {
          if (!c.base || !c.plan) cmp = '<p class="muted">Forecasting a year both ways…</p>';
          else {
            const b = c.base.points[c.base.points.length - 1];
            const p = c.plan.points[c.plan.points.length - 1];
            const row = (label: string, x: number, y: number, fmt: (n: number) => string) =>
              `<tr><td>${label}</td><td class="num">${fmt(x)}</td><td class="num">${fmt(y)}</td><td class="num ${y - x >= 0 ? 'pos' : 'neg'}">${y - x >= 0 ? '+' : ''}${fmt(y - x)}</td></tr>`;
            const n = (v: number) => v.toLocaleString('en-US');
            cmp = `<table class="kv cmp"><tr><td>In a year</td><td class="num muted">Without</td><td class="num muted">With</td><td class="num muted">Change</td></tr>
              ${row('Population', b.population, p.population, n)}${row('Jobs', b.jobs, p.jobs, n)}${row('Treasury', b.funds, p.funds, money)}
              ${row('Power use %', b.powerPercent, p.powerPercent, n)}${row('Abandoned', b.abandoned, p.abandoned, n)}</table>`;
          }
        }
        detail = `
          <p>${esc(o.description)}</p>
          <p class="muted small">${esc(o.availability)}</p>
          ${(o.effects as string[]).length ? `<h4>Effects now</h4><ul class="effects">${(o.effects as string[]).map((e) => `<li>${esc(e)}</li>`).join('')}</ul>` : ''}
          <h4>How it works out in your city</h4>
          <table class="kv">${(o.steps as Msg[]).map((s) => `<tr><td>${esc(s.key)}</td><td>${esc(s.value)}</td></tr>`).join('')}</table>
          <div class="row-flex" style="margin-top:8px">${button}${o.status !== 0 ? `<button data-compare="${esc(o.id)}">Forecast with and without</button>` : ''}${this.topicLink(o.id, 'Guide')}</div>
          ${cmp}`;
      }
      return `<div class="law ${open ? 'open' : ''} s${o.status}">
        <div class="law-head" data-open="${esc(o.id)}"><span class="law-name">${esc(o.name)}</span><span class="chip s${o.status}">${status}${left}</span><span class="law-net ${o.monthlyNet >= 0 ? 'pos' : 'neg'}">${net}</span></div>
        ${open ? detail : `<div class="muted small">${esc((o.effects as string[]).join(', ') || o.description)}</div>`}
      </div>`;
    };
    const enacted = this.ordinances.filter((o) => o.status === 3);
    const net = enacted.reduce((a, o) => a + o.monthlyNet, 0);
    el.innerHTML = `<h3>Laws</h3>
      <p class="muted">Ordinances change how the city runs. Each preview is the ordinance's own rule graph run against your city right now. ${this.topicLink('ordinances')}</p>
      <p>${enacted.length} enacted, ${net >= 0 ? '+' : '-'}$${Math.abs(net).toLocaleString('en-US')} a month.</p>
      ${this.ordinances.length ? '' : '<p class="muted">Loading…</p>'}
      ${[...groups].map(([cat, list]) => `<h4>${esc(cat)}</h4>${list.map(card).join('')}`).join('')}`;
    el.querySelectorAll<HTMLElement>('[data-open]').forEach((h) =>
      h.addEventListener('click', () => { this.openLaw = this.openLaw === h.dataset.open ? '' : h.dataset.open!; this.renderLaws(); }),
    );
    el.querySelectorAll<HTMLButtonElement>('button[data-law]').forEach((b) =>
      b.addEventListener('click', () => {
        const action = { ordinance: { id: b.dataset.law, enact: b.dataset.enact === '1' } };
        const name = this.ordinances.find((o) => o.id === b.dataset.law)?.name ?? b.dataset.law!;
        if (this.capture(action, `${b.dataset.enact === '1' ? 'Enact' : 'Repeal'} ${name}`)) return;
        this.host.conn.action(action);
        setTimeout(() => this.refresh('laws'), 300);
      }),
    );
    el.querySelectorAll<HTMLButtonElement>('button[data-compare]').forEach((b) =>
      b.addEventListener('click', () => {
        const id = b.dataset.compare!;
        const o = this.ordinances.find((x) => x.id === id);
        const action = { ordinance: { id, enact: o?.status !== 3 } };
        // With an enacted ordinance, "with" is the city as it is and "without" repeals it.
        const baseSeq = this.host.conn.send({ forecast: { months: 12, plan: o?.status === 3 ? [action] : [] } });
        const planSeq = this.host.conn.send({ forecast: { months: 12, plan: o?.status === 3 ? [] : [action] } });
        this.compare = { id, baseSeq, planSeq };
        this.renderLaws();
      }),
    );
    this.wireLinks(el);
  }

  // -------------------------------------------------------------------------
  // Encyclopedia
  // -------------------------------------------------------------------------

  private renderGuide() {
    const el = $('#tab-guide');
    const a = this.articleById.get(this.currentArticle);
    if (a && !this.search) {
      el.innerHTML = `
        <a href="#" id="guide-back" class="muted">← All articles</a>
        <h3>${esc(a.title)}</h3><div class="muted small">${esc(a.category)}</div>
        ${(a.paragraphs as string[]).map((p) => `<p>${esc(p)}</p>`).join('')}
        ${(a.facts as Msg[]).length ? `<h4>In your city now</h4><table class="kv">${(a.facts as Msg[]).map((f) => `<tr><td>${esc(f.key)}</td><td>${esc(f.value)}</td></tr>`).join('')}</table>` : ''}
        ${(a.seeAlso as string[]).length ? `<h4>See also</h4><div class="see">${(a.seeAlso as string[]).map((s) => this.topicLink(s, this.articleById.get(s)?.title ?? s)).join('')}</div>` : ''}`;
      $('#guide-back').addEventListener('click', (e) => { e.preventDefault(); this.currentArticle = ''; this.renderGuide(); });
      this.wireLinks(el);
      return;
    }
    const q = this.search.toLowerCase();
    const hits = this.articles.filter((x) => !q || x.title.toLowerCase().includes(q) || (x.paragraphs as string[]).some((p) => p.toLowerCase().includes(q)));
    const groups = new Map<string, Msg[]>();
    for (const h of hits) groups.set(h.category, [...(groups.get(h.category) ?? []), h]);
    el.innerHTML = `
      <h3>Guide</h3>
      <p class="muted">Every rule in the game, written from the live city.</p>
      <input type="search" id="guide-search" placeholder="Search…" value="${esc(this.search)}">
      ${this.articles.length ? '' : '<p class="muted">Loading…</p>'}
      ${[...groups].map(([cat, list]) => `<h4>${esc(cat)}</h4><div class="see">${list.map((x) => this.topicLink(x.id, x.title)).join('')}</div>`).join('')}`;
    const input = $<HTMLInputElement>('#guide-search');
    input.addEventListener('input', () => {
      this.search = input.value;
      this.currentArticle = '';
      this.renderGuide();
      const again = $<HTMLInputElement>('#guide-search');
      again.focus();
      again.setSelectionRange(again.value.length, again.value.length);
    });
    this.wireLinks(el);
  }
}

/** A small line chart as inline SVG, with the first and last values labelled. */
function chart(title: string, values: number[], fmt: (v: number) => string, color: string) {
  if (!values.length) return '';
  const w = 300;
  const h = 56;
  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min || 1;
  const pts = values.map((v, i) => `${(i / Math.max(1, values.length - 1)) * w},${h - 4 - ((v - min) / span) * (h - 8)}`).join(' ');
  const zero = min < 0 && max > 0 ? `<line x1="0" x2="${w}" y1="${h - 4 - ((0 - min) / span) * (h - 8)}" y2="${h - 4 - ((0 - min) / span) * (h - 8)}" class="zero"/>` : '';
  return `<div class="chart"><div class="chart-head"><span>${title}</span><span>${fmt(values[0])} → <b>${fmt(values[values.length - 1])}</b></span></div>
    <svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="none">${zero}<polyline points="${pts}" style="stroke:${color}"/></svg></div>`;
}
