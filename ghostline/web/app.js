// Strings of the web language (locale.web) are put in by the server. t('key', arg0, arg1...).
const I18N = /*I18N*/{};
const t = (k, ...a) => (I18N[k] ?? k).replace(/\{(\d+)\}/g, (_, i) => a[i]);

// Every view has its own address: /calls, /sms, /log, /status, /config, /config/yaml, /about,
// with the filters in the query string. F5 shows the same view, links can be shared and
// the browser's Back / Forward move between views. "/" opens the view seen last.
const TABS = ['calls', 'sms', 'log', 'status', 'config', 'about'];
const val = id => document.getElementById(id).value;
const checked = id => document.getElementById(id).checked;

function esc(s) {
  return String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// Settings of this browser (lines, auto-refresh) and the last address of every tab stay in localStorage.
const PREFS_KEY = 'ghostline.prefs';
const PREFS = ['sms-limit', 'sms-refresh', 'log-lines', 'log-refresh'];

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
const defaultView = () => MODE.mode === 'full' ? 'calls' : 'sms';

function parseRoute() {
  const parts = location.pathname.split('/').filter(Boolean).map(decodeURIComponent);
  const q = Object.fromEntries(new URLSearchParams(location.search));
  let view = TABS.includes(parts[0]) ? parts[0] : defaultView();
  if (view === 'calls' && MODE.mode !== 'full') view = 'sms';
  return { view, sub: parts[1], q };
}

// Addresses built from the toolbar values. Defaults are left out.
function callsUrl() {
  return buildUrl('/calls', {
    line: val('calls-line'), dir: val('calls-dir'),
    rec: checked('calls-rec') ? '' : '0', missed: checked('calls-missed') ? '1' : '',
    q: val('calls-q').trim(), from: readDate('calls-from') || '', to: readDate('calls-to') || ''
  });
}

function smsUrl() {
  return buildUrl('/sms', { line: val('sms-line'), dir: val('sms-dir'), q: val('sms-q').trim() });
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

  if (r.view === 'calls') {
    setSelect('calls-line', q.line || '');
    setSelect('calls-dir', q.dir || '');
    document.getElementById('calls-rec').checked = q.rec !== '0' && q.missed !== '1';
    document.getElementById('calls-missed').checked = q.missed === '1';
    if (document.activeElement?.id !== 'calls-q') document.getElementById('calls-q').value = q.q || '';
    document.getElementById('calls-from').value = q.from || '';
    document.getElementById('calls-to').value = q.to || '';
    loadCalls();
  } else if (r.view === 'sms') {
    setSelect('sms-line', q.line || '');
    setSelect('sms-dir', q.dir || '');
    if (document.activeElement?.id !== 'sms-q') document.getElementById('sms-q').value = q.q || '';
    loadSms();
  } else if (r.view === 'log') {
    loadLog();
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
  document.getElementById('calls-line').innerHTML = `<option value="">${esc(t('web.calls.all_lines'))}</option>` + opts;
  document.getElementById('sms-line').innerHTML = `<option value="">${esc(t('web.all'))}</option>` + opts;
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

let callsCache = {};

async function loadCalls(keepOpen) {
  const q = parseRoute().q;
  const params = new URLSearchParams({ limit: 500 });
  if (q.line) params.set('channel', q.line);
  if (q.dir) params.set('direction', q.dir);
  if (q.missed === '1') params.set('missed', 'true');
  else if (q.rec !== '0') params.set('rec', 'true');
  if (q.q) params.set('q', q.q);
  if (q.from) params.set('from', q.from);
  if (q.to) params.set('to', q.to);

  if (keepOpen && document.querySelector('.call audio')) return;   // do not interrupt playback
  const open = keepOpen
    ? [...document.querySelectorAll('.call-text:not([hidden])')].map(e => e.closest('.call').dataset.id)
    : [];

  const rows = await (await fetch('/api/calls/list?' + params)).json();
  callsCache = Object.fromEntries(rows.map(r => [r.id, r]));
  document.getElementById('calls-count').textContent = t('web.calls.count', rows.length);
  document.getElementById('calls-container').innerHTML =
    rows.map(formatCall).join('') || `<div class="empty">${esc(t('web.calls.nothing'))}</div>`;
  if (q.q) rows.filter(r => r.trState === 'done').forEach(r => toggleText(r.id, true));
  open.forEach(id => toggleText(id, true));
}

function formatCall(r) {
  const dirCls = r.missed ? 'missed' : r.direction;
  const dirIcon = r.missed ? '✕' : (r.direction === 'in' ? '↙' : '↗');
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
      <span class="call-dur">${r.missed ? '' : fmtDur(r.billsec)}</span>
      <span class="call-actions">
        <button class="icon-btn ${rec ? '' : 'off'}" title="${esc(t('web.calls.play'))}" onclick="playCall(${r.id})">${ICON_PLAY}</button>
        <a class="icon-btn ${rec ? '' : 'off'}" title="${esc(t('web.calls.download'))}" href="/api/calls/${r.id}/audio?download=true" download>${ICON_DOWNLOAD}</a>
        ${trBtn}
      </span>
    </div>
    <div class="call-player"></div>
    <div class="call-text" hidden></div>
  </div>`;
}

// A click on the row (not on the buttons) opens or closes the transcript.
function rowClick(e, id) {
  if (e.target.closest('.call-actions')) return;
  if (callsCache[id]?.trState) toggleText(id);
}

function playCall(id, at) {
  const box = document.querySelector(`.call[data-id="${id}"] .call-player`);
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
  const el = document.querySelector(`.call[data-id="${id}"] .call-text`);
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
      <a class="btn" href="/api/calls/${id}/transcript" download>${esc(t('web.calls.download_txt'))}</a></div>`);
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
  loadCalls(true);
}

// ---- SMS ----

let smsTimer = null;
function setSmsRefresh() {
  clearInterval(smsTimer);
  const ms = parseInt(val('sms-refresh'));
  if (ms > 0) smsTimer = setInterval(() => { if (parseRoute().view === 'sms') loadSms(); }, ms);
}

function smsParams(limit) {
  const q = parseRoute().q;
  const params = new URLSearchParams();
  if (limit) params.set('limit', limit);
  if (q.dir) params.set('direction', q.dir);
  if (q.line) params.set('channel', q.line);
  if (q.q) params.set('text', q.q);
  return params;
}

async function loadSms() {
  const rows = await (await fetch('/api/sms/list?' + smsParams(val('sms-limit')))).json();
  const header = `<div class="sms-line hdr">${['time', 'dir', 'gw', 'channel', 'peer', 'note', 'content']
    .map(c => `<span>${esc(t('web.sms.col.' + c))}</span>`).join('')}</div>`;
  document.getElementById('sms-container').innerHTML = header + rows.map(r => `<div class="sms-line">
      <span class="sms-ts">${esc(r.ts)}</span>
      <span class="dir-${esc(r.direction)}">${esc(r.direction)}</span>
      <span>${esc(r.gateway)}</span>
      <span>${esc(r.channel)}</span>
      <span class="sms-peer" title="${esc(r.peer)}">${esc(r.peer)}</span>
      <span class="sms-note" title="${esc(r.note)}">${esc(r.note)}</span>
      <span class="sms-content">${esc(r.content)}</span>
    </div>`).join('');
}

function exportSms() {
  location.href = '/api/sms/export?' + smsParams();
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
  if (data.ok) { showToast(t('web.sms.queued'), 'ok'); closeSendModal(); loadSms(); }
  else showToast(t('web.error', data.error || 'unknown'), 'err');
}

// ---- Log ----

let logTimer = null;
function setLogRefresh() {
  clearInterval(logTimer);
  const ms = parseInt(val('log-refresh'));
  if (ms > 0) logTimer = setInterval(() => { if (parseRoute().view === 'log') loadLog(); }, ms);
}

async function loadLog() {
  const data = await (await fetch('/api/log?lines=' + val('log-lines'))).json();
  // Newest first, as on the Calls and SMS tabs.
  document.getElementById('log-container').innerHTML = data.lines.slice().reverse().map(line => {
    const p = line.split('\t');
    if (p.length < 8) return `<div class="log-line"><span>${esc(line)}</span></div>`;
    const [date, time, gw, dir, , from, dest, ...rest] = p;
    return `<div class="log-line">
      <span class="log-ts">${esc(date)}</span><span class="log-ts">${esc(time)}</span>
      <span class="log-gw">${esc(gw)}</span><span class="dir-${esc(dir)}">${esc(dir)}</span>
      <span>${esc(from)}</span><span>${esc(dest)}</span><span></span><span>${esc(rest.join('\t'))}</span>
    </div>`;
  }).join('');
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

async function loadStatus() {
  let data;
  try {
    const res = await fetch('/api/status');
    if (!res.ok) return;
    data = await res.json();
  } catch { return; }

  document.getElementById('header-status').innerHTML = data.components.map(c =>
    `<div class="hstat" title="${esc(stateText(c) + (c.state === 'err' && c.lastError ? ': ' + c.lastError : ''))}"><span class="hdot ${c.state}"></span>${esc(c.title)}</div>`
  ).join('');

  if (parseRoute().view !== 'status') return;
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
  const head = `<thead><tr><th>${esc(t('web.status.col.line'))}</th><th>${esc(t('web.status.col.number'))}</th>`
    + `<th>${esc(t('web.status.col.sms_in'))}</th><th>${esc(t('web.status.col.sms_out'))}</th>`
    + (full ? `<th>${esc(t('web.status.col.calls_in'))}</th><th>${esc(t('web.status.col.calls_missed'))}</th><th>${esc(t('web.status.col.calls_out'))}</th><th>${esc(t('web.status.col.last_call'))}</th>` : '')
    + `</tr></thead>`;
  const rows = data.lines.map(l => `<tr class="${l.configured ? '' : 'gone'}" title="${l.configured ? esc(l.gateway || '') : esc(t('web.status.not_in_config'))}">
      <td>${esc(l.name)}${l.configured ? '' : ' *'}</td><td>${esc(l.number || '')}</td>
      <td>${l.smsIn}</td><td>${l.smsOut}</td>
      ${full ? `<td>${l.callsIn}</td><td>${l.callsMissed}</td><td>${l.callsOut}</td><td>${l.lastCall ? esc(fmtTs(l.lastCall)) : ''}</td>` : ''}
    </tr>`).join('');
  const note = data.lines.some(l => !l.configured)
    ? `<caption>* ${esc(t('web.status.not_in_config'))}</caption>` : '';
  document.getElementById('status-lines').innerHTML = note + head + `<tbody>${rows ||
    `<tr><td colspan="8" style="color:var(--muted)">${esc(t('web.status.no_data'))}</td></tr>`}</tbody>`;
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
  channels: { prefix: 'ch:', sel: 'name', add: 'web.config.add_channel', remove: 'web.config.remove_channel_confirm', hint: 'web.config.channels_hint' },
};
const NAME_RE = /^[A-Za-z0-9][A-Za-z0-9_.-]*$/;

let formTree = [];
let templates = {};
let removedItems = [];

// Sections with subsections: calls -> cdr_db, transcribe.
function settingsTree(data) {
  const byId = Object.fromEntries((data.groups || []).map(g => [g.id, g]));
  const node = id => ({ id, title: byId[id].title, fields: byId[id].fields || [] });
  const list = id => ({
    id, title: byId[id].title, fields: [], list: id,
    children: (data[id] || []).map(it => ({ id: LISTS[id].prefix + it.key, title: it.key, key: it.key, fields: it.fields }))
  });
  const tree = [node('general'), node('telegram'), node('web'), node('logger'), list('gateways'), list('channels')];
  tree.push(Object.assign(node('calls'), { children: [node('cdr_db'), node('transcribe')] }));
  tree.push(node('goip'));
  return tree;
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
  return nodes.map(n => {
    const add = n.list
      ? `<button type="button" class="tree-item tree-add" style="--d:${depth + 1}" data-add="${n.list}">${esc(t(LISTS[n.list].add))}</button>`
      : '';
    return `<button type="button" class="tree-item${selected === n.id ? ' on' : ''}" style="--d:${depth}" data-node="${esc(n.id)}">${esc(n.title)}</button>`
      + (n.children ? renderTreeNav(n.children, selected, depth + 1) : '') + add;
  }).join('');
}

function renderTreePanes() {
  let html = '';
  walkTree((n, parent) => {
    const del = parent?.list
      ? `<button type="button" class="btn tree-del" data-del="${esc(n.id)}">${esc(t('web.config.remove'))}</button>`
      : '';
    const hintKey = n.list ? LISTS[n.list].hint : 'web.config.hint.' + n.id;
    const hint = I18N[hintKey] ? `<p class="tree-hint">${esc(t(hintKey))}</p>` : '';
    html += `<div class="tree-pane" data-pane="${esc(n.id)}"><div class="tree-pane-head"><h3>${esc(n.title)}</h3>${del}</div>${hint}`
      + (n.fields.length ? `<div class="settings-grid">${n.fields.map(fieldInput).join('')}</div>` : '')
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
  selected = selected || parseRoute().q.s || 'general';
  if (!findNode(selected)) selected = 'general';
  host.innerHTML = `<nav class="tree-nav">${renderTreeNav(formTree, selected, 0)}</nav><div class="tree-main">${renderTreePanes()}</div>`;
  host.querySelectorAll('.tree-item[data-node]').forEach(btn => btn.onclick = () => showConfigNode(btn.dataset.node));
  host.querySelectorAll('[data-add]').forEach(btn => btn.onclick = startAdd);
  host.querySelectorAll('[data-del]').forEach(btn => btn.onclick = () => removeItem(btn.dataset.del));
  host.querySelectorAll('[data-path]').forEach(el => el.addEventListener('input', markChanged));
  host.querySelectorAll('[data-path]').forEach(el => el.addEventListener('change', markChanged));
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
  if (!fromRoute) history.replaceState(history.state, '', buildUrl('/config', { s: id === 'general' ? '' : id }));
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
  list.children.push({ id, title: name, key: name, fields, isNew: true });
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

async function loadConfigForm() {
  const host = document.getElementById('config-form');
  const res = await fetch('/api/settings');
  const data = await res.json().catch(() => ({}));
  if (!res.ok || data.ok === false) {
    host.innerHTML = `<div class="empty">${esc(t('web.error', data.error || res.status))}</div>`;
    return;
  }
  templates = { gateways: data.gatewayTemplate || [], channels: data.channelTemplate || [] };
  removedItems = [];
  formTree = settingsTree(data);
  walkTree(n => n.fields.forEach(f => { f.orig = f.value; }));
  renderSettings();
}

// Only changed fields (and all non-empty fields of new items) go to the YAML; the rest of the file is untouched.
function collectFields() {
  captureFields();
  const fields = {};
  walkTree(n => n.fields.forEach(f => {
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
      ? await configRequest('/api/settings', { fields: collectFields(), remove: removedItems, restart })
      : await configRequest('/api/config', { content: (await getConfigEditor()).getValue(), restart });
    if (!ok) return;
    if (restart) { await waitForRestart(); return; }
    showToast(t('web.config.saved'), 'ok');
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
  const fields = readPrefs().fields || {};
  PREFS.forEach(id => { if (id in fields) document.getElementById(id).value = fields[id]; });
  try { MODE = await (await fetch('/api/mode')).json(); } catch { }
  const full = MODE.mode === 'full';
  document.getElementById('tab-calls').hidden = !full;
  document.getElementById('footer-mode').textContent = t('web.mode.' + MODE.mode);
  await loadChannels();

  const start = location.pathname === '/' ? readPrefs().last || '/' + defaultView() : location.pathname + location.search;
  history.replaceState({ n: navIndex }, '', start);
  await render();

  setSmsRefresh();
  setLogRefresh();
  loadStatus();
  setInterval(loadStatus, 15000);
  // While calls are queued for transcription, refresh so the clock turns into text.
  if (full) setInterval(() => {
    if (parseRoute().view === 'calls' && document.querySelector('.call [data-tr="pending"]')) loadCalls(true);
  }, 15000);

  try {
    const pending = sessionStorage.getItem('ghostline.toast');
    if (pending) { sessionStorage.removeItem('ghostline.toast'); showToast(t(pending), 'ok'); }
  } catch { }
})();
