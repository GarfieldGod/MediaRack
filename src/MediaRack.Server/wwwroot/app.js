// MediaRack Web 页。零依赖、零构建：一个文件管两种角色。
//
// 两个角色由 URL 决定，不靠参数：
//   /            管理页——服务端列表与源管理。只对本机开放（服务端按远端地址拦）。
//   /s/{源标识}/  库页——只看得见这一个源。手机/电视用的就是这条。
//
// 三条与后端对齐的约定：
//   1. 一切写操作都走 api/v1/*，磁盘永不可写；
//   2. 所有请求都要带 BASE 前缀——库页挂在 /s/{slug} 下，绝对路径会越过前缀打到根；
//   3. 出错时读 {code, message}，直接把 message 显示出来。

/** 当前源门户的 URL 前缀；管理页为空串。 */
const PORTAL = (location.pathname.match(/^\/s\/([^/]+)/) || [])[1] || '';
const BASE = PORTAL ? `/s/${PORTAL}` : '';
const IS_ADMIN = PORTAL === '';

const state = {
  sources: [],
  collections: [],
  favorites: [],
  view: { sourceId: null, collectionId: null, favoriteId: null, q: '', includeHidden: false, includeMissing: false, offset: 0, limit: 60 },
  total: 0,
  items: [],
  current: null,
  // 批量操作：multiSelect 开着时点卡片 = 选中/取消；selected 存 `${sourceId}|${key}`。
  multiSelect: false,
  selected: new Set(),
};

const $ = (sel) => document.querySelector(sel);

// ── 基础工具 ────────────────────────────────────────────────

function el(tag, props = {}, children = []) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === 'class') node.className = v;
    else if (k === 'text') node.textContent = v;
    else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
    else if (v !== null && v !== undefined) node.setAttribute(k, v);
  }
  for (const child of [].concat(children)) {
    if (child) node.append(child);
  }
  return node;
}

/** 服务端给的绝对路径（封面、视频流）也要走前缀——它们是 <img>/<video> 直接用的，不过 api()。 */
const asset = (path) => (path ? BASE + path : path);

async function api(path, { method = 'GET', body, skipAuthPrompt = false } = {}) {
  const res = await fetch(BASE + path, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  const text = await res.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = { message: text }; }
  }

  if (!res.ok) {
    // 401 只有一种解释：cookie 过期或换了服务端令牌。直接把登录框顶上来，
    // 否则用户只会看到满屏"HTTP 401"而不知道要去哪输密码。
    if (res.status === 401 && !skipAuthPrompt) showLogin();

    const err = new Error(data?.message || `HTTP ${res.status}`);
    err.code = data?.code;
    err.status = res.status;
    throw err;
  }
  return data;
}

function toast(message, isError = false) {
  const box = $('#toast');
  box.textContent = message;
  box.className = isError ? 'toast error' : 'toast';
  box.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { box.hidden = true; }, isError ? 6000 : 3000);
}

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

function humanSize(bytes) {
  if (!bytes) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) { value /= 1024; i++; }
  return `${value.toFixed(value >= 100 || i === 0 ? 0 : 1)} ${units[i]}`;
}

function humanDuration(ms) {
  if (!ms) return '';
  const total = Math.round(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}

function fmtTime(ms) {
  const d = new Date(ms);
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getMonth() + 1}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    toast('已复制');
  } catch {
    toast('复制失败，请手动选中', true);
  }
}

const enc = (v) => encodeURIComponent(v);

// ── 鉴权 ────────────────────────────────────────────────────
//
// 令牌走 cookie（服务端登录时下发），不是每个请求手动加头。
// 原因只有一个：<img src="poster/..."> 和 <video src="play/..."> 带不了自定义头，
// 而这两样恰好是最需要保护的东西。localStorage 里只留一份副本，用于 cookie 过期后的静默续期。
//
// 键名带 BASE：同一浏览器开两个源，两把令牌必须各存各的，否则后进的那个会把前一个覆盖掉。

const TOKEN_KEY = `mrack.token${BASE || '/admin'}`;

function showLogin(note) {
  if (!$('#login')) return;
  closeSidebar();
  $('#login').hidden = false;
  $('#login-note').textContent = note || '';
  $('#login-token').focus();
}

function hideLogin() {
  $('#login').hidden = true;
}

async function login(token) {
  const res = await fetch(BASE + '/api/v1/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token }),
  });

  if (!res.ok) {
    const data = await res.json().catch(() => ({}));
    throw new Error(data.message || '令牌不正确');
  }

  localStorage.setItem(TOKEN_KEY, token);
}

/** 返回 true 表示可以继续加载；false 表示已经挂上登录框，等用户输入。 */
async function ensureAuth() {
  const auth = await api('/api/v1/auth/state', { skipAuthPrompt: true });
  if (!auth.authRequired || auth.authenticated) return true;

  // cookie 没了但本地存过令牌：静默换一张，不要用登录框打扰。
  const saved = localStorage.getItem(TOKEN_KEY);
  if (saved) {
    try {
      await login(saved);
      return true;
    } catch {
      localStorage.removeItem(TOKEN_KEY);
    }
  }

  showLogin();
  return false;
}

// ── 移动端抽屉 ──────────────────────────────────────────────

function openSidebar() {
  $('#sidebar')?.classList.add('open');
  if ($('#sidebar-scrim')) $('#sidebar-scrim').hidden = false;
}

function closeSidebar() {
  $('#sidebar')?.classList.remove('open');
  if ($('#sidebar-scrim')) $('#sidebar-scrim').hidden = true;
}

// ── 弹层 ────────────────────────────────────────────────────

function show(sel) { $(sel).hidden = false; }
function hide(sel) { $(sel).hidden = true; }

document.querySelectorAll('[data-close]').forEach((btn) => {
  btn.addEventListener('click', () => hide('#' + btn.closest('.overlay').id));
});

document.querySelectorAll('.overlay').forEach((overlay) => {
  // 登录层不能点背景关掉：关掉就既不能操作也不能登录，页面等于死锁。
  if (overlay.classList.contains('login')) return;

  overlay.addEventListener('mousedown', (e) => {
    if (e.target === overlay) hide('#' + overlay.id);
  });
});

/* ══════════════════════════════════════════════════════════════
   库页
   ══════════════════════════════════════════════════════════════ */

async function loadSources() {
  const data = await api('/api/v1/sources');
  state.sources = data.sources || [];
  if (state.sources.length && $('#brand-name')) {
    $('#brand-name').textContent = state.sources[0].name;
  }
  renderSources();
}

function renderSources() {
  const list = $('#source-list');
  if (!list) return;
  list.replaceChildren();

  list.append(el('li', {
    class: state.view.sourceId === null ? 'active' : '',
    onclick: () => setView({ sourceId: null }),
  }, [
    el('span', { text: '全部视频（聚合）' }),
    el('span', { class: 'count', text: state.sources.reduce((n, s) => n + (s.itemCount || 0), 0) }),
  ]));

  for (const source of state.sources) {
    const offline = !source.reachable;
    list.append(el('li', {
      class: [
        state.view.sourceId === source.id ? 'active' : '',
        offline ? 'offline' : '',
      ].join(' ').trim(),
      title: offline ? '源目录当前不可达' : source.id,
      onclick: () => setView({ sourceId: source.id }),
    }, [
      el('span', { text: source.name + (offline ? '（离线）' : '') }),
      el('span', { class: 'count', text: source.itemCount ?? '' }),
    ]));
  }
}

async function loadCollections() {
  state.collections = await api('/api/v1/collections');
  renderCollections();
}

