const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];

const ICONS = {
  grid: '<path d="M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z" fill="none" stroke="currentColor" stroke-width="1.7"/>',
  gamepad: '<path d="M6 8h12a5 5 0 0 1 4.9 4l.9 5a3.5 3.5 0 0 1-6.1 2.9L16 17H8l-1.7 2.9A3.5 3.5 0 0 1 .2 17l.9-5A5 5 0 0 1 6 8z" fill="none" stroke="currentColor" stroke-width="1.6"/><circle cx="8" cy="12.5" r="2.2" fill="currentColor"/><circle cx="16.4" cy="13.6" r="1" fill="currentColor"/>',
  layers: '<path d="M12 3 2 8l10 5 10-5-10-5zM2 13l10 5 10-5M2 18l10 5 10-5" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/>',
  sort: '<path d="M4 6h16M4 12h10M4 18h6" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>',
  gear: '<circle cx="12" cy="12" r="3.2" fill="none" stroke="currentColor" stroke-width="1.6"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9 7 7M17 17l2.1 2.1M19.1 4.9 17 7M7 17l-2.1 2.1" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>',
  terminal: '<path d="m4 7 5 5-5 5M12 17h8" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/>',
  refresh: '<path d="M20 11a8 8 0 1 0-2.3 6.7M20 4v7h-7" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" fill="none" stroke="currentColor" stroke-width="1.6"/>',
  wrench: '<path d="M14.7 6.3a3.7 3.7 0 0 1 5 5l-1.4 1.4-5-5zM13.3 7.7 4.6 16.4a2 2 0 1 0 2.8 2.8l8.7-8.7" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>',
  plus: '<path d="M12 5v14M5 12h14" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/>',
  stop: '<rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor"/>',
  play: '<path d="M7 4.5v15l13-7.5z" fill="currentColor"/>',
  help: '<circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="1.6"/><path d="M9.5 9.2a2.6 2.6 0 1 1 3.4 2.5c-.6.2-.9.8-.9 1.5v.5" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/><circle cx="12" cy="17" r="1" fill="currentColor"/>',
  check: '<path d="m5 12.5 4.5 4.5L19 7" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>',
  chevD: '<path d="m6 9 6 6 6-6" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>',
  trash: '<path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v6M14 11v6" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>',
  save: '<path d="M5 3h11l3 3v15H5zM8 3v6h7V3M8 21v-7h8v7" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/>',
  info: '<circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="1.6"/><path d="M12 10.5v6" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/><circle cx="12" cy="7.3" r="1.1" fill="currentColor"/>',
};

function paintIcons(root = document) {
  $$('i[data-icon]', root).forEach(el => {
    const k = el.dataset.icon;
    if (!ICONS[k] || el.dataset.done) return;
    el.dataset.done = '1';
    el.innerHTML = `<svg viewBox="0 0 24 24" width="100%" height="100%" style="display:block">${ICONS[k]}</svg>`;
  });
}

const state = { pads: [], slots: [], profile: null, game: null, arch: null, dll: null, settings: {}, profiles: [], buttonNames: [], mappingTargets: [] };

const TARGET_KEYS = {
  a: 'a', b: 'b', x: 'x', y: 'y', lb: 'lb', rb: 'rb', back: 'back', start: 'start',
  l3: 'l3', r3: 'r3',
  up: 'up', down: 'down', left: 'left', right: 'right',
  lt: 'lt', rt: 'rt',
  lu: 'lu', ld: 'ld', ll: 'll', lr: 'lr',
  ru: 'ru', rd: 'rd', rl: 'rl', rr: 'rr',
  lx: 'lx', ly: 'ly', rx: 'rx', ry: 'ry'
};
const targetLabel = k => T('target.' + (TARGET_KEYS[k] || k));
const TARGET_LABEL = new Proxy({}, { get: (_, k) => targetLabel(String(k)) });
const TARGET_KEYS_LIST = Object.keys(TARGET_KEYS);
const BUTTON_WORD = new Proxy({}, { get: (_, k) => T('word.' + String(k)) });
const SRC_LABEL = new Proxy({}, { get: (_, k) => T('src.' + String(k)) });

// Backend toast text arrives in English; show the Persian line when selected.
const MSG_FA = {
  'Slot map saved': 'نقشه اسلات‌ها ذخیره شد',
  'Endless vibration stopped': 'ویبرهٔ بی‌نهایت خاموش شد',
  'Profile saved': 'پروفایل ذخیره شد',
  'Profile deleted': 'پروفایل حذف شد',
  'Settings saved': 'تنظیمات ذخیره شد',
  'Hook removed': 'هوک حذف شد',
  'Hook + settings copied — the game keeps this mapping without Mapivo':
    'هوک و تنظیمات کپی شد — بازی این نقشه را بدون Mapivo نگه می‌دارد'
};
function toastText(msg) {
  if (getMapivoLang() !== 'fa' || !msg) return msg;
  if (MSG_FA[msg]) return MSG_FA[msg];
  let m = msg.match(/^(\d+) pad\(s\) detected$/);
  if (m) return m[1] + ' دسته پیدا شد';
  m = msg.match(/^Loaded profile for (.+)$/);
  if (m) return 'پروفایل بارگذاری شد: ' + m[1];
  m = msg.match(/^Launching (.+)$/);
  if (m) return 'در حال اجرای ' + m[1];
  m = msg.match(/^Test vibration sent to (.+)$/);
  if (m) return 'ویبره تست به ' + m[1] + ' فرستاده شد';
  m = msg.match(/^Mapping saved for (.+)$/);
  if (m) return 'نقشه برای ' + m[1] + ' ذخیره شد';
  if (msg === 'Controller added') return 'کنترلر اضافه شد';
  if (msg === 'Controller removed') return 'کنترلر حذف شد';
  if (msg === 'Controller saved') return 'کنترلر ذخیره شد';
  if (msg === 'Virtual controllers stopped') return 'کنترلرهای مجازی خاموش شدند';
  if (msg === 'No controller is turned on') return 'هیچ کنترلری روشن نیست';
  if (msg === 'All 8 controllers are already added') return 'هر ۸ کنترلر اضافه شده‌اند';
  if (msg === 'Turn the driver off before removing a controller') return 'قبل از حذف کنترلر، درایور را خاموش کن';
  if (msg === 'That pad already drives another virtual controller') return 'این دسته همین حالا کنترلر مجازی دیگری را می‌گرداند';
  m = msg.match(/^(\d+) virtual controller\(s\) are live$/);
  if (m) return m[1] + ' کنترلر مجازی فعال شد';
  m = msg.match(/^(.+) does not support vibration$/);
  if (m) return m[1] + ' از ویبره پشتیبانی نمی‌کند';
  if (msg === 'That pad is not connected') return 'آن دسته وصل نیست';
  m = msg.match(/^Endless vibration on (.+) — hit it again to stop$/);
  if (m) return 'ویبرهٔ بی‌نهایت روی ' + m[1] + ' روشن شد — برای خاموشی دوباره بزن';
  if (msg === 'Slot map saved') return 'نقشه اسلات‌ها ذخیره شد';
  return msg;
}

function toast(msg, kind = '') {
  const t = document.createElement('div');
  t.className = 'toast ' + kind;
  t.textContent = toastText(msg);
  $('#toasts').appendChild(t);
  setTimeout(() => { t.style.transition = 'opacity .2s'; t.style.opacity = '0'; setTimeout(() => t.remove(), 220); }, 3200);
}

/* ---------------- render ---------------- */

function driverRow(label, value) {
  return `<div class="kv-row"><span class="kv-k">${esc(label)}</span><span class="kv-v">${esc(value)}</span></div>`;
}

