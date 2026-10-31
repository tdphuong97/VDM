// VDM extension, in every page and frame: a "Tải video này" button over playing videos, a
// "Tải bài này" button while a song plays in an <audio> player, and Alt+click to let the
// browser keep a download. The button can be dragged anywhere and comes in three sizes.
(function () {
  if (window.__vdmContent) return;
  window.__vdmContent = true;

  const api = VDM.api;
  const MIN_W = 240;
  const MIN_H = 135;
  let hasMedia = false;
  let dismissed = false;
  let mode = null;      // 'video' over a video, 'audio' in the corner while a song plays
  let video = null;
  let track = null;     // { src, title, artist } of the playing song
  let hoverUntil = 0;
  let showUntil = 0;
  let expandUntil = 0;
  let overButton = false;
  let menuOpen = false;
  let resetTimer = null;
  let drag = null;      // { x, y, left, top, moved } while the pointer is down on the button
  let host = null;
  let wrap = null;
  let label = null;
  let menu = null;
  let prefs = JSON.parse(JSON.stringify(VDM.DEFAULT_BUTTON));

  const ICON = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" aria-hidden="true"><path d="M8 2v8M4.5 6.5 8 10l3.5-3.5M2.5 11v2.5h11V11"/></svg>';
  const MORE = '<svg viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><circle cx="3.5" cy="8" r="1.2"/><circle cx="8" cy="8" r="1.2"/><circle cx="12.5" cy="8" r="1.2"/></svg>';
  const SIZE_NAMES = { small: 'Nhỏ', medium: 'Vừa', large: 'Lớn' };

  // ---------- preferences: size and where the user dragged the button ----------
  api.storage.local.get('vdmButton').then((r) => { if (r && r.vdmButton) prefs = merge(r.vdmButton); applySize(); update(); }).catch(() => {});
  api.storage.onChanged.addListener((changes, area) => {
    if (area === 'local' && changes.vdmButton) { prefs = merge(changes.vdmButton.newValue || {}); applySize(); update(); }
  });

  function merge(saved) {
    const d = VDM.DEFAULT_BUTTON;
    return {
      size: VDM.BUTTON_SIZES[saved.size] ? saved.size : d.size,
      video: Object.assign({}, d.video, saved.video),
      audio: Object.assign({}, d.audio, saved.audio),
    };
  }

  function savePrefs() {
    api.storage.local.set({ vdmButton: prefs }).catch(() => {});
  }

  // ---------- the button ----------
  function build() {
    host = document.createElement('vdm-video-button');
    host.style.cssText = 'position:fixed;top:0;left:0;z-index:2147483647;display:none;';
    const root = host.attachShadow({ mode: 'open' });
    root.innerHTML = `<style>
      :host { all: initial; }
      .wrap { --h: 32px; --font: 14px; --icon: 16px; --pad: 12px;
        position: relative; display: inline-flex; align-items: stretch; height: var(--h); border-radius: 6px; background: #fff; color: #131416;
        box-shadow: 0 4px 14px rgba(0,0,0,.3); font: 500 var(--font)/var(--h) "Segoe UI", -apple-system, system-ui, sans-serif;
        opacity: 0; transform: translateY(-4px); transition: opacity .15s ease, transform .15s ease; touch-action: none; }
      .wrap.on { opacity: 1; transform: none; }
      .wrap.dragging { transition: none; cursor: grabbing; box-shadow: 0 8px 22px rgba(0,0,0,.35); }
      button { all: unset; display: inline-flex; align-items: center; gap: 8px; padding: 0 var(--pad); cursor: pointer; white-space: nowrap; }
      button:hover { background: #f2f5f6; }
      button:focus-visible { outline: 2px solid #1b6c80; outline-offset: -2px; }
      .go { border-radius: 6px 0 0 6px; }
      .go svg { width: var(--icon); height: var(--icon); color: #1b6c80; flex: none; }
      .go span { max-width: 260px; overflow: hidden; text-overflow: ellipsis; }
      .more { padding: 0 calc(var(--pad) * .6); color: #5a5e60; border-left: 1px solid #eef1f2; border-radius: 0 6px 6px 0; }
      .more svg { width: var(--icon); height: var(--icon); }
      .wrap.bad .go { color: #c4242a; }
      /* a song keeps playing for minutes: after a moment the button folds to its icon */
      .wrap.small { border-radius: calc(var(--h) / 2); }
      .wrap.small .go { padding: 0 calc(var(--pad) * .7); border-radius: calc(var(--h) / 2); }
      .wrap.small .go span, .wrap.small .more { display: none; }
      .menu { position: absolute; right: 0; top: calc(100% + 6px); min-width: 196px; padding: 6px; border-radius: 8px; background: #fff;
        box-shadow: 0 8px 24px rgba(16,24,26,.22); font: 400 13px/18px "Segoe UI", -apple-system, system-ui, sans-serif; color: #131416; }
      .menu[hidden] { display: none; }
      .menu.up { top: auto; bottom: calc(100% + 6px); }
      .menu .label { padding: 6px 8px 4px; color: #5a5e60; font-size: 12px; }
      .menu .sizes { display: flex; gap: 2px; padding: 2px; margin: 0 4px 6px; border-radius: 6px; background: #f2f5f6; }
      .menu .sizes button { flex: 1; justify-content: center; height: 26px; padding: 0 8px; border-radius: 5px; color: #5a5e60; }
      .menu .sizes button[aria-pressed="true"] { background: #fff; color: #131416; font-weight: 500; box-shadow: 0 0 0 1px #e3e8e9; }
      .menu .row { display: flex; width: 100%; box-sizing: border-box; height: 32px; padding: 0 8px; border-radius: 5px; }
      .menu hr { border: 0; border-top: 1px solid #eef1f2; margin: 4px; }
    </style>
    <div class="wrap">
      <button class="go" type="button" title="Kéo để đưa nút đi chỗ khác">${ICON}<span>Tải video này</span></button>
      <button class="more" type="button" aria-label="Tùy chỉnh nút tải" aria-haspopup="menu">${MORE}</button>
      <div class="menu" role="menu" hidden>
        <div class="label">Cỡ nút</div>
        <div class="sizes">${Object.keys(VDM.BUTTON_SIZES).map((s) => `<button type="button" data-size="${s}">${SIZE_NAMES[s]}</button>`).join('')}</div>
        <button class="row" type="button" role="menuitem" data-act="reset">Đưa nút về chỗ cũ</button>
        <hr>
        <button class="row" type="button" role="menuitem" data-act="hide">Ẩn trên trang này</button>
      </div>
    </div>`;
    wrap = root.querySelector('.wrap');
    const go = root.querySelector('.go');
    label = go.querySelector('span');
    menu = root.querySelector('.menu');
    go.addEventListener('click', onClick);
    go.addEventListener('pointerdown', startDrag);
    go.addEventListener('pointermove', moveDrag);
    go.addEventListener('pointerup', endDrag);
    go.addEventListener('pointercancel', endDrag);
    root.querySelector('.more').addEventListener('click', (e) => { e.stopPropagation(); toggleMenu(!menuOpen); });
    menu.addEventListener('click', onMenu);
    wrap.addEventListener('mouseenter', () => { overButton = true; update(); });
    wrap.addEventListener('mouseleave', () => { overButton = false; update(); });
    document.addEventListener('pointerdown', (e) => { if (menuOpen && !e.composedPath().includes(host)) toggleMenu(false); }, true);
    document.addEventListener('keydown', (e) => { if (menuOpen && e.key === 'Escape') toggleMenu(false); }, true);
    (document.body || document.documentElement).appendChild(host);
    applySize();
  }

  function applySize() {
    if (!wrap) return;
    const s = VDM.BUTTON_SIZES[prefs.size] || VDM.BUTTON_SIZES.medium;
    wrap.style.setProperty('--h', s.height + 'px');
    wrap.style.setProperty('--font', s.font + 'px');
    wrap.style.setProperty('--icon', s.icon + 'px');
    wrap.style.setProperty('--pad', s.pad + 'px');
    menu.querySelectorAll('[data-size]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.size === prefs.size)));
  }

  function toggleMenu(open) {
    menuOpen = open;
    menu.hidden = !open;
    if (open) {
      // Open upwards when there is no room below (the song button sits near the bottom).
      menu.classList.toggle('up', host.getBoundingClientRect().bottom + 160 > innerHeight);
      wrap.classList.remove('small');
    }
    update();
  }

  function onMenu(e) {
    const b = e.target.closest('button');
    if (!b) return;
    if (b.dataset.size) { prefs.size = b.dataset.size; applySize(); savePrefs(); return; }
    if (b.dataset.act === 'reset') {
      if (mode === 'audio') prefs.audio = Object.assign({}, VDM.DEFAULT_BUTTON.audio);
      else prefs.video = Object.assign({}, VDM.DEFAULT_BUTTON.video);
      savePrefs();
    }
    if (b.dataset.act === 'hide') dismissed = true;
    toggleMenu(false);
    if (dismissed) hide();
  }

  // ---------- dragging ----------
  function startDrag(e) {
    if (e.button !== 0) return;
    const r = host.getBoundingClientRect();
    drag = { x: e.clientX, y: e.clientY, left: r.left, top: r.top, moved: false, id: e.pointerId };
    // Keep the pointer from the start: a quick drag leaves the button before it has moved 5px.
    e.currentTarget.setPointerCapture(e.pointerId);
  }

  function moveDrag(e) {
    if (!drag) return;
    const dx = e.clientX - drag.x;
    const dy = e.clientY - drag.y;
    if (!drag.moved && Math.hypot(dx, dy) < 5) return; // a click, not a drag
    if (!drag.moved) {
      drag.moved = true;
      wrap.classList.add('dragging');
      toggleMenu(false);
    }
    host.style.left = drag.left + dx + 'px';
    host.style.top = drag.top + dy + 'px';
  }

  function endDrag() {
    if (!drag) return;
    const moved = drag.moved;
    drag = null;
    wrap.classList.remove('dragging');
    if (!moved) return;
    const at = { left: parseFloat(host.style.left), top: parseFloat(host.style.top) };
    const size = { width: wrap.offsetWidth, height: wrap.offsetHeight };
    if (mode === 'video' && video) prefs.video = VDM.offsetFromVideo(rectOf(video), size, at);
    else prefs.audio = VDM.offsetFromCorner(size, at, { width: innerWidth, height: innerHeight });
    savePrefs();
    // The click that ends a drag must not start a download.
    window.__vdmSkipClick = Date.now();
    update();
  }

  function rectOf(el) {
    const r = el.getBoundingClientRect();
    return { left: r.left, top: r.top, right: r.right, bottom: r.bottom };
  }

  // ---------- what to show ----------
  // In a social feed, the post each video sits in; looked up again now and then, as feeds change.
  const posts = new WeakMap();
  function postOf(v) {
    if (VDM.videoPage(location.href)) return null; // the page is the video
    const known = posts.get(v);
    if (known && Date.now() - known.at < 3000) return known.url;
    const url = VDM.postUrl(v, location.href);
    posts.set(v, { url, at: Date.now() });
    return url;
  }

  function wanted(v) {
    if (VDM.videoPage(location.href)) return true;
    if (postOf(v)) return true;
    if (hasMedia) return true;
    return /^https?:/.test(v.currentSrc || v.src || '');
  }

  function biggestVideo() {
    let best = null;
    let area = 0;
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect();
      if (r.width < MIN_W || r.height < MIN_H) continue;
      if (r.bottom < 40 || r.top > innerHeight - 40 || r.right < 40 || r.left > innerWidth - 40) continue;
      if (!wanted(v)) continue;
      if (r.width * r.height > area) { best = v; area = r.width * r.height; }
    }
    return best;
  }

  /** The song an <audio> player is playing from a real file, with the name the page shows for it. */
  function nowPlaying() {
    const a = [...document.querySelectorAll('audio')].find((x) => !x.paused && /^https?:/i.test(x.currentSrc || x.src));
    if (!a) return null;
    const meta = navigator.mediaSession && navigator.mediaSession.metadata;
    const shown = VDM.trackTitle(location.hostname, document);
    return {
      src: a.currentSrc || a.src,
      title: (meta && meta.title) || shown.title || '',
      artist: (meta && meta.artist) || shown.artist || '',
    };
  }

  function hide() {
    if (host) { wrap.classList.remove('on'); host.style.display = 'none'; toggleMenu(false); }
  }

  function setText(text) {
    if (label.textContent !== text) label.textContent = text;
  }

  function place(at) {
    host.style.left = at.left + 'px';
    host.style.top = at.top + 'px';
  }

  function update() {
    if (drag && drag.moved) return; // the pointer is placing it
    if (dismissed || document.fullscreenElement) { hide(); return; }
    const now = Date.now();
    const viewport = { width: innerWidth, height: innerHeight };
    video = biggestVideo();
    if (video) {
      mode = 'video';
      if (now > hoverUntil && now > showUntil && !overButton && !menuOpen) { hide(); return; }
      if (!host) build();
      if (!resetTimer) setText('Tải video này');
      wrap.classList.remove('small');
      host.style.display = 'block';
      place(VDM.placeOnVideo(rectOf(video), { width: wrap.offsetWidth || 160, height: wrap.offsetHeight || 32 }, prefs.video, viewport));
      requestAnimationFrame(() => wrap.classList.add('on'));
      return;
    }

    const playing = nowPlaying();
    if (!playing) { mode = null; track = null; hide(); return; }
    if (!track || track.src !== playing.src) expandUntil = now + 8000; // a new song: say so for a moment
    track = playing;
    mode = 'audio';
    if (!host) build();
    if (!resetTimer) setText(track.title ? 'Tải bài này · ' + track.title : 'Tải bài đang phát');
    wrap.classList.toggle('small', now > expandUntil && !overButton && !resetTimer && !menuOpen);
    wrap.title = track.title ? `Tải ${track.title}${track.artist ? ' - ' + track.artist : ''} bằng VDM` : 'Tải bài đang phát bằng VDM';
    host.style.display = 'block';
    place(VDM.placeInCorner({ width: wrap.offsetWidth || 200, height: wrap.offsetHeight || 32 }, prefs.audio, viewport));
    requestAnimationFrame(() => wrap.classList.add('on'));
  }

  function feedback(text, bad) {
    setText(text);
    wrap.classList.toggle('bad', !!bad);
    wrap.classList.remove('small');
    clearTimeout(resetTimer);
    resetTimer = setTimeout(() => { resetTimer = null; wrap.classList.remove('bad'); update(); }, 2600);
  }

  async function onClick(e) {
    e.preventDefault();
    e.stopPropagation();
    if (window.__vdmSkipClick && Date.now() - window.__vdmSkipClick < 400) return; // end of a drag
    feedback('Đang gửi…');
    let res = null;
    try {
      res = mode === 'audio' && track
        ? await api.runtime.sendMessage({ type: 'vdm-audio', src: track.src, title: track.title, artist: track.artist, pageUrl: location.href })
        : await api.runtime.sendMessage({ type: 'vdm-video', frameUrl: location.href, title: document.title, src: video ? video.currentSrc || video.src : null, postUrl: video ? postOf(video) : null });
    } catch (err) { /* extension reloaded */ }
    if (res && res.ok) feedback('Đã gửi sang VDM');
    else if (res && res.reason === 'app') feedback('Hãy mở phần mềm VDM', true);
    else feedback('Không gửi được', true);
    showUntil = Date.now() + 2600;
  }

  // Show while the pointer is over the video, and for a moment when one starts playing.
  document.addEventListener('mousemove', (e) => {
    if (!video) video = biggestVideo();
    if (!video) return;
    const r = video.getBoundingClientRect();
    if (e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) {
      hoverUntil = Date.now() + 2000;
      update();
    }
  }, { passive: true, capture: true });
  document.addEventListener('play', () => { showUntil = Date.now() + 4000; update(); }, true);
  addEventListener('scroll', () => requestAnimationFrame(update), { passive: true, capture: true });
  addEventListener('resize', () => requestAnimationFrame(update), { passive: true });
  setInterval(update, 500);

  // Alt+click: this one download stays with the browser.
  document.addEventListener('click', (e) => {
    if (e.altKey && e.target.closest && e.target.closest('a[href]')) api.runtime.sendMessage({ type: 'vdm-skip' }).catch(() => {});
  }, true);

  api.runtime.onMessage.addListener((msg, sender, reply) => {
    if (msg && msg.type === 'vdm-media') { hasMedia = msg.count > 0; update(); }
    if (msg && msg.type === 'vdm-now-playing') {
      const playing = nowPlaying();
      if (playing) reply(playing); // frames with nothing playing stay silent
    }
  });
  api.runtime.sendMessage({ type: 'vdm-has-media' }).then((r) => { if (r) hasMedia = r.count > 0; }).catch(() => {});
})();
