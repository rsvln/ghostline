// Strings of the web language (locale.web) are put in by the server. t('key', arg0, arg1...).
const I18N = /*I18N*/{};
const t = (k, ...a) => (I18N[k] ?? k).replace(/\{(\d+)\}/g, (_, i) => a[i]);

// Every view has its own address: /calls, /sms, /pbx, /system, /status, /config, /config/yaml, /about,
// with the filters in the query string. F5 shows the same view, links can be shared and
// the browser's Back / Forward move between views. "/" opens the view seen last.
const TABS = ['pbx', 'calls', 'sms', 'system', 'status', 'config', 'about'];
const LISTS_VIEWS = ['calls', 'sms', 'pbx'];
const val = id => document.getElementById(id).value;
const checked = id => document.getElementById(id).checked;

function esc(s) {
  return String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// Settings of this browser (lines, auto-refresh) and the last address of every tab stay in localStorage.
const PREFS_KEY = 'ghostline.prefs';
const PREFS = ['calls-refresh', 'sms-refresh', 'pbx-refresh', 'sys-level', 'sys-lines', 'sys-refresh', 'status-refresh'];

function readPrefs() {
  try { return JSON.parse(localStorage.getItem(PREFS_KEY)) || {}; } catch { return {}; }
}

function writePrefs(update) {
  try { localStorage.setItem(PREFS_KEY, JSON.stringify(Object.assign(readPrefs(), update))); } catch { }
}

document.addEventListener('change', e => {
  if (PREFS.includes(e.target.id)) writePrefs({ fields: Object.fromEntries(PREFS.map(id => [id, val(id)])) });
});

// A select gets the value even if the option isn't there yet (a line from a link), so the view matches the address.
function setSelect(id, value) {
  const el = document.getElementById(id);
  if (value && ![...el.options].some(o => o.value === value))
    el.insertAdjacentHTML('beforeend', `<option value="${esc(value)}">${esc(value)}</option>`);
  el.value = value;
}

function buildUrl(path, query) {
  const q = new URLSearchParams(Object.entries(query).filter(([, v]) => v !== undefined && v !== null && v !== '' && v !== false)).toString();
  return path + (q ? '?' + q : '');
}

let MODE = { mode: 'sms', transcribe: false };
const defaultView = () => 'pbx';   // the first tab: SMS and calls together

function parseRoute() {
  const parts = location.pathname.split('/').filter(Boolean).map(decodeURIComponent);
  const q = Object.fromEntries(new URLSearchParams(location.search));
  if (parts[0] === 'log') parts[0] = 'pbx';   // the PBX tab was called Log
  let view = TABS.includes(parts[0]) ? parts[0] : defaultView();
  if (view === 'calls' && MODE.mode !== 'full') view = 'sms';
  return { view, sub: parts[1], q };
}

// What a list shows: 'calls', 'sms' or '' (both, the PBX tab). Without calls (mode: sms) the PBX tab is SMS only.
function listKind(v, q) {
  return v === 'pbx' ? (MODE.mode === 'full' ? q.kind || '' : 'sms') : v;
}

// Address built from the toolbar values. Defaults are left out.
function listUrl(v) {
  const kind = v === 'pbx' && MODE.mode === 'full' ? val('pbx-kind') : undefined;
  const calls = listKind(v, { kind }) === 'calls';
  return buildUrl('/' + v, {
    kind, line: val(v + '-line'), dir: val(v + '-dir'),
    rec: calls && !checked(v + '-rec') ? '0' : '', missed: calls && checked(v + '-missed') ? '1' : '',
    q: val(v + '-q').trim(), from: readDate(v + '-from') || '', to: readDate(v + '-to') || ''
  });
}

// history.state.n counts the views opened in this browser tab.
let navIndex = history.state?.n ?? 0;

function navigate(url, replace) {
  if (url !== location.pathname + location.search) {
    if (replace) history.replaceState({ n: navIndex }, '', url);
    else history.pushState({ n: ++navIndex }, '', url);
  }
  return render();
}

window.addEventListener('popstate', e => { navIndex = e.state?.n ?? 0; render(); });

// Tabs and links inside the page change the view without reloading it; a tab opens where it was left,
// a click on the open tab goes to its start.
document.addEventListener('click', e => {
  const a = e.target.closest('a[href^="/"]');
  if (!a || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || a.target || a.hasAttribute('download')) return;
  const url = new URL(a.href);
  if (/^\/(api|js|css)\//.test(url.pathname)) return;
  e.preventDefault();
  const tab = a.dataset.tab;
  if (tab) navigate(parseRoute().view === tab ? '/' + tab : (readPrefs().urls || {})[tab] || '/' + tab);
  else if (url.pathname === '/') navigate('/' + defaultView());
  else navigate(url.pathname + url.search);
});

let renderedView = null;

async function render() {
  const r = parseRoute();
  if (location.pathname === '/log') history.replaceState(history.state, '', '/pbx' + location.search);
  const url = location.pathname + location.search;
  const urls = readPrefs().urls || {};
  urls[r.view] = url;
  writePrefs({ urls, last: url });

  document.querySelectorAll('.tab').forEach(el => el.classList.toggle('active', el.dataset.tab === r.view));
  document.querySelectorAll('.panel').forEach(p => p.classList.toggle('active', p.id === 'panel-' + r.view));
  document.title = 'ghostline · ' + (document.querySelector(`.tab[data-tab="${r.view}"]`)?.textContent || '');
  const entering = renderedView !== r.view;
  renderedView = r.view;
  const q = r.q;

  if (LISTS_VIEWS.includes(r.view)) {
    fillToolbar(r.view, q);
    loadList(r.view);
  } else if (r.view === 'system') {
    loadSystemLog();
  } else if (r.view === 'status') {
    loadStatus();
  } else if (r.view === 'config') {
    // The open config view is not reloaded when another tree section is chosen, so edits are kept.
    const mode = r.sub === 'yaml' ? 'yaml' : 'form';
    if (entering || mode !== configMode) setConfigMode(mode);
    else if (mode === 'form' && q.s) showConfigNode(q.s, true);
  } else if (r.view === 'about') {
    loadAbout();
  }
}

let debounceTimer = null;
function debounced(fn) {
  clearTimeout(debounceTimer);
  debounceTimer = setTimeout(fn, 350);
}

function showToast(msg, type) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.className = 'toast ' + type + ' show';
  clearTimeout(showToast.timer);
  showToast.timer = setTimeout(() => el.classList.remove('show'), type === 'err' ? 6000 : 3000);
}

function setBusy(text) {
  document.getElementById('busy-text').textContent = text || '';
  document.getElementById('busy').classList.toggle('show', !!text);
}

// ---- lines (for the filters and for sending SMS) ----

let channelsCache = [];

async function loadChannels() {
  try {
    channelsCache = await (await fetch('/api/channels')).json();
  } catch { channelsCache = []; }
  const opts = channelsCache.map(c => `<option value="${esc(c.name)}">${esc(c.name)}</option>`).join('');
  LISTS_VIEWS.forEach(v => document.getElementById(v + '-line').innerHTML =
    `<option value="">${esc(t('web.calls.all_lines'))}</option>` + opts);
  document.getElementById('send-channel').innerHTML =
    channelsCache.map(c => `<option value="${esc(c.name)}">${esc(c.name)} (${esc(c.type)})</option>`).join('');
}

// ---- Calls (mode: full) ----

function fmtDur(s) { return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); }
function fmtSec(x) { x = Math.floor(x); return Math.floor(x / 60) + ':' + String(x % 60).padStart(2, '0'); }

// "2026-09-26 13:16:43" -> "26.09 13:16" (the year only if it is not the current one)
function fmtTs(ts) {
  const [d, time] = ts.split(' ');
  const [y, mo, da] = d.split('-');
  const year = y != new Date().getFullYear() ? '.' + y.slice(2) : '';
  return `${da}.${mo}${year} ${time.slice(0, 5)}`;
}

function highlight(text, q) {
  let html = esc(text);
  const words = (q || '').split(/\s+/).map(w => w.replace(/[^\p{L}\p{N}]/gu, '')).filter(w => w.length > 1);
  for (const w of words)
    html = html.replace(new RegExp('(' + w + ')', 'giu'), '<mark>$1</mark>');
  return html;
}