function renderDriver() {
  const d = state.driver;
  if (!d) return;
  $('#driverState').innerHTML =
    `<div class="note ${d.vigemInstalled ? 'ok' : ''}">${esc(d.vigemInstalled ? T('driver.vigemFound') : T('driver.vigemMissing'))}</div>`;
  $('#driverFiles').innerHTML =
    driverRow(T('driver.x86'), String(d.x86Count)) +
    driverRow(T('driver.x64'), String(d.x64Count));

  const live = $('#driverLive');
  if (live) {
    live.textContent = T('driver.running', { n: d.live || 0 });
    live.className = 'tag ' + (d.running ? 'ok' : '');
  }
  const startBtn = $('#driverStartBtn'), stopBtn = $('#driverStopBtn');
  if (startBtn) startBtn.disabled = !!d.running;
  if (stopBtn) stopBtn.disabled = !d.running;

  const rows = d.rows || [];
  const box = $('#driverList');
  if (!box) return;
  if (!rows.length) {
    box.innerHTML = `<div class="empty">${esc(T('driver.notAdded'))}</div>`;
    return;
  }
  box.innerHTML = rows.map(r => {
    const opts = [`<option value=""${r.token ? '' : ' selected'}>${esc(T('driver.pickPad'))}</option>`]
      .concat((d.pads || []).filter(p => p.usedBy < 0 || p.usedBy === r.index)
        .map(p => `<option value="${esc(p.token)}"${r.token === p.token ? ' selected' : ''}>${esc(p.name)}</option>`));
    if (r.token && !(d.pads || []).some(p => p.token === r.token)) {
      opts.push(`<option value="${esc(r.token)}" selected>${esc(r.name || r.token)} (${esc(T('mapping.offline'))})</option>`);
    }
    const active = drvEdit === r.index;
    const map = r.map || {};
    const names = Object.keys(map).sort();
    const chips = names.length
      ? names.map(k => `<span class="drv-chip">${esc(TARGET_LABEL[k] || k)}<b>${esc(map[k])}</b></span>`).join('')
      : `<span class="drv-empty">${esc(T('driver.untouched'))}</span>`;
    return `<div class="drv-row${r.enabled ? '' : ' off'}${active ? ' editing' : ''}" data-i="${r.index}">
      <div class="drv-head">
        <span class="drv-name">${esc(T('driver.virtual', { n: r.index + 1 }))}</span>
        <label class="switch" title="${esc(T('driver.enabled'))}">
          <input type="checkbox" data-drv-on="${r.index}"${r.enabled ? ' checked' : ''}><span></span>
        </label>
      </div>
      <select data-drv-pad="${r.index}">${opts.join('')}</select>
      <div class="drv-map">${chips}</div>
      <div class="drv-foot">
        <span class="drv-note">${esc(r.connected || !r.token ? '' : T('driver.waiting'))}</span>
        <div class="drv-btns">
          <button class="btn ghost sm" data-drv-open="${r.index}"${r.token ? '' : ' disabled'}>${esc(active ? T('driver.close') : T('driver.configure'))}</button>
          <button class="btn ghost sm" data-drv-del="${r.index}">${esc(T('driver.remove'))}</button>
        </div>
      </div>
    </div>`;
  }).join('');

  $$('[data-drv-on]').forEach(el => el.addEventListener('change', e => {
    call('driverToggle', { index: +e.target.dataset.drvOn, on: e.target.checked })
      .then(r => { if (r && r.driver) { state.driver = r.driver; renderDriver(); } });
  }));
  $$('[data-drv-pad]').forEach(el => el.addEventListener('change', e => {
    call('driverSource', { index: +e.target.dataset.drvPad, token: e.target.value })
      .then(r => { if (r && r.driver) { state.driver = r.driver; renderDriver(); } });
  }));
  $$('[data-drv-del]').forEach(el => el.addEventListener('click', e => {
    const i = +e.currentTarget.dataset.drvDel;
    if (drvEdit === i) closeDrvEdit();
    call('driverRemove', { index: i })
      .then(r => { if (r && r.driver) { state.driver = r.driver; renderDriver(); } });
  }));
  $$('[data-goto]').forEach(el => el.addEventListener('click', () => goPage(el.dataset.goto)));
  $$('[data-drv-open]').forEach(el => el.addEventListener('click', e => {
    const i = +e.currentTarget.dataset.drvOpen;
    openDrvEdit(drvEdit === i ? -1 : i);
  }));
  renderDrvEditBox();
}

function openDrvEdit(i) {
  drvEdit = i;
  learnTarget = null;
  prevPressed.clear();
  renderDriver();
  if (i >= 0) startLive(); else stopLive();
}

function closeDrvEdit() {
  drvEdit = -1;
  learnTarget = null;
  prevPressed.clear();
  stopLive();
  const box = $('#drvEditCard');
  if (box) box.hidden = true;
}

// The editor card: pad diagram plus one row per mapping target.
function renderDrvEditBox() {
  const card = $('#drvEditCard');
  if (!card) return;
  const r = drvRow();
  if (!r) { card.hidden = true; return; }
  card.hidden = false;
  $('#drvEditTitle').textContent = T('driver.editTitle', { n: r.index + 1 });
  $('#drvEditSub').textContent = T('driver.editSub');
  const names = state.buttonNames && state.buttonNames.length ? state.buttonNames : TARGET_KEYS_LIST;
  const map = r.map || {};
  const targets = state.mappingTargets && state.mappingTargets.length ? state.mappingTargets : TARGET_KEYS_LIST;
  const srcLabel = n => TARGET_LABEL[n] || BUTTON_WORD[n] || SRC_LABEL[n] || n;
  $('#drvMapGrid').innerHTML = targets.map(t => {
    const cur = map[t] || '';
    const opts = [`<option value=""${cur ? '' : ' selected'}>${esc(T('driver.unchanged'))}</option>`]
      .concat(names.map(n => `<option value="${esc(n)}"${cur === n ? ' selected' : ''}>${esc(srcLabel(n))}</option>`));
    if (cur && !names.includes(cur)) opts.push(`<option value="${esc(cur)}" selected>${esc(cur)}</option>`);
    const learning = learnTarget === t;
    return `<div class="map-row${learning ? ' learning' : ''}" data-drv-row="${t}"><span>${esc(TARGET_LABEL[t] || t)}</span>
      <select data-drv-map="${t}">${opts.join('')}</select>
      <button class="btn ghost sm" data-drv-learn="${t}">${esc(learning ? T('mapping.pressEllipsis') : T('mapping.learn'))}</button></div>`;
  }).join('');
  $$('[data-drv-map]').forEach(el => el.addEventListener('change', e => {
    const row = drvRow(); if (!row) return;
    row.map = row.map || {};
    if (e.target.value) row.map[e.target.dataset.drvMap] = e.target.value;
    else delete row.map[e.target.dataset.drvMap];
    saveDriverMap();
  }));
  $$('[data-drv-learn]').forEach(el => el.addEventListener('click', () => {
    const t = el.dataset.drvLearn;
    if (learnTarget === t) closeLearn(); else openLearn(t);
  }));
  if (!r.connected) {
    $('#drvPadView').innerHTML = `<div class="empty">${esc(T('driver.noPads'))}</div>`;
    $('#drvLiveGrid').innerHTML = '';
  } else {
    renderPad({ map: map }, '#drvPadView');
  }
}

function renderEditPanels() {
  renderMapping();
  renderDrvEditBox();
}

function renderNav() {
  $$('.nav-item').forEach(b => b.addEventListener('click', () => {
    $$('.nav-item').forEach(x => x.classList.toggle('active', x === b));
    $$('.page').forEach(p => p.classList.toggle('active', p.id === 'page-' + b.dataset.page));
    if (b.dataset.page === 'mapping') { renderMapping(); startLive(); liveTick(); }
    else stopLive();
    if (b.dataset.page === 'logs') refreshLogs();
    if (b.dataset.page === 'driver') { call('driver').then(renderDriver); if (drvEdit >= 0) startLive(); }
    else if (drvEdit >= 0) closeDrvEdit();
  }));
}