// 结构合集（v4）：源级、平铺。single 是"目录"，dual 是"UP 主"——
// 不再有父子层级，所以不做缩进，靠 kind 前缀区分。
function renderCollections() {
  const list = $('#collection-list');
  if (!list) return;
  list.replaceChildren();

  list.append(el('li', {
    class: state.view.collectionId === null ? 'active' : '',
    onclick: () => setView({ collectionId: null }),
  }, [el('span', { text: '不限合集' })]));

  const visible = state.collections.filter((c) => {
    // itemCount 只数真实存在的条目：全条目都已缺失的目录/UP 默认整条藏起来，
    // 打开「显示缺失」时才露出来（这时计数是 0，一眼能看出是空壳）。
    if (!state.view.includeMissing && c.itemCount === 0) return false;
    if (!state.view.sourceId) return c.itemCount > 0 || state.view.includeMissing;
    return c.sourceId === state.view.sourceId;
  });

  for (const c of visible) {
    const label = c.kind === 'up' ? `UP · ${c.name}` : c.name;
    list.append(el('li', {
      class: state.view.collectionId === c.id ? 'active' : '',
      title: c.path || c.id,
      onclick: () => setView({ collectionId: c.id }),
    }, [
      el('span', { text: label }),
      el('span', { class: 'count', text: c.itemCount }),
    ]));
  }
}

// ── 收藏（用户层，跨端）───────────────────────────────────────

async function loadFavorites() {
  // 计数跟着视图走：选中某个源时只数该源的收藏（与侧栏「合集」同一套口径），
  // 聚合视图才数全库。不传 source 时后端按全库算。
  const sid = state.view.sourceId;
  const path = sid ? `/api/v1/favorites?source=${enc(sid)}` : '/api/v1/favorites';
  state.favorites = await api(path);
  renderFavorites();
}

function renderFavorites() {
  const list = $('#favorite-list');
  if (!list) return;
  list.replaceChildren();

  if (!state.favorites.length) {
    list.append(el('li', { class: 'hint' }, [el('span', { text: '（还没有收藏夹）' })]));
    return;
  }

  // 门户页（/s/{slug}）本身就是"只看这一个源"，即使前端没设 sourceId，
  // 服务端也会按门户作用域收窄计数——显示口径必须跟它一致。
  const singleScope = !!state.view.sourceId || !!PORTAL;

  // 单源视图下，该源一条都没收藏的夹子不显示——点进去只会是空列表。
  // （收藏夹是用户层资产，在聚合视图里始终完整可见。）
  const visible = singleScope
    ? state.favorites.filter((f) => f.itemCount > 0)
    : state.favorites;

  if (singleScope && visible.length === 0) {
    list.append(el('li', { class: 'hint' }, [el('span', { text: '（这个源还没有收藏）' })]));
    return;
  }

  for (const f of visible) {
    list.append(el('li', {
      class: state.view.favoriteId === f.id ? 'active' : '',
      title: f.id,
      onclick: () => setView({ favoriteId: f.id }),
    }, [
      el('span', { text: '★ ' + f.name }),
      // 单源视图下不显示合集数：收藏的合集没跟着按源过滤，两个数字并排会自相矛盾。
      el('span', {
        class: 'count',
        text: singleScope ? String(f.itemCount) : `${f.itemCount} / ${f.collectionCount}`,
      }),
    ]));
  }
}

async function loadHealth() {
  const health = await api('/api/v1/health');
  const box = $('#health-box');
  if (!box) return;
  box.replaceChildren();

  const offline = health.sources.filter((s) => !s.reachable);
  // 门户下只有一个源：再报"5 个源"是错的，用户会觉得串台了。
  box.append(el('div', {
    text: health.sources.length === 1
      ? (offline.length ? '源目录当前不可达' : '源可达')
      : `${health.sources.length} 个源 · ${offline.length} 个离线`,
  }));
  box.append(el('div', { text: `schema v${health.schemaVersion}` }));
  box.append(el('div', {
    class: health.capabilities.ffmpeg ? '' : 'warn',
    text: health.capabilities.ffmpeg ? 'ffmpeg 可用（可转封装）' : 'ffmpeg 缺失：无法 remux，只能直连双流',
  }));
}

// ── 列表 ────────────────────────────────────────────────────

function setView(patch) {
  Object.assign(state.view, patch);
  // 换视图 = 换一批条目，选择集里的旧 key 全部失效，直接清掉。
  if (state.multiSelect) state.selected.clear();
  if (patch.sourceId !== undefined) { state.view.collectionId = null; state.view.favoriteId = null; }
  if (patch.collectionId !== undefined) state.view.favoriteId = null;
  // 收藏夹**不**清 sourceId：选中某个源时点夹子 = 看"这个源里我收藏的"，
  // 与侧栏显示的该源计数一致（以前强制切聚合，于是数量是源的、内容是全库的，两边对不上）。
  if (patch.favoriteId !== undefined) state.view.collectionId = null;
  state.view.offset = 0;
  $('#btn-aggregate').classList.toggle('active', state.view.sourceId === null);
  // 手机上选完源就该回到列表，抽屉不该继续挡着内容。
  closeSidebar();
  renderSources();
  renderCollections();
  renderFavorites();
  // 收藏计数是"按源的"，换源就得重拉；否则侧栏还写着上一个源的数字。
  if (patch.sourceId !== undefined) loadFavorites();
  loadLibrary();
}

async function loadLibrary() {
  const { sourceId, collectionId, favoriteId, q, includeHidden, includeMissing, offset, limit } = state.view;

  // 每页条数 = 网格列数 × 10 行：网格列数随窗口宽度变化（auto-fill），
  // 固定 60 会经常在最后一行留缺口。每次拉取前重算，窗口宽度变了下一页自然跟上。
  state.view.limit = pageLimitForGrid();

  const params = new URLSearchParams();
  if (q) params.set('q', q);
  if (collectionId) params.set('collection', collectionId);
  if (favoriteId) params.set('favorite', favoriteId);
  if (includeHidden) params.set('includeHidden', 'true');
  // 缺失条目默认不显示：文件已不在源里，条目留着只为将来搬回来能认领档案。
  // 想看（比如确认某个文件夹是不是被误移走）打开「显示缺失」即可。
  if (includeMissing) params.set('includeMissing', 'true');
  params.set('offset', String(offset));
  params.set('limit', String(limit));

  const path = sourceId
    ? `/api/v1/sources/${enc(sourceId)}/library?${params}`
    : `/api/v1/library?${params}`;

  try {
    const page = await api(path);
    state.items = page.items || [];
    state.total = page.total || 0;
    renderGrid();
    renderCrumbs();
    fillPageToGrid();
  } catch (err) {
    toast(err.message, true);
    state.items = [];
    state.total = 0;
    renderGrid();
  }
}

/** 由当前网格宽度推出列数（与 style.css 的 auto-fill minmax(184px,1fr) + gap 14px 联动）。 */
function pageLimitForGrid() {
  const grid = $('#grid');
  const w = grid ? grid.clientWidth : 0;
  if (!w) return state.view.limit || 60;
  const cols = Math.max(1, Math.floor((w + 14) / (184 + 14)));
  return cols * 10;   // 每页 = 列数 × 10 行，最后一行必满
}

/** 换页大小后把 offset 对齐回"同一页码"，避免窗口宽度变化后翻页跳页。 */
function fillPageToGrid() {
  const desired = pageLimitForGrid();
  if (desired === state.view.limit) return;
  const page = Math.floor(state.view.offset / state.view.limit);
  state.view.limit = desired;
  state.view.offset = page * desired;
  loadLibrary();
}