// Date from a text field to yyyy-mm-dd. Understands dd.mm.yyyy, dd.mm.yy, dd.mm (current year)
// and yyyy-mm-dd. '': the field is empty; false: not recognized (the field turns red).
function readDate(id) {
  const el = document.getElementById(id);
  const v = el.value.trim();
  el.classList.remove('bad');
  if (!v) return '';
  let m, y, mo, d;
  if ((m = v.match(/^(\d{4})-(\d{1,2})-(\d{1,2})$/))) [, y, mo, d] = m;
  else if ((m = v.match(/^(\d{1,2})[.\/](\d{1,2})(?:[.\/](\d{2}|\d{4}))?$/))) {
    [, d, mo, y] = m;
    y = !y ? String(new Date().getFullYear()) : (y.length === 2 ? '20' + y : y);
  }
  const dt = m && new Date(+y, +mo - 1, +d);
  if (!dt || dt.getMonth() !== +mo - 1 || dt.getDate() !== +d) {
    el.classList.add('bad');
    showToast(t('web.calls.date_invalid', v), 'err');
    return false;
  }
  return `${y}-${String(mo).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
}

// Icons are SVG in one outline style: the same everywhere, no internet needed.
const svg = d => `<svg viewBox="0 0 24 24">${d}</svg>`;
const ICON_PLAY = svg('<path d="M7 4.5v15l12.5-7.5z" fill="currentColor" stroke="none"/>');
const ICON_DOWNLOAD = svg('<path d="M12 3v12"/><path d="M7 10l5 5 5-5"/><path d="M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/>');
const ICON_TEXT = svg('<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/><path d="M9 13h6M9 17h6"/>');
const ICON_PENDING = svg('<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>');
const ICON_ERROR = svg('<circle cx="12" cy="12" r="9"/><path d="M12 7.5v5.5"/><path d="M12 16.5h.01"/>');
// A call is a handset (SMS rows have plain arrows), so the two kinds differ at a glance on the PBX tab.
const PHONE = '<path d="M22 16.9v3a2 2 0 0 1-2.2 2 19.8 19.8 0 0 1-8.6-3.1 19.5 19.5 0 0 1-6-6A19.8 19.8 0 0 1 2.1 4.2 2 2 0 0 1 4.1 2h3a2 2 0 0 1 2 1.7c.1 1 .4 1.9.7 2.8a2 2 0 0 1-.5 2.1L8.1 9.9a16 16 0 0 0 6 6l1.3-1.3a2 2 0 0 1 2.1-.4c.9.3 1.8.6 2.8.7a2 2 0 0 1 1.7 2z"/>';
const ICON_CALL = {
  in: svg(PHONE + '<path d="M16 2v6h6"/><path d="M22 2l-6 6"/>'),
  out: svg(PHONE + '<path d="M22 8V2h-6"/><path d="M16 8l6-6"/>'),
  missed: svg(PHONE + '<path d="M16 2l6 6"/><path d="M22 2l-6 6"/>'),
};
const ICON_COPY = svg('<rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v1"/>');

// The web interface usually runs over plain http, where navigator.clipboard is missing:
// then a hidden textarea and execCommand('copy'), which works within the click.
async function copyText(text) {
  try {
    if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(text);
    else {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', '');
      ta.style.cssText = 'position:fixed;top:0;left:0;opacity:0';
      document.body.appendChild(ta);
      ta.select();
      const ok = document.execCommand('copy');
      ta.remove();
      if (!ok) throw new Error('copy');
    }
    showToast(t('web.copied'), 'ok');
  } catch { showToast(t('web.copy_failed'), 'err'); }
}

// The transcript as plain text, the same as "Download .txt": header (date, duration, who) and the dialog.
async function copyTranscript(id) {
  const res = await fetch(`/api/calls/${id}/transcript`);
  if (!res.ok) { showToast(t('web.copy_failed'), 'err'); return; }
  copyText((await res.text()).replace(/^\uFEFF/, ''));
}

// An SMS as plain text with a header, like the transcript: kind, date, who, line, then the text.
function copySms(id) {
  const r = smsCache[id];
  if (!r) return;
  const [d, time] = r.ts.split(' ');
  const who = r.peerName ? `${r.peerName} (${r.peer})` : r.peer;
  const head = [
    t('web.sms.kind.' + r.direction),
    `${t('web.pdf.date')}: ${d.split('-').reverse().join('.')} ${time}`,
    `${t(r.direction === 'in' ? 'web.sms.copy.from' : 'web.sms.copy.to')}: ${who}`,
    `${t('web.sms.copy.line')}: ${r.channel}`,
  ];
  if (r.origin) head.push(`${t('web.sms.origin')}: ${r.origin}`);
  copyText(head.join('\n') + '\n\n' + (r.text || '') + '\n');
}

// ---- Calls, SMS and PBX: one toolbar, one row look ----

const LIST_API = { calls: '/api/calls/list', sms: '/api/sms/list', pbx: '/api/pbx/list' };
const LIST_COUNT = { calls: 'web.calls.count', sms: 'web.sms.count', pbx: 'web.list.count' };

function listToolbar(v) {
  const go = `onchange="navigate(listUrl('${v}'))"`;
  const kind = v === 'pbx' && MODE.mode === 'full'
    ? `<select id="pbx-kind" ${go}>${['', 'sms', 'calls'].map(k =>
        `<option value="${k}">${esc(t('web.list.kind.' + (k || 'all')))}</option>`).join('')}</select>` : '';
  const refresh = [0, 5000, 10000, 30000].map(ms =>
    `<option value="${ms}"${v === 'pbx' && ms === 5000 ? ' selected' : ''}>${ms ? ms / 1000 + 's' : esc(t('web.off'))}</option>`).join('');
  return `<select id="${v}-line" ${go}><option value="">${esc(t('web.calls.all_lines'))}</option></select>
    <select id="${v}-dir" ${go}>
      <option value="">${esc(t('web.all'))}</option>
      <option value="in">${esc(t('web.calls.dir.in'))}</option>
      <option value="out">${esc(t('web.calls.dir.out'))}</option>
    </select>
    ${kind}
    <span class="only-calls">
      <label><input type="checkbox" id="${v}-rec" ${go}> ${esc(t('web.calls.with_recording'))}</label>
      <label><input type="checkbox" id="${v}-missed" onchange="if (this.checked) document.getElementById('${v}-rec').checked = false; navigate(listUrl('${v}'))"> ${esc(t('web.calls.missed_only'))}</label>
    </span>
    <input type="text" id="${v}-q" placeholder="${esc(t(v === 'sms' ? 'web.sms.search' : 'web.calls.search'))}" oninput="debounced(() => navigate(listUrl('${v}'), true))" style="width:220px">
    <label>${esc(t('web.calls.date_from'))}</label>
    ${dateField(v, v + '-from')}
    <label>${esc(t('web.calls.date_to'))}</label>
    ${dateField(v, v + '-to')}
    <button class="btn" onclick="loadList('${v}')">${esc(t('web.refresh'))}</button>
    <a class="btn" href="/${v}">${esc(t('web.clear'))}</a>
    <span class="count" id="${v}-count"></span>
    <span class="only-sms">
      <button class="btn" onclick="exportSms()">${esc(t('web.sms.export'))}</button>
      <button class="btn primary" onclick="openSendModal()">${esc(t('web.sms.send'))}</button>
    </span>
    <div class="autoscroll-toggle">
      <label>${esc(t('web.autorefresh'))}</label>
      <select id="${v}-refresh" onchange="setListRefresh('${v}')">${refresh}</select>
    </div>`;
}

// Dates are a text field in the format of the interface language, with a mask (digits only, the
// separators come by themselves) and a calendar button: <input type="date"> itself would force the
// browser's own format, so it is hidden and only opens its calendar.
const DATE_ISO = /^[yг]/i.test(t('web.calls.date_format'));
const ICON_CALENDAR = svg('<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M3 10h18M8 3v4M16 3v4"/>');

function dateField(v, id) {
  return `<span class="date-field">
      <input type="text" id="${id}" class="date" placeholder="${esc(t('web.calls.date_format'))}" inputmode="numeric" maxlength="10"
        oninput="maskDate(this)" onchange="navigate(listUrl('${v}'))">
      <button type="button" class="date-btn" title="${esc(t('web.calendar'))}" onclick="openCalendar('${id}')">${ICON_CALENDAR}</button>
      <input type="date" class="date-picker" id="${id}-picker" tabindex="-1" aria-hidden="true"
        onchange="document.getElementById('${id}').value = showDate(this.value); navigate(listUrl('${v}'))">
    </span>`;
}

// yyyy-mm-dd (as in the address) -> the interface format.
function showDate(iso) {
  if (!iso || DATE_ISO) return iso || '';
  const [y, m, d] = iso.split('-');
  return `${d}.${m}.${y}`;
}

// Typing at the end: digits only, the separators added. Editing in the middle is left as typed.
function maskDate(el) {
  if (el.selectionStart !== el.value.length) return;
  const d = el.value.replace(/\D/g, '').slice(0, 8);
  const parts = DATE_ISO ? [d.slice(0, 4), d.slice(4, 6), d.slice(6)] : [d.slice(0, 2), d.slice(2, 4), d.slice(4)];
  el.value = parts.filter(Boolean).join(DATE_ISO ? '-' : '.');
}

function openCalendar(id) {
  const picker = document.getElementById(id + '-picker');
  const text = document.getElementById(id);
  const cur = text.value.trim() ? readDate(id) : '';
  picker.value = cur || '';
  try { picker.showPicker(); } catch { picker.focus(); picker.click(); }
}

// The toolbar shows the address; the controls of one kind only where that kind is listed.
function fillToolbar(v, q) {
  if (v === 'pbx' && MODE.mode === 'full') setSelect('pbx-kind', q.kind || '');
  setSelect(v + '-line', q.line || '');
  setSelect(v + '-dir', q.dir || '');
  document.getElementById(v + '-rec').checked = q.rec !== '0' && q.missed !== '1';
  document.getElementById(v + '-missed').checked = q.missed === '1';
  if (document.activeElement?.id !== v + '-q') document.getElementById(v + '-q').value = q.q || '';
  document.getElementById(v + '-from').value = showDate(q.from);
  document.getElementById(v + '-to').value = showDate(q.to);
  const kind = listKind(v, q);
  const bar = document.getElementById(v + '-toolbar');
  bar.querySelector('.only-calls').style.display = kind === 'calls' ? 'contents' : 'none';
  bar.querySelector('.only-sms').style.display = kind === 'sms' ? 'contents' : 'none';
}

function listParams(v, forExport) {
  const q = parseRoute().q;
  const kind = listKind(v, q);
  const params = new URLSearchParams();
  if (!forExport) params.set('limit', 500);
  if (v === 'pbx' && kind) params.set('kind', kind);
  if (q.line) params.set('channel', q.line);
  if (q.dir) params.set('direction', q.dir);
  if (q.q) params.set('q', q.q);
  if (q.from) params.set('from', q.from);
  if (q.to) params.set('to', q.to);
  if (kind === 'calls') {
    if (q.missed === '1') params.set('missed', 'true');
    else if (q.rec !== '0') params.set('rec', 'true');
  }
  return params;
}

const listTimers = {};
function setListRefresh(v) {
  clearInterval(listTimers[v]);
  const ms = parseInt(val(v + '-refresh'));
  if (ms > 0) listTimers[v] = setInterval(() => { if (parseRoute().view === v) loadList(v, true); }, ms);
}

// A row of the open list: a call by data-id, an SMS by data-sid (the PBX tab has both).
const rowOf = (attr, id) => document.querySelector(`.panel.active .call[data-${attr}="${id}"]`);

let callsCache = {};
let smsCache = {};

async function loadList(v, keepOpen) {
  const panel = document.getElementById('panel-' + v);
  if (keepOpen && panel.querySelector('.call audio')) return;   // do not interrupt playback
  const open = keepOpen
    ? [...panel.querySelectorAll('.call-text:not([hidden])')].map(e => e.closest('.call').dataset) : [];

  const rows = await (await fetch(LIST_API[v] + '?' + listParams(v))).json();
  callsCache = {}; smsCache = {};
  rows.forEach(r => { if (r.kind === 'sms') smsCache[r.id] = r; else callsCache[r.id] = r; });
  document.getElementById(v + '-count').textContent = t(LIST_COUNT[v], rows.length);
  document.getElementById(v + '-container').innerHTML =
    rows.map(r => r.kind === 'sms' ? formatSms(r) : formatCall(r)).join('')
    || `<div class="empty">${esc(t('web.calls.nothing'))}</div>`;
  if (parseRoute().q.q) rows.filter(r => r.kind !== 'sms' && r.trState === 'done').forEach(r => toggleText(r.id, true));
  open.forEach(d => d.sid ? toggleSms(d.sid, true) : toggleText(d.id, true));
}

// An SMS in the same grid as a call: time, direction, line, contact; the text in one line,
// all of it with a click on the row.
function formatSms(r) {
  const q = parseRoute().q.q;
  const who = r.peerName
    ? `<b>${esc(r.peerName)}</b><span class="num">${esc(r.peer)}</span>`
    : `<b class="num" style="margin:0">${esc(r.peer)}</b>`;
  const origin = r.origin ? `<span class="sms-origin" title="${esc(t('web.sms.origin'))}">${esc(r.origin)}</span>` : '';
  const copy = `<button class="icon-btn" title="${esc(t('web.copy'))}" onclick="copySms(${r.id})">${ICON_COPY}</button>`;
  return `<div class="call sms" data-sid="${r.id}">
    <div class="call-main sms-main" onclick="if (!event.target.closest('.call-actions')) toggleSms(${r.id})">
      <span class="call-ts" data-line="${esc(r.channel)}">${esc(fmtTs(r.ts))}</span>
      <span class="call-dir ${esc(r.direction)}" title="${esc(t('web.sms.kind.' + r.direction))}">${r.direction === 'in' ? '↙' : '↗'}</span>
      <span class="call-line">${esc(r.channel)}</span>
      <span class="call-who" title="${esc(r.peerName || '')} ${esc(r.peer)}">${who}</span>
      <span class="row-preview">${highlight((r.text || '').replace(/\s+/g, ' '), q)}</span>
      <span class="call-actions">${origin}${copy}</span>
    </div>
    <div class="call-text" hidden></div>
  </div>`;
}

function toggleSms(id, forceOpen) {
  const r = smsCache[id];
  const el = rowOf('sid', id)?.querySelector('.call-text');
  if (!r || !el) return;
  if (!el.hidden && !forceOpen) { el.hidden = true; return; }
  el.innerHTML = highlight(r.text || '', parseRoute().q.q);
  el.hidden = false;
}

function formatCall(r) {
  const dirCls = r.missed ? 'missed' : r.direction;
  const dirIcon = ICON_CALL[r.missed ? 'missed' : r.direction] || ICON_CALL.in;
  const dirTitle = t(r.missed ? (r.direction === 'in' ? 'web.calls.kind.missed' : 'web.calls.kind.no_answer')
                              : (r.direction === 'in' ? 'web.calls.kind.in' : 'web.calls.kind.out'));
  const who = r.peerName
    ? `<b>${esc(r.peerName)}</b><span class="num">${esc(r.peer)}</span>`
    : `<b class="num" style="margin:0">${esc(r.peer)}</b>`;
  const rec = r.hasRecording;
  // Transcript: done, the button downloads the PDF (it opens with a click on the row); queued, a clock;
  // failed, the button opens the row with the error and Retry.
  const trBtn = {
    done: `<a class="icon-btn" data-tr="done" title="${esc(t('web.calls.transcript_download'))}" href="/api/calls/${r.id}/transcript.pdf" download>${ICON_TEXT}</a>`,
    pending: `<span class="icon-btn" data-tr="pending" title="${esc(t('web.calls.tr_pending'))}">${ICON_PENDING}</span>`,
    error: `<button class="icon-btn err" data-tr="error" title="${esc(t('web.calls.tr_failed', r.trError || ''))}" onclick="toggleText(${r.id})">${ICON_ERROR}</button>`
  }[r.trState] || '';
  return `<div class="call" data-id="${r.id}">
    <div class="call-main" onclick="rowClick(event, ${r.id})">
      <span class="call-ts" data-line="${esc(r.channel)}">${esc(fmtTs(r.ts))}</span>
      <span class="call-dir ${dirCls}" title="${esc(dirTitle)}">${dirIcon}</span>
      <span class="call-line">${esc(r.channel)}</span>
      <span class="call-who" title="${esc(r.peerName || '')} ${esc(r.peer)}">${who}</span>
      <span class="row-preview">${callPreview(r)}</span>
      <span class="call-dur">${r.missed ? '' : fmtDur(r.billsec)}</span>
      <span class="call-actions">
        <button class="icon-btn ${rec ? '' : 'off'}" title="${esc(t('web.calls.play'))}" onclick="playCall(${r.id})">${ICON_PLAY}</button>
        <a class="icon-btn ${rec ? '' : 'off'}" title="${esc(t('web.calls.download'))}" href="/api/calls/${r.id}/audio?download=true" download>${ICON_DOWNLOAD}</a>
        ${trBtn}
        <button class="icon-btn ${r.trState === 'done' ? '' : 'off'}" title="${esc(t('web.calls.copy_transcript'))}" onclick="copyTranscript(${r.id})">${ICON_COPY}</button>
      </span>
    </div>
    <div class="call-player"></div>
    <div class="call-text" hidden></div>
  </div>`;
}

// The transcript in one line for the collapsed row: the turns apart by three spaces.
function callPreview(r) {
  if (r.trState !== 'done') return '';
  let segs = [];
  try { segs = JSON.parse(r.trSegments || '[]'); } catch { }
  const text = segs.length ? segs.map(s => s.t.replace(/\s+/g, ' ').trim()).join('   ') : (r.trText || '').replace(/\s+/g, ' ');
  return highlight(text, parseRoute().q.q);
}

// A click on the row (not on the buttons) opens or closes the transcript.
function rowClick(e, id) {
  if (e.target.closest('.call-actions')) return;
  if (callsCache[id]?.trState) toggleText(id);
}

function playCall(id, at) {
  const box = rowOf('id', id).querySelector('.call-player');
  let a = box.querySelector('audio');
  if (!a) {
    document.querySelectorAll('.call audio').forEach(x => { x.pause(); x.remove(); });
    box.innerHTML = `<audio controls autoplay preload="auto" src="/api/calls/${id}/audio"></audio>`;
    a = box.querySelector('audio');
    a.addEventListener('ended', e => e.target.remove());
  }
  if (at != null) {
    const seek = () => { a.currentTime = at; a.play(); };
    if (a.readyState >= 1) seek(); else a.addEventListener('loadedmetadata', seek, { once: true });
  }
}

// Transcript as a chat: all lines on the left, mine in blue, the other side's in grey.
// Old mono recordings: lines by time, without a side.
function renderDialog(r, q) {
  let segs = [];
  try { segs = JSON.parse(r.trSegments || '[]'); } catch { }
  if (!segs.length) return null;
  const them = r.peerName || t('web.calls.them');
  return segs.map(s => {
    const who = s.who === 'me' ? esc(t('web.calls.me')) : (s.who === 'them' ? esc(them) : '');
    return `<div class="dl ${s.who || 'mono'}">
      <div class="hdr">${who ? who + ' · ' : ''}<a onclick="playCall(${r.id}, ${s.s})" title="${esc(t('web.calls.play_from'))}">${fmtSec(s.s)}</a></div>
      <div class="bubble">${highlight(s.t, q)}</div>
    </div>`;
  }).join('');
}

function toggleText(id, forceOpen) {
  const r = callsCache[id];
  const el = rowOf('id', id)?.querySelector('.call-text');
  if (!r || !el) return;
  if (!el.hidden && !forceOpen) { el.hidden = true; return; }
  const q = parseRoute().q.q;
  el.classList.remove('dialog');
  if (r.trState === 'done') {
    const dialog = renderDialog(r, q);
    if (dialog) { el.innerHTML = dialog; el.classList.add('dialog'); }
    else el.innerHTML = r.trText ? highlight(r.trText, q) : `<span style="color:var(--muted)">${esc(t('web.calls.no_speech'))}</span>`;
    el.insertAdjacentHTML('beforeend', `<div class="meta tr-actions">
      <button class="btn" onclick="retranscribe(${id})">↻ ${esc(t('web.calls.retranscribe'))}</button>
      <a class="btn" href="/api/calls/${id}/transcript" download>${esc(t('web.calls.download_txt'))}</a>
      <button class="btn" onclick="copyTranscript(${id})">${esc(t('web.copy'))}</button></div>`);
  }
  else if (r.trState === 'pending')
    el.innerHTML = `<span style="color:var(--muted)">⏳ ${esc(t('web.calls.tr_pending'))}</span>`;
  else
    el.innerHTML = `<span style="color:var(--red)">✖ ${esc(t('web.calls.tr_failed', r.trError || ''))}</span>
      <div class="meta"><button class="btn" onclick="retranscribe(${id})">${esc(t('web.calls.retry'))}</button></div>`;
  el.hidden = false;
}

async function retranscribe(id) {
  const d = await (await fetch(`/api/calls/${id}/retranscribe`, { method: 'POST' })).json();
  showToast(t(d.ok ? 'web.calls.requeued' : 'web.calls.no_recording'), d.ok ? 'ok' : 'err');
  loadList(parseRoute().view, true);
}

// ---- SMS: export and send ----

function exportSms() {
  location.href = '/api/sms/export?' + listParams(parseRoute().view, true);
}

function openSendModal() {
  document.getElementById('send-number').value = '';
  document.getElementById('send-text').value = '';
  const line = parseRoute().q.line;
  if (line) document.getElementById('send-channel').value = line;
  document.getElementById('send-modal').classList.add('show');
}

function closeSendModal() {
  document.getElementById('send-modal').classList.remove('show');
}

async function submitSend() {
  const channel = val('send-channel');
  const number = val('send-number').trim();
  const text = val('send-text').trim();
  if (!channel || !number || !text) { showToast(t('web.sms.fill_all'), 'err'); return; }
  const data = await (await fetch('/api/sms/send', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ channel, number, text })
  })).json();
  if (data.ok) { showToast(t('web.sms.queued'), 'ok'); closeSendModal(); loadList(parseRoute().view); }
  else showToast(t('web.error', data.error || 'unknown'), 'err');
}

// ---- System log ----

let sysTimer = null;
function setSystemRefresh() {
  clearInterval(sysTimer);
  const ms = parseInt(val('sys-refresh'));
  if (ms > 0) sysTimer = setInterval(() => { if (parseRoute().view === 'system') loadSystemLog(); }, ms);
}

async function loadSystemLog() {
  const q = new URLSearchParams({ level: val('sys-level'), area: val('sys-area'), lines: val('sys-lines') });
  const data = await (await fetch('/api/system-log?' + q)).json();
  const sel = document.getElementById('sys-area');
  const cur = sel.value;
  sel.innerHTML = `<option value="">${esc(t('web.sys.all'))}</option>`
    + [...new Set([...data.areas, cur].filter(Boolean))].sort()
      .map(a => `<option${a === cur ? ' selected' : ''}>${esc(a)}</option>`).join('');
  document.getElementById('system-container').innerHTML = data.entries.map(e =>
    `<div class="sys-line sys-${esc(e.level)}"><span class="log-ts">${esc(e.ts)}</span>`
    + `<span class="sys-level">${esc(t('web.sys.tag.' + e.level))}</span>`
    + `<span class="log-gw">${esc(e.area)}</span><span class="log-msg">${esc(e.message)}</span></div>`).join('')
    || `<div class="muted">${esc(t('web.sys.empty'))}</div>`;
}

// ---- State: dots in the header and the Status tab ----
// The server decides the state of every component (ok / err / unknown) by the same rule as /status
// in Telegram: connection-based components go by their connection, so an old transient error does
// not paint a working one red.

// "5 min ago"; the exact time goes into the title.
function ago(iso) {
  if (!iso) return t('web.never');
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return t('web.ago.now');
  if (s < 3600) return t('web.ago.min', Math.floor(s / 60));
  if (s < 86400) return t('web.ago.hour', Math.floor(s / 3600));
  return t('web.ago.day', Math.floor(s / 86400));
}

function exact(iso) {
  return iso ? new Date(iso).toLocaleString() : '';
}

function stateText(c) {
  if (c.state === 'ok') return t('web.status.state.ok');
  if (c.state === 'err') return t('web.status.state.err');
  return t(c.kind === 'transcribe' ? 'web.status.state.idle' : 'web.status.state.unknown');
}

function componentCard(c) {
  // An error is shown in red only while the component is down; an older one, after a success, in grey.
  const err = c.lastError
    ? `<div class="row ${c.state === 'err' ? 'err' : 'old'}" title="${esc(exact(c.lastErrorAt))}">${esc(t('web.status.error_at', ago(c.lastErrorAt)))} ${esc(c.lastError)}</div>`
    : '';
  return `<div class="status-card">
    <h3><span class="hdot ${c.state}"></span>${esc(c.title)}<span class="state ${c.state}">${esc(stateText(c))}</span></h3>
    ${c.detail ? `<div class="row">${esc(t('web.status.address'))} <b>${esc(c.detail)}</b></div>` : ''}
    <div class="row" title="${esc(exact(c.lastActivityAt))}">${esc(t('web.status.last_activity'))} <b>${esc(ago(c.lastActivityAt))}</b></div>
    ${err}
  </div>`;
}

function queueCard(title, rows) {
  return `<div class="status-card"><h3>${esc(title)}</h3>${rows.map(([k, v]) =>
    `<div class="row">${esc(k)} <b>${esc(v)}</b></div>`).join('')}</div>`;
}

// Line monitoring: network (registration and operator), type, signal; the details in tooltips.
function radioCells(r) {
  if (!r) return '<td></td><td></td><td></td><td></td>';
  if (!r.data) return `<td class="muted">${esc(t('web.status.radio.no_data'))}</td><td></td><td></td><td></td>`;
  const modem = [r.model, r.usbPort ? t('web.status.radio.usb', r.usbPort) : ''].filter(Boolean).join(' · ');
  const modemCell = `<td title="${esc(r.dataPort || '')}">${esc(modem)}</td>`;
  const where = [r.lac || r.cell ? t('web.status.radio.cell', r.lac || '?', r.cell || '?') : '',
    t('web.status.radio.updated', ago(r.updatedAt))].filter(Boolean).join(' · ');
  if (!r.up) {
    const mins = r.downSince ? Math.floor((Date.now() - new Date(r.downSince)) / 60000) : 0;
    return `<td title="${esc([r.state, r.registration, where].filter(Boolean).join(' · '))}"><span class="hdot err"></span>`
      + `${esc(mins ? t('web.status.radio.down_for', mins) : t('web.status.radio.down'))}</td><td></td><td></td>` + modemCell;
  }
  const bars = [1, 2, 3, 4].map(i => `<i class="${i <= r.bars ? 'on' : ''}"></i>`).join('');
  return `<td title="${esc([r.registration, where].filter(Boolean).join(' · '))}"><span class="hdot ok"></span>${esc(r.provider || '')}</td>`
    + `<td>${esc(r.net || '')}</td>`
    + `<td title="${esc(signalTitle(r))}"><span class="sig">${bars}</span>${r.dbm != null ? ` <span class="muted">${esc(r.dbm)}</span>` : ''}</td>`
    + modemCell;
}

// LTE quality when the driver reports it (RSRP, RSRQ, SINR), else RSSI only.
function signalTitle(r) {
  if (r.rsrp != null) return `RSRP ${r.rsrp} dBm · RSRQ ${r.rsrq} dB · SINR ${r.sinr} dB`
    + (r.rssi != null ? ` · RSSI ${r.rssi} dBm` : '');
  return r.rssi != null ? `RSSI ${r.rssi} dBm` : '';
}

let statusTimer = null;
function setStatusRefresh() {
  clearInterval(statusTimer);
  const ms = parseInt(val('status-refresh'));
  if (ms > 0) statusTimer = setInterval(() => { if (parseRoute().view === 'status') loadStatus(); }, ms);
}

// headerOnly: the dots in the header (kept fresh on every tab), not the Status tab itself.
async function loadStatus(headerOnly) {
  let data;
  try {
    const res = await fetch('/api/status');
    if (!res.ok) return;
    data = await res.json();
  } catch { return; }

  document.getElementById('header-status').innerHTML = data.components.map(c =>
    `<div class="hstat" title="${esc(stateText(c) + (c.state === 'err' && c.lastError ? ': ' + c.lastError : ''))}"><span class="hdot ${c.state}"></span>${esc(c.title)}</div>`
  ).join('');

  if (headerOnly || parseRoute().view !== 'status') return;
  document.getElementById('status-updated').textContent =
    t('web.status.updated', new Date().toLocaleTimeString(), ago(data.startedAt), data.version);
  document.getElementById('status-cards').innerHTML = data.components.map(componentCard).join('');

  const q = data.queues;
  document.getElementById('status-queues').innerHTML =
    queueCard(t('web.status.q.sending'), [[t('web.status.q.sms'), q.sms], [t('web.status.q.telegram'), q.telegram]])
    + (q.calls ? queueCard(t('web.status.q.calls'), [
        [t('web.status.q.total'), q.calls.total],
        [t('web.status.q.tr_done'), q.calls.transcribeDone],
        [t('web.status.q.tr_pending'), q.calls.transcribePending],
        [t('web.status.q.tr_error'), q.calls.transcribeError],
        [t('web.status.q.call_tg'), q.calls.tgPending]]) : '');

  const full = MODE.mode === 'full';
  const radio = data.lines.some(l => l.radio);
  const head = `<thead><tr><th>${esc(t('web.status.col.line'))}</th><th>${esc(t('web.status.col.number'))}</th>`
    + (radio ? `<th>${esc(t('web.status.col.network'))}</th><th>${esc(t('web.status.col.net'))}</th><th>${esc(t('web.status.col.signal'))}</th><th>${esc(t('web.status.col.modem'))}</th>` : '')
    + `<th>${esc(t('web.status.col.sms_in'))}</th><th>${esc(t('web.status.col.sms_out'))}</th>`
    + (full ? `<th>${esc(t('web.status.col.calls_in'))}</th><th>${esc(t('web.status.col.calls_missed'))}</th><th>${esc(t('web.status.col.calls_out'))}</th><th>${esc(t('web.status.col.last_call'))}</th>` : '')
    + `</tr></thead>`;
  const rows = data.lines.map(l => `<tr class="${l.configured ? '' : 'gone'}" title="${l.configured ? esc(l.gateway || '') : esc(t('web.status.not_in_config'))}">
      <td>${esc(l.name)}${l.configured ? '' : ' *'}</td><td>${esc(l.number || '')}</td>
      ${radio ? radioCells(l.radio) : ''}
      <td>${l.smsIn}</td><td>${l.smsOut}</td>
      ${full ? `<td>${l.callsIn}</td><td>${l.callsMissed}</td><td>${l.callsOut}</td><td>${l.lastCall ? esc(fmtTs(l.lastCall)) : ''}</td>` : ''}
    </tr>`).join('');
  const note = data.lines.some(l => !l.configured)
    ? `<caption>* ${esc(t('web.status.not_in_config'))}</caption>` : '';
  document.getElementById('status-lines').innerHTML = note + head + `<tbody>${rows ||
    `<tr><td colspan="12" style="color:var(--muted)">${esc(t('web.status.no_data'))}</td></tr>`}</tbody>`;
  loadStatusPbx();
}

// ---- PBX on the Status tab: Asterisk gateways, their modems, the phones, SIP security ----
// Read only; modems and phones are set up in the settings (a gateway's Modems, a line's Phone).

// A link to a section of the settings tree.
function settingsLink(node, text) {
  return `<a href="${esc(buildUrl('/config', { s: node }))}" onclick="event.preventDefault(); navigate(this.getAttribute('href'))">${esc(text)}</a>`;
}

function modemStateCell(m, connected) {
  if (!connected) return `<span class="muted">${esc(t('web.pbx.no_ami'))}</span>`;
  if (!m.found) return `<span class="hdot err"></span>${esc(t('web.pbx.modem.not_seen'))}`;
  const ok = /free|ring|dial|active|busy|incoming|outgoing|waiting/i.test(m.state || '');
  return `<span class="hdot ${ok ? 'ok' : 'err'}"></span>${esc(m.state || '')}`
    + (m.provider ? ` <span class="muted">${esc(m.provider)}</span>` : '');
}

function phoneRegCell(p) {
  if (!p.contacts) return `<span class="muted">${esc(t('web.pbx.no_ami'))}</span>`;
  const c = p.contacts[0];
  if (!c) return `<span class="hdot err"></span>${esc(t('web.pbx.not_registered'))}`;
  return `<span class="hdot ${/avail|reachable/i.test(c.status) ? 'ok' : 'err'}"></span>${esc(c.status)}`
    + (c.rttMs != null ? ` <span class="muted">${esc(c.rttMs.toFixed(0))} ms</span>` : '');
}

async function loadStatusPbx() {
  const host = document.getElementById('status-pbx');
  let d;
  try { d = await (await fetch('/api/status/pbx')).json(); } catch { return; }
  if (!d.gateways?.length) { host.innerHTML = ''; return; }
  const th = keys => `<thead><tr>${keys.map(k => `<th>${esc(k.startsWith('web.') ? t(k) : k)}</th>`).join('')}</tr></thead>`;
  let html = '';

  for (const g of d.gateways) {
    const title = t(g.managed ? 'web.pbx.status.managed' : 'web.pbx.status.freepbx', g.id);
    const addr = g.address ? ` <span class="muted">· AMI ${esc(g.address)}</span>` : '';
    const conn = `<span class="hdot ${g.connected ? 'ok' : 'err'}"></span>${esc(t(g.connected ? 'web.status.state.ok' : 'web.status.state.err'))}`;
    const cert = g.cert ? ` <span class="muted">· ${esc(t('web.pbx.cert'))} ${esc(t('web.pbx.cert.until'))} ${esc(g.cert.notAfter)}, `
      + `${esc(t('web.pbx.cert.days', g.cert.daysLeft))}</span>` : '';
    const rows = g.modems.map(m => `<tr><td>${settingsLink('gw:' + g.id + '/modems', m.name)}</td><td>${esc(m.driver)}</td>
        <td>${esc(m.model || '')}</td><td>${esc(m.usb || '')}</td><td>${modemStateCell(m, g.connected)}</td>
        <td>${m.line ? settingsLink('ln:' + m.line, m.line) : `<span class="muted">${esc(t('web.pbx.modem.free'))}</span>`}</td></tr>`)
      .concat((g.unknown || []).map(x => `<tr class="gone"><td>${esc(x.device)} *</td><td></td><td>${esc(x.model || '')}</td><td></td>
        <td>${esc(x.state || '')} <span class="muted">${esc(x.provider || '')}</span></td><td class="muted">${esc(t('web.pbx.modem.unknown'))}</td></tr>`))
      .join('');
    const usb = (g.usb || []).map(u => `<div>USB ${esc(u.usb)} · ${esc(u.model || u.product || '')} · ${esc(u.vid + ':' + u.pid)} — `
      + `<span class="muted">${esc(t(u.access ? 'web.pbx.usb.free' : 'web.pbx.modem.no_access'))}</span></div>`).join('');
    const errs = (g.errors || []).map(e => `<div class="pbx-err">${esc(e)}</div>`).join('');
    html += `<div class="section"><h3>${esc(title)}${addr} · ${conn}${cert}</h3>
      <table class="stat-table">${th(['web.pbx.modem.name', 'web.pbx.modem.driver', 'web.pbx.modem.model', 'USB', 'web.pbx.modem.state', 'web.status.col.line'])}
      <tbody>${rows || `<tr><td colspan="6" class="muted">${esc(t('web.pbx.modem.none'))}</td></tr>`}</tbody></table>
      ${usb ? `<div class="pbx-usb"><b>${esc(t('web.pbx.usb.title'))}</b>${usb}${(g.usb || []).some(u => !u.access)
        ? `<div class="tree-hint pbx-hint">${esc(t('web.pbx.modem.no_access_hint'))}</div>` : ''}</div>` : ''}
      ${errs ? `<div class="pbx-usb"><b>${esc(t('web.pbx.problems'))}</b>${errs}</div>` : ''}</div>`;
  }

  if (d.phones?.length) {
    const rows = d.phones.map(p => `<tr><td>${settingsLink('ln:' + p.line + '/phone', p.line)}</td><td>${esc(p.extension)}</td>
      <td title="${esc(p.contacts?.[0]?.uri || d.contactsError || '')}">${phoneRegCell(p)}</td></tr>`).join('');
    html += `<div class="section"><h3>${esc(t('web.pbx.phones'))}</h3>
      <table class="stat-table">${th(['web.status.col.line', 'web.pbx.col.ext', 'web.pbx.col.reg'])}<tbody>${rows}</tbody></table></div>`;
  }

  if (d.security) {
    const rows = d.security.map(e => `<tr><td>${esc(fmtTs(e.at.replace('T', ' ').slice(0, 19)))}</td><td>${esc(e.address)}</td>`
      + `<td>${esc(e.account)}</td><td>${esc(e.kind)}</td></tr>`).join('');
    html += `<div class="section"><h3>${esc(t('web.pbx.security'))}</h3>
      <table class="stat-table">${th(['web.pbx.sec.time', 'web.pbx.sec.address', 'web.pbx.sec.account', 'web.pbx.sec.event'])}
      <tbody>${rows || `<tr><td colspan="4" class="muted">${esc(t('web.pbx.sec.none'))}</td></tr>`}</tbody></table></div>`;
    html += acmeSection(d.gateways.find(g => g.managed)?.cert, d.acme);
    html += fail2banSection(d.fail2ban);
  }
  host.innerHTML = html;
}

// The SIP certificate and its Let's Encrypt renewal, as gl-acme on the Asterisk host reports it.
function acmeSection(cert, a) {
  const head = `<div class="section"><h3>${esc(t('web.acme.title'))}</h3>`;
  const info = cert ? t('web.acme.cert', cert.subject.replace(/^CN=/, ''), cert.issuer || '', cert.notAfter, t('web.pbx.cert.days', cert.daysLeft))
    : t('web.acme.no_cert');
  let html = head + `<p>${esc(info)}</p>`;
  if (!a) return html + `<p class="muted">${esc(t('web.acme.no_report'))}</p></div>`;
  if (!a.enabled) return html + `<p class="muted">${esc(t('web.acme.off'))} · ${esc(t('web.f2b.reported', ago(a.at)))}</p></div>`;
  const parts = [t('web.acme.auto', a.domain)];
  if (a.next_renew) parts.push(t('web.acme.next', fmtIso(a.next_renew)));
  if (a.last_ok) parts.push(t('web.acme.last_ok', fmtIso(a.last_ok)));
  parts.push(t('web.f2b.reported', ago(a.at)));
  html += `<p class="muted">${esc(parts.join(' · '))}</p>`;
  if (a.error) html += `<p class="pbx-err">${esc(t('web.acme.error', fmtIso(a.error_at)))}: ${esc(a.error)}</p>`;
  return html + `<button class="btn" onclick="renewCert()">${esc(t('web.acme.renew'))}</button></div>`;
}

async function renewCert() {
  if (!confirm(t('web.acme.renew_confirm'))) return;
  const d = await (await fetch('/api/pbx/acme/renew', { method: 'POST' })).json();
  showToast(d.ok ? t('web.acme.renew_sent') : t('web.error', d.error), d.ok ? 'ok' : 'err');
  if (d.ok) setTimeout(loadStatus, 30000);
}

// fail2ban on the Asterisk host, as gl-fail2ban there reports it (every 30 s).
function fmtSpan(sec) {
  if (sec < 0) return t('web.f2b.forever');
  return sec < 3600 ? t('web.f2b.min', Math.round(sec / 60)) : sec < 86400 ? t('web.f2b.h', Math.round(sec / 3600)) : t('web.f2b.d', Math.round(sec / 86400));
}

function fmtIso(iso) {
  if (!iso) return '';
  const d = new Date(iso), p = n => String(n).padStart(2, '0');
  return `${p(d.getDate())}.${p(d.getMonth() + 1)} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

function fail2banSection(f) {
  const head = `<div class="section"><h3>${esc(t('web.f2b.title'))}</h3>`;
  if (!f) return head + `<p class="muted">${esc(t('web.f2b.no_report'))}</p></div>`;
  const rule = f.max_retry ? t('web.f2b.rule', f.max_retry, fmtSpan(f.find_seconds), fmtSpan(f.ban_seconds))
    + (f.ban_growth ? ' ' + t('web.f2b.growth', fmtSpan(f.ban_seconds * 24), fmtSpan(f.ban_seconds * 168)) : '') : '';
  const meta = `<p class="muted">${esc(rule)} · ${esc(t('web.f2b.totals', f.total_banned ?? 0, f.total_failed ?? 0))}`
    + ` · ${esc(t('web.f2b.reported', ago(f.at)))}</p>`;
  const errors = (f.errors || []).map(e => `<p class="pbx-err">${esc(e)}</p>`).join('');
  const rows = (f.banned || []).map(b => `<tr><td>${esc(b.ip)}</td><td>${esc(fmtIso(b.since))}</td>`
    + `<td>${b.until ? esc(fmtIso(b.until)) : esc(t('web.f2b.forever'))}</td><td>${b.count ?? ''}</td>`
    + `<td><button class="btn" onclick="unbanIp('${esc(b.ip)}')">${esc(t('web.f2b.unban'))}</button></td></tr>`).join('');
  return head + meta + errors + `<table class="stat-table">${['web.f2b.col.ip', 'web.f2b.col.since', 'web.f2b.col.until', 'web.f2b.col.count', '']
      .map(k => `<th>${k ? esc(t(k)) : ''}</th>`).join('').replace(/^/, '<thead><tr>') + '</tr></thead>'}`
    + `<tbody>${rows || `<tr><td colspan="5" class="muted">${esc(t('web.f2b.none'))}</td></tr>`}</tbody></table></div>`;
}

async function unbanIp(ip) {
  if (!confirm(t('web.f2b.unban_confirm', ip))) return;
  const d = await (await fetch('/api/pbx/fail2ban/unban', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ip })
  })).json();
  showToast(d.ok ? t('web.f2b.unban_sent', ip) : t('web.error', d.error), d.ok ? 'ok' : 'err');
  if (d.ok) setTimeout(loadStatus, 5000);
}