function padIcon(p) {
  return p.source === 'xinput'
    ? '<path d="M6 8h12a5 5 0 0 1 4.9 4l.9 5a3.5 3.5 0 0 1-6.1 2.9L16 17H8l-1.7 2.9A3.5 3.5 0 0 1 .2 17l.9-5A5 5 0 0 1 6 8z" fill="none" stroke="currentColor" stroke-width="1.6"/><circle cx="8" cy="12.5" r="2.2" fill="currentColor"/>'
    : '<circle cx="12" cy="12" r="8" fill="none" stroke="currentColor" stroke-width="1.6"/><path d="M12 7v5l3 2" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>';
}

function renderPads() {
  const g = $('#padGrid');
  if (!state.pads.length) {
    g.innerHTML = '<div class="empty">' + esc(T('connected.empty')) + '</div>';
    return;
  }
  g.innerHTML = state.pads.map(p => `
    <div class="pad">
      <div class="pad-ic"><svg viewBox="0 0 24 24" width="19" height="19">${padIcon(p)}</svg></div>
      <div class="pad-body">
        <div class="pad-name" title="${esc(p.name)}">${esc(p.name)}</div>
        <div class="pad-src">${p.source === 'xinput' ? esc(T('pad.xinputSlot', { n: p.index })) : esc(T('pad.directInput'))}</div>
      </div>
      <span class="tag ${p.source === 'xinput' ? 'xinput' : 'di'}">${p.source === 'xinput' ? esc(T('tag.xinput')) : esc(T('tag.dinput'))}</span>
    </div>`).join('');
}

function slotOptions(sel) {
  const opts = [`<option value=""${!sel ? ' selected' : ''}>${esc(T('priority.none'))}</option>`];
  state.pads.forEach(p => {
    opts.push(`<option value="${esc(p.token)}"${sel === p.token ? ' selected' : ''}>${esc(p.name)} [${esc(T(p.source === 'xinput' ? 'tag.xinput' : 'tag.dinput'))}]</option>`);
  });
  if (sel && !state.pads.some(p => p.token === sel)) {
    const slot = state.slots.find(s => s.physical === sel);
    const nm = slot && slot.name ? slot.name : sel;
    opts.push(`<option value="${esc(sel)}" selected>${esc(nm)} (${esc(T('mapping.offline'))})</option>`);
  }
  return opts.join('');
}

function slotHtml(s, idx) {
  const offline = !!s.physical && !s.connected;
  return `
    <div class="slot${offline ? ' offline' : ''}" data-idx="${idx}">
      <div class="slot-top">
        <span class="slot-lbl">${esc(T('mapping.virtual', { n: idx + 1 }))}${offline ? ' <em class="off">' + esc(T('mapping.offline')) + '</em>' : ''}</span>
        <label class="switch" title="${esc(T('priority.vibration'))}">
          <input type="checkbox" data-vib="${idx}"${s.vib ? ' checked' : ''}><span></span>
        </label>
        <button class="btn ghost sm vib-test" data-testvib="${idx}"
          ${s.canVib ? '' : 'disabled'}
          title="${esc(s.canVib ? T('mapping.testVib') : T('mapping.noRumble'))}">${esc(T('common.test'))}</button>
      </div>
      <select data-slot="${idx}">${slotOptions(s.physical)}</select>
    </div>`;
}

function renderSlots() {
  const html = state.slots.map((s, i) => slotHtml(s, i)).join('');
  $('#slotGrid').innerHTML = html;
  $('#ctrlGrid').innerHTML = html;
  $$('[data-slot]').forEach(el => el.addEventListener('change', e => {
    state.slots[+e.target.dataset.slot].physical = e.target.value;
    saveSlots();
  }));
  $$('[data-vib]').forEach(el => el.addEventListener('change', e => {
    state.slots[+e.target.dataset.vib].vib = e.target.checked;
    saveSlots();
  }));
  $$('[data-testvib]').forEach(el => el.addEventListener('click', e => {
    call('testVib', { slot: +e.currentTarget.dataset.testvib });
  }));
}

function renderOnboard() {
  const card = $('#onboardCard');
  if (!card) return;
  const hasProfile = (state.profiles || []).length > 0;
  const hasGame = !!(state.game && state.game.name);
  card.hidden = hasProfile;
  $('#onboardStep1').hidden = hasProfile;
  $('#onboardStep2').hidden = !hasGame;
  if (hasGame) {
    $('#onboardGame').textContent = state.game.name;
    const lb = $('#onboardLaunch');
    if (lb) lb.disabled = !state.arch || state.arch === 'unsupported';
  }
}

function goPage(name) {
  const b = $$('.nav-item').find(x => x.dataset.page === name);
  if (b) b.click();
}

function profileItemHtml(p, i) {
  const cur = !!(state.profile && state.profile.executable === p.executable);
  const noProfile = p.hasProfile === false;
  const sub = esc(p.executable) + (noProfile ? ' · ' + esc(T('pm.noProfileTag')) : '');
  return `
      <button class="pm-item${cur ? ' sel' : ''}${noProfile ? ' neutral' : ''}" data-pick="${i}" role="option">
        <span class="pm-ic"><i data-icon="${noProfile ? 'folder' : 'gamepad'}"></i></span>
        <span class="pm-txt">
          <span class="pm-name">${esc(p.displayName)}</span>
          <span class="pm-exe">${sub}</span>
        </span>
        <span class="pm-cur">${cur ? '<i data-icon="check"></i>' : ''}</span>
      </button>`;
}

function fillProfileList(list) {
  list.innerHTML = state.profiles.length
    ? state.profiles.map((p, i) => profileItemHtml(p, i)).join('')
    : '<div class="pm-empty">' + esc(T('pm.empty')) + '</div>';
  paintIcons(list);
  list.querySelectorAll('[data-pick]').forEach(el => el.addEventListener('click', e => {
    e.stopPropagation();
    selectProfile(+el.dataset.pick);
    closeProfileMenu();
    closeLaunchMenu();
  }));
}

function renderProfileMenu() {
  const list = $('#pmList');
  if (list) {
    const head = $('#profileMenu .pm-head');
    if (head) paintIcons(head);
    fillProfileList(list);
  }
  const lm = $('#lmList');
  if (lm) {
    const head = $('#launchMenu .pm-head');
    if (head) paintIcons(head);
    fillProfileList(lm);
  }
}

function openLaunchMenu() {
  renderProfileMenu();
  const m = $('#launchMenu');
  if (m) m.hidden = false;
  const b = $('#launchPick');
  if (b) b.setAttribute('aria-expanded', 'true');
}

function closeLaunchMenu() {
  const m = $('#launchMenu');
  if (m) m.hidden = true;
  const b = $('#launchPick');
  if (b) b.setAttribute('aria-expanded', 'false');
}

function toggleLaunchMenu() {
  const m = $('#launchMenu');
  if (m && m.hidden) openLaunchMenu(); else closeLaunchMenu();
}

function openProfileMenu() {
  renderProfileMenu();
  const m = $('#profileMenu');
  m.hidden = false;
  $('#gamePick').setAttribute('aria-expanded', 'true');
}

function closeProfileMenu() {
  const m = $('#profileMenu');
  if (m) m.hidden = true;
  const g = $('#gamePick');
  if (g) g.setAttribute('aria-expanded', 'false');
}

function toggleProfileMenu() {
  const m = $('#profileMenu');
  if (m && m.hidden) openProfileMenu(); else closeProfileMenu();
}

function renderGame() {
  if (state.game) {
    $('#gameName').textContent = state.game.name;
    $('#gameMeta').textContent = state.game.path;
    $('#launchBtn').disabled = !state.arch || state.arch === 'unsupported';
    $('#launchPick').disabled = !(state.profiles || []).length;
    $('#archLine').textContent = state.arch
      ? `${state.arch}  ·  ${state.dll || 'no hook dll'}`
      : '';
  } else {
    $('#gameName').textContent = T('launch.noGame');
    $('#gameMeta').textContent = T('launch.chooseExe');
    $('#launchBtn').disabled = true;
    $('#launchPick').disabled = !(state.profiles || []).length;
    $('#archLine').textContent = '';
  }
}