function renderCrumbs() {
  const parts = [];
  if (state.view.favoriteId) {
    // 收藏夹视图跟着源走（选中源时只看该源里的收藏），所以面包屑要带上源名，
    // 否则"收藏夹 · 默认收藏夹"看不出这些收藏是从哪个源里筛出来的。
    if (state.view.sourceId) {
      const s = state.sources.find((x) => x.id === state.view.sourceId);
      parts.push(s ? s.name : state.view.sourceId);
    }
    const f = state.favorites.find((x) => x.id === state.view.favoriteId);
    parts.push(`收藏夹 · ${f ? f.name : state.view.favoriteId}`);
  } else if (state.view.sourceId) {
    const s = state.sources.find((x) => x.id === state.view.sourceId);
    parts.push(s ? s.name : state.view.sourceId);
  } else {
    parts.push('全部视频');
  }

  if (state.view.collectionId) {
    const c = state.collections.find((x) => x.id === state.view.collectionId);
    if (c) parts.push(c.kind === 'up' ? `UP · ${c.name}` : c.name);
    else parts.push(state.view.collectionId);
  }

  if (state.view.q) parts.push(`“${state.view.q}”`);

  if ($('#crumbs')) $('#crumbs').textContent = `${parts.join(' / ')} · ${state.total} 条`;

  const pageIndex = Math.floor(state.view.offset / state.view.limit) + 1;
  const pageCount = Math.max(1, Math.ceil(state.total / state.view.limit));
  if ($('#page-info')) $('#page-info').textContent = `第 ${pageIndex} / ${pageCount} 页`;
  if ($('#btn-prev')) $('#btn-prev').disabled = state.view.offset <= 0;
  if ($('#btn-next')) $('#btn-next').disabled = state.view.offset + state.view.limit >= state.total;
}

function renderGrid() {
  const grid = $('#grid');
  const empty = $('#empty');
  if (!grid) return;
  grid.replaceChildren();

  if (state.items.length === 0) {
    empty.hidden = false;
    empty.textContent = state.sources.length === 0
      ? '这个源还没有登记文件夹。请在本机管理页里新建源。'
      : '这里没有条目。点右上角「扫描」重新索引这个目录。';
    updateBatchUi();
    return;
  }

  empty.hidden = true;

  for (const item of state.items) {
    grid.append(card(item));
  }

  updateBatchUi();
}

function card(item) {
  const thumb = el('div', { class: 'thumb' });

  const img = el('img', { src: asset(item.posterUrl), alt: '', loading: 'lazy' });
  // rotation 是 meta 层的"额外再转"，poster 文件本身没转——与 Android 端同思路，
  // 渲染层旋转：poster 不用重抽，改 meta 后重新渲染即生效
  const rot = ((item.rotation % 360) + 360) % 360;
  if (rot === 90 || rot === 180 || rot === 270) img.classList.add(`rot${rot}`);
  img.addEventListener('error', () => {
    img.remove();
    thumb.append(el('span', { class: 'placeholder', text: '无缩略图' }));
  });
  thumb.append(img);

  if (item.status !== 'ok') {
    thumb.append(el('span', {
      class: `badge ${item.status}`,
      text: item.status === 'missing' ? '缺失' : '损坏',
    }));
  } else if (item.hidden) {
    thumb.append(el('span', { class: 'badge hidden', text: '已隐藏' }));
  }

  if (item.durationMs && item.playPosMs > 0) {
    const ratio = Math.min(1, item.playPosMs / item.durationMs);
    thumb.append(el('div', { class: 'progress-line', style: `width:${(ratio * 100).toFixed(1)}%` }));
  }

  const sub = [];
  if (item.durationMs) sub.push(el('span', { text: humanDuration(item.durationMs) }));
  if (item.width && item.height) sub.push(el('span', { text: `${item.width}×${item.height}` }));
  if (item.sizeBytes) sub.push(el('span', { text: humanSize(item.sizeBytes) }));
  if (item.sources && item.sources.length > 1) {
    sub.push(el('span', { text: `${item.sources.length} 个源`, title: item.sources.join(', ') }));
  }

  const id = `${item.sourceId}|${item.key}`;
  const selected = state.selected.has(id);

  const pick = el('span', { class: 'pick', text: selected ? '✓' : '' });
  thumb.append(pick);

  const cardNode = el('div', {
    class: 'card' + (selected ? ' selected' : ''),
  }, [
    thumb,
    el('div', { class: 'body' }, [
      el('div', { class: 'title', text: item.title || item.key }),
      el('div', { class: 'sub' }, sub),
    ]),
  ]);

  cardNode.addEventListener('click', () => {
    // 多选模式下点卡片 = 选中/取消，详情弹窗不打开。
    // 缺失/损坏的条目没有 meta 可改，不允许进选择集。
    if (state.multiSelect) {
      if (item.status !== 'ok') return;
      state.selected.has(id) ? state.selected.delete(id) : state.selected.add(id);
      const on = state.selected.has(id);
      pick.textContent = on ? '✓' : '';
      cardNode.classList.toggle('selected', on);
      updateBatchUi();
      return;
    }
    openDetail(item);
  });

  return cardNode;
}

// ── 批量操作（多选模式 + 操作面板）─────────────────────────

/** 能进选择集的条目：缺失/损坏的没有 meta 可改。 */
function selectableItems() {
  return state.items.filter((it) => it.status === 'ok');
}

function isPageFullySelected() {
  const avail = selectableItems();
  return avail.length > 0 && avail.every((it) => state.selected.has(`${it.sourceId}|${it.key}`));
}

/** 切换多选模式。清空选择集，body.multi-select 驱动勾选圈的显隐。 */
function setMultiSelect(on) {
  state.multiSelect = on;
  state.selected.clear();
  document.body.classList.toggle('multi-select', on);
  const btn = $('#btn-multiselect');
  if (btn) btn.classList.toggle('active', on);
  updateBatchUi();
  renderGrid();
}

/** 同步工具条与面板上的计数/按钮态。多选模式关着时工具条隐藏。 */
function updateBatchUi() {
  const n = state.selected.size;
  const bar = $('#batch-toolbar');
  if (bar) bar.hidden = !state.multiSelect;
  const count = $('#batch-count');
  if (count) count.textContent = `已选 ${n} 条`;
  const open = $('#btn-batch-open');
  if (open) open.disabled = n === 0;
  const selPage = $('#btn-select-page');
  if (selPage) {
    const avail = selectableItems();
    selPage.disabled = avail.length === 0;
    selPage.textContent = isPageFullySelected() ? '取消本页' : '全选本页';
  }
}

/** 翻页 / 切视图 / 隐藏之后，把选择集里已经不在当前列表里的条目清掉。 */
function pruneSelection() {
  const present = new Set(state.items.map((it) => `${it.sourceId}|${it.key}`));
  for (const id of [...state.selected]) {
    if (!present.has(id)) state.selected.delete(id);
  }
}

function togglePageSelection() {
  const on = !isPageFullySelected();
  for (const it of selectableItems()) {
    const id = `${it.sourceId}|${it.key}`;
    if (on) state.selected.add(id);
    else state.selected.delete(id);
  }
  updateBatchUi();
  renderGrid();
}

/**
 * 合集 id → 去源化的 override 键。
 * id 形如 "dir:SRC:/电影" / "up:SRC:mid" / "custom:SRC:名字"，
 * 去掉中间的源段后得到 "dir:/电影" / "up:mid" / "custom:名字"。
 * 跨源的 dropdown 用它去重：同名目录在哪个源都对应同一个 override 键。
 */
function storedCollectionKey(id) {
  for (const prefix of ['dir:', 'up:', 'custom:']) {
    if (!id.startsWith(prefix)) continue;
    const rest = id.slice(prefix.length);
    const sep = rest.indexOf(':');
    return sep >= 0 ? prefix + rest.slice(sep + 1) : null;
  }
  return null;
}

function openBatchPanel() {
  const count = $('#batch-dialog-count');
  if (count) count.textContent = `（已选 ${state.selected.size} 条）`;

  // 合集下拉：全部合集按去源化键去重（同名目录跨源只显示一项）
  const sel = $('#batch-collection');
  if (sel) {
    const seen = new Map();
    for (const c of state.collections || []) {
      const key = storedCollectionKey(c.id);
      if (key && !seen.has(key)) seen.set(key, c);
    }
    sel.replaceChildren(
      el('option', { value: '', text: '（选择目标合集）' }),
      ...[...seen.entries()].map(([key, c]) =>
        el('option', { value: key, text: c.name || key })),
    );
    sel.value = '';
    $('#batch-new-collection').value = '';
  }

  show('#dialog-batch');
}

