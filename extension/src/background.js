// VDM extension, background: catches downloads, notices video streams, and hands both to the
// VDM app over http://localhost. Works as a Chrome/Edge/Cốc Cốc service worker and a Firefox
// background script.
if (typeof importScripts === 'function') importScripts('shared.js');

const api = VDM.api;
const PORTS = [47913, 47914, 47915, 47916, 47917];
const BROWSER = detectBrowser();
const state = { port: null, config: null, at: 0, skipUntil: 0 };

function detectBrowser() {
  const ua = navigator.userAgent;
  if (/Firefox\//.test(ua)) return 'Firefox';
  if (/Edg\//.test(ua)) return 'Edge';
  if (/coc_coc_browser|CocCoc/i.test(ua)) return 'Cốc Cốc';
  return 'Chrome';
}

// ---------- talking to the app ----------
async function request(port, path, init, timeout) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeout || 1500);
  try {
    const res = await fetch(`http://localhost:${port}${path}`, {
      ...init,
      headers: { 'X-VDM': '1', ...(init && init.body ? { 'Content-Type': 'application/json' } : {}) },
      signal: ctrl.signal,
      credentials: 'omit',
      cache: 'no-store',
    });
    return res;
  } finally {
    clearTimeout(timer);
  }
}

/** Finds the running app and reads its capture settings; null when VDM is not running. */
async function ping(force) {
  if (!force && state.config && Date.now() - state.at < 20000) return state.config;
  const order = state.port ? [state.port, ...PORTS.filter((p) => p !== state.port)] : PORTS;
  for (const port of order) {
    try {
      const res = await request(port, `/ping?browser=${encodeURIComponent(BROWSER)}`, { method: 'GET' }, 800);
      if (!res.ok) continue;
      const json = await res.json();
      if (json.app !== 'VDM') continue;
      Object.assign(state, { port, config: json, at: Date.now() });
      setTitle(true);
      followApp(json.version);
      return json;
    } catch (e) { /* try the next port */ }
  }
  Object.assign(state, { port: null, config: null, at: 0 });
  setTitle(false);
  return null;
}

async function send(path, body) {
  const cfg = await ping();
  if (!cfg) return false;
  try {
    const res = await request(state.port, path, { method: 'POST', body: JSON.stringify(body) }, 3000);
    return res.ok;
  } catch (e) {
    state.config = null;
    return false;
  }
}

function newerVersion(a, b) {
  const pa = String(a).split('.').map(Number);
  const pb = String(b).split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d > 0;
  }
  return false;
}

/**
 * VDM updated itself, and the extension folder it installs with it: load the new files, which the
 * browser does not do on its own for an unpacked extension. Once per version, in case this copy was
 * loaded from some other folder that did not change.
 */
async function followApp(appVersion) {
  if (!appVersion || !newerVersion(appVersion, api.runtime.getManifest().version)) return;
  const { reloadedFor } = await api.storage.local.get('reloadedFor');
  if (reloadedFor === appVersion) return;
  await api.storage.local.set({ reloadedFor: appVersion });
  api.runtime.reload();
}

function setTitle(connected) {
  const action = api.action || api.browserAction;
  if (action) action.setTitle({ title: connected ? 'VDM đang bắt link' : 'VDM chưa chạy trên máy' });
}

async function paused() {
  const { paused } = await api.storage.local.get('paused');
  return !!paused;
}

// ---------- cookies: the app fetches with the browser's login ----------
async function cookieHeader(url) {
  try {
    const list = await api.cookies.getAll({ url });
    return list.map((c) => `${c.name}=${c.value}`).join('; ');
  } catch (e) {
    return '';
  }
}

async function cookieList(url) {
  if (VDM.isYouTube(url)) return null;
  try {
    const list = await api.cookies.getAll({ url });
    return list.map((c) => ({
      name: c.name, value: c.value, domain: c.domain, path: c.path, secure: c.secure,
      hostOnly: c.hostOnly, expirationDate: c.expirationDate,
    }));
  } catch (e) {
    return null;
  }
}

// ---------- catching downloads ----------
function extensionOf(name) {
  const m = /\.([a-z0-9]{1,10})$/i.exec(name || '');
  return m ? m[1].toLowerCase() : '';
}

function baseName(path) {
  return (path || '').split(/[\\/]/).pop();
}

function urlName(url) {
  try { return decodeURIComponent(new URL(url).pathname.split('/').pop() || ''); } catch (e) { return ''; }
}