// ---- Modems of an Asterisk gateway, in the settings ----
// Added and removed at once, not with Save: on the managed Asterisk the driver config is written and the
// driver reloads gracefully. The USB list comes from its host (gl-usb-inventory: sysfs only, nothing is
// sent to the modems).

let modemUsb = null;   // /api/pbx/usb while the add form is open

// The line (by the form's current values) that uses the modem, or null.
function lineOfModem(gateway, modem) {
  const lines = findNode('lines')?.children || [];
  const v = (item, key) => item.fields.find(f => f.path.endsWith('].' + key))?.value;
  return lines.find(l => v(l, 'gateway') === gateway && v(l, 'modem') === modem)?.key || null;
}

function modemsPaneHtml(n) {
  const g = n.modems;
  const port = p => p ? esc(p.replace('/dev/serial/by-path/', '')) : '';
  const rows = g.list.map(m => {
    const line = lineOfModem(g.gateway, m.name);
    return `<tr><td>${esc(m.name)}</td><td>${esc(m.driver)}</td>`
      + (g.managed ? `<td class="muted pbx-port">${port(m.data)}</td>`
        + `<td class="muted pbx-port">${m.alsadev ? 'UAC ' + esc(m.alsadev) : port(m.audio)}${m.noqpcmv ? ' · noqpcmv' : ''}</td>` : '')
      + `<td>${line ? esc(line) : `<span class="muted">${esc(t('web.pbx.modem.free'))}</span>`}</td>`
      + `<td>${line ? '' : `<button type="button" class="btn" data-modem-del="${esc(m.name)}" title="${esc(t('web.pbx.modem.remove'))}">✕</button>`}</td></tr>`;
  }).join('');
  const cols = ['web.pbx.modem.name', 'web.pbx.modem.driver'].concat(g.managed ? ['web.pbx.modem.data', 'web.pbx.modem.audio'] : [])
    .concat(['web.status.col.line', '']);
  return `<table class="stat-table"><thead><tr>${cols.map(k => `<th>${k ? esc(t(k)) : ''}</th>`).join('')}</tr></thead>
    <tbody>${rows || `<tr><td colspan="${cols.length}" class="muted">${esc(t(g.managed ? 'web.pbx.modem.none' : 'web.pbx.modem.none_freepbx'))}</td></tr>`}</tbody></table>
    <div class="pbx-add modem-add"><button type="button" class="btn" data-modem-open>${esc(t('web.pbx.modem.add'))}</button></div>`;
}