/** 把选中的条目按源分组后批量写 meta；聚合视图可能混多个源，必须分组。 */
async function batchApply(patch, doneMsg) {
  const keysBySource = new Map();
  for (const id of state.selected) {
    const sourceId = id.slice(0, id.indexOf('|'));
    const key = id.slice(id.indexOf('|') + 1);
    if (!keysBySource.has(sourceId)) keysBySource.set(sourceId, []);
    keysBySource.get(sourceId).push(key);
  }

  let updated = 0;
  const failed = [];
  try {
    for (const [sourceId, keys] of keysBySource) {
      const res = await api('/api/v1/meta/batch', {
        method: 'POST',
        body: { source: sourceId, keys, ...patch },
      });
      updated += res.updated || 0;
      if (res.failed?.length) failed.push(...res.failed);
    }
    toast(`已更新 ${updated} 条` + (failed.length ? `，${failed.length} 条失败` : ''));
  } catch (err) {
    toast('批量操作失败：' + err.message, true);
  } finally {
    hide('#dialog-batch');
    await loadLibrary();   // 重拉列表：隐藏的条目从视图消失
    // 只在批量操作后清理选择集：翻页/普通刷新绝不碰（跨页多选全靠选择集常驻）
    pruneSelection();
    updateBatchUi();
  }
}

// ── 详情 ────────────────────────────────────────────────────

async function openDetail(item) {
  try {
    const detail = await api(`/api/v1/media/${enc(item.sourceId)}/${enc(item.key)}`);
    state.current = detail;

    $('#detail-title').textContent = detail.title || detail.key;
    $('#edit-title').value = detail.meta.titleDisplay || '';
    $('#edit-tags').value = (detail.meta.tags || []).join(', ');
    $('#edit-hidden').checked = !!detail.meta.hidden;
    $('#detail-note').textContent = '';

    const facts = $('#detail-facts');
    facts.replaceChildren(
      fact('源', detail.sourceId),
      fact('键', detail.key),
      fact('布局', detail.layout === 'dual' ? 'dual（双流）' : 'single（单流）'),
      fact('状态', detail.status),
      fact('尺寸', detail.width && detail.height ? `${detail.width}×${detail.height}` : '—'),
      fact('旋转', detail.meta.rotation ? `${detail.meta.rotation}°（用户覆盖）` : '—'),
      fact('时长', detail.durationMs ? humanDuration(detail.durationMs) : '—'),
      fact('大小', detail.sizeBytes ? humanSize(detail.sizeBytes) : '—'),
      fact('编码', detail.codec || '—'),
      fact('路径', detail.relPath || '—'),
      fact('轨', `${detail.tracks.length} 条 · 可用 ${detail.delivery.join('/')}`),
      fact('rev', String(detail.meta.rev)),
      fact('播放次数', String(detail.meta.playCount)),
    );

    show('#dialog-detail');
  } catch (err) {
    toast(err.message, true);
  }
}

function fact(label, value) {
  return el('div', {}, [
    el('b', { text: label + '：' }),
    document.createTextNode(value),
  ]);
}

async function saveDetail() {
  const detail = state.current;
  if (!detail) return;

  const tags = $('#edit-tags').value
    .split(',')
    .map((t) => t.trim())
    .filter(Boolean);

  try {
    const meta = await api(`/api/v1/meta/${enc(detail.sourceId)}/${enc(detail.key)}`, {
      method: 'POST',
      body: {
        // 空串 = 清除覆盖、恢复源端标题。后端的语义就是这样，不需要单独的 unset。
        titleDisplay: $('#edit-title').value,
        tags,
        hidden: $('#edit-hidden').checked,
      },
    });

    detail.meta = meta;
    toast('已保存（rev ' + meta.rev + '）');
    await loadLibrary();
  } catch (err) {
    $('#detail-note').textContent = err.message;
    toast(err.message, true);
  }
}

// ── 播放 ────────────────────────────────────────────────────

async function playCurrent() {
  const detail = state.current;
  if (!detail) return;

  hide('#dialog-detail');
  $('#play-title').textContent = detail.title || detail.key;

  const hint = $('#play-hint');
  if (detail.layout === 'dual') {
    hint.textContent = '双流条目：服务端正在按需转封装成单流（-c copy，不重新编码）。首次播放需要等几秒，之后直接读缓存产物。';
  } else {
    hint.textContent = '单流条目：直接读取原文件，零转码。';
  }

  const video = $('#video');
  video.src = asset(`/play/${enc(detail.sourceId)}/${enc(detail.key)}?mode=auto`);
  show('#dialog-play');

  try {
    await video.play();
  } catch {
    // 自动播放被拦是正常现象，用户点一下播放键就行。
  }
}

function stopPlayback() {
  const video = $('#video');
  if (!video || !video.src) return;

  reportProgress(true);
  video.pause();
  video.removeAttribute('src');
  video.load();
}

function reportProgress(force = false) {
  const detail = state.current;
  const video = $('#video');
  if (!detail || !video || !video.currentSrc) return;

  const now = Date.now();
  if (!force && now - (reportProgress.last || 0) < 10000) return;
  reportProgress.last = now;

  const posMs = Math.round(video.currentTime * 1000);
  if (!Number.isFinite(posMs) || posMs <= 0) return;

  api(`/api/v1/progress/${enc(detail.sourceId)}/${enc(detail.key)}`, {
    method: 'POST',
    body: { posMs },
  }).catch(() => { /* 进度回传失败不该打断播放 */ });
}

// ── 扫描 ────────────────────────────────────────────────────

const SCAN_STAGE_NAME = {
  queued: '排队中',
  scan: '扫描文件',
  write: '写入库',
  collections: '同步合集',
  posters: '生成缩略图',
  done: '已完成',
  failed: '失败',
};

// ── 扫描任务面板（右下角）────────────────────────────────
// 后端支持并发扫描（每个磁盘并发 1、全局上限 3，同盘自动排队），
// 所以面板列出全部任务各自跟进，而不是单条进度条。
// 已结束的任务保留 8 秒让人看见结果，然后面板自动收起。

const SCAN_STATE_NAME = {
  queued: '排队中',
  running: '扫描中',
  cancelling: '取消中',
  done: '完成',
  cancelled: '已取消',
  failed: '失败',
};

/** 记录上一轮各任务的 state，用来感知「刚完成」并触发一次页面刷新。 */
const scanLastState = new Map();

let scanPanelTimer = null;
let scanPanelIdleTicks = 0;
let scanPanelErrStreak = 0;

function ensureScanPolling() {
  if (scanPanelTimer) return;
  scanPanelIdleTicks = 0;
  scanPanelErrStreak = 0;
  scanPanelTimer = setInterval(async () => {
    let jobs;
    try {
      jobs = await api('/api/v1/scan');
      scanPanelErrStreak = 0;
    } catch {
      // 无权限（源门户）或网络问题：连续失败就停轮询，别空转。
      if (++scanPanelErrStreak >= 3) {
        clearInterval(scanPanelTimer);
        scanPanelTimer = null;
      }
      return;
    }
    const panel = $('#scan-panel');
    const list = $('#scan-jobs');
    if (!panel || !list) return;

    const now = Date.now();
    const visible = (jobs || []).filter((j) =>
      ['queued', 'running', 'cancelling'].includes(j.state) ||
      (j.finishedAt && now - j.finishedAt < 8000));

    // 有任务刚从活动态结束：提示一次并刷新页面数据（每任务只触发一次）
    for (const j of jobs || []) {
      const prev = scanLastState.get(j.jobId);
      if (prev && prev !== j.state &&
          ['done', 'cancelled', 'failed'].includes(j.state)) {
        if (j.state === 'done') toast(j.message || '扫描完成');
        else if (j.state === 'cancelled') toast('扫描已取消');
        else toast('扫描失败：' + (j.message || '未知原因'), true);
        refreshAll().catch(() => {});
      }
      scanLastState.set(j.jobId, j.state);
    }

    if (visible.length === 0) {
      panel.hidden = true;
      list.replaceChildren();
      if (++scanPanelIdleTicks >= 2) {
        clearInterval(scanPanelTimer);
        scanPanelTimer = null;
        scanLastState.clear();
      }
      return;
    }

    scanPanelIdleTicks = 0;
    panel.hidden = false;
    list.replaceChildren(...visible.map(scanJobRow));
  }, 800);
}

