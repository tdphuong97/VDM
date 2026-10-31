// The toolbar popup: is VDM running, what videos are on this tab, and a per-browser off switch.
(async function () {
  const api = VDM.api;
  const $ = (id) => document.getElementById(id);
  const [tab] = await api.tabs.query({ active: true, currentWindow: true });

  const kindLabel = { hls: 'Luồng video HLS', dash: 'Luồng video DASH', file: 'File video', audio: 'File nhạc', post: 'Video Facebook đang phát' };

  function host(url) {
    try { return new URL(url).hostname.replace(/^www\./, ''); } catch (e) { return ''; }
  }

  function sizeText(b) {
    if (!b) return '';
    const mb = b / 1048576;
    return mb >= 1024 ? (mb / 1024).toFixed(1).replace('.', ',') + ' GB' : Math.round(mb) + ' MB';
  }

  async function sendVideo(button, url, song) {
    button.disabled = true;
    const res = await api.runtime.sendMessage(song ? { type: 'vdm-popup-audio', tabId: tab.id } : { type: 'vdm-popup-video', tabId: tab.id, url });
    button.textContent = res && res.ok ? 'Đã gửi' : 'Lỗi';
    if (res && res.ok) setTimeout(() => window.close(), 700);
  }

  async function refresh() {
    const s = await api.runtime.sendMessage({ type: 'vdm-status', tabId: tab ? tab.id : null });
    $('dot').className = 'dot ' + (s && s.running ? 'on' : 'off');
    $('status').textContent = s && s.running
      ? `Đã kết nối với VDM trên máy`
      : 'Phần mềm VDM chưa chạy. Mở VDM rồi thử lại.';
    $('open').disabled = !(s && s.running);
    $('retry').hidden = !!(s && s.running);
    $('capture').checked = !(s && s.paused);

    const list = $('mediaList');
    list.innerHTML = '';
    const rows = [];
    const song = s && s.nowPlaying;
    if (song) rows.push({ title: song.title || 'Bài đang phát', sub: [song.artist, 'Bài đang phát'].filter(Boolean).join(' · '), url: song.src, song: true });
    if (s && s.videoPage) rows.push({ title: tab.title, sub: host(tab.url), url: null });
    const found = (s && s.media) || [];
    const playing = found.filter((m) => m.kind === 'post').pop(); // of the Facebook videos seen, the one playing
    for (const m of found) if ((!song || m.url !== song.src) && (m.kind !== 'post' || m === playing)) rows.push({ title: kindLabel[m.kind] || 'Video', sub: [host(m.url), sizeText(m.size)].filter(Boolean).join(' · '), url: m.url });
    $('videoBlock').hidden = rows.length === 0;
    for (const r of rows.slice(0, 6)) {
      const li = document.createElement('li');
      li.innerHTML = '<div class="what"><b></b><small></small></div><button class="btn primary">Tải</button>';
      li.querySelector('b').textContent = r.title;
      li.querySelector('small').textContent = r.sub;
      const b = li.querySelector('button');
      b.disabled = !(s && s.running);
      b.addEventListener('click', () => sendVideo(b, r.url, r.song));
      list.appendChild(li);
    }
  }

  // The on-page button: size, and forgetting where it was dragged.
  async function loadButton() {
    const saved = (await api.storage.local.get('vdmButton')).vdmButton || {};
    const size = VDM.BUTTON_SIZES[saved.size] ? saved.size : VDM.DEFAULT_BUTTON.size;
    document.querySelectorAll('#sizes [data-size]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.size === size)));
    return saved;
  }
  $('sizes').addEventListener('click', async (e) => {
    const b = e.target.closest('[data-size]');
    if (!b) return;
    const saved = await loadButton();
    await api.storage.local.set({ vdmButton: Object.assign({}, VDM.DEFAULT_BUTTON, saved, { size: b.dataset.size }) });
    loadButton();
  });
  $('resetPos').addEventListener('click', async () => {
    const saved = await loadButton();
    await api.storage.local.set({ vdmButton: Object.assign({}, VDM.DEFAULT_BUTTON, { size: saved.size || VDM.DEFAULT_BUTTON.size }) });
    $('resetPos').textContent = 'Đã đưa nút về chỗ cũ';
  });
  loadButton();

  $('capture').addEventListener('change', (e) => api.runtime.sendMessage({ type: 'vdm-pause', paused: !e.target.checked }));
  $('open').addEventListener('click', async () => { await api.runtime.sendMessage({ type: 'vdm-show' }); window.close(); });
  $('retry').addEventListener('click', refresh);
  refresh();
})();