function bindModemPane(pane, n) {
  pane.querySelectorAll('[data-modem-del]').forEach(b => b.onclick = () =>
    modemRequest({ action: 'remove', gateway: n.modems.gateway, name: b.dataset.modemDel }, t('web.pbx.modem.remove_confirm', b.dataset.modemDel)));
  pane.querySelectorAll('[data-modem-open]').forEach(b => b.onclick = () => openModemAdd(n));
}

function modemFreeName(n, driver) {
  const prefix = driver === 'dongle' ? 'hw' : 'gsm';
  const names = new Set(Object.values(gatewayInfo).flatMap(g => g.modems).concat(n.modems.list.map(m => m.name)));
  let i = 1;
  while (names.has(prefix + i)) i++;
  return prefix + i;
}

// One add form at a time: the tree is drawn anew (edits are kept), then the form goes into this pane.
async function openModemAdd(n) {
  captureFields();
  renderSettings(n.id);
  const host = document.querySelector(`.tree-pane[data-pane="${CSS.escape(n.id)}"] .modem-add`);
  const cancel = `<button type="button" class="btn" onclick="renderSettings(parseRoute().q.s)">${esc(t('web.cancel'))}</button>`;
  const opts = (values, current) => values.map(v => `<option value="${esc(v)}"${v === current ? ' selected' : ''}>${esc(v)}</option>`).join('');
  if (!n.modems.managed) {
    // FreePBX owns the driver config: the modem is only named here, as in its quectel.conf / dongle.conf.
    host.innerHTML = `<div class="settings-grid pbx-add-form">
      <label>${esc(t('web.pbx.modem.name'))}<input id="pbx-m-name" maxlength="32" spellcheck="false" value="${esc(modemFreeName(n, 'quectel'))}"></label>
      <label>${esc(t('web.pbx.modem.driver'))}<select id="pbx-m-driver">${opts(['quectel', 'dongle'], 'quectel')}</select></label>
    </div>
    <div class="pbx-add-buttons"><button type="button" class="btn primary" id="pbx-m-submit">${esc(t('web.pbx.modem.submit'))}</button>${cancel}
      <span class="config-hint">${esc(t('web.pbx.modem.hint_freepbx'))}</span></div>`;
    document.getElementById('pbx-m-submit').onclick = () => {
      const body = { action: 'add', gateway: n.modems.gateway, name: document.getElementById('pbx-m-name').value.trim(),
        driver: document.getElementById('pbx-m-driver').value };
      modemRequest(body, t('web.pbx.modem.add_confirm_freepbx', body.name, body.driver));
    };
    return;
  }

  try { modemUsb = await (await fetch('/api/pbx/usb')).json(); } catch { modemUsb = null; }
  if (!modemUsb?.ok || !modemUsb.usb) {
    host.innerHTML = `<span class="muted">${esc(modemUsb?.error || t('web.pbx.modem.no_usb'))}</span> ${cancel}`;
    return;
  }
  // USB devices not used by a modem yet; those Asterisk cannot open are listed but disabled.
  const free = modemUsb.usb.filter(u => !u.configured);
  const label = u => [`USB ${u.usb}`, u.model || `${u.manufacturer} ${u.product}`.trim(), `${u.vid}:${u.pid}`].join(' · ')
    + (u.access ? '' : ' · ' + t('web.pbx.modem.no_access'));
  if (!free.some(u => u.access)) {
    host.innerHTML = `<div class="muted">${esc(t('web.pbx.modem.no_free_usb'))}</div>
      ${free.length ? `<div class="muted pbx-port">${free.map(u => esc(label(u))).join('<br>')}</div>
      <div class="tree-hint pbx-hint">${esc(t('web.pbx.modem.no_access_hint'))}</div>` : ''}${cancel}`;
    return;
  }
  host.innerHTML = `<div class="settings-grid pbx-add-form">
    <label>${esc(t('web.pbx.modem.usb'))}<select id="pbx-m-usb">${free.map(u =>
      `<option value="${esc(u.usb)}"${u.access ? '' : ' disabled'}>${esc(label(u))}</option>`).join('')}</select></label>
    <label>${esc(t('web.pbx.modem.name'))}<input id="pbx-m-name" maxlength="32" spellcheck="false"></label>
    <label>${esc(t('web.pbx.modem.driver'))}<select id="pbx-m-driver">${opts(['quectel', 'dongle'], 'quectel')}</select></label>
    <label>${esc(t('web.pbx.modem.data'))}<select id="pbx-m-data"></select></label>
    <label>${esc(t('web.pbx.modem.audio'))}<select id="pbx-m-audio"></select></label>
    <label class="chk"><input type="checkbox" id="pbx-m-noqpcmv">noqpcmv (EC200A)</label>
  </div>
  <div class="pbx-add-buttons"><button type="button" class="btn primary" id="pbx-m-submit">${esc(t('web.pbx.modem.submit'))}</button>${cancel}
    <span class="config-hint">${esc(t('web.pbx.modem.hint'))}</span></div>`;
  document.getElementById('pbx-m-usb').value = free.find(u => u.access).usb;
  document.getElementById('pbx-m-usb').onchange = () => modemUsbChanged(n);
  document.getElementById('pbx-m-driver').onchange = modemDriverChanged;
  document.getElementById('pbx-m-submit').onclick = () => {
    const audio = document.getElementById('pbx-m-audio').value;
    const body = {
      action: 'add', gateway: n.modems.gateway, name: document.getElementById('pbx-m-name').value.trim(),
      driver: document.getElementById('pbx-m-driver').value,
      data: document.getElementById('pbx-m-data').value,
      audio: audio.startsWith('uac:') ? '' : audio,
      alsadev: audio.startsWith('uac:') ? audio.slice(4) : '',
      noqpcmv: document.getElementById('pbx-m-noqpcmv').checked
    };
    modemRequest(body, t('web.pbx.modem.add_confirm', body.name, body.driver));
  };
  modemUsbChanged(n);
}