function renderStats() {
  const xi = state.pads.filter(p => p.source === 'xinput').length;
  $('#statPads').textContent = state.pads.length;
  $('#statXInput').textContent = xi;
  $('#statDi').textContent = state.pads.length - xi;
  $('#statProfiles').textContent = state.profiles.filter(p => p.hasProfile !== false).length;
  $('#scanHint').textContent = state.pads.length ? T('connected.devices', { n: state.pads.length }) : '';
}

function renderConn() {
  const on = state.pads.length > 0;
  $('#connDot').className = 'dot ' + (on ? 'on' : 'off');
  $('#connText').textContent = on ? T('conn.ready', { n: state.pads.length }) : T('conn.none');
}

function renderProfiles() {
  const list = $('#profileList');
  if (!state.profiles.length) {
    list.innerHTML = '<div class="empty">' + esc(T('profiles.empty')) + '</div>';
  } else {
    list.innerHTML = state.profiles.map((p, i) => `
      <button class="profile-item${state.profile && state.profile.executable === p.executable ? ' sel' : ''}" data-pidx="${i}">
        <div class="pi-name">${esc(p.displayName)}</div>
        <div class="pi-exe">${esc(p.executable)}</div>
      </button>`).join('');
  }
  $$('[data-pidx]').forEach(el => el.addEventListener('click', () => selectProfile(+el.dataset.pidx)));
  renderProfileDetail();
}

function renderProfileDetail() {
  const p = state.profile;
  if (!p) {
    $('#profileTitle').textContent = T('profiles.noProfile');
    $('#profileName').value = '';
    $('#profileEnabled').checked = false;
    $('#profileKv').innerHTML = '';
    return;
  }
  $('#profileTitle').textContent = p.displayName;
  $('#profileName').value = p.displayName;
  $('#profileEnabled').checked = p.enabled;
  $('#profileKv').innerHTML = `
    <div><span>${esc(T('profiles.executable'))}</span><span>${esc(p.executable)}</span></div>
    <div><span>${esc(T('profiles.gamePath'))}</span><span>${esc(p.gamePath || '-')}</span></div>
    <div><span>${esc(T('profiles.arch'))}</span><span>${state.arch || '-'}</span></div>
    <div><span>${esc(T('profiles.hookDll'))}</span><span>${esc(state.dll || '-')}</span></div>`;
}

function renderPriority() {
  $('#priorityTitle').textContent = state.profile ? T('priority.title') + ' — ' + state.profile.displayName : T('priority.noGame');
  const box = $('#priorityList');
  if (!state.profile) { box.innerHTML = '<div class="empty">' + esc(T('priority.noGameFirst')) + '</div>'; return; }
  box.innerHTML = state.slots.map((s, i) => {
    const p = s.physical ? state.pads.find(x => x.token === s.physical) : null;
    return `
      <div class="priority-row">
        <div class="rank">${i + 1}</div>
        <div class="pr-main">
          <div class="pr-name">${p ? esc(p.name) : esc(T('priority.none'))}</div>
          <div class="pr-sub">${p ? esc(T(p.source === 'xinput' ? 'tag.xinput' : 'tag.dinput')) + ' · ' + esc(T('priority.vibration')) + ' ' + (s.vib ? esc(T('mapping.vibOn').split(/[:：]\s*/)[1]) : esc(T('mapping.vibOff').split(/[:：]\s*/)[1])) : esc(T('priority.slotDisabled'))}</div>
        </div>
        <span class="tag ${p && p.source === 'xinput' ? 'xinput' : 'di'}">${p ? (p.source === 'xinput' ? esc(T('tag.xinput')) : esc(T('tag.dinput'))) : esc(T('priority.empty'))}</span>
      </div>`;
  }).join('');
}

function renderSettings() {
  $('#setAutoLaunch').checked = !!state.settings.autoLaunchGame;
  $('#setStartMin').checked = !!state.settings.startMinimized;
  $('#setDllFolder').value = state.settings.dllFolder || 'DLL';
  $('#setLanguage').value = state.settings.language || getMapivoLang() || 'en';
}

function renderDz() {
  const b = state.profile && state.profile.leftDeadzone != null ? state.profile.leftDeadzone : 0.08;
  const r = state.profile && state.profile.rightDeadzone != null ? state.profile.rightDeadzone : 0.08;
  $('#leftDz').value = b; $('#leftDzOut').textContent = (+b).toFixed(2);
  $('#rightDz').value = r; $('#rightDzOut').textContent = (+r).toFixed(2);
}

/* ---------------- mapping + live test ---------------- */

const LIVE_KEYS = ['a', 'b', 'x', 'y', 'lb', 'rb', 'back', 'start', 'l3', 'r3', 'up', 'down', 'left', 'right'];

function mapSlotIndex() { return +($('#mapSlot').value || 0); }
function currentSlot() { return state.slots[mapSlotIndex()] || { physical: '', map: {} }; }

// The same pad diagram and learn flow serve two places: the mapping page
// (one pad per profile slot) and each virtual-driver row. drvEdit is the row
// being configured, or -1 when the mapping page owns the editor.
let drvEdit = -1;
function drvRow(i = drvEdit) {
  return (state.driver && (state.driver.rows || []).find(r => r.index === i)) || null;
}
function activeCtx() {
  const r = drvEdit >= 0 ? drvRow() : null;
  if (r) return { token: r.token, map: r.map || {}, save: () => saveDriverMap() };
  const s = currentSlot();
  return { token: s.physical, map: s.map || {}, save: () => saveMapNow() };
}
function saveMapNow() {
  call('saveMap', { slot: mapSlotIndex(), map: currentSlot().map || {} });
}
function saveDriverMap() {
  const r = drvRow();
  if (!r) return;
  call('driverMap', { index: r.index, map: r.map || {} })
    .then(x => { if (x && x.driver) { state.driver = x.driver; renderDriver(); } });
}

