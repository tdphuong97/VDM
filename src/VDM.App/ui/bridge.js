// Talks to the C# host: WebView2 on Windows, Photino (WebKit) on macOS. Opened in a plain browser
// (for design checks), it falls back to a mock host with believable sample data that keeps moving.
(function () {
  // Photino injects window.external.sendMessage; on Windows it also runs on WebView2, so check it first.
  const photino = !!(window.external && typeof window.external.sendMessage === 'function');
  const webview = !photino && window.chrome && window.chrome.webview;
  const listeners = {};
  const pending = new Map();
  let nextId = 1;

  function emit(event, data) {
    (listeners[event] || []).forEach((fn) => fn(data));
  }

  function receive(msg) {
    if (typeof msg === 'string') msg = JSON.parse(msg);
    if (msg && msg.id && pending.has(msg.id)) {
      const p = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) p.reject(new Error(msg.error));
      else p.resolve(msg.result);
    } else if (msg && msg.event) {
      emit(msg.event, msg.data);
    }
  }

  function send(msg) {
    if (webview) webview.postMessage(msg);
    else window.external.sendMessage(JSON.stringify(msg));
  }

  const bridge = {
    real: !!(webview || photino),
    on(event, fn) {
      (listeners[event] = listeners[event] || []).push(fn);
    },
    call(cmd, args) {
      if (!bridge.real) return Mock.call(cmd, args || {});
      const id = nextId++;
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        send({ id, cmd, args: args || {} });
      });
    },
  };

  if (webview) webview.addEventListener('message', (e) => receive(e.data));
  if (photino) window.external.receiveMessage(receive);

  // ---------- mock host ----------
  const MB = 1024 * 1024;
  const GB = 1024 * MB;
  const now = Date.now();
  const iso = (minutesAgo) => new Date(now - minutesAgo * 60000).toISOString();

  function segmentsFor(total, done, count) {
    // Evenly spread connections, each part as far along as the overall progress allows.
    const segs = [];
    const size = Math.floor(total / count);
    for (let i = 0; i < count; i++) {
      const start = i * size;
      const end = i === count - 1 ? total - 1 : start + size - 1;
      const len = end - start + 1;
      const share = Math.min(len, Math.round(len * (done / total) * (0.82 + 0.36 * ((i * 37) % 10) / 10)));
      segs.push([start, end, share, share < len ? (0.9 + (i % 4) * 0.4) * MB : 0]);
    }
    return segs;
  }

  const items = [
    { id: 'a1', kind: 'file', name: 'Win11_24H2_Vietnamese_x64.iso', category: 'archive', total: 5.82 * GB, status: 'downloading',
      url: 'https://software.download.prss.microsoft.com/dbazure/Win11_24H2_Vietnamese_x64.iso', referrer: 'https://www.microsoft.com/vi-vn/software-download/windows11',
      connections: 8, resumable: true, addedAt: iso(9), progress: 0.62 },
    { id: 'a2', kind: 'media', name: 'Big Buck Bunny - Blender Foundation (CC BY 3.0).mp4', category: 'video', total: 398 * MB, status: 'downloading',
      url: 'https://www.youtube.com/watch?v=aqz-KE-bpKQ', connections: 1, resumable: true, addedAt: iso(4), progress: 0.34, stage: 'Đang tải',
      media: { audioOnly: false, height: 1080, site: 'Youtube', duration: 635, title: 'Big Buck Bunny - Blender Foundation (CC BY 3.0)', thumbnail: '' } },
    { id: 'a3', kind: 'file', name: 'DaVinci_Resolve_19.1.3_Windows.zip', category: 'archive', total: 3.14 * GB, status: 'queued',
      url: 'https://swr.cloud.blackmagicdesign.com/DaVinciResolve/v19.1.3/DaVinci_Resolve_19.1.3_Windows.zip', connections: 8, resumable: true, addedAt: iso(3), progress: 0 },
    { id: 'a4', kind: 'file', name: 'ubuntu-24.04.3-desktop-amd64.iso', category: 'archive', total: 6.08 * GB, status: 'paused',
      url: 'https://releases.ubuntu.com/24.04.3/ubuntu-24.04.3-desktop-amd64.iso', connections: 8, resumable: true, addedAt: iso(140), progress: 0.41 },
    { id: 'a5', kind: 'file', name: 'ZaloSetup-25.10.2.exe', category: 'program', total: 212 * MB, status: 'failed',
      url: 'https://res-download-pc.zadn.vn/win/ZaloSetup-25.10.2.exe', connections: 8, resumable: true, addedAt: iso(61), progress: 0.18,
      error: 'Máy chủ từ chối (HTTP 403). Link có thể đã hết hạn, hãy bấm tải lại từ trang web.' },
    { id: 'a6', kind: 'file', name: 'Podcast Học tiếng Anh - Tập 12.mp3', category: 'audio', total: 38.4 * MB, status: 'completed',
      url: 'https://podcast.example/hoc-tieng-anh/tap-12.mp3', connections: 8, resumable: true, addedAt: iso(185), completedAt: iso(184), progress: 1 },
    { id: 'a7', kind: 'file', name: 'Bao-cao-tai-chinh-Q3-2026.pdf', category: 'document', total: 2.4 * MB, status: 'completed',
      url: 'https://cdn.congty.example/docs/Bao-cao-tai-chinh-Q3-2026.pdf', connections: 1, resumable: false, addedAt: iso(300), completedAt: iso(300), progress: 1 },
    { id: 'a8', kind: 'file', name: 'vlc-3.0.21-win64.exe', category: 'program', total: 43.2 * MB, status: 'completed',
      url: 'https://get.videolan.org/vlc/3.0.21/win64/vlc-3.0.21-win64.exe', connections: 8, resumable: true, addedAt: iso(1500), completedAt: iso(1499), progress: 1 },
    { id: 'a9', kind: 'media', name: 'Bài giảng Toán 12 - Chương 3 Nguyên hàm.mp4', category: 'video', total: 412 * MB, status: 'completed',
      url: 'https://vod.hocmai.example/hls/toan12-c3/master.m3u8', referrer: 'https://hocmai.example/khoa-hoc/toan-12', connections: 1, resumable: true,
      addedAt: iso(2900), completedAt: iso(2880), progress: 1, media: { audioOnly: false, height: 720, site: 'Generic', duration: 3120, title: 'Bài giảng Toán 12 - Chương 3 Nguyên hàm' } },
    { id: 'a10', kind: 'file', name: 'Album-anh-du-lich-Da-Lat.zip', category: 'archive', total: 1.23 * GB, status: 'completed',
      url: 'https://drive.usercontent.google.com/download?id=1Qx', connections: 8, resumable: true, addedAt: iso(4400), completedAt: iso(4390), progress: 1 },
  ];
  items.forEach((it) => {
    it.total = Math.round(it.total);
    it.done = Math.round(it.total * it.progress);
    it.folder = 'D:\\Tải về';
    if (it.kind === 'file' && it.connections > 1 && it.status !== 'completed' && it.status !== 'queued')
      it.segments = segmentsFor(it.total, it.done, it.connections);
    it.speed = it.status === 'downloading' ? (it.kind === 'file' ? 11.4 * MB : 3.2 * MB) : 0;
  });

  const state = {
    items,
    folder: { path: 'D:\\Tải về', drive: 'D:', freeBytes: 214.6 * GB, totalBytes: 931.5 * GB },
    browsers: [
      { name: 'Chrome', lastSeen: iso(2) },
      { name: 'Edge', lastSeen: null },
      { name: 'Firefox', lastSeen: null },
      { name: 'Cốc Cốc', lastSeen: null },
    ],
  };
  let settings = {
    downloadFolder: 'D:\\Tải về', connections: 8, maxParallel: 3, captureEnabled: true, askBeforeDownload: true,
    captureExtensions: ['7z', 'apk', 'exe', 'iso', 'mkv', 'mp3', 'mp4', 'msi', 'pdf', 'rar', 'zip'], captureMinSizeKb: 0,
    videoHeight: 1080, audioFormat: 'mp3', startWithWindows: false, closeToTray: true, autoUpdateTools: true,
    captureAll: true, sortByType: true, shareBandwidth: true,
  };
  const tools = { ytdlp: '2026.10.22', ffmpeg: 'n7.1.1', deno: '2.5.4', checkedAt: iso(1600) };

  function tick() {
    state.items.forEach((it) => {
      if (it.status !== 'downloading') return;
      const step = it.speed * 0.5;
      if (it.segments) {
        it.segments.forEach((s) => {
          const len = s[1] - s[0] + 1;
          if (s[2] < len) s[2] = Math.min(len, s[2] + s[3] * 0.5);
          else s[3] = 0;
        });
        it.done = it.segments.reduce((a, s) => a + s[2], 0);
      } else {
        it.done = Math.min(it.total, it.done + step);
      }
      if (it.done >= it.total) {
        it.status = 'completed';
        it.completedAt = new Date().toISOString();
        it.speed = 0;
      }
    });
    emit('state', state);
  }

  const Mock = {
    started: false,
    async call(cmd, args) {
      const find = () => state.items.find((i) => i.id === args.id);
      switch (cmd) {
        case 'ready':
          if (!this.started && !/static/.test(location.search)) {
            this.started = true;
            setInterval(tick, 500);
          }
          if (/empty/.test(location.search)) state.items = [];
          return { state, settings, tools, version: '1.0.0' };
        case 'pause': { const it = find(); if (it) { it.status = 'paused'; it.speed = 0; } emit('state', state); return true; }
        case 'resume': { const it = find(); if (it) { it.status = 'downloading'; it.speed = 6 * MB; it.error = null; } emit('state', state); return true; }
        case 'remove': state.items = state.items.filter((i) => i.id !== args.id); emit('state', state); return true;
        case 'pauseAll': state.items.forEach((i) => { if (i.status === 'downloading' || i.status === 'queued') { i.status = 'paused'; i.speed = 0; } }); emit('state', state); return true;
        case 'resumeAll': state.items.forEach((i) => { if (i.status === 'paused' || i.status === 'failed') { i.status = 'downloading'; i.speed = 5 * MB; i.error = null; } }); emit('state', state); return true;
        case 'getSettings': return settings;
        case 'saveSettings': settings = Object.assign({}, settings, args.settings); return settings;
        case 'chooseFolder': return 'D:\\Phim';
        case 'tools': return tools;
        case 'updateTools': return { message: 'yt-dlp đã là bản mới nhất (2026.10.22).', tools };
        case 'clipboardUrl': return null;
        case 'captureInit': return Mock.capture();
        case 'inspectFile': await wait(600); return { fileName: 'Win11_24H2_Vietnamese_x64.iso', size: 6249512960, resumable: true, mime: 'application/octet-stream' };
        case 'probeMedia': await wait(/slow/.test(location.search) ? 600000 : 900); return Mock.media;
        default: return true;
      }
    },
    capture() {
      const folders = { file: 'D:\\Tải về\\Compressed', video: 'D:\\Tải về\\Video', audio: 'D:\\Tải về\\Music' };
      if (/file/.test(location.search))
        return { folders, mode: 'file', request: { url: 'https://software.download.prss.microsoft.com/dbazure/Win11_24H2_Vietnamese_x64.iso', fileName: 'Win11_24H2_Vietnamese_x64.iso', fileSize: 6249512960, referrer: 'https://www.microsoft.com/vi-vn/software-download/windows11' }, settings };
      return { folders, mode: 'media', request: { url: 'https://www.youtube.com/watch?v=aqz-KE-bpKQ', title: 'Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film' }, settings };
    },
    media: {
      // An open movie (CC BY 3.0): sample data that can appear in public screenshots.
      title: 'Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film', thumbnail: '', duration: 635, uploader: 'Blender', site: 'Youtube',
      qualities: [
        { height: 2160, label: '2160p 4K', approxBytes: 1.31 * GB },
        { height: 1440, label: '1440p 2K', approxBytes: 682 * MB },
        { height: 1080, label: '1080p Full HD', approxBytes: 398 * MB },
        { height: 720, label: '720p HD', approxBytes: 211 * MB },
        { height: 480, label: '480p', approxBytes: 99 * MB },
        { height: 360, label: '360p', approxBytes: 62 * MB },
      ],
      audioApproxBytes: 10.1 * MB,
    },
  };
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));

  window.vdm = bridge;
})();