// A USB device was chosen: its ports in the combos, the suggestion for a known model, a free name.
function modemUsbChanged(n) {
  const u = modemUsb.usb.find(x => x.usb === document.getElementById('pbx-m-usb').value);
  if (!u) return;
  const s = u.suggest || {};
  const driver = s.driver || 'quectel';
  document.getElementById('pbx-m-driver').value = driver;
  const portOpt = p => `<option value="${esc(p.path)}"${p.access && !p.used ? '' : ' disabled'}>`
    + `${esc(t('web.pbx.modem.iface', p.interface))} · ${esc(p.path.replace('/dev/serial/by-path/', ''))}</option>`;
  document.getElementById('pbx-m-data').innerHTML = u.ports.map(portOpt).join('');
  document.getElementById('pbx-m-audio').innerHTML =
    u.sound.map(x => `<option value="uac:${esc(x.alsadev)}"${x.access && !x.used ? '' : ' disabled'}>UAC ${esc(x.alsadev)}`
      + `${x.access ? '' : ' · ' + esc(t('web.pbx.modem.no_access'))}</option>`).join('')
    + u.ports.map(portOpt).join('');
  if (s.data) document.getElementById('pbx-m-data').value = s.data;
  if (s.alsadev) document.getElementById('pbx-m-audio').value = 'uac:' + s.alsadev;
  else if (s.audio) document.getElementById('pbx-m-audio').value = s.audio;
  document.getElementById('pbx-m-noqpcmv').checked = !!s.noqpcmv;
  document.getElementById('pbx-m-name').value = modemFreeName(n, driver);
  modemDriverChanged();
}