function renderMapping() {
  const slot = currentSlot();
  const pad = state.pads.find(p => p.token === slot.physical);
  $('#livePadTag').textContent = pad
    ? (pad.name + ' · ' + T(pad.source === 'xinput' ? 'tag.xinput' : 'tag.dinput'))
    : (slot.physical ? T('mapping.savedOffline') : T('mapping.noPadSlot'));
  $('#livePadTag').className = 'tag ' + (pad ? (pad.source === 'xinput' ? 'xinput' : 'di') : 'di');
  paintVib(pad ? (slot.canVib ? 'on' : 'off') : 'off');

  const targets = state.mappingTargets && state.mappingTargets.length ? state.mappingTargets
    : TARGET_KEYS_LIST;
  const map = slot.map || {};
  const names = state.buttonNames.length ? state.buttonNames : TARGET_KEYS_LIST;
  const token = slot.physical;
  // Show a human name for a source: a learned label, the device's own mapping
  // (so b2 reads "A" on a pad whose A is HID button 2), then a generic name.
  const srcLabel = n => {
    if (/^b\d+$/.test(n)) {
      const idx = +n.slice(1);
      const k = LIVE_KEYS.find(key => srcIndex(map[key] || key) === idx);
      return (k ? BUTTON_WORD[k] : 'Button') + ' (#' + (idx + 1) + ')';
    }
    return SRC_LABEL[n] || BUTTON_WORD[n] || TARGET_LABEL[n] || n;
  };
  const label = n => {
    const l = learnedLabel(token, srcIndex(n));
    return l ? l : srcLabel(n);
  };
  const rowHtml = t => {
    const cur = map[t] || '';
    const dflt = DEFAULT_SRC[t] ? SRC_LABEL[DEFAULT_SRC[t]] : (BUTTON_WORD[t] || TARGET_LABEL[t] || t);
    const opts = [`<option value=""${cur ? '' : ' selected'}>${esc(T('mapping.defaultOpt', { name: dflt }))}</option>`]
      .concat(names.map(n => `<option value="${n}"${cur === n ? ' selected' : ''}>${esc(label(n))}</option>`));
    if (cur && !names.includes(cur)) {
      opts.push(`<option value="${esc(cur)}" selected>${esc(label(cur))}</option>`);
    }
    const learning = learnTarget === t;
    return `<div class="map-row${learning ? ' learning' : ''}" data-row="${t}"><span>${esc(TARGET_LABEL[t] || t)}</span>
      <select data-map="${t}">${opts.join('')}</select>
      <button class="btn ghost sm" data-learn="${t}" title="${esc(T('mapping.learnTitle'))}">${esc(learning ? T('mapping.pressEllipsis') : T('mapping.learn'))}</button></div>`;
  };
  const GROUP_BTN = ['a', 'b', 'x', 'y', 'lb', 'rb', 'back', 'start', 'up', 'down', 'left', 'right'];
  const GROUP_STICK = ['l3', 'r3', 'lt', 'rt', 'lu', 'ld', 'll', 'lr', 'ru', 'rd', 'rl', 'rr'];
  const groups = [
    [T('mapping.groupButtons'), GROUP_BTN],
    [T('mapping.groupSticks'), GROUP_STICK]
  ];
  $('#mapGrid').innerHTML = groups.map(([title, keys]) => {
    const rows = keys.filter(k => targets.includes(k));
    if (!rows.length) return '';
    return `<div class="map-group"><div class="map-group-title">${title}</div>${rows.map(rowHtml).join('')}</div>`;
  }).join('');

  $$('[data-map]').forEach(el => el.addEventListener('change', e => {
    const s = currentSlot();
    s.map = s.map || {};
    if (e.target.value) s.map[e.target.dataset.map] = e.target.value;
    else delete s.map[e.target.dataset.map];
  }));
  const toggleLearn = t => {
    if (learnTarget === t) closeLearn();
    else openLearn(t);
  };
  $$('[data-learn]').forEach(el => el.addEventListener('click', () => toggleLearn(el.dataset.learn)));
  // Clicking a row with the mouse arms it too, like most mapping apps: click
  // R2 here, then press the pad button. The dropdown is left alone so it can
  // still be used to pick a source by name.
  $$('[data-row]').forEach(el => el.addEventListener('click', e => {
    if (e.target.closest('select') || e.target.closest('button')) return;
    toggleLearn(el.dataset.row);
  }));
}

let learnTarget = null;
const prevPressed = new Set();

// Where each control sits on the pad diagram, in % of the image, so the popup
// can point at the control being bound.
const LEARN_POS = {
  a: [76.5, 58], b: [85.1, 49.5], x: [69.1, 49.5], y: [77.6, 40.6],
  lb: [20, 24], rb: [79, 24], back: [39.4, 52], start: [60.9, 51.1],
  l3: [40.4, 49.8], r3: [60.1, 49.9],
  up: [36.7, 63.5], down: [36.5, 77.4], left: [32.3, 70.4], right: [41.1, 70.4],
  lt: [25.1, 9.4], rt: [74.8, 9.4],
  lu: [40.4, 49.8], ld: [40.4, 49.8], ll: [40.4, 49.8], lr: [40.4, 49.8],
  lx: [40.4, 49.8], ly: [40.4, 49.8],
  ru: [60.1, 49.9], rd: [60.1, 49.9], rl: [60.1, 49.9], rr: [60.1, 49.9],
  rx: [60.1, 49.9], ry: [60.1, 49.9]
};

function openLearn(t) {
  learnTarget = t;
  prevPressed.clear();
  const modal = $('#learnModal');
  if (!modal) return;
  $('#learnTitle').textContent = T('mapping.bindTitle', { target: TARGET_LABEL[t] || t });
  $('#learnSub').textContent = T('mapping.bindSub');
  const pos = LEARN_POS[t] || [50, 50];
  const mark = $('#learnTargetMark');
  mark.style.left = pos[0] + '%';
  mark.style.top = pos[1] + '%';
  modal.hidden = false;
  startLive();
  renderMapping();
}

function closeLearn() {
  learnTarget = null;
  prevPressed.clear();
  const modal = $('#learnModal');
  if (modal) modal.hidden = true;
  renderEditPanels();
}

let vibLevel = 2;
let vibInfinite = false;
function setVibLevel(l) {
  vibLevel = l;
  const sel = $('#vibLevelSel');
  if (sel && +sel.value !== l) sel.value = String(l);
  paintVib(vibInfinite ? 'buzz' : (currentSlot().canVib ? 'on' : 'off'));
}
function setVibInf(on) {
  vibInfinite = on;
  const b = $('#vibInfBtn');
  if (!b) return;
  b.classList.toggle('hold', on);
  b.dataset.i18n = on ? 'mapping.vibInfOn' : 'mapping.vibInfinite';
  b.textContent = T(b.dataset.i18n);
}

// Xbox 360 diagram (public domain, Jishenaz via Wikimedia Commons) with live
// highlight dots laid over each control. Coordinates are % of the image.
const PAD_SVG = `<div class="pad-stage" id="padStage">
  <img class="pad-img" src="pad.png" alt="Xbox 360 controller">
  <i class="pad-ov" data-b="4"  style="left:20%;top:24%"></i>
  <i class="pad-ov" data-b="5"  style="left:79%;top:24%"></i>
  <i class="pad-ov" data-b="6"  style="left:39.4%;top:52%"></i>
  <i class="pad-ov" data-b="7"  style="left:60.9%;top:51.1%"></i>
  <i class="pad-ov" data-b="3"  style="left:77.6%;top:40.6%"></i>
  <i class="pad-ov" data-b="2"  style="left:69.1%;top:49.5%"></i>
  <i class="pad-ov" data-b="1"  style="left:85.1%;top:49.5%"></i>
  <i class="pad-ov" data-b="0"  style="left:76.5%;top:58%"></i>
  <i class="pad-ov" data-b="8"  style="left:40.4%;top:49.8%"></i>
  <i class="pad-ov" data-b="9"  style="left:60.1%;top:49.9%"></i>
  <i class="pad-ov" data-b="10" style="left:36.7%;top:63.5%"></i>
  <i class="pad-ov" data-b="11" style="left:36.5%;top:77.4%"></i>
  <i class="pad-ov" data-b="12" style="left:32.3%;top:70.4%"></i>
  <i class="pad-ov" data-b="13" style="left:41.1%;top:70.4%"></i>
  <i class="pad-dot" id="padLs"></i>
  <i class="pad-dot" id="padRs"></i>
  <i class="pad-dot trig" id="padLt"></i>
  <i class="pad-dot trig" id="padRt"></i>
</div>
<div class="pad-raw" id="padRaw"></div>`;

function stickDot(el, cx, cy, x, y) {
  const dx = Math.max(-1, Math.min(1, (x - 32767) / 32767)) * 3.2;
  const dy = Math.max(-1, Math.min(1, (32767 - y) / 32767)) * 3.2;
  el.style.left = (cx + dx) + '%';
  el.style.top = (cy + dy) + '%';
}

// A trigger dot is a plain on/off lamp, like the LB/RB indicators: it never
// slides along the rail, it only lights when the trigger is pressed.
function trigDot(el, cx, top) {
  el.style.left = cx + '%';
  el.style.top = top + '%';
}

// A trigger mapped to a physical button is on or off: no rail travel.
const trigIsBtn = (f, k) => /^b\d+$/.test((f.map || {})[k] || '');
const trigOn = (f, k) => trigIsBtn(f, k) ? (f[k] || 0) > 0 : (f[k] || 0) > 4000;
const trigAmt = (f, k) => trigIsBtn(f, k) ? ((f[k] || 0) > 0 ? 65535 : 0) : (f[k] || 0);