function scanJobRow(j) {
  const done = j.done || 0;
  const total = j.total || 0;
  const known = total > 0;
  const pct = known ? Math.min(100, Math.round((done * 100) / total)) : 0;
  const active = ['queued', 'running', 'cancelling'].includes(j.state);

  const fill = el('div', { class: 'scan-fill' });
  fill.style.width = known ? `${pct}%` : '';
  const track = el('div', { class: 'scan-track' + (known ? '' : ' unknown') }, [fill]);

  const stateText = SCAN_STATE_NAME[j.state] || j.state;
  const stageText = j.state === 'queued' && j.current
    ? j.current // 排队原因，如「等待磁盘 H: 空闲…」
    : (SCAN_STAGE_NAME[j.stage] || '');

  let action = null;
  if (j.state === 'queued' || j.state === 'running') {
    action = el('button', {
      class: 'btn ghost tiny',
      text: '取消',
      onclick: async (e) => {
        e.target.disabled = true;
        try {
          await api(`/api/v1/scan/${enc(j.jobId)}/cancel`, { method: 'POST' });
        } catch (err) {
          toast('取消失败：' + err.message, true);
          e.target.disabled = false;
        }
      },
    });
  } else if (j.state === 'cancelling') {
    action = el('button', { class: 'btn ghost tiny', text: '取消中…', disabled: true });
  }

  const subParts = [];
  if (!active && j.message) subParts.push(j.message);
  if (active && stageText) subParts.push(stageText);
  if (known) subParts.push(`${done} / ${total}（${pct}%）`);
  else if (done) subParts.push(`已处理 ${done} 条`);
  if (j.state === 'running' && j.current) subParts.push(j.current);

  return el('div', { class: 'scan-job' + (j.state === 'failed' ? ' failed' : '') }, [
    el('div', { class: 'scan-job-top' }, [
      el('span', { class: 'scan-job-src', text: j.sourceId || '未命名源' }),
      el('span', { class: 'scan-job-state', text: stateText }),
      ...(action ? [action] : []),
    ]),
    track,
    el('div', { class: 'scan-job-sub', text: subParts.join(' · ') }),
  ]);
}

/** 发起扫描。不再有全局锁：并发/排队由后端磁盘闸门决定，面板负责展示。 */
async function startScan(payload) {
  try {
    await api('/api/v1/scan', { method: 'POST', body: payload });
    ensureScanPolling();
  } catch (err) {
    toast(err.message, true);
  }
}

async function refreshAll() {
  await Promise.all([loadSources(), loadCollections(), loadFavorites(), loadHealth()]);
  // 管理页开着时统计也要跟着刷：否则扫描完成后页面上还是搬走前的条数/体积，
  // 用户会以为扫描没生效（得手动切页面才更新）。
  if (admin.service) {
    try {
      admin.service = await api('/api/v1/services');
      renderAdminSources();
      renderAdminStats();
    } catch {
      // 管理页被关掉/降权时不管它，库页照常刷新。
    }
  }
  await loadLibrary();
}

/* ══════════════════════════════════════════════════════════════
   管理页
   ══════════════════════════════════════════════════════════════ */

const admin = { service: null, current: null };

const KIND_NAME = { loopback: '本机', lan: '内网', virtual: '虚拟网络', other: '其他' };

async function loadService() {
  admin.service = await api('/api/v1/services');
  renderAdminSources();
  renderAdminStats();
}

// ── 统计 ────────────────────────────────────────────────────
//
// 口径随选中项走：站在某个源里就是该源的数，站在「总览」才是全服务器的数。
// 之前四张卡恒为全库聚合，用户点进一个源看到"13237 条"其实是所有源加起来的，纯误导。

function renderAdminStats() {
  const box = $('#admin-stats');
  if (!box) return;
  box.hidden = false;

  const scope = $('#admin-scope');
  if (scope) scope.hidden = false;
  const s = admin.current;
  const sources = admin.service?.sources || [];

  if (s) {
    if (scope) scope.textContent = `当前源 · ${s.name}`;
    const folders = (s.folders || []).length;
    box.replaceChildren(
      statCard('可用条目', String(s.itemCount ?? 0), '条'),
      statCard('缺失', String(s.missingCount ?? 0), '条'),
      statCard('占用', s.totalBytes ? humanSize(s.totalBytes) : '—'),
      statCard('最近扫描', s.lastScanAt ? fmtTime(s.lastScanAt) : '尚未扫描'),
      statCard('文件夹', String(folders), folders ? `个${s.reachable ? '' : ' · 不可达'}` : '个'),
    );
    return;
  }

  if (scope) scope.textContent = '全部源 · 整台服务器';

  const items = sources.reduce((n, x) => n + (x.itemCount || 0), 0);
  const missing = sources.reduce((n, x) => n + (x.missingCount || 0), 0);
  const bytes = sources.reduce((n, x) => n + (x.totalBytes || 0), 0);
  const offline = sources.filter((x) => !x.reachable).length;
  const lastScan = sources.reduce((m, x) => Math.max(m, x.lastScanAt || 0), 0);

  box.replaceChildren(
    // 条数只算真实存在的条目：搬走文件夹后被标 missing 的行不计入，但单独给个数，
    // 免得用户以为"文件没了却什么都没发生"。
    statCard('视频总数', String(items), missing ? `条 · ${missing} 缺失` : '条'),
    statCard('存储占用', bytes ? humanSize(bytes) : '—'),
    statCard('源', String(sources.length), offline ? ` · ${offline} 离线` : ''),
    statCard('最近扫描', lastScan ? fmtTime(lastScan) : '尚未扫描'),
  );
}

function statCard(label, value, sub = '') {
  return el('div', { class: 'stat' }, [
    el('div', { class: 'stat-label', text: label }),
    el('div', { class: 'stat-value' }, [
      document.createTextNode(value),
      sub ? el('span', { class: 'stat-sub', text: sub }) : null,
    ].filter(Boolean)),
  ]);
}

// ── 源列表（侧栏）────────────────────────────────────────────

function renderAdminSources() {
  const list = $('#admin-source-list');
  if (!list) return;
  list.replaceChildren();

  const sources = admin.service?.sources || [];
  $('#admin-side-hint').textContent = sources.length ? '' : '还没有源，点右上角「新建源」。';

  // 总览放在源列表顶部：服务器级的信息（端口、访问地址、源汇总）都在那里，
  // 否则这些东西只能塞进某个源的详情里，看着就像那个源的属性。
  const items = sources.reduce((n, x) => n + (x.itemCount || 0), 0);
  list.append(el('li', {
    class: ['src-overview', admin.current ? '' : 'active'].join(' ').trim(),
    onclick: () => selectSource(null),
  }, [
    el('div', { class: 'src-row' }, [
      el('span', { class: 'src-name', text: '总览' }),
    ]),
    el('div', {
      class: 'src-meta',
      text: `${sources.length} 个源 · ${items} 条`,
    }),
  ]));

  for (const s of sources) {
    const offline = !s.reachable;
    list.append(el('li', {
      class: [admin.current?.id === s.id ? 'active' : '', offline ? 'offline' : ''].join(' ').trim(),
      onclick: () => selectSource(s.id),
    }, [
      el('div', { class: 'src-row' }, [
        el('span', { class: 'src-name', text: s.name }),
        offline ? el('span', { class: 'src-dot', title: '文件夹不可达' }) : null,
      ].filter(Boolean)),
      el('div', {
        class: 'src-meta',
        text: `${s.itemCount} 条${s.missingCount ? ` · ${s.missingCount} 缺失` : ''} · ${s.totalBytes ? humanSize(s.totalBytes) : '空'}`,
      }),
    ]));
  }
}