function modemDriverChanged() {
  const dongle = document.getElementById('pbx-m-driver').value === 'dongle';
  const box = document.getElementById('pbx-m-noqpcmv');
  box.disabled = dongle;
  if (dongle) box.checked = false;
}

// Unsaved edits in the form: they would be lost when the form reloads.
function formHasEdits() {
  return Object.keys(collectFields()).length > 0 || removedItems.length > 0 || addedSections.length > 0 || removedSections.length > 0;
}

async function modemRequest(body, question) {
  if (formHasEdits() && !confirm(t('web.pbx.discard_edits'))) return;
  if (!confirm(question)) return;
  setBusy(t('web.pbx.applying'));
  const r = await configRequest('/api/pbx/modem', body);
  setBusy(null);
  if (!r) return;
  if (r.applyErrors?.length) alert(t('web.pbx.apply_failed') + '\n\n' + r.applyErrors.join('\n'));
  else showToast(t('web.pbx.modem.done'), 'ok');
  await loadConfigForm();
}

// ---- A line's phone, in the settings ----

// The secret is not a form field: one more grid cell shows whether it is set, with "New secret" beside it.
function phoneSecretCell(n) {
  const set = !n.phone.isNew && n.phone.hasSecret;
  const button = set ? `<button type="button" class="btn" data-new-secret="${esc(n.phone.line)}">${esc(t('web.pbx.new_secret'))}</button>` : '';
  return `<label>${esc(t('web.pbx.secret'))}<span class="input-row"><input type="text" readonly tabindex="-1"
    value="${esc(set ? '••••••••••••' : t('web.pbx.secret_on_save_short'))}">${button}</span></label>`;
}

async function newSecret(line) {
  if (formHasEdits() && !confirm(t('web.pbx.discard_edits'))) return;
  if (!confirm(t('web.pbx.new_secret_confirm', line))) return;
  const r = await configRequest('/api/pbx/secret', { line });
  if (!r) return;
  // Shown once: afterwards it is only in the config.
  prompt(t('web.pbx.secret_shown', line), r.secret);
  if (r.applyErrors?.length) alert(t('web.pbx.apply_failed') + '\n\n' + r.applyErrors.join('\n'));
  await loadConfigForm();
}

// ---- About ----

let aboutLoaded = false;
async function loadAbout() {
  if (aboutLoaded) return;
  try {
    const d = await (await fetch('/api/about')).json();
    document.getElementById('about-readme').innerHTML = d.readme || '';
    aboutLoaded = true;
  } catch { }
}

// ---- Config: a form with a tree or YAML ----

// YAML editor: CodeMirror from /js/yaml-editor.js; if it fails to load, a plain text area.
let configEditor = null;
function getConfigEditor() {
  return configEditor ??= (async () => {
    const area = document.getElementById('config-editor');
    try {
      const { createYamlEditor } = await import('/js/yaml-editor.js?v=%VERSION%');
      const host = document.getElementById('config-editor-host');
      const editor = createYamlEditor(host, area.value);
      area.style.display = 'none';
      host.style.display = '';
      return editor;
    } catch (e) {
      console.warn('YAML editor is not available, using the plain text area', e);
      return { getValue: () => area.value, setValue: v => { area.value = v; }, focus: () => area.focus() };
    }
  })();
}

let configMode = null;

function setConfigMode(mode) {
  configMode = mode;
  document.getElementById('config-form').style.display = mode === 'form' ? 'flex' : 'none';
  document.getElementById('config-yaml').style.display = mode === 'yaml' ? 'flex' : 'none';
  document.getElementById('config-mode-form').classList.toggle('primary', mode === 'form');
  document.getElementById('config-mode-yaml').classList.toggle('primary', mode === 'yaml');
  if (mode === 'yaml') loadConfigYaml();
  else loadConfigForm();
}

async function loadConfigYaml() {
  const data = await (await fetch('/api/config')).json();
  if (!data.ok) { showToast(t('web.error', data.error || 'unknown'), 'err'); return; }
  (await getConfigEditor()).setValue(data.content);
}

// Lists in the tree: gateways and lines. A list item is the node "<prefix><key>", its fields use a path with a selector.
const LISTS = {
  gateways: { prefix: 'gw:', sel: 'id', add: 'web.config.add_gateway', remove: 'web.config.remove_gateway_confirm', hint: 'web.config.gateways_hint' },
  lines: { prefix: 'ln:', sel: 'name', add: 'web.config.add_line', remove: 'web.config.remove_line_confirm', hint: 'web.config.lines_hint' },
};
const NAME_RE = /^[A-Za-z0-9][A-Za-z0-9_.-]*$/;

let formTree = [];
let templates = {};
let removedItems = [];
// Saved gateways: type, PBX mode, modem names (the lines choose a modem from these).
let gatewayInfo = {};
let phoneSecrets = [];

// The tree follows the hardware: a gateway -> its PBX settings and modems; a line -> its phone,
// clients (telegram, ntfy) and calls.
function settingsTree(data) {
  const byId = Object.fromEntries((data.groups || []).map(g => [g.id, g]));
  const node = id => ({ id, title: byId[id].title, fields: byId[id].fields || [] });
  const list = id => ({
    id, title: byId[id].title, fields: [], list: id,
    children: (data[id] || []).map(it => itemNode(id, it.key, it.fields, false, it.clients, it))
  });
  const clientFields = { telegram: byId.telegram.fields, ntfy: byId.ntfy.fields };
  // System: mode and languages, with the settings of ghostline itself under it.
  const calls = Object.assign(node('calls'), { children: [node('cdr_db'), node('transcribe')] });
  const system = Object.assign(node('system'),
    { children: [node('web'), node('logger'), node('monitor'), calls, node('goip')] });
  return [system, clientsNode('clients', '', data.clients || [], clientFields), list('gateways'), list('lines')];
}