// Every source string the pad is currently emitting: raw HID buttons, named
// buttons, POV directions and stick/trigger axes. This is the same vocabulary
// the hook resolves, so anything shown here can be learned and saved.
function activeSources(f) {
  const ctx = activeCtx();
  const token = ctx.token;
  const isX = !!token && token[0] === 'x';
  const out = new Set();
  if (isX) {
    (f.buttons || []).forEach(i => out.add(SRC_NAMES[i] || 'b' + i));
  } else {
    (f.raw || []).forEach(i => out.add('b' + i));
    // POV hat: the reader reports the four D-pad directions as 10..13.
    (f.buttons || []).forEach(i => { if (i >= 10 && i < 14 && SRC_NAMES[i]) out.add(SRC_NAMES[i]); });
  }
  // A pad may report triggers/sticks as buttons rather than axes (SDL's
  // device database says which). Only offer the axis form when the pad's own
  // mapping does not already use a button for it.
  const map = ctx.map || {};
  const srcKey = n => { const k = LIVE_KEYS.find(key => map[key] === n); return k || n; };
  const isBtn = k => /^b\d+$/.test(map[k] || '');
  const ax = (name, v, target) => {
    if (target && isBtn(target)) return;
    const d = (v == null ? 32768 : v) - 32768;
    if (d > 19660) out.add(srcKey(name + '+'));
    else if (d < -19660) out.add(srcKey(name + '-'));
  };
  ax('lx', f.lx, 'lx'); ax('ly', f.ly, 'ly');
  ax('rx', f.rx, 'rx'); ax('ry', f.ry, 'ry');
  if (!isBtn('lt') && (f.lt || 0) > 16000) out.add(srcKey('lt'));
  if (!isBtn('rt') && (f.rt || 0) > 16000) out.add(srcKey('rt'));
  return out;
}

function canonicalOn(f) {
  const active = activeSources(f);
  const map = activeCtx().map || {};
  const out = new Set();
  LIVE_KEYS.forEach(k => { if (active.has(map[k] || k)) out.add(k); });
  return out;
}

function renderPad(f, sel = '#padView') {
  const box = $(sel);
  if (!box) return;
  if (!box.dataset.init || !$('#padLs', box)) { box.innerHTML = PAD_SVG; box.dataset.init = '1'; }
  const on = canonicalOn(f);
  const map = activeCtx().map || {};
  $$('[data-b]', box).forEach(el => {
    const k = el.dataset.b;
    const pov = { 10: 'up', 11: 'down', 12: 'left', 13: 'right' }[k];
    el.classList.toggle('on', k === 'lt'
      ? trigOn(f, 'lt')
      : k === 'rt'
        ? trigOn(f, 'rt')
        : pov ? on.has(pov)
          : on.has(LIVE_KEYS[+k]));
  });
  const at = id => $('#' + id, box);
  const ls = at('padLs'), rs = at('padRs'), lt = at('padLt'), rt = at('padRt');
  if (ls) stickDot(ls, 21.88, 53.79, f.lx, f.ly);
  if (rs) stickDot(rs, 62.66, 74.07, f.rx, f.ry);
  if (lt) { trigDot(lt, 25.1, 9.4); lt.classList.toggle('on', trigOn(f, 'lt')); }
  if (rt) { trigDot(rt, 74.8, 9.4); rt.classList.toggle('on', trigOn(f, 'rt')); }
  const token = activeCtx().token;
  const active = [...activeSources(f)];
  const rawBox = $('#padRaw', box);
  if (rawBox) rawBox.innerHTML = active.length
    ? active.map(n => {
        const idx = srcIndex(n);
        const lbl = idx != null && learnedLabel(token, idx) ? learnedLabel(token, idx) + ' (' + n + ')' : n;
        return `<span class="on">${esc(lbl)}</span>`;
      }).join('')
    : `<span>${esc(T('mapping.noInput'))}</span>`;
}

function vibLevelName(l) {
  return T(l <= 1 ? 'mapping.vibLow' : l === 2 ? 'mapping.vibMid' : 'mapping.vibHigh');
}
function paintVib(state) {
  const text = state === 'buzz'
    ? (vibInfinite
      ? T('mapping.vibBuzzing') + ' · ' + T('mapping.vibInfinite') + ' (' + vibLevelName(vibLevel) + ')'
      : T('mapping.vibBuzzing') + ' (' + vibLevelName(vibLevel) + ')')
    : state === 'on' ? T('mapping.vibOn') : T('mapping.vibOff');
  const cls = 'vib-state ' + (state === 'buzz' ? 'buzz' : state === 'on' ? 'on' : 'off');
  [$('#vibState')].forEach(el => {
    if (!el) return;
    el.textContent = text;
    el.className = cls;
  });
}

function renderLive(f, padSel = '#padView', gridSel = '#liveGrid') {
  const grid = $(gridSel);
  if (!grid) return;
  if (!f || f.error) {
    grid.innerHTML = `<div class="empty">${esc(f && f.error ? f.error : T('mapping.noPadSlot'))}</div>`;
    if (drvEdit < 0) $('#padView').innerHTML = '<div class="empty">—</div>';
    $('#liveHint').textContent = '';
    return;
  }
  renderPad(f, padSel);
  if (typeof f.vib === 'number' && drvEdit < 0)
    paintVib(f.vib > 0 ? 'buzz' : (currentSlot().canVib ? 'on' : 'off'));
  // Show the *mapped* output: a target lights up when its mapped physical
  // button is pressed, so learning a binding is visibly confirmed here.
  const map = activeCtx().map || {};
  const active = activeSources(f);
  const chips = LIVE_KEYS.map(k => {
    const src = map[k] || k;
    const on = active.has(src);
    const lbl = map[k] ? (TARGET_LABEL[k] || k) : TARGET_LABEL[k].replace(/^D-pad /, '');
    return `<span class="live-chip${on ? ' on' : ''}">${esc(lbl)}</span>`;
  }).join('');
  const trigVal = (k, v) => trigAmt(f, k);
  const bar = (lbl, v) => `<div class="live-bar"><span>${lbl}</span>
    <div class="track"><i style="width:${Math.round((v / 65535) * 100)}%"></i></div>
    <b>${Math.round((v / 65535) * 100)}</b></div>`;
  grid.innerHTML = `<div class="live-chips">${chips}</div>
    <div class="live-bars">
      ${bar(T('target.lt'), trigVal('lt', f.lt))}${bar(T('target.rt'), trigVal('rt', f.rt))}
      ${bar(T('target.lx') + ' X', f.lx)}${bar(T('target.ly') + ' Y', f.ly)}
      ${bar(T('target.rx') + ' X', f.rx)}${bar(T('target.ry') + ' Y', f.ry)}
    </div>`;
  $('#liveHint').textContent = learnTarget
    ? T('mapping.pressBind', { target: TARGET_LABEL[learnTarget] || learnTarget })
    : (active.size ? T('mapping.activeSources', { n: active.size }) : T('mapping.idle'));
  learnFrom(f);
}
function renderDrvEditor(f) {
  if (drvEdit < 0) return;
  renderLive(f, '#drvPadView', '#drvLiveGrid');
}

// Canonical the hook understands as physical sources, in button-index order.
const SRC_NAMES = ['a', 'b', 'x', 'y', 'lb', 'rb', 'back', 'start', 'l3', 'r3',
  'up', 'down', 'left', 'right'];

// The input each stick-direction target uses when it is left on default.
const DEFAULT_SRC = {
  lu: 'ly+', ld: 'ly-', ll: 'lx-', lr: 'lx+',
  ru: 'ry+', rd: 'ry-', rl: 'rx-', rr: 'rx+'
};