/** id 传 null = 总览。 */
function selectSource(id) {
  admin.current = id === null ? null : ((admin.service?.sources || []).find((s) => s.id === id) || null);
  renderAdminSources();
  renderAdminStats();
  renderAdminDetail();
}

// ── 源详情 ──────────────────────────────────────────────────

function renderAdminDetail() {
  const box = $('#admin-detail');
  box.replaceChildren();

  const s = admin.current;
  if (!s) {
    renderOverview(box);
    return;
  }

  box.append(el('div', { class: 'admin-head' }, [
    el('h3', { text: s.name }),
    el('code', { class: 'slug-badge', text: `/s/${s.slug}` }),
    el('a', {
      class: 'btn ghost tiny portal-link',
      href: `/s/${s.slug}/`, target: '_blank', rel: 'noopener',
      text: '打开库页',
    }),
  ]));

  // ── 基本信息 ──
  const nameInput = el('input', { id: 'adm-name', value: s.name });
  const slugInput = el('input', { id: 'adm-slug', value: s.slug });
  const tokenInput = el('input', { id: 'adm-token', type: 'password', value: s.token || '' });
  const levelSelect = el('select', { id: 'adm-level' }, [
    el('option', { value: 'full', text: '可写 — 标题、标签、收藏、进度' }),
    el('option', { value: 'whitelist', text: '受限 — 收藏 / 进度 / 标签' }),
    el('option', { value: 'none', text: '只读' }),
  ]);
  levelSelect.value = s.virtualWrite;

  const note = el('p', { class: 'hint', id: 'adm-note' });

  box.append(el('div', { class: 'card-block' }, [
    el('h4', { text: '基本信息' }),
    el('div', { class: 'field-grid' }, [
      el('label', {}, [document.createTextNode('名称'), nameInput]),
      el('label', { title: '改动后已配对的设备需要重新扫码' }, [
        document.createTextNode('源标识'),
        slugInput,
      ]),
    ]),
    el('label', {}, [document.createTextNode('访问令牌'), tokenInput]),
    el('div', { class: 'btn-row' }, [
      el('button', { class: 'btn ghost tiny', text: '显示', onclick: () => {
        tokenInput.type = tokenInput.type === 'password' ? 'text' : 'password';
      } }),
      el('button', { class: 'btn ghost tiny', text: '复制', onclick: () => copyText(tokenInput.value) }),
      el('button', { class: 'btn ghost tiny', text: '重新生成', onclick: () => {
        tokenInput.value = newToken();
        tokenInput.type = 'text';
        note.textContent = '新令牌保存后生效，旧令牌立刻失效。';
      } }),
    ]),
    el('label', {}, [document.createTextNode('访问等级'), levelSelect]),
    el('div', { class: 'btn-row' }, [
      el('button', { class: 'btn', text: '保存', onclick: () => saveSource() }),
      note,
    ]),
  ]));

  // ── 访问方式 ──
  const hosts = admin.service?.hosts || [];
  const rows = hosts.map((h) => {
    const url = `http://${h.address}:${h.port}/s/${s.slug}`;
    return el('div', { class: 'addr-row' }, [
      el('span', { class: `kind ${h.kind}`, text: KIND_NAME[h.kind] || h.kind }),
      el('code', { text: url }),
      el('button', { class: 'btn ghost tiny', text: '复制', onclick: () => copyText(url) }),
    ]);
  });

  if (!rows.length) {
    rows.push(el('p', { class: 'hint', text: '未检测到局域网地址。' }));
  }

  box.append(el('div', { class: 'card-block' }, [
    el('h4', { text: '访问方式' }),
    el('div', { class: 'btn-row' }, [
      el('button', { class: 'btn', text: '配对二维码', onclick: () => openQr(null, s) }),
      el('span', { class: 'hint', text: `端口 ${admin.service?.port ?? ''}` }),
    ]),
    el('div', { class: 'addr-list' }, rows),
    // 公网地址是服务器级的（所有源共用同一个出口），已经挪到「总览」里改。
    el('p', { class: 'hint', text: '公网地址在「总览」里统一设置。' }),
  ]));

  // ── 文件夹来源 ──
  const folders = s.folders || [];
  const folderRows = folders.map((f) => {
    const meta = [
      f.layout === 'auto' ? '自动识别' :
        f.layout === 'dual' ? 'dual · 双流' : 'single · 单流',
      f.reachable ? null : '不可达',
    ].filter(Boolean).join(' · ');
    return el('div', { class: 'folder-row' }, [
      el('div', { class: 'folder-path' }, [
        el('code', { text: f.root }),
        el('div', { class: 'folder-meta', text: meta }),
      ]),
      el('span', {
        class: f.reachable ? 'badge ok' : 'badge missing',
        text: f.reachable ? '可达' : '不可达',
      }),
      el('button', {
        class: 'btn ghost tiny',
        text: '移除',
        onclick: () => {
          if (!window.confirm(`移除文件夹「${f.root}」？其条目将从库中清除，磁盘文件不受影响。`)) return;
          removeFolder(s.id, f.id);
        },
      }),
    ]);
  });

  if (!folders.length) {
    folderRows.push(el('p', { class: 'hint', text: '还没有文件夹，添加后才能扫描出内容。' }));
  }

  const rescanBtn = el('button', { class: 'btn ghost tiny', text: '重新扫描' });
  rescanBtn.onclick = () => rescan(s.id, rescanBtn);

  const folderPath = el('input', {
    placeholder: '视频目录绝对路径，如 D:/videos',
  });
  const folderLayout = el('select', {}, [
    el('option', { value: 'auto', text: '自动识别' }),
    el('option', { value: 'dual', text: 'dual · B 站缓存' }),
    el('option', { value: 'single', text: 'single · 普通视频' }),
  ]);
  const folderNote = el('span', { class: 'hint' });

  box.append(el('div', { class: 'card-block' }, [
    el('h4', { text: '文件夹来源' }),
    el('div', { class: 'addr-list' }, folderRows),
    el('div', { class: 'btn-row' }, [
      folderPath,
      folderLayout,
      el('button', {
        class: 'btn ghost tiny',
        text: '添加',
        onclick: () => addFolder(s.id, folderPath.value, folderLayout.value, folderNote),
      }),
    ]),
    folderNote,
    el('div', { class: 'btn-row' }, [
      rescanBtn,
      el('span', { class: 'hint', text: `${s.itemCount} 条 · ${s.lastScanAt ? `上次扫描 ${fmtTime(s.lastScanAt)}` : '尚未扫描'}` }),
    ]),
  ]));

  // ── 危险操作 ──
  // 删源只删登记，磁盘文件不动。语义在弹层里写清楚，这里给出入口。
  box.append(el('div', { class: 'card-block danger-block' }, [
    el('h4', { text: '删除源' }),
    el('div', { class: 'btn-row' }, [
      el('button', { class: 'btn danger', text: '删除这个源', onclick: () => openDeleteSource(s) }),
      el('span', { class: 'hint', text: '只删库里的登记，磁盘上的视频文件不会被删除。' }),
    ]),
  ]));
}

// ── 总览（服务器级）──────────────────────────────────────────