// A gateway or a line. A gateway's managed.* fields and its modems, a line's phone, clients and calls
// are subsections, shown when they apply. clients: the line's client sections, null: no "clients"
// section. saved: the gateway as saved (its type and modems), none for a new one.
function itemNode(listId, key, fields, isNew, clients, saved) {
  const id = LISTS[listId].prefix + key;
  const item = { id, title: key, key, fields, isNew };
  if (listId === 'gateways') {
    const managed = fields.filter(f => f.path.includes('].managed.'));
    const fail2ban = managed.filter(f => f.path.includes('].managed.fail2ban.'));
    const acme = managed.filter(f => f.path.includes('].managed.sip.tls.acme.'));
    item.fields = fields.filter(f => !managed.includes(f));
    item.children = [
      { id: id + '/managed', title: t('web.config.group.managed'), fields: managed.filter(f => !fail2ban.includes(f) && !acme.includes(f)),
        hintKey: 'web.config.hint.managed', visible: () => gatewayState(key)?.managed,
        children: [
          { id: id + '/acme', title: t('web.config.group.acme'), fields: acme, hintKey: 'web.config.hint.acme' },
          { id: id + '/fail2ban', title: t('web.config.group.fail2ban'), fields: fail2ban, hintKey: 'web.config.hint.fail2ban' },
        ] },
      { id: id + '/modems', title: t('web.config.group.modems'), fields: [],
        hintKey: saved?.managed ? 'web.config.hint.modems_managed' : 'web.config.hint.modems',
        modems: { gateway: key, managed: !!saved?.managed, list: saved?.modems || [] },
        visible: () => saved?.type === 'asterisk' && gatewayState(key)?.type === 'asterisk' }
    ];
    return item;
  }
  const sub = (part) => fields.filter(f => f.path.includes(']' + '.' + part + '.'));
  const telegram = sub('clients.telegram'), ntfy = sub('clients.ntfy'), calls = sub('calls'), phone = sub('phone');
  item.fields = fields.filter(f => ![telegram, ntfy, calls, phone].some(x => x.includes(f)));
  const base = `lines[name=${key}].`;
  item.children = [
    { id: id + '/phone', title: t('web.config.group.phone'), fields: phone, hintKey: 'web.config.hint.phone',
      phone: { line: key, isNew, hasSecret: phoneSecrets.includes(key) },
      visible: () => gatewayState(lineGateway(item))?.managed },
    clientsNode(id + '/clients', base, clients || [], { telegram, ntfy }, clients == null),
    { id: id + '/calls', title: t('web.config.group.calls'), fields: calls, hintKey: 'web.config.hint.line_calls' }
  ];
  return item;
}

// A gateway by the form's current values (an unsaved change of the type or the PBX mode counts).
function gatewayState(id) {
  const item = findNode('gateways')?.children.find(c => c.key === id);
  if (!item) return null;
  const v = key => item.fields.find(f => f.path.endsWith('].' + key))?.value;
  return { type: v('type'), managed: v('type') === 'asterisk' && v('pbx') === 'managed' };
}

const lineGateway = item => item.fields.find(f => f.path.endsWith('].gateway'))?.value;

// The fields a gateway's type has; a line has a modem on Asterisk, a port on yeastar / goip.
const GATEWAY_FIELDS = { ami_port: ['asterisk', 'yeastar'], http_port: ['yeastar', 'goip'], monitor: ['asterisk'], pbx: ['asterisk'] };

function fieldVisible(f, n) {
  const g = f.path.match(/^gateways\[[^\]]*\]\.(\w+)$/);
  if (g && GATEWAY_FIELDS[g[1]]) return GATEWAY_FIELDS[g[1]].includes(n.fields.find(x => x.path.endsWith('].type'))?.value);
  const l = f.path.match(/^lines\[[^\]]*\]\.(modem|port)$/);
  if (l) {
    const type = gatewayState(lineGateway(n))?.type;
    return l[1] === 'modem' ? type === 'asterisk' : type !== 'asterisk';
  }
  return true;
}

// A line's modem: one of its gateway's saved modems.
function refreshModemOptions() {
  for (const item of findNode('lines')?.children || []) {
    const f = item.fields.find(x => x.path.endsWith('].modem'));
    if (!f) continue;
    const names = gatewayInfo[lineGateway(item)]?.modems || [];
    f.options = [''].concat(names, f.value && !names.includes(f.value) ? [f.value] : []);
  }
}

// Nodes that apply (and whose parents do), with their parent.
function walkShown(fn, nodes = formTree, parent = null) {
  for (const n of nodes) {
    if (n.visible && !n.visible()) continue;
    fn(n, parent);
    if (n.children) walkShown(fn, n.children, n);
  }
}

// "Clients": Telegram and ntfy as optional subsections, added and removed like list items.
// base: '' at the top, 'lines[name=…].' in a line. unset: a line without a "clients" section,
// which goes to every client that is on.
const CLIENTS = ['telegram', 'ntfy'];
const CLIENT_TITLES = { telegram: 'Telegram', ntfy: 'ntfy' };

function clientsNode(id, base, present, fieldsOf, unset) {
  const line = base !== '';
  const node = {
    id, title: t('web.config.group.clients'), fields: [], clientsBase: base, clientFields: fieldsOf,
    hintKey: line ? (unset ? 'web.config.hint.line_clients_unset' : 'web.config.hint.line_clients') : 'web.config.hint.clients',
    children: []
  };
  present.forEach(c => node.children.push(clientNode(node, c)));
  return node;
}

function clientNode(parent, client) {
  const line = parent.clientsBase !== '';
  return {
    id: parent.id + '/' + client, title: CLIENT_TITLES[client], client,
    section: parent.clientsBase + 'clients.' + client,
    fields: parent.clientFields[client] || [],
    hintKey: line ? 'web.config.hint.line_' + client : 'web.config.hint.' + client
  };
}

function walkTree(fn, nodes = formTree, parent = null) {
  for (const n of nodes) {
    fn(n, parent);
    if (n.children) walkTree(fn, n.children, n);
  }
}

function findNode(id) {
  let found = null;
  walkTree(n => { if (n.id === id) found = n; });
  return found;
}

function fieldInput(f) {
  const cls = f.value !== f.orig ? ' class="changed"' : '';
  const attrs = `data-path="${esc(f.path)}"${cls}`;
  if (f.type === 'checkbox')
    return `<label class="chk"><input type="checkbox" ${attrs}${f.value === 'true' ? ' checked' : ''}> ${esc(f.label)}</label>`;
  if (f.type === 'select')
    return `<label>${esc(f.label)}<select ${attrs}>${(f.options || []).map(o =>
      `<option value="${esc(o)}"${o === f.value ? ' selected' : ''}>${esc(o === '' ? t('web.config.default') : o)}</option>`).join('')}</select></label>`;
  if (f.type === 'textarea')
    return `<label>${esc(f.label)}<textarea ${attrs} rows="3">${esc(f.value)}</textarea></label>`;
  const type = f.type === 'number' ? 'number' : 'text';
  return `<label>${esc(f.label)}<input type="${type}" ${attrs} value="${esc(f.value)}" spellcheck="false"></label>`;
}

function renderTreeNav(nodes, selected, depth) {
  return nodes.filter(n => !n.visible || n.visible()).map(n => {
    const missing = n.clientsBase !== undefined ? CLIENTS.filter(c => !n.children.some(x => x.client === c)) : [];
    const add = n.list
      ? `<button type="button" class="tree-item tree-add" style="--d:${depth + 1}" data-add="${n.list}">${esc(t(LISTS[n.list].add))}</button>`
      : missing.length
      ? `<button type="button" class="tree-item tree-add" style="--d:${depth + 1}" data-add-client="${esc(n.id)}">${esc(t('web.config.add_client'))}</button>`
      : '';
    return `<button type="button" class="tree-item${selected === n.id ? ' on' : ''}" style="--d:${depth}" data-node="${esc(n.id)}">${esc(n.title)}</button>`
      + (n.children ? renderTreeNav(n.children, selected, depth + 1) : '') + add;
  }).join('');
}

function renderTreePanes() {
  let html = '';
  walkShown((n, parent) => {
    const del = parent?.list
      ? `<button type="button" class="btn tree-del" data-del="${esc(n.id)}">${esc(t('web.config.remove'))}</button>`
      : n.section
      ? `<button type="button" class="btn tree-del" data-del-client="${esc(n.id)}">${esc(t('web.config.remove'))}</button>`
      : '';
    const hintKey = n.hintKey || (n.list ? LISTS[n.list].hint : 'web.config.hint.' + n.id);
    const hint = I18N[hintKey] ? `<p class="tree-hint">${esc(t(hintKey))}</p>` : '';
    const fields = n.fields.filter(f => fieldVisible(f, n));
    html += `<div class="tree-pane" data-pane="${esc(n.id)}"><div class="tree-pane-head"><h3>${esc(n.title)}</h3>${del}</div>${hint}`
      + (fields.length ? `<div class="settings-grid">${fields.map(fieldInput).join('')}${n.phone ? phoneSecretCell(n) : ''}</div>` : '')
      + (n.modems ? modemsPaneHtml(n) : '')
      + `</div>`;
  });
  return html;
}

// Values from the inputs into the tree (before re-rendering, so edits are not lost).
function captureFields() {
  document.querySelectorAll('#config-form [data-path]').forEach(el => {
    const v = el.type === 'checkbox' ? (el.checked ? 'true' : 'false') : el.value;
    walkTree(n => { const f = n.fields.find(x => x.path === el.dataset.path); if (f) f.value = v; });
  });
}

function renderSettings(selected) {
  const host = document.getElementById('config-form');
  refreshModemOptions();
  selected = selected || parseRoute().q.s || 'system';
  let shown = false;
  walkShown(n => { if (n.id === selected) shown = true; });
  if (!shown) selected = 'system';
  host.innerHTML = `<nav class="tree-nav">${renderTreeNav(formTree, selected, 0)}</nav><div class="tree-main">${renderTreePanes()}</div>`;
  host.querySelectorAll('.tree-item[data-node]').forEach(btn => btn.onclick = () => showConfigNode(btn.dataset.node));
  host.querySelectorAll('[data-add]').forEach(btn => btn.onclick = startAdd);
  host.querySelectorAll('[data-del]').forEach(btn => btn.onclick = () => removeItem(btn.dataset.del));
  host.querySelectorAll('[data-add-client]').forEach(btn => btn.onclick = startAddClient);
  host.querySelectorAll('[data-del-client]').forEach(btn => btn.onclick = () => removeClient(btn.dataset.delClient));
  host.querySelectorAll('[data-path]').forEach(el => el.addEventListener('input', markChanged));
  host.querySelectorAll('[data-path]').forEach(el => el.addEventListener('change', markChanged));
  // The type, the PBX mode and a line's gateway decide which fields and sections are shown.
  host.querySelectorAll('select[data-path]').forEach(el => {
    if (/\]\.(type|pbx|gateway)$/.test(el.dataset.path))
      el.addEventListener('change', () => { captureFields(); renderSettings(parseRoute().q.s); });
  });
  walkShown(n => {
    const pane = host.querySelector(`.tree-pane[data-pane="${CSS.escape(n.id)}"]`);
    if (n.modems && pane) bindModemPane(pane, n);
  });
  host.querySelectorAll('[data-new-secret]').forEach(b => b.onclick = () => newSecret(b.dataset.newSecret));
  showConfigNode(selected, true);
}