/** Matches an extension against the list, where IDM-style entries such as R0* and R1* are patterns. */
function listed(ext, list) {
  return list.some((p) => {
    if (!/[*?]/.test(p)) return p === ext;
    const re = new RegExp('^' + p.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.') + '$');
    return re.test(ext);
  });
}

function shouldCatch(item, cfg) {
  if (!cfg || !cfg.capture || !cfg.capture.enabled) return false;
  if (Date.now() < state.skipUntil) return false; // Alt held on the click
  if (item.incognito) return false;
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return false; // blob: and data: links only exist in the page
  const name = baseName(item.filename) || urlName(url);
  const ext = extensionOf(name);
  // "Save page as" writes the page with its pictures; fetched again by VDM it would lose them.
  if (/^(html?|xhtml|mhtml?|shtml)$/.test(ext) || /^(text\/html|multipart\/related)/.test(item.mime || '')) return false;
  if (!cfg.capture.all && !listed(ext, cfg.capture.extensions)) return false;
  const size = item.fileSize > 0 ? item.fileSize : item.totalBytes;
  if (cfg.capture.minSizeKb > 0 && size > 0 && size < cfg.capture.minSizeKb * 1024) return false;
  return true;
}

async function handOver(item) {
  const url = item.finalUrl || item.url;
  const size = item.fileSize > 0 ? item.fileSize : item.totalBytes > 0 ? item.totalBytes : null;
  return send('/download', {
    url,
    referrer: item.referrer || '',
    userAgent: navigator.userAgent,
    cookieHeader: await cookieHeader(url),
    fileName: baseName(item.filename) || urlName(url) || null,
    fileSize: size,
    mime: item.mime || null,
  });
}

async function cancelInBrowser(item) {
  try { await api.downloads.cancel(item.id); } catch (e) { /* already gone */ }
  try { await api.downloads.erase({ id: item.id }); } catch (e) { /* fine */ }
}

if (api.downloads.onDeterminingFilename) {
  // Chromium: the name, size and type are known here, before anything is written to disk.
  api.downloads.onDeterminingFilename.addListener((item, suggest) => {
    (async () => {
      if (!(await paused())) {
        const cfg = await ping();
        if (shouldCatch(item, cfg)) {
          await cancelInBrowser(item);
          if (await handOver(item)) return;
          // The app vanished between ping and hand-over: let the browser download after all.
          state.skipUntil = Date.now() + 4000;
          api.downloads.download({ url: item.finalUrl || item.url });
          return;
        }
      }
      suggest();
    })();
    return true;
  });
} else {
  // Firefox has no onDeterminingFilename; it reports the name when the download is created.
  api.downloads.onCreated.addListener(async (item) => {
    if (await paused()) return;
    const cfg = await ping();
    if (!shouldCatch(item, cfg)) return;
    await cancelInBrowser(item);
    try { await api.downloads.removeFile(item.id); } catch (e) { /* nothing written yet */ }
    if (!(await handOver(item))) {
      state.skipUntil = Date.now() + 4000;
      api.downloads.download({ url: item.finalUrl || item.url });
    }
  });
}

// ---------- noticing video streams on a page ----------
const media = new Map(); // tabId -> [{url, kind, size, frameUrl}]
const store = api.storage.session || null;

async function tabMedia(tabId) {
  if (!media.has(tabId) && store) {
    const saved = (await store.get('m' + tabId))['m' + tabId];
    if (saved) media.set(tabId, saved);
  }
  return media.get(tabId) || [];
}

async function saveTabMedia(tabId, list) {
  media.set(tabId, list);
  if (store) await store.set({ ['m' + tabId]: list });
}

function header(headers, name) {
  const h = (headers || []).find((x) => x.name.toLowerCase() === name);
  return h ? h.value || '' : '';
}

/**
 * Facebook and Instagram play a video as pieces of a file (bytestart=…), never a whole file to fetch.
 * On Facebook the piece's efg parameter, base64 JSON, names the video: its watch page, for yt-dlp.
 */
function facebookVideo(url) {
  try {
    const efg = new URL(url).searchParams.get('efg');
    // Read as text: the id has more digits than a JavaScript number keeps.
    const id = efg && /"video_id"\s*:\s*"?(\d{5,})/.exec(atob(efg.replace(/-/g, '+').replace(/_/g, '/')));
    return id ? 'https://www.facebook.com/watch/?v=' + id[1] : null;
  } catch (e) {
    return null;
  }
}

function classify(d) {
  const type = header(d.responseHeaders, 'content-type').toLowerCase();
  let path = '';
  try { path = new URL(d.url).pathname.toLowerCase(); } catch (e) { return null; }
  if (/googlevideo\.com|\.ytimg\.com/.test(d.url)) return null; // YouTube goes by page address
  if (/[?&]bytestart=/.test(d.url)) {
    const from = d.documentUrl || d.initiator || d.originUrl || '';
    return /^https:\/\/([\w-]+\.)?facebook\.com/.test(from) && facebookVideo(d.url) ? 'post' : null;
  }
  if (type.includes('mpegurl') || path.endsWith('.m3u8')) return 'hls';
  if (type.includes('dash+xml') || path.endsWith('.mpd')) return 'dash';
  if (/^(video|audio)\//.test(type) && !/mp2t|iso\.segment/.test(type) && !/\.(ts|m4s|aac)$/.test(path)) {
    const len = Number(header(d.responseHeaders, 'content-length')) || 0;
    const range = header(d.responseHeaders, 'content-range');
    const total = Number((/\/(\d+)$/.exec(range) || [])[1]) || len;
    if (total >= 1024 * 1024) return type.startsWith('audio/') ? 'audio' : 'file';
  }
  return null;
}

/** Video found on the tab; a song's file alone does not put a button on every video ad. */
const videoCount = (list) => list.filter((m) => m.kind !== 'audio').length;

function cleanName(text) {
  return (text || '').replace(/[\\/:*?"<>|\u0000-\u001f]+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 150);
}

/** Sends the song an <audio> player is playing, named "Title - Artist" as the page shows it. */
async function downloadTrack(tab, track) {
  const ext = extensionOf(urlName(track.src)) || 'mp3';
  const base = cleanName([track.title, track.artist].filter(Boolean).join(' - ')) || cleanName(urlName(track.src).replace(/\.[^.]+$/, '')) || 'bai-hat';
  return send('/download', {
    url: track.src,
    referrer: track.pageUrl || (tab && tab.url) || '',
    userAgent: navigator.userAgent,
    cookieHeader: await cookieHeader(track.src),
    fileName: base + '.' + ext,
  });
}

async function askNowPlaying(tabId) {
  try {
    return (await api.tabs.sendMessage(tabId, { type: 'vdm-now-playing' })) || null;
  } catch (e) {
    return null; // no frame is playing a song
  }
}

api.webRequest.onHeadersReceived.addListener(
  (d) => {
    if (d.tabId < 0) return;
    const kind = classify(d);
    if (!kind) return;
    (async () => {
      const list = await tabMedia(d.tabId);
      const url = kind === 'post' ? facebookVideo(d.url) : d.url;
      const known = list.findIndex((m) => m.url === url);
      if (known >= 0) {
        // The video playing now goes last, where "the one playing" is looked up.
        if (kind === 'post') { list.push(list.splice(known, 1)[0]); await saveTabMedia(d.tabId, list); }
        return;
      }
      const len = Number(header(d.responseHeaders, 'content-length')) || null;
      list.push({ url, kind, size: kind === 'file' ? len : null, frameUrl: d.documentUrl || d.initiator || null, at: Date.now() });
      await saveTabMedia(d.tabId, list.slice(-12));
      api.tabs.sendMessage(d.tabId, { type: 'vdm-media', count: videoCount(list) }).catch(() => {});
    })();
  },
  { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other', 'object'] },
  ['responseHeaders'],
);

api.tabs.onUpdated.addListener((tabId, info) => {
  if (info.url) saveTabMedia(tabId, []);
});
api.tabs.onRemoved.addListener((tabId) => {
  media.delete(tabId);
  if (store) store.remove('m' + tabId);
});

/**
 * Picks what to send for a video the user clicked: the post it sits in on a social feed, a known
 * site's page, the Facebook video playing, a stream, or a file.
 */
async function downloadVideo(tab, frameUrl, title, src, postUrl) {
  const pageUrl = tab.url;
  const common = { pageUrl, title: title || tab.title, referrer: frameUrl || pageUrl, userAgent: navigator.userAgent };
  // A feed's title ("Facebook") is not the video's; yt-dlp finds the real one.
  if (postUrl && VDM.videoPage(postUrl)) return send('/media', { ...common, url: postUrl, pageUrl: postUrl, title: null, cookies: await cookieList(postUrl) });
  const page = VDM.videoPage(frameUrl) ? frameUrl : VDM.videoPage(pageUrl) ? pageUrl : null;
  if (page) {
    const watch = page.replace(/youtube(-nocookie)?\.com\/embed\/([\w-]{11}).*/, 'youtube.com/watch?v=$2');
    return send('/media', { ...common, url: watch, pageUrl: VDM.isYouTube(pageUrl) ? watch : pageUrl, cookies: await cookieList(watch) });
  }
  const found = await tabMedia(tab.id);
  const playing = found.filter((m) => m.kind === 'post').pop();
  if (playing) return send('/media', { ...common, url: playing.url, pageUrl: playing.url, title: null, cookies: await cookieList(playing.url) });
  const stream = found.find((m) => m.kind === 'hls') || found.find((m) => m.kind === 'dash');
  if (stream) return send('/media', { ...common, url: stream.url, referrer: stream.frameUrl || common.referrer, cookies: await cookieList(stream.url) });
  const file = (src && /^https?:/.test(src) && src) || (found.find((m) => m.kind === 'file') || {}).url;
  if (file) {
    const ext = extensionOf(urlName(file)) || 'mp4';
    const name = (title || tab.title || 'video').replace(/[\\/:*?"<>|]+/g, ' ').trim().slice(0, 150) + '.' + ext;
    return send('/download', { url: file, referrer: common.referrer, userAgent: navigator.userAgent, cookieHeader: await cookieHeader(file), fileName: name });
  }
  return send('/media', { ...common, url: pageUrl, cookies: await cookieList(pageUrl) });
}

// ---------- messages from the page and the popup ----------
api.runtime.onMessage.addListener((msg, sender, reply) => {
  (async () => {
    switch (msg && msg.type) {
      case 'vdm-skip':
        state.skipUntil = Date.now() + 4000;
        return { ok: true };
      case 'vdm-has-media': {
        const tab = sender.tab;
        return { count: tab ? videoCount(await tabMedia(tab.id)) : 0, running: !!(await ping()) };
      }
      case 'vdm-video': {
        if (!(await ping())) return { ok: false, reason: 'app' };
        return { ok: await downloadVideo(sender.tab, msg.frameUrl, msg.title, msg.src, msg.postUrl) };
      }
      case 'vdm-audio': {
        if (!(await ping())) return { ok: false, reason: 'app' };
        return { ok: await downloadTrack(sender.tab, msg) };
      }
      case 'vdm-popup-audio': {
        const tab = await api.tabs.get(msg.tabId);
        const track = await askNowPlaying(tab.id);
        if (!track) return { ok: false };
        return { ok: await downloadTrack(tab, { ...track, pageUrl: tab.url }) };
      }
      case 'vdm-status': {
        const cfg = await ping(true);
        const tab = msg.tabId != null ? await api.tabs.get(msg.tabId) : null;
        return {
          running: !!cfg, browser: BROWSER, paused: await paused(),
          videoPage: tab ? VDM.videoPage(tab.url) : false,
          media: tab ? await tabMedia(tab.id) : [],
          nowPlaying: tab ? await askNowPlaying(tab.id) : null,
        };
      }
      case 'vdm-popup-video': {
        const tab = await api.tabs.get(msg.tabId);
        if (msg.url) {
          const item = (await tabMedia(tab.id)).find((m) => m.url === msg.url);
          if (item && (item.kind === 'file' || item.kind === 'audio')) return { ok: await send('/download', { url: item.url, referrer: tab.url, userAgent: navigator.userAgent, cookieHeader: await cookieHeader(item.url), fileName: null }) };
          return { ok: await send('/media', { url: msg.url, pageUrl: tab.url, title: tab.title, referrer: (item && item.frameUrl) || tab.url, userAgent: navigator.userAgent, cookies: await cookieList(msg.url) }) };
        }
        return { ok: await downloadVideo(tab, tab.url, tab.title, null) };
      }
      case 'vdm-show':
        return { ok: await send('/show', {}) };
      case 'vdm-pause':
        await api.storage.local.set({ paused: !!msg.paused });
        return { ok: true };
    }
    return null;
  })().then(reply, () => reply(null));
  return true;
});

// ---------- right-click menu ----------
function createMenus() {
  api.contextMenus.removeAll(() => {
    api.contextMenus.create({ id: 'vdm-link', title: 'Tải bằng VDM', contexts: ['link'] });
    api.contextMenus.create({ id: 'vdm-media', title: 'Tải video này bằng VDM', contexts: ['video', 'audio'] });
    api.contextMenus.create({ id: 'vdm-page', title: 'Tải video trên trang này bằng VDM', contexts: ['page'] });
  });
}

api.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === 'vdm-link') {
    const url = info.linkUrl;
    if (VDM.videoPage(url)) {
      send('/media', { url, pageUrl: url, referrer: info.pageUrl, userAgent: navigator.userAgent, cookies: await cookieList(url) });
    } else {
      send('/download', { url, referrer: info.pageUrl || (tab && tab.url) || '', userAgent: navigator.userAgent, cookieHeader: await cookieHeader(url), fileName: null });
    }
  } else if (info.menuItemId === 'vdm-media') {
    downloadVideo(tab, info.frameUrl || info.pageUrl, tab.title, info.srcUrl);
  } else if (info.menuItemId === 'vdm-page') {
    downloadVideo(tab, info.frameUrl || info.pageUrl, tab.title, null);
  }
});

// ---------- keep the app aware of this browser ----------
api.runtime.onInstalled.addListener(() => {
  createMenus();
  api.alarms.create('vdm-ping', { periodInMinutes: 5 });
  ping(true);
});
api.runtime.onStartup.addListener(() => ping(true));
api.alarms.onAlarm.addListener((a) => { if (a.name === 'vdm-ping') ping(true); });
