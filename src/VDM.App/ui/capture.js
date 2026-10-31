// The capture dialog: confirm a caught file, or choose video quality / audio only for a video page.
(function () {
  const $ = (sel) => document.querySelector(sel);
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  const C = { mode: 'file', request: null, settings: null, folders: null, picked: false, remember: true, remembered: {}, fileCategory: null, info: null, media: null, audioOnly: false, height: null, audioFormat: 'mp3', busy: false };

  // Until the user picks a folder, it follows the type: Compressed for a .zip, Music for a song…
  function suggestFolder(path) {
    if (!C.picked && path) setFolder(path);
    renderFolderNote();
  }

  // A long path shows its end, where the folder's own name is.
  function setFolder(path) {
    const input = $('#folder');
    input.value = path;
    input.title = path;
    // Again once the layout and the font have settled, which widen the text after the first try.
    const toEnd = () => { input.scrollLeft = input.scrollWidth; };
    toEnd();
    requestAnimationFrame(toEnd);
    document.fonts.ready.then(toEnd);
  }

  function category() {
    return C.mode === 'file' ? C.fileCategory : (C.audioOnly ? 'audio' : 'video');
  }

  // A picked folder can be kept for the type; a kept one says so and can be let go.
  function renderFolderNote() {
    const cat = category();
    const kept = cat && C.remembered[cat];
    const note = $('#folderNote');
    const before = note.innerHTML;
    if (cat && C.picked) {
      note.innerHTML = `<label class="check"><input type="checkbox" id="remember" ${C.remember ? 'checked' : ''}>Lần sau tải ${esc(fmt.typeName(cat))} cũng lưu vào đây</label>`;
    } else if (kept && fmt.sameFolder(kept, $('#folder').value)) {
      note.innerHTML = `<span>Thư mục đã nhớ cho ${esc(fmt.typeName(cat))}.</span><button type="button" class="text-action" id="forget">Về thư mục mặc định</button>`;
    } else {
      note.innerHTML = '';
    }
    if (note.innerHTML !== before) fitWindow();
  }

  function fitWindow() {
    requestAnimationFrame(() => vdm.call('resize', { height: Math.ceil(document.querySelector('.cap').getBoundingClientRect().height) }).catch(() => {}));
  }

  // ---------- file ----------
  function renderFile() {
    const r = C.request;
    const info = C.info;
    const size = info ? info.size : r.fileSize;
    const parts = fmt.bytesParts(size);
    const facts = [];
    if (info) facts.push(info.resumable ? 'Tải tiếp được nếu mất mạng' : 'Máy chủ không cho tải tiếp, bị ngắt sẽ tải lại từ đầu');
    $('#body').innerHTML = `
      <div class="cap-head">${markSvg(22)}<h1>Tải file</h1><span class="from" title="${esc(r.referrer || r.url)}">từ ${esc(fmt.host(r.referrer || r.url))}</span></div>
      <div class="field"><label for="name">Tên file</label>
        <input class="input" id="name" value="${esc((info && info.fileName) || r.fileName || '')}" spellcheck="false" autocomplete="off"></div>
      <div>
        <div class="file-size num">${parts ? `${parts.value}<span class="unit">${parts.unit}</span>` : (info ? '<span class="muted" style="font-size:16px">Chưa rõ dung lượng</span>' : '<span class="skeleton" style="width:120px;height:30px"></span>')}</div>
        <div class="file-facts" style="margin-top:6px">${facts.map(esc).join(' · ') || (info ? '' : '<span class="loading-note">Đang hỏi máy chủ…</span>')}</div>
      </div>`;
    fitWindow();
  }

  async function inspectFile() {
    try {
      C.info = await vdm.call('inspectFile', { request: C.request });
      C.fileCategory = C.info.category || C.fileCategory;
      suggestFolder(C.info.folder);
    } catch (e) {
      C.info = { fileName: C.request.fileName || '', size: C.request.fileSize || -1, resumable: false, error: e.message };
    }
    const typed = $('#name') && $('#name').value;
    renderFile();
    if (typed && typed !== C.request.fileName) $('#name').value = typed;
  }

  // ---------- media ----------
  function nearestHeight(qualities, wanted) {
    const withH = qualities.filter((q) => q.height);
    if (!withH.length) return qualities[0] ? qualities[0].height : null;
    const below = withH.filter((q) => q.height <= wanted);
    return (below[0] || withH[withH.length - 1]).height;
  }

  function mp3Bytes(duration) {
    return duration ? duration * 245000 / 8 : null; // MP3 at best VBR quality averages about 245 kbit/s
  }

  function renderMedia() {
    const r = C.request;
    const m = C.media;
    const title = (m && m.title) || r.title || 'Video';
    const thumb = (m && m.thumbnail) || youtubeThumb(r.url);
    const meta = m ? [m.site && m.site !== 'Generic' ? (m.site === 'Youtube' ? 'YouTube' : m.site) : fmt.host(r.pageUrl || r.url), m.uploader, fmt.clock(m.duration)].filter(Boolean).join(' · ') : fmt.host(r.pageUrl || r.url);
    let choices;
    if (C.error) {
      choices = `<div class="state error" role="alert"><span class="why">${esc(C.error)}</span><button type="button" class="text-action" id="retry">Thử lại</button></div>`;
    } else if (C.audioOnly || (m && !m.qualities.length)) {
      // MP3 or M4A can be chosen before the site has answered; only the sizes wait for it.
      const opts = [
        { f: 'mp3', lbl: 'MP3', note: 'nghe được trên mọi máy và loa', bytes: m && mp3Bytes(m.duration) },
        { f: 'm4a', lbl: 'M4A', note: 'âm thanh gốc, không nén lại', bytes: m && m.audioApproxBytes },
      ];
      choices = `<div class="choices" role="radiogroup" aria-label="Định dạng âm thanh">${opts.map((o) => `
        <label class="choice"><input type="radio" name="fmt" value="${o.f}" ${C.audioFormat === o.f ? 'checked' : ''}>
          <span class="lbl">${o.lbl}<small>${o.note}</small></span><span class="size">${o.bytes ? 'khoảng ' + fmt.bytes(o.bytes) : '–'}</span></label>`).join('')}</div>`;
    } else if (!m) {
      // "Tải" works already: it takes the usual quality, and yt-dlp finds the streams on its own.
      choices = `<div class="loading-rows" aria-busy="true">${[1, 2, 3, 4].map(() => '<div><span class="skeleton" style="width:140px;height:14px"></span><span class="skeleton" style="width:64px;height:14px"></span></div>').join('')}</div>
        <span class="loading-note">Đang lấy danh sách chất lượng… Bấm Tải xuống ngay để lấy bản ${esc(C.height ? C.height + 'p' : 'tốt nhất')}.</span>`;
    } else {
      choices = `<div class="choices" role="radiogroup" aria-label="Chất lượng video">${m.qualities.map((q) => `
        <label class="choice"><input type="radio" name="q" value="${q.height == null ? '' : q.height}" ${q.height === C.height ? 'checked' : ''}>
          <span class="lbl">${esc(q.label)}</span><span class="size">${q.approxBytes ? 'khoảng ' + fmt.bytes(q.approxBytes) : '–'}</span></label>`).join('')}</div>`;
    }
    const hasVideo = !m || m.qualities.length > 0;
    $('#body').innerHTML = `
      <div class="media">
        ${thumb ? `<img class="thumb" src="${esc(thumb)}" alt="">` : '<div class="thumb"></div>'}
        <div><h1>${esc(title)}</h1><p>${esc(meta)}</p></div>
      </div>
      ${hasVideo ? `<div class="segmented" role="group" aria-label="Loại tải">
        <button type="button" data-kind="video" aria-pressed="${!C.audioOnly}">Video</button>
        <button type="button" data-kind="audio" aria-pressed="${C.audioOnly}">Chỉ âm thanh</button></div>` : ''}
      ${choices}`;
    $('#later').disabled = !!C.error;
    $('#go').disabled = !!C.error || C.busy;
    fitWindow();
  }

  function youtubeThumb(url) {
    const id = /(?:youtube\.com\/(?:watch\?v=|shorts\/)|youtu\.be\/)([\w-]{11})/.exec(url || '');
    return id ? `https://i.ytimg.com/vi/${id[1]}/mqdefault.jpg` : null;
  }

  async function probeMedia() {
    C.error = null;
    C.media = null;
    renderMedia();
    try {
      C.media = await vdm.call('probeMedia', { request: C.request });
      C.height = nearestHeight(C.media.qualities, C.settings.videoHeight || 1080);
      if (!C.media.qualities.length) { C.audioOnly = true; suggestFolder(C.folders.audio); }
    } catch (e) {
      C.error = e.message || 'Không lấy được thông tin video.';
    }
    renderMedia();
  }

  // ---------- shared ----------
  async function submit(start) {
    if (C.busy) return;
    C.busy = true;
    $('#go').disabled = true;
    const remember = C.picked && C.remember;
    try {
      if (C.mode === 'file') {
        await vdm.call('confirmFile', { fileName: $('#name').value.trim(), folder: $('#folder').value, start, remember, size: C.info ? C.info.size : C.request.fileSize });
      } else {
        await vdm.call('confirmMedia', { audioOnly: C.audioOnly, height: C.height, audioFormat: C.audioFormat, folder: $('#folder').value, start, remember });
      }
    } catch (e) {
      C.busy = false;
      $('#go').disabled = false;
    }
  }

  async function start() {
    document.querySelectorAll('[data-icon]').forEach((el) => { el.outerHTML = icon(el.dataset.icon); });
    const init = await vdm.call('captureInit');
    C.mode = init.mode;
    C.request = init.request;
    C.settings = init.settings;
    C.audioFormat = init.settings.audioFormat || 'mp3';
    C.audioOnly = !!init.audioOnly;
    C.height = init.settings.videoHeight || 1080;
    C.folders = init.folders || {};
    C.remembered = init.remembered || {};
    C.fileCategory = init.fileCategory || null;
    setFolder((C.mode === 'file' ? C.folders.file : C.folders[C.audioOnly ? 'audio' : 'video']) || init.settings.downloadFolder);
    renderFolderNote();

    $('#cap').addEventListener('submit', (e) => { e.preventDefault(); submit(true); });
    $('#later').addEventListener('click', () => submit(false));
    $('#cancel').addEventListener('click', () => vdm.call('close'));
    $('#changeFolder').addEventListener('click', async () => {
      const p = await vdm.call('chooseFolder', { current: $('#folder').value });
      if (p) { setFolder(p); C.picked = true; renderFolderNote(); }
    });
    $('#folderNote').addEventListener('change', (e) => { if (e.target.id === 'remember') C.remember = e.target.checked; });
    $('#folderNote').addEventListener('click', async (e) => {
      if (e.target.id !== 'forget') return;
      const cat = category();
      const path = await vdm.call('forgetFolder', { category: cat });
      delete C.remembered[cat];
      if (cat === 'audio' || cat === 'video') C.folders[cat] = path;
      setFolder(path);
      renderFolderNote();
      $('#changeFolder').focus();
    });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') vdm.call('close'); });
    $('#body').addEventListener('click', (e) => {
      const k = e.target.closest('[data-kind]');
      if (k) {
        C.audioOnly = k.dataset.kind === 'audio';
        suggestFolder(C.folders[C.audioOnly ? 'audio' : 'video']);
        renderMedia();
        $(`[data-kind="${k.dataset.kind}"]`).focus();
      }
      if (e.target.id === 'retry') probeMedia();
    });
    $('#body').addEventListener('change', (e) => {
      if (e.target.name === 'q') C.height = e.target.value === '' ? null : Number(e.target.value);
      if (e.target.name === 'fmt') C.audioFormat = e.target.value;
    });

    if (C.mode === 'file') {
      renderFile();
      $('#name').focus();
      const name = $('#name');
      const dot = name.value.lastIndexOf('.');
      name.setSelectionRange(0, dot > 0 ? dot : name.value.length);
      inspectFile();
    } else {
      probeMedia();
      $('#go').focus();
    }
  }

  document.addEventListener('DOMContentLoaded', start);
})();