function markChanged(e) {
  const el = e.target;
  let orig;
  walkTree(n => { const f = n.fields.find(x => x.path === el.dataset.path); if (f) orig = f.orig; });
  const v = el.type === 'checkbox' ? (el.checked ? 'true' : 'false') : el.value;
  el.classList.toggle('changed', v !== orig);
}

// The tree section goes into the address (?s=...), without a new history entry.
function showConfigNode(id, fromRoute) {
  const host = document.getElementById('config-form');
  host.querySelectorAll('.tree-item[data-node]').forEach(el => el.classList.toggle('on', el.dataset.node === id));
  host.querySelectorAll('.tree-pane').forEach(el => el.classList.toggle('on', el.dataset.pane === id));
  if (!fromRoute) history.replaceState(history.state, '', buildUrl('/config', { s: id === 'system' ? '' : id }));
}

function startAdd(ev) {
  const btn = ev.currentTarget;
  const listId = btn.dataset.add;
  if (document.querySelector('.tree-add-input')) return;
  const wrap = document.createElement('div');
  wrap.className = 'tree-add-form';
  wrap.style.setProperty('--d', getComputedStyle(btn).getPropertyValue('--d') || '1');
  wrap.innerHTML = `<input type="text" class="tree-add-input" placeholder="${esc(t('web.config.item_name'))}" maxlength="64" spellcheck="false">`;
  btn.replaceWith(wrap);
  const input = wrap.querySelector('input');
  input.focus();
  let done = false;
  const finish = commit => {
    if (done) return;
    done = true;
    captureFields();
    renderSettings(parseRoute().q.s);
    if (commit) commitAdd(listId, input.value.trim());
  };
  input.onkeydown = e => {
    if (e.key === 'Enter') { e.preventDefault(); finish(true); }
    if (e.key === 'Escape') { e.preventDefault(); finish(false); }
  };
  input.onblur = () => finish(!!input.value.trim());
}

function commitAdd(listId, name) {
  if (!name) return;
  if (!NAME_RE.test(name)) { showToast(t('web.config.name_invalid'), 'err'); return; }
  const list = findNode(listId);
  const id = LISTS[listId].prefix + name;
  if (list.children.some(c => c.id === id)) { showToast(t('web.config.name_exists', name), 'err'); return; }
  removedItems = removedItems.filter(x => x !== `${listId}[${LISTS[listId].sel}=${name}]`);
  // Fields of a new item come from the template; they have no orig, so all non-empty ones are saved.
  const fields = (templates[listId] || []).map(f => Object.assign({}, f, {
    path: f.path.split('{key}').join(name),
    value: f.path.endsWith('.' + LISTS[listId].sel) ? name : f.value,
    orig: undefined
  }));
  list.children.push(itemNode(listId, name, fields, true));
  renderSettings(id);
  showConfigNode(id);
}

function removeItem(nodeId) {
  let list = null, item = null;
  walkTree((n, parent) => { if (n.id === nodeId) { item = n; list = parent; } });
  if (!item || !list?.list || !confirm(t(LISTS[list.list].remove, item.title))) return;
  captureFields();
  list.children = list.children.filter(c => c !== item);
  if (!item.isNew) removedItems.push(`${list.list}[${LISTS[list.list].sel}=${item.key}]`);
  renderSettings(list.id);
  showConfigNode(list.id);
}

// Client sections added and removed in the form, sent with the save as "add" / "remove".
let addedSections = [];
let removedSections = [];

function startAddClient(ev) {
  const btn = ev.currentTarget;
  const node = findNode(btn.dataset.addClient);
  if (!node || document.querySelector('.tree-add-input')) return;
  const missing = CLIENTS.filter(c => !node.children.some(x => x.client === c));
  const wrap = document.createElement('div');
  wrap.className = 'tree-add-form';
  wrap.style.setProperty('--d', getComputedStyle(btn).getPropertyValue('--d') || '1');
  wrap.innerHTML = `<select class="tree-add-input"><option value=""></option>${missing.map(c =>
    `<option value="${c}">${esc(CLIENT_TITLES[c])}</option>`).join('')}</select>`;
  btn.replaceWith(wrap);
  const sel = wrap.querySelector('select');
  sel.focus();
  let done = false;
  const finish = client => {
    if (done) return;
    done = true;
    captureFields();
    if (client) {
      const child = clientNode(node, client);
      node.children.push(child);
      if (removedSections.includes(child.section)) removedSections = removedSections.filter(x => x !== child.section);
      else addedSections.push(child.section);
      renderSettings(child.id);
      showConfigNode(child.id);
    } else renderSettings(parseRoute().q.s);
  };
  sel.onchange = () => finish(sel.value);
  sel.onkeydown = e => { if (e.key === 'Escape') { e.preventDefault(); finish(''); } };
  sel.onblur = () => setTimeout(() => finish(''), 150);
}

function removeClient(nodeId) {
  let child = null, parent = null;
  walkTree((n, p) => { if (n.id === nodeId) { child = n; parent = p; } });
  if (!child || !parent || !confirm(t('web.config.remove_client_confirm', child.title))) return;
  captureFields();
  parent.children = parent.children.filter(c => c !== child);
  if (addedSections.includes(child.section)) addedSections = addedSections.filter(x => x !== child.section);
  else removedSections.push(child.section);
  renderSettings(parent.id);
  showConfigNode(parent.id);
}

async function loadConfigForm() {
  const host = document.getElementById('config-form');
  const res = await fetch('/api/settings');
  const data = await res.json().catch(() => ({}));
  if (!res.ok || data.ok === false) {
    host.innerHTML = `<div class="empty">${esc(t('web.error', data.error || res.status))}</div>`;
    return;
  }
  templates = { gateways: data.gatewayTemplate || [], lines: data.lineTemplate || [] };
  gatewayInfo = data.gatewayInfo || {};
  phoneSecrets = data.phoneSecrets || [];
  removedItems = [];
  addedSections = [];
  removedSections = [];
  formTree = settingsTree(data);
  walkTree(n => n.fields.forEach(f => { f.orig = f.value; }));
  // Fields of clients not in the config yet (shown when such a client is added).
  walkTree(n => Object.values(n.clientFields || {}).forEach(list => list.forEach(f => { if (f.orig === undefined) f.orig = f.value; })));
  renderSettings();
}

// Only changed fields (and all non-empty fields of new items) go to the YAML; the rest of the file is untouched.
// Fields and sections that do not apply (another gateway type, no managed PBX) are left out.
function collectFields() {
  captureFields();
  const fields = {};
  walkShown(n => n.fields.forEach(f => {
    if (!fieldVisible(f, n)) return;
    if (f.orig === undefined ? f.value !== '' : f.value !== f.orig) fields[f.path] = f.value;
  }));
  return fields;
}

async function configRequest(url, body) {
  try {
    const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : null });
    const data = await res.json().catch(() => ({}));
    if (res.ok && data.ok) return data;
    showToast(data.error || t('web.error', res.status), 'err');
  } catch (e) {
    showToast(t('web.network_error', e.message), 'err');
  }
  return null;
}

async function saveConfig(restart) {
  setBusy(t('web.config.saving'));
  try {
    const ok = configMode === 'form'
      ? await configRequest('/api/settings', { fields: collectFields(), remove: removedItems.concat(removedSections), add: addedSections, restart })
      : await configRequest('/api/config', { content: (await getConfigEditor()).getValue(), restart });
    if (!ok) return;
    // A new phone's secret: shown once, afterwards it is only in the config.
    for (const [line, secret] of Object.entries(ok.secrets || {})) prompt(t('web.pbx.secret_shown', line), secret);
    // The managed Asterisk: its files were written and reloaded, or why not.
    if (ok.pbx && !ok.pbx.ok) alert(t('web.pbx.apply_failed') + '\n\n' + (ok.pbx.errors || []).join('\n'));
    if (restart) { await waitForRestart(); return; }
    showToast(ok.pbx?.ok && ok.pbx.changed?.length ? t('web.pbx.applied', ok.pbx.changed.join(', ')) : t('web.config.saved'), 'ok');
    if (configMode === 'form') await loadConfigForm();
  } finally {
    setBusy(null);
  }
}

async function restartService() {
  if (!confirm(t('web.config.restart_confirm'))) return;
  setBusy(t('web.config.restarting'));
  try {
    if (await configRequest('/api/restart')) await waitForRestart();
  } finally {
    setBusy(null);
  }
}

// The service exits and systemd brings it back in a few seconds. The page reloads:
// the new settings may have changed the language and the mode.
async function waitForRestart() {
  setBusy(t('web.config.restarting'));
  await new Promise(r => setTimeout(r, 3000));
  for (let i = 0; i < 30; i++) {
    try {
      if ((await fetch('/api/mode', { cache: 'no-store' })).ok) {
        try { sessionStorage.setItem('ghostline.toast', 'web.config.restarted'); } catch { }
        location.reload();
        return;
      }
    } catch { }
    await new Promise(r => setTimeout(r, 1000));
  }
  showToast(t('web.config.restart_timeout'), 'err');
}

// ---- start ----

(async () => {
  try { MODE = await (await fetch('/api/mode')).json(); } catch { }
  const full = MODE.mode === 'full';
  LISTS_VIEWS.forEach(v => document.getElementById(v + '-toolbar').innerHTML = listToolbar(v));
  const fields = readPrefs().fields || {};
  PREFS.forEach(id => { if (id in fields) document.getElementById(id).value = fields[id]; });
  document.getElementById('tab-calls').hidden = !full;
  document.getElementById('footer-mode').textContent = t('web.mode.' + MODE.mode);
  await loadChannels();

  const start = location.pathname === '/' ? readPrefs().last || '/' + defaultView() : location.pathname + location.search;
  history.replaceState({ n: navIndex }, '', start);
  await render();

  LISTS_VIEWS.forEach(setListRefresh);
  setSystemRefresh();
  setStatusRefresh();
  loadStatus();
  setInterval(() => loadStatus(true), 15000);
  // While calls are queued for transcription, refresh so the clock turns into text.
  if (full) setInterval(() => {
    const v = parseRoute().view;
    if (LISTS_VIEWS.includes(v) && document.querySelector('.panel.active .call [data-tr="pending"]')) loadList(v, true);
  }, 15000);

  try {
    const pending = sessionStorage.getItem('ghostline.toast');
    if (pending) { sessionStorage.removeItem('ghostline.toast'); showToast(t(pending), 'ok'); }
  } catch { }
})();