function renderOverview(box) {
  const svc = admin.service || {};
  const sources = svc.sources || [];
  const hosts = svc.hosts || [];

  box.append(el('div', { class: 'admin-head' }, [
    el('h3', { text: '总览' }),
    el('span', { class: 'slug-badge', text: '整台服务器' }),
    el('button', { class: 'btn ghost tiny portal-link', text: '刷新', onclick: () => reloadService() }),
  ]));

  // ── 服务器 ──
  const rows = hosts.map((h) => {
    const url = `http://${h.address}:${h.port}`;
    return el('div', { class: 'addr-row' }, [
      el('span', { class: `kind ${h.kind}`, text: KIND_NAME[h.kind] || h.kind }),
      el('code', { text: url }),
      el('button', { class: 'btn ghost tiny', text: '复制', onclick: () => copyText(url) }),
    ]);
  });

  if (!rows.length) rows.push(el('p', { class: 'hint', text: '未检测到局域网地址。' }));

  box.append(el('div', { class: 'card-block' }, [
    el('h4', { text: '服务器' }),
    el('div', { class: 'btn-row' }, [
      el('span', { class: 'hint', text: `名称 ${svc.name || '—'} · 端口 ${svc.port ?? '—'} · ${sources.length} 个源` }),
    ]),
    el('div', { class: 'addr-list' }, rows),
    el('label', {}, [
      document.createTextNode('公网地址'),
      el('input', {
        id: 'adm-public',
        value: svc.publicUrl || '',
        placeholder: 'https://… 或 http://1.2.3.4:8080',
      }),
    ]),
    el('div', { class: 'btn-row' }, [
      el('button', { class: 'btn ghost', text: '保存公网地址', onclick: () => savePublicUrl() }),
      el('span', { class: 'hint', text: '未做端口映射可留空；所有源共用这一个出口。' }),
    ]),
  ]));

  // ── 源汇总 ──
  const table = el('table', { class: 'src-table' }, [
    el('thead', {}, [
      el('tr', {}, [
        el('th', { text: '源' }),
        el('th', { text: '条目' }),
        el('th', { text: '缺失' }),
        el('th', { text: '占用' }),
        el('th', { text: '最近扫描' }),
        el('th', { text: '状态' }),
      ]),
    ]),
    el('tbody', {}, sources.map((s) => el('tr', {
      class: s.reachable ? '' : 'offline',
      title: '点击进入该源',
      onclick: () => selectSource(s.id),
    }, [
      el('td', {}, [
        el('div', { class: 'src-table-name', text: s.name }),
        el('code', { class: 'src-table-slug', text: `/s/${s.slug}` }),
      ]),
      el('td', { class: 'num', text: String(s.itemCount ?? 0) }),
      el('td', { class: 'num', text: String(s.missingCount ?? 0) }),
      el('td', { class: 'num', text: s.totalBytes ? humanSize(s.totalBytes) : '—' }),
      el('td', { class: 'num', text: s.lastScanAt ? fmtTime(s.lastScanAt) : '—' }),
      el('td', {}, [
        el('span', {
          class: s.reachable ? 'badge ok' : 'badge missing',
          text: s.reachable ? '可达' : '不可达',
        }),
      ]),
    ]))),
  ]);

  box.append(el('div', { class: 'card-block' }, [
    el('h4', { text: '源汇总' }),
    sources.length ? el('div', { class: 'src-table-wrap' }, [table])
      : el('p', { class: 'hint', text: '还没有源，点右上角「新建源」。' }),
    el('p', { class: 'hint', text: '点任意一行进入该源；「条目」只算文件还在的，缺失单独一列。' }),
  ]));
}

async function reloadService() {
  try {
    await loadService();
    selectSource(admin.current?.id ?? null);
  } catch (err) {
    toast(err.message, true);
  }
}

// ── 删除源 ──────────────────────────────────────────────────

function openDeleteSource(s) {
  $('#del-name').textContent = s.name;
  $('#del-with-archive').checked = false;

  const total = (s.itemCount || 0) + (s.missingCount || 0);
  const folders = (s.folders || []).length;
  $('#del-summary').textContent =
    `这个源登记了 ${folders} 个文件夹，库里有 ${total} 条条目` +
    `（可用 ${s.itemCount || 0} / 缺失 ${s.missingCount || 0}），占用 ${s.totalBytes ? humanSize(s.totalBytes) : '0'}。`;

  show('#dialog-delete-source');
}

async function confirmDeleteSource() {
  const s = admin.current;
  if (!s) return;

  const withArchive = $('#del-with-archive').checked;
  const btn = $('#btn-del-confirm');
  btn.disabled = true;

  try {
    const r = await api(`/api/v1/sources/${enc(s.id)}?withArchive=${withArchive ? 'true' : 'false'}`, {
      method: 'DELETE',
    });
    hide('#dialog-delete-source');

    const parts = [`已删除源「${s.name}」· 清掉 ${r.mediaRemoved} 条条目`];
    if (withArchive) {
      parts.push(`档案 ${r.metaRemoved} 份、收藏 ${r.favoriteItemsRemoved} 条一并删除`);
    } else if (r.metaKept || r.favoriteItemsKept) {
      parts.push(`保留 ${r.metaKept} 份档案、${r.favoriteItemsKept} 条收藏（等文件回来认领）`);
    }
    if (r.cancelledJobs) parts.push(`打断 ${r.cancelledJobs} 个扫描任务`);
    toast(parts.join('，'));

    await loadService();
    selectSource(null);
  } catch (err) {
    toast('删除失败：' + err.message, true);
  } finally {
    btn.disabled = false;
  }
}

async function addFolder(sourceId, root, layout, note) {
  if (!root || !root.trim()) {
    note.textContent = '请填写文件夹地址';
    return;
  }
  note.textContent = '';
  try {
    const result = await api(`/api/v1/sources/${enc(sourceId)}/folders`, {
      method: 'POST',
      body: { root: root.trim(), layout },
    });
    await loadService();
    selectSource(sourceId);
    toast('文件夹已添加，正在扫描…');
    ensureScanPolling();
  } catch (err) {
    note.textContent = err.message;
  }
}

async function removeFolder(sourceId, folderId) {
  try {
    const result = await api(`/api/v1/sources/${enc(sourceId)}/folders/${folderId}`, { method: 'DELETE' });
    await loadService();
    selectSource(sourceId);
    toast('文件夹已移除，正在清理条目…');
    ensureScanPolling();
  } catch (err) {
    toast(err.message, true);
  }
}