// Per-pad labels learned from the user: token -> raw index -> target label.
let learned = {};
try { learned = JSON.parse(localStorage.getItem('mapivo.labels') || '{}'); } catch { }
function srcIndex(src) {
  const i = SRC_NAMES.indexOf(src);
  if (i >= 0) return i;
  const m = /^b(\d+)$/.exec(src);
  return m ? +m[1] : null;
}
function learnedLabel(token, idx) {
  const m = idx != null && learned[token];
  return m && m[idx] ? (TARGET_LABEL[m[idx]] || m[idx]) : null;
}

// Auto-bind: while a target is in "learn" mode, the next button pressed on the
// pad becomes its source, using the same vocabulary the hook reads.
function learnFrom(f) {
  if (!learnTarget) return;
  // Any input the pad emits can become a source: a face button, a D-pad
  // direction from the POV hat, or a stick/trigger axis once it passes the
  // threshold. The moment it goes from idle to active it is bound.
  const now = activeSources(f);
  for (const src of now) {
    if (prevPressed.has(src)) continue;
    const ctx = activeCtx();
    const slot = drvEdit >= 0 ? drvRow() : currentSlot();
    if (!slot) return;
    slot.map = slot.map || {};
    const tgt = learnTarget;
    slot.map[tgt] = src;
    // Remember which printed button this HID index really is, so the dropdown
    // shows the pad's own label instead of our assumed order.
    const idx = srcIndex(src);
    if (idx != null) {
      const tok = ctx.token;
      learned[tok] = learned[tok] || {};
      learned[tok][idx] = tgt;
      try { localStorage.setItem('mapivo.labels', JSON.stringify(learned)); } catch { }
    }
    learnTarget = null;
    prevPressed.clear();
    const modal = $('#learnModal');
    if (modal) modal.hidden = true;
    renderEditPanels();
    ctx.save();
    toast(T('mapping.bound', { target: TARGET_LABEL[tgt] || tgt, source: src }));
    return;
  }
  prevPressed.clear();
  now.forEach(x => prevPressed.add(x));
}

let liveTimer = null;
let lastLive = '';
async function liveTick() {
  if (drvEdit >= 0 && $('#page-driver').classList.contains('active')) {
    const raw = await send('act', { action: 'live', driver: drvEdit });
    if (!raw || raw === lastLive) return;
    lastLive = raw;
    try { renderDrvEditor(JSON.parse(raw)); } catch (e) { }
    return;
  }
  if (!$('#page-mapping').classList.contains('active')) return;
  const raw = await send('act', { action: 'live', slot: mapSlotIndex() });
  if (!raw || raw === lastLive) return;
  lastLive = raw;
  try { renderLive(JSON.parse(raw)); } catch (e) { }
}
function startLive() {
  if (liveTimer) return;
  liveTimer = setInterval(liveTick, 120);
}
function stopLive() { clearInterval(liveTimer); liveTimer = null; }

function renderAll() {
  renderPads(); renderStats(); renderConn();
  renderGame(); renderProfileMenu(); renderOnboard(); renderSlots(); renderProfiles(); renderPriority(); renderSettings(); renderDz();
  renderMapping(); renderDriver();
}

function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* ---------------- bridge ---------------- */

const pending = new Map();
let seq = 0;

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', e => {
    let msg;
    try { msg = JSON.parse(e.data); } catch { return; }
    const key = String(msg.id);
    const resolve = pending.get(key);
    if (!resolve) return;
    pending.delete(key);
    resolve(msg.result);
  });
}

function send(cmd, payload) {
  return new Promise(resolve => {
    const id = String(++seq);
    pending.set(id, resolve);
    try {
      window.chrome.webview.postMessage(JSON.stringify({ cmd, payload, id }));
    } catch (e) {
      pending.delete(id);
      toast(T('err.bridge'), 'err');
      resolve(null);
    }
  });
}

async function call(action, payload = {}) {
  const raw = await send('act', { action, ...payload });
  if (!raw) return null;
  try {
    const res = JSON.parse(raw);
    if (res.message) toast(res.message, res.ok === false ? 'err' : 'ok');
    if (res.driver) state.driver = res.driver;
    if (res.state) { Object.assign(state, res.state); renderAll(); }
    else if (res.driver) renderDriver();
    return res;
  } catch (e) { toast(T('err.badResponse'), 'err'); return null; }
}

function saveSlots() {
  call('saveSlots', {
    slots: state.slots,
    leftDeadzone: +$('#leftDz').value,
    rightDeadzone: +$('#rightDz').value
  });
}

function selectProfile(i) {
  const p = state.profiles[i];
  if (!p) return;
  call('selectProfile', { executable: p.executable, gamePath: p.gamePath || '' });
}

document.addEventListener('click', e => {
  const m = $('#profileMenu');
  if (!m || m.hidden) return;
  if (!e.target.closest('#profileMenu') && !e.target.closest('#gamePick')) closeProfileMenu();
  if (!e.target.closest('#launchMenu') && !e.target.closest('#launchPick')) closeLaunchMenu();
});
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeProfileMenu(); });

async function load() {
  const raw = await send('load');
  if (!raw) return;
  try {
    Object.assign(state, JSON.parse(raw));
    if (state.settings && state.settings.language && state.settings.language !== getMapivoLang())
      setMapivoLang(state.settings.language);
    renderAll();
    renderDriver();
  } catch (e) { toast(T('err.load', { msg: e }), 'err'); }
}

document.addEventListener('click', e => {
  const go = e.target.closest('[data-goto]');
  if (go) { goPage(go.dataset.goto); return; }
  const el = e.target.closest('[data-act]');
  if (!el) return;
  const act = el.dataset.act;
  if (act === 'browse') call('browse');
  else if (act === 'launch') call('launch');
  else if (act === 'install') checkThenCopy();
  else if (act === 'unhook') call('unhook');
  else if (act === 'rescan') call('rescan');
  else if (act === 'saveSlots') call('saveSlots', { slots: state.slots, leftDeadzone: +$('#leftDz').value, rightDeadzone: +$('#rightDz').value });
  else if (act === 'saveProfile') call('saveProfile', { displayName: $('#profileName').value, enabled: $('#profileEnabled').checked, slots: state.slots, leftDeadzone: +$('#leftDz').value, rightDeadzone: +$('#rightDz').value });
  else if (act === 'deleteProfile') call('deleteProfile');
  else if (act === 'saveSettings') call('saveSettings', { autoLaunchGame: $('#setAutoLaunch').checked, startMinimized: $('#setStartMin').checked, language: getMapivoLang() });
  else if (act === 'browseDll') call('browseDll');
  else if (act === 'driverRefresh') call('driver').then(renderDriver);
  else if (act === 'driverFolder') call('openDllFolder').then(renderDriver);
  else if (act === 'profileMenu') toggleProfileMenu();
  else if (act === 'launchMenu') toggleLaunchMenu();
  else if (act === 'driverAdd') call('driverAdd').then(renderDriver);
  else if (act === 'driverStart') call('driverStart').then(renderDriver);
  else if (act === 'driverStop') call('driverStop').then(renderDriver);
  else if (act === 'saveMap') saveMapNow();
  else if (act === 'clearLogs') {
    logsCleared = { app: lastLogCounts.app, hook: lastLogCounts.hook };
    $('#logBox').innerHTML = '';
    $('#logErr').hidden = true;
    $('#logErr').textContent = '';
    lastLogSig = '';
  }
  else if (act === 'refreshLogs') refreshLogs();
  else if (act === 'copyLogs') copyLogs();
});

$('#mapSlot').addEventListener('change', () => { renderMapping(); liveTick(); });

$('#vibLevelSel').addEventListener('change', e => setVibLevel(+e.target.value));
$('#vibTestBtn').addEventListener('click', () => {
  call('testVib', { slot: mapSlotIndex(), level: vibLevel, infinite: false });
});
$('#vibInfBtn').addEventListener('click', async () => {
  const res = await call('testVib', { slot: mapSlotIndex(), level: vibLevel, infinite: true });
  if (res) setVibInf(!!res.hold);
});