/** 与后端 SourceIdentity.NewToken 同样的格式：16 字节 base64url。 */
function newToken() {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return btoa(String.fromCharCode(...bytes))
    .replace(/=+$/, '')
    .replace(/\+/g, '-')
    .replace(/\//g, '_');
}

async function saveSource() {
  const s = admin.current;
  if (!s) return;

  const note = $('#adm-note');
  const body = {
    name: $('#adm-name').value.trim(),
    slug: $('#adm-slug').value.trim(),
    token: $('#adm-token').value.trim(),
    virtualWrite: $('#adm-level').value,
  };

  try {
    const updated = await api(`/api/v1/sources/${enc(s.id)}`, { method: 'PATCH', body });
    const slugChanged = updated.slug !== s.slug;
    await loadService();
    selectSource(updated.id);
    toast(slugChanged ? '已保存 · 源标识已变更，设备需重新扫码' : '已保存');
  } catch (err) {
    if (note) note.textContent = err.message;
    toast(err.message, true);
  }
}

async function savePublicUrl() {
  const value = $('#adm-public').value.trim();
  try {
    await api('/api/v1/services', { method: 'PATCH', body: { publicUrl: value } });
    if (admin.service) admin.service.publicUrl = value || null;
    toast(value ? '公网地址已保存' : '公网地址已清空');
    selectSource(null);
  } catch (err) {
    toast(err.message, true);
  }
}

async function rescan(sourceId, btn) {
  await startScan({ sourceId, verify: 'sample' });
}

function openQr(host, source) {
  // host 为 null = 配对码（服务端打包全部候选地址，手机自动选连通的）
  const qs = host
    ? `host=${enc(host)}&source=${enc(source.slug)}`
    : `source=${enc(source.slug)}`;
  $('#qr-img').src = BASE + `/api/v1/auth/pairing/qr?${qs}`;
  $('#qr-title').textContent = host ? `扫码连接 · ${host}` : `配对二维码 · ${source.name}`;
  $('#qr-url').textContent = `/s/${source.slug}`;
  show('#dialog-qr');
}

async function bootAdmin() {
  $('#admin-shell').hidden = false;

  if (!(await ensureAuth())) return;

  try {
    await loadService();
    $('#admin-brand').textContent = admin.service.name || 'MediaRack';
    // 落地点是「总览」而不是第一个源：用户打开控制台首先要知道整台服务器的状况，
    // 而"上次看的是哪个源"没有记住的价值。
    selectSource(null);
    ensureScanPolling();
  } catch (err) {
    toast('加载失败：' + err.message, true);
  }
}

// ── 事件绑定（管理页）──────────────────────────────────────

if (IS_ADMIN) {
  $('#btn-new-source')?.addEventListener('click', () => {
    $('#form-new-source').reset();
    $('#new-source-note').textContent = '';
    show('#dialog-new-source');
  });

  $('#btn-admin-reload')?.addEventListener('click', async () => {
    try {
      await loadService();
      selectSource(admin.current?.id ?? null);
      toast('已刷新');
    } catch (err) {
      toast(err.message, true);
    }
  });

  $('#btn-del-confirm')?.addEventListener('click', () => confirmDeleteSource());

  // 清理缺失：只删"既没有档案也没有收藏"的行。有档案的一律留着——
  // 那些是把文件夹搬回来时能自动恢复旋转/进度/收藏/合集归属的锚点。
  $('#btn-purge-missing')?.addEventListener('click', async () => {
    const sources = admin.service?.sources || [];
    const missing = sources.reduce((n, s) => n + (s.missingCount || 0), 0);
    if (!missing) {
      toast('没有缺失条目可清理');
      return;
    }

    const ok = window.confirm(
      `库里现有 ${missing} 条"位置记录"（记的是"某个源在某个路径曾经有过一条视频"，文件现在已经不在原处）。\n\n` +
      '清理会把这些位置记录全部删掉。' +
      '旋转/进度/标签/合集归属/收藏都不动——它们另存，文件搬回原处会自动生效，' +
      '搬到别处也能按指纹认回来。\n\n继续？');
    if (!ok) return;

    try {
      const r = await api('/api/v1/maintenance/purge-missing', { method: 'POST' });
      const anchors = r.anchorsKept || 0;
      toast(`已清理 ${r.deleted} 条位置记录` +
        (anchors ? `，保留 ${anchors} 份设置/收藏（等文件回来认领）` : ''));
      await refreshAll();
    } catch (err) {
      toast('清理失败：' + err.message, true);
    }
  });

  $('#form-new-source')?.addEventListener('submit', async (e) => {
    e.preventDefault();
    const data = new FormData(e.target);
    const payload = {
      name: String(data.get('name') || '').trim() || null,
      slug: String(data.get('slug') || '').trim() || null,
      token: String(data.get('token') || '').trim() || null,
    };

    const submit = e.target.querySelector('button[type=submit]');
    submit.disabled = true;
    try {
      const result = await api('/api/v1/sources', { method: 'POST', body: payload });
      hide('#dialog-new-source');
      toast(`已创建「${result.source.name}」，请在详情页添加文件夹`);
      await loadService();
      selectSource(result.source.id);
    } catch (err) {
      $('#new-source-note').textContent = err.message;
    } finally {
      submit.disabled = false;
    }
  });
}

// ── 事件绑定（库页）────────────────────────────────────────

if (!IS_ADMIN) {
  $('#btn-aggregate')?.addEventListener('click', () => setView({ sourceId: null, collectionId: null }));

  $('#btn-multiselect')?.addEventListener('click', () => setMultiSelect(!state.multiSelect));
  $('#btn-batch-exit')?.addEventListener('click', () => setMultiSelect(false));
  $('#btn-select-page')?.addEventListener('click', togglePageSelection);
  $('#btn-batch-open')?.addEventListener('click', openBatchPanel);
  $('#dialog-batch')?.querySelectorAll('[data-rotate]').forEach((btn) => {
    btn.addEventListener('click', () => batchApply({ rotation: Number(btn.dataset.rotate) }));
  });
  $('#btn-batch-hide')?.addEventListener('click', () => batchApply({ hidden: true }));
  $('#btn-batch-unhide')?.addEventListener('click', () => batchApply({ hidden: false }));
  $('#btn-batch-move')?.addEventListener('click', () => {
    const fromSelect = $('#batch-collection')?.value || '';
    const newName = $('#batch-new-collection')?.value.trim() || '';
    const key = newName ? `custom:${newName}` : fromSelect;
    if (!key) {
      toast('先在下拉里选个合集，或填一个新合集名', true);
      return;
    }
    batchApply({ collection: key }, '已移动');
  });
  $('#btn-batch-auto')?.addEventListener('click', () => batchApply({ collection: '' }, '已恢复自动归类'));

  $('#btn-scan')?.addEventListener('click', async () => {
    // 扫描按钮作用于"当前视图的源"；在聚合视图下则依次扫描全部源。
    if (state.view.sourceId) {
      await startScan({ sourceId: state.view.sourceId, verify: 'sample' });
      return;
    }

    if (state.sources.length === 0) {
      toast('这个源还没有登记文件夹', true);
      return;
    }

    for (const source of state.sources) {
      await startScan({ sourceId: source.id, verify: 'sample' });
    }
  });

  $('#btn-save')?.addEventListener('click', saveDetail);
  $('#btn-play')?.addEventListener('click', playCurrent);

  $('#btn-prev')?.addEventListener('click', () => {
    state.view.offset = Math.max(0, state.view.offset - state.view.limit);
    loadLibrary();
  });

  $('#btn-next')?.addEventListener('click', () => {
    state.view.offset += state.view.limit;
    loadLibrary();
  });

  let searchTimer = null;
  $('#search')?.addEventListener('input', (e) => {
    clearTimeout(searchTimer);
    const value = e.target.value.trim();
    searchTimer = setTimeout(() => setView({ q: value, offset: 0 }), 260);
  });

  // 同一個开关有两份：桌面在顶栏，手机在抽屉里。两边必须互相同步，
  // 否则在小屏切到隐藏视图、拉宽窗口会看到复选框是关闭的但列表还是隐藏内容。
  const hiddenBoxes = [$('#chk-hidden'), $('#chk-hidden-m')].filter(Boolean);
  for (const box of hiddenBoxes) {
    box.addEventListener('change', (e) => {
      for (const other of hiddenBoxes) other.checked = e.target.checked;
      setView({ includeHidden: e.target.checked, offset: 0 });
    });
  }

  // 「显示缺失」同样是两份（桌面 + 抽屉），互相同步。
  const missingBoxes = [$('#chk-missing'), $('#chk-missing-m')].filter(Boolean);
  for (const box of missingBoxes) {
    box.addEventListener('change', (e) => {
      for (const other of missingBoxes) other.checked = e.target.checked;
      setView({ includeMissing: e.target.checked, offset: 0 });
    });
  }

  const video = $('#video');
  video?.addEventListener('timeupdate', () => reportProgress(false));
  document.querySelector('#dialog-play [data-close]')?.addEventListener('click', stopPlayback);

  // 视频流 401 时 <video> 只会静默失败，不提示的话用户以为片子坏了。
  video?.addEventListener('error', () => {
    if (!video.currentSrc) return;
    toast('视频加载失败：如果这个源配了令牌，请先在页面登录；两者都不是就是 remux 出错了', true);
  });

  $('#btn-menu')?.addEventListener('click', () => {
    if ($('#sidebar').classList.contains('open')) closeSidebar();
    else openSidebar();
  });

  $('#sidebar-scrim')?.addEventListener('click', closeSidebar);
}

// ── 登录（两种页面共用）────────────────────────────────────

$('#form-login')?.addEventListener('submit', async (e) => {
  e.preventDefault();

  const token = $('#login-token').value.trim();
  if (!token) return;

  $('#login-note').textContent = '正在验证…';

  try {
    await login(token);
    hideLogin();
    $('#login-token').value = '';
    $('#login-note').textContent = '';
    if (IS_ADMIN) await bootAdmin();
    else await refreshAll();
  } catch (err) {
    $('#login-note').textContent = err.message;
  }
});

// ── 启动 ────────────────────────────────────────────────────

(async () => {
  if (IS_ADMIN) {
    await bootAdmin();
    return;
  }

  $('#library-shell').hidden = false;
  if (!(await ensureAuth())) return;
  ensureScanPolling();
  await refreshAll();
})().catch((err) => toast('初始化失败：' + err.message, true));