$('#learnClose').addEventListener('click', closeLearn);
$('#learnCancelBtn').addEventListener('click', closeLearn);
$('#learnModal').addEventListener('click', e => { if (e.target.id === 'learnModal') closeLearn(); });
document.addEventListener('keydown', e => {
  if (e.key !== 'Escape') return;
  const m = $('#learnModal');
  if (m && !m.hidden) closeLearn();
});

$('#setLanguage').addEventListener('change', e => {
  const lang = setMapivoLang(e.target.value);
  renderAll();
  call('saveSettings', {
    autoLaunchGame: $('#setAutoLaunch').checked,
    startMinimized: $('#setStartMin').checked,
    language: lang
  });
});

document.addEventListener('pf-lang', () => { renderAll(); syncGuideShots(); });

$('#leftDz').addEventListener('input', e => { $('#leftDzOut').textContent = (+e.target.value).toFixed(2); });
$('#rightDz').addEventListener('input', e => { $('#rightDzOut').textContent = (+e.target.value).toFixed(2); });

/* ---------------- reports / logs ---------------- */

let lastLogSig = '';
let lastLogCounts = { app: 0, hook: 0 };
let logsCleared = { app: 0, hook: 0 };
const LOG_BAD = /(error|failed|fail|exception|invalid|denied|could not|unable|hr 8|not found)/i;

function logLineEl(text, cls) {
  const d = document.createElement('div');
  d.className = 'logline' + (cls ? ' ' + cls : '');
  d.textContent = text;
  if (LOG_BAD.test(text)) d.classList.add('err');
  return d;
}

function appendHookLines(box, lines) {
  if (!lines || !lines.length) return;
  const head = document.createElement('div');
  head.className = 'loghead';
  head.textContent = T('logs.hook');
  box.appendChild(head);
  lines.forEach(l => box.appendChild(logLineEl(l, 'hk')));
}

function renderLogs(appLines, hookLines, err) {
  const box = $('#logBox');
  const errBox = $('#logErr');
  lastLogCounts = { app: (appLines || []).length, hook: (hookLines || []).length };
  appLines = (appLines || []).slice(logsCleared.app);
  hookLines = (hookLines || []).slice(logsCleared.hook);
  const sig = appLines.join('\n') + '\u0000' + hookLines.join('\n') + '\u0000' + (err || '');
  if (sig === lastLogSig) { if (box) box.scrollTop = box.scrollHeight; return; }
  lastLogSig = sig;

  if (errBox) {
    errBox.hidden = !err;
    errBox.textContent = err ? (getMapivoLang() === 'fa' ? 'خطا در خواندن گزارش‌ها: ' : 'Could not read logs: ') + err : '';
  }
  if (!box) return;
  box.innerHTML = '';
  appLines.forEach(l => box.appendChild(logLineEl(l)));
  appendHookLines(box, hookLines);
  if (!box.children.length) {
    const p = document.createElement('div');
    p.className = 'logline empty';
    p.textContent = T('logs.empty');
    box.appendChild(p);
  }
  box.scrollTop = box.scrollHeight;
}

async function refreshLogs() {
  if (!$('#page-logs') || !$('#page-logs').classList.contains('active')) return;
  const raw = await send('act', { action: 'logs' });
  if (!raw) return;
  try {
    const res = JSON.parse(raw);
    renderLogs(res.lines, res.hookLines, res.ok === false ? res.message : '');
  } catch (e) {
    renderLogs([], [], String(e));
  }
}

async function checkThenCopy() {
  const m = $('#copyWarnModal');
  const list = $('#copyWarnReasons');

  if (!state.game || !state.game.path) { call('install'); return; }

  let res = null;
  try { res = await call('checkGame'); } catch { res = null; }

  if (!res || !res.risk) {
    toast(T('copyWarn.clean'), 'ok');
    call('install');
    return;
  }

  if (list) {
    list.innerHTML = '';
    (res.reasons || []).forEach(r => {
      const li = document.createElement('li');
      li.textContent = r;
      list.appendChild(li);
    });
  }

  const detail = $('#copyWarnDetail');
  detail.hidden = false;
  $('#copyWarnOk').hidden = false;

  $('#copyWarnOk').onclick = () => { m.hidden = true; call('install'); };
  $('#copyWarnCancel').onclick = () => { m.hidden = true; };
  $('#copyWarnX').onclick = () => { m.hidden = true; };
  m.hidden = false;
}

function copyLogs() {
  const box = $('#logBox');
  const text = box ? [...box.children].map(c => c.textContent).join('\n') : '';
  const done = () => toast(getMapivoLang() === 'fa' ? 'گزارش‌ها کپی شد' : 'Logs copied', 'ok');
  if (!text) { toast(getMapivoLang() === 'fa' ? 'چیزی برای کپی نیست' : 'Nothing to copy', 'err'); return; }
  if (navigator.clipboard && navigator.clipboard.writeText)
    navigator.clipboard.writeText(text).then(done).catch(() => fallbackCopy(text, done));
  else fallbackCopy(text, done);
}

function fallbackCopy(text, done) {
  const ta = document.createElement('textarea');
  ta.value = text;
  ta.style.position = 'fixed';
  ta.style.opacity = '0';
  document.body.appendChild(ta);
  ta.select();
  try { document.execCommand('copy'); done(); }
  catch { toast(getMapivoLang() === 'fa' ? 'کپی نشد' : 'Copy failed', 'err'); }
  ta.remove();
}

/* host pushes live app log lines through this */
window.mapivoLog = line => {
  if (!$('#page-logs') || !$('#page-logs').classList.contains('active')) return;
  const box = $('#logBox');
  if (!box) return;
  const empty = box.querySelector('.logline.empty');
  if (empty) empty.remove();
  box.appendChild(logLineEl(line, 'app'));
  while (box.children.length > 900) box.firstElementChild.remove();
  box.scrollTop = box.scrollHeight;
  lastLogSig = '';
};

paintIcons();

/* host pushes device changes the moment Windows reports them */
window.mapivoState = raw => {
  let st;
  try { st = JSON.parse(raw); } catch { return; }
  const before = new Set((state.pads || []).map(p => p.token));
  Object.assign(state, st);
  renderAll();
  const after = new Set((state.pads || []).map(p => p.token));
  const added = [...after].filter(t => !before.has(t));
  const removed = [...before].filter(t => !after.has(t));
  if (added.length) {
    const p = state.pads.find(x => x.token === added[0]);
    toast(T('toast.padConnected', { name: p ? p.name : '' }), 'ok');
  }
  if (removed.length) toast(T('toast.padDisconnected', { name: '' }), '');
};

/* ---- first run: pick language, then Start ---- */
function setupFirstRun() {
  const gate = $('#firstRun');
  if (!gate) return;
  let firstRun = false;
  try { firstRun = !localStorage.getItem('mapivo.firstRun'); } catch { firstRun = false; }
  if (!firstRun) return;

  setMapivoLang('en');
  gate.hidden = false;
  const choices = $$('.lang-choice', gate);
  const pick = lang => {
    setMapivoLang(lang);
    choices.forEach(b => b.classList.toggle('selected', b.dataset.lang === lang));
  };
  choices.forEach(b => b.addEventListener('click', () => pick(b.dataset.lang)));
  $('#firstRunStart').addEventListener('click', () => {
    const lang = getMapivoLang();
    try { localStorage.setItem('mapivo.firstRun', '1'); } catch { }
    gate.hidden = true;
    send('act', { action: 'saveSettings', language: lang });
  });
}

function syncGuideShots() {
  const fa = getMapivoLang() === 'fa';
  document.querySelectorAll('[data-shot]').forEach(el => {
    const src = `help-${el.dataset.shot}${fa ? '-fa' : ''}.png`;
    if (el.getAttribute('src') !== src) el.setAttribute('src', src);
  });
}
syncGuideShots();
renderNav();
setupFirstRun();
load();
