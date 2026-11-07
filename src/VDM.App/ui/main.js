// Main window: the download list, the browsers view and settings, all rendered from host state.
(function () {
  // Views that are not open are detached; lookups still reach them.
  const views = {};
  const $ = (sel, root) => {
    if (root) return root.querySelector(sel);
    return document.querySelector(sel) || Object.values(views).reduce((found, v) => found || v.querySelector(sel), null);
  };
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  // The few words that differ between Windows and macOS.
  const WORDS = {
    windows: {
      paste: 'Ctrl+V', system: 'Windows', startLabel: 'Chạy cùng Windows', startDesc: 'Mở sẵn dưới khay hệ thống mỗi khi bật máy.',
      closeLabel: 'Bấm X thì thu xuống khay hệ thống', closeDesc: 'Tắt đi thì bấm X là thoát hẳn và ngừng các file đang tải.',
      folderTip: '',
    },
    mac: {
      paste: '⌘V', system: 'Khi mở máy', startLabel: 'Mở cùng máy Mac', startDesc: 'Mở sẵn, thu nhỏ trên Dock, mỗi khi đăng nhập.',
      closeLabel: 'Bấm đóng cửa sổ thì thu xuống Dock', closeDesc: 'Tắt đi thì đóng cửa sổ là thoát hẳn và ngừng các file đang tải.',
      folderTip: ' Trong hộp chọn thư mục, bấm ⌘⇧G rồi dán đường dẫn.',
    },
  };
  let W = WORDS.windows;

  const S = {
    items: [], folder: null, browsers: [], settings: null, tools: null, extension: null, remembered: {}, update: null, updateMsg: '',
    view: 'downloads', tab: 'all', type: 'all', query: '', selected: null,
  };

  const STATUS_ORDER = { downloading: 0, queued: 1, paused: 2, failed: 3, completed: 4 };
  const TABS = [
    { id: 'all', label: 'Tất cả', test: () => true },
    { id: 'active', label: 'Đang tải', test: (i) => i.status === 'downloading' || i.status === 'queued' },
    { id: 'paused', label: 'Tạm dừng', test: (i) => i.status === 'paused' },
    { id: 'failed', label: 'Lỗi', test: (i) => i.status === 'failed' },
    { id: 'completed', label: 'Đã xong', test: (i) => i.status === 'completed' },
  ];

  // ---------- shell ----------
  function hydrateIcons(root) {
    (root || document).querySelectorAll('[data-icon]').forEach((el) => {
      el.outerHTML = icon(el.dataset.icon);
    });
  }

  function renderShell() {
    const failed = S.items.filter((i) => i.status === 'failed').length;
    const navFailed = $('#navFailed');
    navFailed.hidden = failed === 0;
    navFailed.textContent = failed;
    navFailed.setAttribute('aria-label', failed + ' file lỗi');

    const f = S.folder;
    if (f && f.totalBytes > 0) {
      const used = 1 - f.freeBytes / f.totalBytes;
      const low = f.freeBytes < 10 * 1024 * 1024 * 1024;
      $('#disk').innerHTML = `
        <div class="disk-line"><span>Ổ ${esc(f.drive)} còn trống</span><b class="num">${fmt.bytes(f.freeBytes)}</b></div>
        <div class="disk-bar${low ? ' low' : ''}" role="img" aria-label="Đã dùng ${Math.round(used * 100)}% của ${fmt.bytes(f.totalBytes)}"><span style="width:${(used * 100).toFixed(1)}%"></span></div>
        <div class="disk-path" title="${esc(f.path)}">Lưu vào ${esc(f.path)}</div>`;
    }

    const connected = S.browsers.filter((b) => b.lastSeen && Date.now() - new Date(b.lastSeen) < 15 * 60000);
    $('#conn').innerHTML = connected.length
      ? `<span class="dot on"></span>${esc(connected.map((b) => b.name).join(', '))} đã kết nối`
      : `<span class="dot"></span>Chưa kết nối trình duyệt`;
  }

  // Only the open view stays in the document, so the page has exactly one h1.
  function setView(view) {
    if (!['downloads', 'browsers', 'settings'].includes(view)) view = 'downloads';
    S.view = view;
    if (!views.downloads) document.querySelectorAll('.view').forEach((v) => { views[v.id.slice(5)] = v; });
    const sheet = $('#sheet');
    Object.entries(views).forEach(([name, el]) => {
      if (name === view) { el.hidden = false; if (!el.isConnected) sheet.appendChild(el); }
      else el.remove();
    });
    sheet.scrollTop = 0;
    document.querySelectorAll('.nav-item').forEach((a) => {
      if (a.dataset.view === view) a.setAttribute('aria-current', 'page');
      else a.removeAttribute('aria-current');
    });
    $('#topbarTitle').textContent = { downloads: 'Tải xuống', browsers: 'Trình duyệt', settings: 'Cài đặt' }[view];
    $('#sidebar').classList.remove('open');
    if (view === 'browsers') renderBrowsers();
    if (view === 'settings') renderSettings();
  }

  // ---------- downloads ----------
  function visibleItems() {
    const q = S.query.trim().toLowerCase();
    return S.items.filter((i) => {
      if (S.type !== 'all' && (i.category || 'other') !== S.type && !(S.type === 'other' && i.category === 'image')) return false;
      if (q && !(i.name.toLowerCase().includes(q) || fmt.host(i.url).includes(q) || fmt.siteName(i).toLowerCase().includes(q))) return false;
      return true;
    });
  }

  function sorted(list) {
    return list.slice().sort((a, b) => {
      const s = STATUS_ORDER[a.status] - STATUS_ORDER[b.status];
      if (s) return s;
      const ta = a.status === 'completed' ? a.completedAt : a.addedAt;
      const tb = b.status === 'completed' ? b.completedAt : b.addedAt;
      return String(tb).localeCompare(String(ta));
    });
  }

  function eta(i) {
    if (i.status !== 'downloading' || !i.speed || !(i.total > 0)) return null;
    return Math.max(0, (i.total - i.done) / i.speed);
  }

  function renderFinding() {
    const running = S.items.filter((i) => i.status === 'downloading');
    const queued = S.items.filter((i) => i.status === 'queued');
    const failed = S.items.filter((i) => i.status === 'failed').length;
    const done = S.items.filter((i) => i.status === 'completed');
    const parts = [];
    if (running.length) {
      const speed = running.reduce((a, i) => a + (i.speed || 0), 0);
      let line = `<b>${running.length} file đang tải</b>`;
      if (fmt.speed(speed)) line += `, <span class="num">${fmt.speed(speed)}</span>`;
      parts.push(line);
      const etas = running.map(eta);
      if (etas.every((e) => e != null) && !queued.length) {
        const end = new Date(Date.now() + Math.max(...etas) * 1000);
        parts.push(`xong khoảng <span class="num">${fmt.hhmm(end)}</span>`);
      } else if (queued.length) {
        parts.push(`${queued.length} file đang chờ lượt`);
      }
    } else if (queued.length) {
      parts.push(`<b>${queued.length} file đang chờ lượt</b>`);
    } else if (!failed && done.length) {
      const last = done.map((i) => i.completedAt).sort().pop();
      parts.push(`Đã xong ${done.length} file, file gần nhất lúc ${fmt.when(last)}`);
    }
    if (failed) parts.push(`<span class="bad">${failed} file lỗi cần tải lại</span>`);
    if (!S.items.length) parts.push('Chưa có file nào trong danh sách');
    $('#finding').innerHTML = parts.join(' · ');

    const canPause = S.items.some((i) => i.status === 'downloading' || i.status === 'queued');
    const canResume = S.items.some((i) => i.status === 'paused' || i.status === 'failed');
    const pa = $('#pauseAll');
    pa.disabled = !canPause && !canResume;
    pa.dataset.mode = canPause ? 'pause' : 'resume';
    pa.innerHTML = canPause || !canResume ? `${icon('pause')}Tạm dừng tất cả` : `${icon('play')}Tiếp tục tất cả`;
  }

  function renderTabs(base) {
    const tabs = $('#tabs');
    tabs.innerHTML = TABS.map((t) => {
      const n = base.filter(t.test).length;
      return `<button role="tab" data-tab="${t.id}" aria-selected="${S.tab === t.id}">${t.label}<span class="num muted">${n}</span></button>`;
    }).join('');
  }

  function thumbFor(i) {
    if (i.media && i.media.thumbnail) return i.media.thumbnail;
    const m = /(?:youtube\.com\/(?:watch\?v=|shorts\/)|youtu\.be\/)([\w-]{11})/.exec(i.url || '');
    return m ? `https://i.ytimg.com/vi/${m[1]}/mqdefault.jpg` : null;
  }

  function glyphFor(i) {
    if (i.media) return i.media.audioOnly ? 'audio' : 'video';
    return { video: 'video', audio: 'audio', archive: 'archive', program: 'program', document: 'document', image: 'image' }[i.category] || 'file';
  }

  function sourceLine(i) {
    const parts = [fmt.siteName(i)];
    if (i.media) {
      if (i.media.audioOnly) parts.push((i.media.audioFormat || 'mp3').toUpperCase());
      else parts.push(i.media.height ? `Video ${i.media.height}p` : 'Video');
      if (i.media.duration) parts.push(fmt.clock(i.media.duration));
    } else if (i.status !== 'completed' && i.connections > 1 && i.resumable) {
      parts.push(`${i.connections} kết nối`);
    }
    return parts.filter(Boolean).join(' · ');
  }

  function nameCell(i) {
    const thumb = thumbFor(i);
    const slot = thumb ? `<img src="${esc(thumb)}" alt="" loading="lazy" onerror="this.replaceWith(document.createRange().createContextualFragment(icon('${glyphFor(i)}')))">` : icon(glyphFor(i));
    return `<div class="name-cell"><span class="media-slot">${slot}</span><div class="name-text">
      <span class="name" title="${esc(i.name)}">${esc(i.name)}</span>
      <span class="sub">${esc(sourceLine(i))}</span></div></div>`;
  }

  // The connection strip: each segment at its real offset, filled as far as it has come.
  function stripModel(i) {
    const cls = i.status === 'paused' || i.status === 'queued' ? 'paused' : i.status === 'failed' ? 'failed' : '';
    if (i.segments && i.segments.length > 1 && i.total > 0) {
      return {
        cls, key: i.segments.map((s) => s[0] + '-' + s[1]).join(','),
        slots: i.segments.map((s) => ({
          left: (s[0] / i.total) * 100, width: ((s[1] - s[0] + 1) / i.total) * 100,
          fill: Math.min(100, (s[2] / (s[1] - s[0] + 1)) * 100),
        })),
      };
    }
    const p = i.total > 0 ? Math.min(100, (i.done / i.total) * 100) : 0;
    return { cls, key: 'one', slots: [{ left: 0, width: 100, fill: p }] };
  }

  function stripHtml(model, big) {
    return `<div class="strip${big ? ' big' : ''}${model.cls ? ' ' + model.cls : ''}" data-key="${model.key}">${model.slots
      .map((s) => `<div class="slot" style="left:${s.left}%;width:${s.width}%"><div class="fill" style="width:${s.fill.toFixed(2)}%"></div></div>`)
      .join('')}</div>`;
  }

  function progressText(i) {
    const pct = fmt.percent(i.done, i.total);
    switch (i.status) {
      case 'downloading': {
        if (i.stage && i.kind === 'media' && !/^Đang tải/.test(i.stage)) return esc(i.stage);
        const left = fmt.duration(eta(i), 'còn');
        return [pct != null ? `<span class="num">${pct}%</span>` : (fmt.bytes(i.done) ? `đã tải <span class="num">${fmt.bytes(i.done)}</span>` : 'Đang bắt đầu'), left].filter(Boolean).join(' · ');
      }
      case 'queued': return 'Đang chờ lượt';
      case 'paused': return pct ? `Tạm dừng ở <span class="num">${pct}%</span>` : 'Tạm dừng';
      case 'failed': return esc(i.error || 'Lỗi không rõ');
      case 'completed': return `Xong lúc ${fmt.when(i.completedAt)}`;
    }
    return '';
  }

  function progressCell(i) {
    const text = `<span class="sub${i.status === 'failed' ? ' bad' : ''}" title="${i.status === 'failed' ? esc(i.error) : ''}">${progressText(i)}</span>`;
    if (i.status === 'completed') return `<span class="sub">${progressText(i)}</span>`;
    return stripHtml(stripModel(i)) + text;
  }

  function speedCell(i) {
    const s = i.status === 'downloading' ? fmt.speed(i.speed) : null;
    if (!s) return '<span class="dash">–</span>';
    const k = s.lastIndexOf(' ');
    return `<span class="v">${s.slice(0, k)}</span><span class="unit">${s.slice(k + 1)}</span>`;
  }

  function sizeCell(i) {
    const t = fmt.bytesParts(i.total);
    const head = t ? `<span class="v">${t.value}</span><span class="unit">${t.unit}</span>` : '<span class="dash">–</span>';
    const sub = i.status !== 'completed' && i.done > 0 && i.total > 0 ? `<span class="sub num">đã tải ${fmt.bytes(i.done)}</span>` : '';
    return head + sub;
  }

  function actionCell(i) {
    const btn = {
      downloading: ['pause', 'pause', 'Tạm dừng'],
      queued: ['pause', 'pause', 'Tạm dừng'],
      paused: ['resume', 'play', 'Tiếp tục'],
      failed: ['resume', 'retry', 'Thử lại'],
      completed: ['open', 'external', 'Mở'],
    }[i.status];
    return `<div class="acts">
      <button class="btn ghost" data-act="${btn[0]}" aria-label="${btn[2]} ${esc(i.name)}">${icon(btn[1])}<span class="lbl">${btn[2]}</span></button>
      <button class="btn ghost icon" data-act="menu" aria-label="Thêm thao tác cho ${esc(i.name)}" aria-haspopup="menu">${icon('more')}</button>
    </div>`;
  }

  function detailHtml(i) {
    let left;
    if (i.kind === 'media') {
      const thumb = thumbFor(i);
      const m = i.media || {};
      left = `<div class="media-head">
        ${thumb ? `<img src="${esc(thumb)}" alt="">` : '<div class="thumb-blank"></div>'}
        <div><h3>${esc(m.title || i.name)}</h3>
          <p>${esc(sourceLine(i))}</p>
          ${i.status === 'downloading' && i.stage ? `<p class="stage">${esc(i.stage)}</p>` : ''}
        </div></div>`;
    } else if (i.segments && i.segments.length > 1 && i.status !== 'completed') {
      const active = i.segments.filter((s) => s[2] < s[1] - s[0] + 1);
      const doneCount = i.segments.length - active.length;
      left = `<div class="conn-head"><h3>${i.status === 'downloading' ? active.length + ' kết nối đang chạy' : i.segments.length + ' đoạn'}</h3>
          <span class="muted num">${fmt.bytes(i.done)} / ${fmt.bytes(i.total)}</span></div>
        ${stripHtml(stripModel(i), true)}
        <ol class="conn-list">${active.slice(0, 16).map((s, n) => `<li><span>Kết nối ${n + 1}</span>
          <span class="num">${i.status === 'downloading' ? fmt.speed(s[3]) || '–' : '–'}</span>
          <span class="num muted">còn ${fmt.bytes(s[1] - s[0] + 1 - s[2])}</span></li>`).join('')}
          ${doneCount ? `<li><span>Đoạn đã xong</span><span class="num">${doneCount}</span><span class="num muted"></span></li>` : ''}</ol>`;
    } else {
      left = `<div class="conn-head"><h3>${i.status === 'completed' ? 'Đã tải xong' : 'Một kết nối'}</h3>
        <span class="muted num">${fmt.bytes(i.status === 'completed' ? i.total : i.done) || ''}</span></div>
        ${i.status === 'completed' ? '' : stripHtml(stripModel(i), true)}
        <p class="muted" style="margin-top:12px">${i.resumable ? 'Mất mạng hay tắt máy vẫn tải tiếp được từ chỗ đang dừng.' : 'Máy chủ này không cho tải tiếp: nếu bị ngắt, file sẽ tải lại từ đầu.'}</p>`;
    }
    const facts = [
      ['Lưu tại', `<span class="val" title="${esc(i.folder)}">${esc(i.folder)}</span><button class="text-action" data-act="reveal">Mở thư mục</button>`],
      ['Link tải', `<span class="val selectable" title="${esc(i.url)}">${esc(i.url)}</span><button class="icon-action" data-act="copy" aria-label="Sao chép link">${icon('copy')}</button>`],
    ];
    if (i.referrer && fmt.host(i.referrer) !== fmt.host(i.url)) facts.push(['Trang gốc', `<span class="val">${esc(fmt.host(i.referrer))}</span>`]);
    if (i.kind === 'file') facts.push(['Tải tiếp được', i.resumable ? 'Có' : 'Không']);
    facts.push(['Thêm lúc', `<span class="num">${fmt.when(i.addedAt)}</span>`]);
    return `<div class="detail-grid"><div>${left}</div>
      <dl class="facts">${facts.map(([k, v]) => `<div class="fact"><dt>${k}</dt><dd>${v}</dd></div>`).join('')}</dl></div>`;
  }

  // Rows are keyed and patched cell by cell, so buttons keep focus and the strip animates.
  const rowCache = new Map();
  function patchRow(i) {
    let entry = rowCache.get(i.id);
    if (!entry) {
      const tr = document.createElement('tr');
      tr.className = 'item';
      tr.dataset.id = i.id;
      tr.tabIndex = -1;
      tr.innerHTML = '<td class="c-name"></td><td class="c-progress"></td><td class="c-speed n"></td><td class="c-size n"></td><td class="c-act"></td>';
      entry = { tr, cells: {}, detail: null, detailHtml: '' };
      rowCache.set(i.id, entry);
    }
    const tr = entry.tr;
    tr.classList.toggle('done', i.status === 'completed');
    tr.setAttribute('aria-selected', String(S.selected === i.id));
    const cells = { name: nameCell(i), speed: speedCell(i), size: sizeCell(i), act: actionCell(i) };
    const tds = tr.children;
    const map = { name: tds[0], speed: tds[2], size: tds[3], act: tds[4] };
    for (const k in cells) {
      if (entry.cells[k] !== cells[k]) {
        map[k].innerHTML = cells[k];
        entry.cells[k] = cells[k];
      }
    }
    patchProgress(tds[1], i, entry);

    if (S.selected === i.id) {
      if (!entry.detail) {
        entry.detail = document.createElement('tr');
        entry.detail.className = 'detail';
        entry.detail.dataset.id = i.id;
        entry.detail.innerHTML = '<td colspan="5"></td>';
      }
      const html = detailHtml(i);
      if (html !== entry.detailHtml) {
        patchDetail(entry.detail.firstChild, html, i);
        entry.detailHtml = html;
      }
    } else if (entry.detail) {
      entry.detail.remove();
      entry.detail = null;
      entry.detailHtml = '';
    }
    return entry;
  }

  function patchStrip(container, model) {
    const strip = container.querySelector('.strip');
    if (!strip || strip.dataset.key !== model.key) return false;
    strip.className = 'strip' + (strip.classList.contains('big') ? ' big' : '') + (model.cls ? ' ' + model.cls : '');
    const fills = strip.querySelectorAll('.fill');
    model.slots.forEach((s, n) => { fills[n].style.width = s.fill.toFixed(2) + '%'; });
    return true;
  }

  function patchProgress(td, i, entry) {
    const structure = i.status === 'completed' ? 'done' : 'strip';
    const text = progressText(i);
    if (entry.progressStructure === structure && structure === 'strip' && patchStrip(td, stripModel(i))) {
      const sub = td.querySelector('.sub');
      if (entry.progressText !== text) {
        sub.innerHTML = text;
        sub.className = 'sub' + (i.status === 'failed' ? ' bad' : '');
        sub.title = i.status === 'failed' ? i.error || '' : '';
      }
    } else {
      td.innerHTML = progressCell(i);
    }
    entry.progressStructure = structure;
    entry.progressText = text;
  }

  function patchDetail(td, html, i) {
    // Keep the big strip alive between ticks so its fills glide.
    if (td.firstChild && i.segments && td.querySelector('.strip.big')) {
      const tmp = document.createElement('td');
      tmp.innerHTML = html;
      const oldStrip = td.querySelector('.strip.big');
      const newStrip = tmp.querySelector('.strip.big');
      if (newStrip && oldStrip.dataset.key === newStrip.dataset.key) {
        patchStrip(oldStrip.parentNode, stripModel(i));
        newStrip.replaceWith(oldStrip);
      }
      td.replaceChildren(...tmp.childNodes);
      return;
    }
    td.innerHTML = html;
  }

  function renderDownloads() {
    renderFinding();
    const base = visibleItems();
    renderTabs(base);
    const tab = TABS.find((t) => t.id === S.tab) || TABS[0];
    const list = sorted(base.filter(tab.test));

    const totalBytes = list.reduce((a, i) => a + (i.total > 0 ? i.total : 0), 0);
    $('#total').textContent = list.length ? `${list.length} file · ${fmt.bytes(totalBytes)}` : '';

    const tbody = $('#rows');
    const seen = new Set();
    let cursor = null;
    list.forEach((i) => {
      seen.add(i.id);
      const entry = patchRow(i);
      const nextExpected = cursor ? cursor.nextSibling : tbody.firstChild;
      if (nextExpected !== entry.tr) tbody.insertBefore(entry.tr, nextExpected);
      cursor = entry.tr;
      if (entry.detail) {
        if (cursor.nextSibling !== entry.detail) tbody.insertBefore(entry.detail, cursor.nextSibling);
        cursor = entry.detail;
      }
    });
    for (const [id, entry] of rowCache) {
      if (!seen.has(id)) {
        entry.tr.remove();
        if (entry.detail) entry.detail.remove();
        rowCache.delete(id);
      }
    }

    const empty = $('#empty');
    $('#list').hidden = list.length === 0;
    empty.hidden = list.length > 0;
    if (!list.length) {
      if (!S.items.length) {
        const connected = S.browsers.some((b) => b.lastSeen);
        empty.innerHTML = `<b>Chưa có file nào.</b>
          <span>Bấm tải trên trang web là VDM tự nhận file. Có link trong tay thì dán vào bằng ${W.paste}.</span>
          ${connected ? '' : '<a class="text-action" href="#browsers">Kết nối trình duyệt với VDM</a>'}`;
      } else if (S.query) {
        empty.innerHTML = `Không có file nào khớp với “${esc(S.query)}”.`;
      } else {
        empty.innerHTML = { active: 'Không có file nào đang tải.', paused: 'Không có file nào đang tạm dừng.', failed: 'Không có file nào bị lỗi.', completed: 'Chưa có file nào tải xong.' }[S.tab] || 'Không có file nào thuộc loại này.';
      }
    }
  }

  // ---------- menus and dialogs ----------
  function closeLayer() {
    $('#layer').innerHTML = '';
  }

  function openMenu(anchor, item) {
    closeLayer();
    const done = item.status === 'completed';
    const rows = [
      done ? ['open', 'external', 'Mở file'] : null,
      ['reveal', 'folder', 'Mở thư mục chứa file'],
      ['copy', 'copy', 'Sao chép link tải'],
      item.kind === 'media' && item.pageUrl ? ['page', 'link', 'Mở trang video'] : null,
      !done ? ['restart', 'retry', 'Tải lại từ đầu'] : null,
      'hr',
      ['remove', 'close', done ? 'Xóa khỏi danh sách' : 'Hủy và xóa phần đã tải', done ? '' : 'danger'],
      done ? ['delete', 'close', 'Xóa cả file trên máy', 'danger'] : null,
    ].filter(Boolean);
    const menu = document.createElement('div');
    menu.className = 'menu';
    menu.setAttribute('role', 'menu');
    menu.innerHTML = rows.map((r) => r === 'hr' ? '<hr>' : `<button role="menuitem" data-act="${r[0]}" class="${r[3] || ''}">${icon(r[1])}${r[2]}</button>`).join('');
    $('#layer').appendChild(menu);
    const a = anchor.getBoundingClientRect();
    const m = menu.getBoundingClientRect();
    const top = a.bottom + 4 + m.height > innerHeight ? a.top - m.height - 4 : a.bottom + 4;
    menu.style.top = Math.max(8, top) + 'px';
    menu.style.left = Math.max(8, Math.min(innerWidth - m.width - 8, a.right - m.width)) + 'px';
    menu.querySelector('button').focus();
    menu.addEventListener('click', (e) => {
      const b = e.target.closest('button[data-act]');
      if (!b) return;
      closeLayer();
      act(b.dataset.act, item);
    });
    menu.addEventListener('keydown', (e) => {
      const items = [...menu.querySelectorAll('button')];
      const k = items.indexOf(document.activeElement);
      if (e.key === 'ArrowDown') { items[(k + 1) % items.length].focus(); e.preventDefault(); }
      if (e.key === 'ArrowUp') { items[(k - 1 + items.length) % items.length].focus(); e.preventDefault(); }
      if (e.key === 'Escape') { closeLayer(); anchor.focus(); }
    });
  }

  function confirmDialog({ title, body, ok, danger }) {
    return new Promise((resolve) => {
      closeLayer();
      $('#layer').innerHTML = `<div class="scrim"><div class="dialog" role="alertdialog" aria-modal="true" aria-labelledby="dlgTitle">
        <h2 id="dlgTitle">${title}</h2><p>${body}</p>
        <div class="actions"><button class="btn tall" data-r="0">Giữ lại</button><button class="btn tall ${danger ? 'primary' : 'primary'}" data-r="1">${ok}</button></div>
      </div></div>`;
      const done = (r) => { closeLayer(); resolve(r); };
      $('#layer .scrim').addEventListener('click', (e) => {
        if (e.target.classList.contains('scrim')) done(false);
        const b = e.target.closest('button[data-r]');
        if (b) done(b.dataset.r === '1');
      });
      $('#layer [data-r="1"]').focus();
      $('#layer .dialog').addEventListener('keydown', (e) => { if (e.key === 'Escape') done(false); });
    });
  }

  async function addLinkDialog(prefill) {
    closeLayer();
    const clip = prefill || (await vdm.call('clipboardUrl').catch(() => null)) || '';
    $('#layer').innerHTML = `<div class="scrim"><form class="dialog" role="dialog" aria-modal="true" aria-labelledby="addTitle" novalidate>
      <h2 id="addTitle">Thêm link tải</h2>
      <p>Link file hoặc link video YouTube, Facebook, TikTok… VDM tự nhận ra loại link.</p>
      <div class="field"><label for="addUrl">Link</label>
        <input class="input" id="addUrl" type="url" value="${esc(clip)}" placeholder="https://" autocomplete="off" spellcheck="false" aria-describedby="addErr">
        <span class="hint" id="addErr"></span></div>
      <div class="actions"><button type="button" class="btn tall" data-r="0">Hủy</button><button type="submit" class="btn primary tall">Tiếp tục</button></div>
    </form></div>`;
    const form = $('#layer form');
    const input = $('#addUrl');
    input.focus();
    input.select();
    form.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeLayer(); });
    form.querySelector('[data-r="0"]').addEventListener('click', closeLayer);
    $('#layer .scrim').addEventListener('click', (e) => { if (e.target.classList.contains('scrim')) closeLayer(); });
    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const url = input.value.trim();
      if (!/^https?:\/\/\S+\.\S+/i.test(url)) {
        $('#addErr').textContent = 'Link phải bắt đầu bằng http:// hoặc https://';
        $('#addErr').style.color = 'var(--negative)';
        input.focus();
        return;
      }
      closeLayer();
      await vdm.call('addLink', { url });
    });
  }

  async function act(action, item) {
    switch (action) {
      case 'pause': return vdm.call('pause', { id: item.id });
      case 'resume': return vdm.call('resume', { id: item.id });
      case 'open': return vdm.call('open', { id: item.id });
      case 'reveal': return vdm.call('reveal', { id: item.id });
      case 'copy': return vdm.call('copyText', { text: item.url });
      case 'page': return vdm.call('openUrl', { url: item.pageUrl });
      case 'restart':
        if (item.done > 0 && !(await confirmDialog({ title: 'Tải lại từ đầu?', body: `Phần đã tải ${esc(fmt.bytes(item.done))} của <b>${esc(item.name)}</b> sẽ bị bỏ.`, ok: 'Tải lại' }))) return;
        return vdm.call('restart', { id: item.id });
      case 'remove':
        if (item.status !== 'completed' && item.done > 0 &&
            !(await confirmDialog({ title: 'Hủy tải file này?', body: `Phần đã tải ${esc(fmt.bytes(item.done))} của <b>${esc(item.name)}</b> sẽ bị xóa khỏi máy.`, ok: 'Hủy tải và xóa', danger: true }))) return;
        if (S.selected === item.id) S.selected = null;
        return vdm.call('remove', { id: item.id, deleteFile: false });
      case 'delete':
        if (!(await confirmDialog({ title: 'Xóa hẳn file này?', body: `<b>${esc(item.name)}</b> sẽ bị xóa khỏi ${esc(item.folder)}. Không khôi phục lại được.`, ok: 'Xóa file', danger: true }))) return;
        if (S.selected === item.id) S.selected = null;
        return vdm.call('remove', { id: item.id, deleteFile: true });
    }
  }

  // ---------- browsers ----------
  const BROWSERS = [
    { name: 'Chrome', kind: 'chromium', page: 'chrome://extensions' },
    { name: 'Edge', kind: 'chromium', page: 'edge://extensions' },
    { name: 'Cốc Cốc', kind: 'chromium', page: 'coccoc://extensions' },
    { name: 'Firefox', kind: 'firefox', page: 'about:addons' },
  ];
  const openSteps = new Set();

  function renderBrowsers() {
    const seen = Object.fromEntries(S.browsers.map((b) => [b.name, b.lastSeen]));
    const connected = BROWSERS.filter((b) => seen[b.name]);
    $('#browsersSub').textContent = connected.length
      ? `${connected.map((b) => b.name).join(', ')} đã gửi link cho VDM`
      : 'Chưa trình duyệt nào kết nối với VDM';
    if (!connected.length && !openSteps.size) openSteps.add('Chrome');
    const ext = S.extension || {};
    $('#browserList').innerHTML = BROWSERS.map((b) => {
      const last = seen[b.name];
      const status = last
        ? `<span class="pill pos">Đã kết nối</span>`
        : `<span class="pill">Chưa cài</span>`;
      const meta = last ? `Lần cuối ${fmt.when(last)}` : (b.kind === 'firefox' ? 'Cần bản extension đã ký' : 'Cài một lần, dùng mãi');
      const open = openSteps.has(b.name);
      let steps = '';
      if (open && b.kind === 'chromium') {
        steps = `<ol class="steps">
          <li>Mở trang tiện ích của ${b.name}.<div class="step-actions"><button class="btn" data-bact="page" data-b="${b.name}">${icon('external')}Mở trang tiện ích</button></div></li>
          <li>Bật <b>Chế độ dành cho nhà phát triển</b> (Developer mode) ở góc trên.</li>
          <li>Bấm <b>Tải tiện ích đã giải nén</b> (Load unpacked) rồi chọn chính thư mục <b>chromium</b> này.${W.folderTip}
            <code>${esc(ext.chromium || '')}</code>
            <div class="step-actions"><button class="btn" data-bact="folder" data-b="${b.name}">${icon('folder')}Mở thư mục</button><button class="btn" data-bact="copy" data-text="${esc(ext.chromium || '')}">${icon('copy')}Sao chép đường dẫn</button></div></li>
          <li>Tải lại trang web đang mở. Dòng ${b.name} ở đây sẽ chuyển sang “Đã kết nối”.</li>
        </ol>`;
      } else if (open) {
        steps = ext.firefoxXpi
          ? `<ol class="steps"><li>Bấm nút dưới đây, Firefox sẽ hỏi có thêm VDM không, chọn <b>Thêm</b>.<div class="step-actions"><button class="btn" data-bact="xpi">${icon('external')}Cài vào Firefox</button></div></li></ol>`
          : `<ol class="steps">
              <li>Firefox chỉ giữ extension đã được Mozilla ký. Bản này chưa ký nên chỉ dùng thử được tới khi tắt Firefox.</li>
              <li>Mở <b>about:debugging</b>, chọn <b>This Firefox</b>, bấm <b>Load Temporary Add-on</b> và chọn file manifest.json trong thư mục:
                <code>${esc(ext.firefox || '')}</code>
                <div class="step-actions"><button class="btn" data-bact="folder" data-b="Firefox">${icon('folder')}Mở thư mục</button></div></li>
            </ol>`;
      }
      return `<li><div class="b-row">
          <div class="grow"><div class="b-name">${b.name}</div><div class="b-meta">${meta}</div></div>
          ${status}
          <button class="btn ghost" data-bact="toggle" data-b="${b.name}" aria-expanded="${open}">${open ? 'Ẩn hướng dẫn' : 'Hướng dẫn cài'}</button>
        </div>${steps}</li>`;
    }).join('');
  }

  // ---------- a newer VDM ----------
  const noteLines = (notes) => String(notes || '').split('\n').map((l) => l.replace(/^\s*[-*•]\s*/, '').trim()).filter(Boolean);

  // The bar over every view: what is new, then the download and the hand-over to the installer.
  function renderUpdate() {
    const u = S.update;
    const a = u && u.available;
    const bar = $('#updateBar');
    let html = '';
    if (a && u.stage === 'downloading') {
      html = `<div class="grow"><b>Đang tải VDM ${esc(a.version)}…</b><span class="num">${u.percent}%</span>
        <div class="update-progress" role="progressbar" aria-valuenow="${u.percent}" aria-valuemin="0" aria-valuemax="100" aria-label="Tải bản mới"><span style="width:${u.percent}%"></span></div></div>`;
    } else if (a && u.stage === 'installing') {
      html = `<div class="grow"><b>Đang cài VDM ${esc(a.version)}.</b><span>VDM sẽ tự đóng rồi mở lại sau ít giây, file đang tải dở được tải tiếp.</span></div>`;
    } else if (a) {
      html = `<div class="grow"><b>Có bản mới VDM ${esc(a.version)}</b>${u.error ? `<span class="bad">${esc(u.error)}</span>` : `<span>${esc(noteLines(a.notes)[0] || '')}</span>`}</div>
        ${a.page ? '<button class="btn ghost" data-uact="page">Có gì mới</button>' : ''}
        <button class="btn primary" data-uact="install">${!a.canInstall ? 'Tải bản mới' : u.error ? 'Thử lại' : 'Cập nhật ngay'}</button>`;
    }
    if (bar.innerHTML !== html) bar.innerHTML = html; // the same bar keeps its focus
    bar.hidden = !html;
  }

  function appUpdateHtml() {
    const u = S.update || {};
    const a = u.available;
    const s = S.settings;
    return `
      <div class="set-line"><div class="grow"><div class="t">Phiên bản đang dùng: VDM ${esc(u.current || '')}</div>
        <div class="d">${a ? `Có bản mới ${esc(a.version)}.` : u.checkedAt ? 'Kiểm tra lần cuối ' + fmt.when(u.checkedAt) + '.' : 'Chưa kiểm tra lần nào.'}</div></div>
        ${a ? `<button class="btn primary" data-uact="install">${a.canInstall ? 'Cập nhật ngay' : 'Tải bản mới'}</button>` : ''}</div>
      ${a && noteLines(a.notes).length ? `<div class="field"><span class="label">Có gì mới trong bản ${esc(a.version)}</span><ul class="update-notes">${noteLines(a.notes).map((l) => `<li>${esc(l)}</li>`).join('')}</ul></div>` : ''}
      <div class="set-line"><div class="grow"><label class="t" for="sUpdAuto">Tự kiểm tra bản mới mỗi ngày</label><div class="d">Có bản mới thì VDM báo, bạn bấm Cập nhật ngay là xong. Không tự cài khi bạn chưa bấm.</div></div><input type="checkbox" class="switch" id="sUpdAuto" ${s.autoCheckUpdates ? 'checked' : ''}></div>
      <div class="set-line"><button class="btn" data-sact="checkUpdate">${icon('retry')}Kiểm tra bản mới</button><span class="d" id="appUpdateMsg" aria-live="polite">${esc(S.updateMsg)}</span></div>`;
  }

  function refreshAppUpdate() {
    const box = $('#appUpdate');
    if (box) box.innerHTML = appUpdateHtml();
  }

  document.addEventListener('click', (e) => {
    const b = e.target.closest('[data-uact]');
    if (!b || !S.update || !S.update.available) return;
    if (b.dataset.uact === 'page') vdm.call('openUrl', { url: S.update.available.page });
    if (b.dataset.uact === 'install') { b.disabled = true; vdm.call('installUpdate').catch(() => { b.disabled = false; }); }
  });

  // ---------- settings ----------
  let saveTimer = null;
  function save(patch) {
    Object.assign(S.settings, patch);
    clearTimeout(saveTimer);
    saveTimer = setTimeout(async () => {
      S.settings = await vdm.call('saveSettings', { settings: S.settings });
      $('#savedStamp').textContent = 'Đã lưu lúc ' + fmt.hhmm(new Date());
      renderSettingsSub();
    }, 250);
  }

  function renderSettingsSub() {
    const s = S.settings;
    $('#settingsSub').textContent = `Lưu vào ${s.downloadFolder} · ${s.connections} kết nối mỗi file · tải cùng lúc ${s.maxParallel} file`;
  }

  // Folders picked in the download dialog and kept for their type, with a way to let each go.
  function rememberedHtml() {
    const order = ['video', 'audio', 'document', 'archive', 'program', 'image', 'other'];
    const rows = Object.entries(S.remembered || {}).sort((a, b) => order.indexOf(a[0]) - order.indexOf(b[0]));
    if (!rows.length) return '<span class="hint">Đổi thư mục trong hộp thoại tải thì VDM nhớ thư mục đó cho loại file ấy (video, nhạc, tài liệu…), lần sau tự lưu vào đó.</span>';
    const name = (cat) => { const t = fmt.typeName(cat); return t[0].toUpperCase() + t.slice(1); };
    // Keep the end of a long path, where the folder's own name is: "…\Phim\Hàn Quốc".
    const tail = (path) => {
      if (path.length <= 56) return path;
      const sep = path.includes('\\') ? '\\' : '/';
      const parts = path.split(sep);
      let out = parts.pop();
      while (parts.length && out.length + parts[parts.length - 1].length < 52) out = parts.pop() + sep + out;
      return '…' + sep + out;
    };
    return `<span class="label">Thư mục riêng đã nhớ</span>
      <ul class="kept">${rows.map(([cat, path]) => `<li><span class="k">${esc(name(cat))}</span><span class="v" title="${esc(path)}">${esc(tail(path))}</span><button class="text-action" data-forget="${esc(cat)}">Quên</button></li>`).join('')}</ul>
      <span class="hint">Các loại này lưu vào thư mục riêng thay cho thư mục ở trên. Quên đi thì về lại thư mục mặc định.</span>`;
  }

  function connectionPreview(n) {
    const fills = [92, 64, 78, 40, 88, 55, 71, 33, 60, 85, 47, 69, 52, 76, 38, 81];
    const slots = Array.from({ length: n }, (_, k) => ({ left: (k / n) * 100, width: 100 / n, fill: fills[k % fills.length] }));
    return stripHtml({ cls: '', key: 'p' + n, slots }, true);
  }

  function renderSettings() {
    const s = S.settings;
    if (!s) return;
    renderSettingsSub();
    const t = S.tools || {};
    const heights = [2160, 1440, 1080, 720, 480, 360];
    $('#settingsForm').innerHTML = `
      <section class="set-row">
        <div class="set-label"><h2>Nơi lưu file</h2><p>File tải xong nằm ở đây, video và nhạc cũng vậy.</p></div>
        <div class="set-panel">
          <div class="field"><label for="sFolder">Thư mục</label>
            <div class="joined"><input class="input" id="sFolder" value="${esc(s.downloadFolder)}" readonly><button class="btn" data-sact="folder">Đổi thư mục</button></div>
            ${S.folder ? `<span class="hint num">Ổ ${esc(S.folder.drive)} còn trống ${fmt.bytes(S.folder.freeBytes)} trong ${fmt.bytes(S.folder.totalBytes)}</span>` : ''}</div>
          <div class="set-line"><div class="grow"><label class="t" for="sSort">Xếp file vào thư mục theo loại</label>
            <div class="d">${s.sortByType ? 'Trong thư mục trên: Compressed, Programs, Documents, Video, Music, Picture. File khác để ở ngoài.' : 'Tắt: mọi file nằm chung trong thư mục trên.'}</div></div>
            <input type="checkbox" class="switch" id="sSort" ${s.sortByType ? 'checked' : ''}></div>
          ${s.sortByType ? `<div class="sort-preview" aria-label="Ví dụ">${[['Win11_24H2.iso', 'Compressed'], ['Hop-dong.pdf', 'Documents'], ['Cô Bé Ơi.mp3', 'Music'], ['setup.exe', 'Programs']].map(([f, d]) => `<span><b>${esc(f)}</b> → ${d}</span>`).join('')}</div>` : ''}
          <div class="field" id="remembered">${rememberedHtml()}</div>
        </div>
      </section>
      <section class="set-row">
        <div class="set-label"><h2>Tốc độ</h2><p>File lớn được chia thành nhiều đoạn, mỗi kết nối tải một đoạn cùng lúc.</p></div>
        <div class="set-panel">
          <div class="set-line"><div class="grow"><label class="t" for="sConnSel">Số kết nối cho mỗi file</label><div class="d">16 hoặc 32 nhanh hơn với máy chủ mạnh. Trang nào giới hạn, VDM tự dùng ít kết nối hơn.</div></div>
            <select class="input" id="sConnSel" style="width:96px">${[...new Set([1, 2, 4, 8, 16, 24, 32, s.connections])].sort((a, b) => a - b).map((n) => `<option value="${n}" ${n === s.connections ? 'selected' : ''}>${n}</option>`).join('')}</select></div>
          <div class="set-preview" id="connPreview">${connectionPreview(s.connections)}<span class="d">Một file 5,8 GB chia thành ${s.connections} đoạn, mỗi đoạn khoảng ${fmt.bytes((5.8 * 1024 ** 3) / s.connections)}</span></div>
          <div class="set-line"><div class="grow"><label class="t" for="sShare">Chia đều tốc độ cho các file đang tải</label><div class="d">Ví dụ mạng 21 MB/s, 3 file cùng tải thì mỗi file khoảng 7 MB/s. File bị máy chủ giới hạn chậm hơn phần của nó thì phần dư chia cho file khác.</div></div><input type="checkbox" class="switch" id="sShare" ${s.shareBandwidth ? 'checked' : ''}></div>
          <div class="set-line"><div class="grow"><div class="t">Số file tải cùng lúc</div><div class="d">File thứ ${s.maxParallel + 1} trở đi chờ tới lượt.</div></div>
            <div class="stepper"><button data-step="maxParallel" data-d="-1" aria-label="Bớt một file">−</button><output>${s.maxParallel}</output><button data-step="maxParallel" data-d="1" aria-label="Thêm một file">+</button></div></div>
        </div>
      </section>
      <section class="set-row">
        <div class="set-label"><h2>Bắt link từ trình duyệt</h2><p>Khi bạn bấm tải trên trang web, VDM nhận file thay cho trình duyệt.</p></div>
        <div class="set-panel">
          <div class="set-line"><div class="grow"><label class="t" for="sCapture">Tự bắt link tải</label><div class="d">Tắt đi thì trình duyệt tự tải như bình thường.</div></div><input type="checkbox" class="switch" id="sCapture" ${s.captureEnabled ? 'checked' : ''}></div>
          <div class="set-line"><div class="grow"><label class="t" for="sAsk">Hỏi trước khi tải</label><div class="d">Hiện tên file và nơi lưu để bạn đổi trước khi tải.</div></div><input type="checkbox" class="switch" id="sAsk" ${s.askBeforeDownload ? 'checked' : ''}></div>
          <div class="set-line"><div class="grow"><label class="t" for="sAll">Bắt mọi loại file</label><div class="d">Tắt đi thì chỉ bắt các đuôi file trong danh sách bên dưới.</div></div><input type="checkbox" class="switch" id="sAll" ${s.captureAll ? 'checked' : ''}></div>
          ${s.captureAll ? '' : `<div class="field"><label for="sExt">Loại file tự bắt</label>
            <textarea class="input ext-input" id="sExt" spellcheck="false" aria-describedby="sExtHint">${esc(s.captureExtensions.join(' ').toUpperCase())}</textarea>
            <span class="hint" id="sExtHint">Đuôi file cách nhau bằng dấu cách. Dấu * thay cho nhiều ký tự: R0* là .r00 tới .r09.</span></div>`}
          <div class="field"><label for="sMin">Bỏ qua file nhỏ hơn</label>
            <div class="joined" style="max-width:220px"><input class="input num" id="sMin" type="number" min="0" step="100" value="${s.captureMinSizeKb}"><button class="btn" tabindex="-1" disabled>KB</button></div></div>
        </div>
      </section>
      <section class="set-row">
        <div class="set-label"><h2>Video và nhạc</h2><p>Chọn sẵn cho hộp thoại tải video, đổi được mỗi lần tải.</p></div>
        <div class="set-panel">
          <div class="field"><label for="sHeight">Chất lượng video chọn sẵn</label>
            <select class="input" id="sHeight" style="max-width:260px">${heights.map((h) => `<option value="${h}" ${s.videoHeight === h ? 'selected' : ''}>${h}p${h >= 2160 ? ' 4K' : h >= 1440 ? ' 2K' : h >= 1080 ? ' Full HD' : h >= 720 ? ' HD' : ''}</option>`).join('')}</select></div>
          <div class="field"><span class="label" id="sAudioLbl">Khi chỉ tải âm thanh, lưu thành</span>
            <div class="segmented" role="group" aria-labelledby="sAudioLbl">
              <button data-audio="mp3" aria-pressed="${s.audioFormat === 'mp3'}">MP3</button><button data-audio="m4a" aria-pressed="${s.audioFormat === 'm4a'}">M4A</button></div>
            <span class="hint">${s.audioFormat === 'mp3' ? 'MP3 mở được trên mọi máy, mọi điện thoại, mọi loa.' : 'M4A giữ nguyên âm thanh gốc của video, file nhỏ hơn MP3.'}</span></div>
        </div>
      </section>
      <section class="set-row">
        <div class="set-label"><h2>Cập nhật VDM</h2><p>Bản mới sửa lỗi và thêm tính năng. Cập nhật giữ nguyên danh sách tải và cài đặt.</p></div>
        <div class="set-panel" id="appUpdate">${appUpdateHtml()}</div>
      </section>
      <section class="set-row">
        <div class="set-label"><h2>Công cụ tải video</h2><p>YouTube thay đổi thường xuyên, công cụ cũ là lý do hay gặp nhất khi tải video bị lỗi.</p></div>
        <div class="set-panel">
          <table class="tools-table"><tbody>
            <tr><td>yt-dlp, tải video từ hàng nghìn trang</td><td class="${t.ytdlp ? '' : 'missing'}">${esc(t.ytdlp || 'Chưa có')}</td></tr>
            <tr><td>FFmpeg, ghép video và chuyển MP3</td><td class="${t.ffmpeg ? '' : 'missing'}">${esc(t.ffmpeg || 'Chưa có')}</td></tr>
            <tr><td>Deno, cần cho YouTube</td><td class="${t.deno ? '' : 'missing'}">${esc(t.deno || 'Chưa có')}</td></tr>
          </tbody></table>
          <div class="set-line"><div class="grow"><label class="t" for="sAuto">Tự cập nhật mỗi tuần</label><div class="d">${t.checkedAt ? 'Kiểm tra lần cuối ' + fmt.when(t.checkedAt) : 'Chưa kiểm tra lần nào'}</div></div><input type="checkbox" class="switch" id="sAuto" ${s.autoUpdateTools ? 'checked' : ''}></div>
          <div class="set-line"><button class="btn" data-sact="update">${icon('retry')}Kiểm tra cập nhật ngay</button><span class="d" id="updateMsg" aria-live="polite"></span></div>
        </div>
      </section>
      <section class="set-row">
        <div class="set-label"><h2>${W.system}</h2><p>VDM cần chạy thì mới bắt được link từ trình duyệt.</p></div>
        <div class="set-panel">
          <div class="set-line"><div class="grow"><label class="t" for="sStart">${W.startLabel}</label><div class="d">${W.startDesc}</div></div><input type="checkbox" class="switch" id="sStart" ${s.startWithWindows ? 'checked' : ''}></div>
          <div class="set-line"><div class="grow"><label class="t" for="sTray">${W.closeLabel}</label><div class="d">${W.closeDesc}</div></div><input type="checkbox" class="switch" id="sTray" ${s.closeToTray ? 'checked' : ''}></div>
          <div class="set-line"><div class="grow"><div class="t">Thoát VDM</div><div class="d">Dừng mọi file đang tải. Lần mở sau, chúng tải tiếp từ chỗ dừng.</div></div><button class="btn" data-sact="quit">Thoát VDM</button></div>
        </div>
      </section>`;
  }

  function bindSettings() {
    const form = $('#settingsForm');
    form.addEventListener('change', (e) => {
      const id = e.target.id;
      if (id === 'sCapture') save({ captureEnabled: e.target.checked });
      if (id === 'sShare') save({ shareBandwidth: e.target.checked });
      if (id === 'sAll') { save({ captureAll: e.target.checked }); renderSettings(); $('#sAll').focus(); }
      if (id === 'sSort') { save({ sortByType: e.target.checked }); renderSettings(); $('#sSort').focus(); }
      if (id === 'sAsk') save({ askBeforeDownload: e.target.checked });
      if (id === 'sAuto') save({ autoUpdateTools: e.target.checked });
      if (id === 'sUpdAuto') save({ autoCheckUpdates: e.target.checked });
      if (id === 'sStart') save({ startWithWindows: e.target.checked });
      if (id === 'sTray') save({ closeToTray: e.target.checked });
      if (id === 'sHeight') save({ videoHeight: Number(e.target.value) });
      if (id === 'sConnSel') { save({ connections: Number(e.target.value) }); renderSettings(); $('#sConnSel').focus(); }
      if (id === 'sExt') save({ captureExtensions: e.target.value.split(/[\s,;]+/).map((x) => x.replace(/^\./, '').toLowerCase()).filter(Boolean) });
      if (id === 'sMin') save({ captureMinSizeKb: Math.max(0, Number(e.target.value) || 0) });
    });
    form.addEventListener('click', async (e) => {
      const step = e.target.closest('[data-step]');
      if (step) {
        const key = step.dataset.step;
        const [lo, hi] = key === 'connections' ? [1, 32] : [1, 10];
        const v = Math.min(hi, Math.max(lo, S.settings[key] + Number(step.dataset.d)));
        save({ [key]: v });
        renderSettings();
        form.querySelector(`[data-step="${key}"][data-d="${step.dataset.d}"]`).focus();
        return;
      }
      const audio = e.target.closest('[data-audio]');
      if (audio) {
        save({ audioFormat: audio.dataset.audio });
        renderSettings();
        form.querySelector(`[data-audio="${audio.dataset.audio}"]`).focus();
        return;
      }
      const forget = e.target.closest('[data-forget]');
      if (forget) {
        await vdm.call('forgetFolder', { category: forget.dataset.forget });
        form.querySelector('[data-sact="folder"]').focus();
        return;
      }
      const a = e.target.closest('[data-sact]');
      if (!a) return;
      if (a.dataset.sact === 'folder') {
        const path = await vdm.call('chooseFolder', { current: S.settings.downloadFolder });
        if (path) { save({ downloadFolder: path }); renderSettings(); }
      }
      if (a.dataset.sact === 'quit') { vdm.call('quit'); return; }
      if (a.dataset.sact === 'checkUpdate') {
        a.disabled = true;
        $('#appUpdateMsg').textContent = 'Đang kiểm tra…';
        try {
          S.update = await vdm.call('checkUpdate');
          S.updateMsg = S.update.available ? '' : 'Bạn đang dùng bản mới nhất.'; // a new one shows above
        } catch (err) {
          S.updateMsg = err.message;
        }
        renderUpdate();
        refreshAppUpdate();
        $('[data-sact="checkUpdate"]').focus();
        return;
      }
      if (a.dataset.sact === 'update') {
        a.disabled = true;
        $('#updateMsg').textContent = 'Đang kiểm tra…';
        try {
          const r = await vdm.call('updateTools');
          S.tools = r.tools || S.tools;
          renderSettings();
          $('#updateMsg').textContent = r.message;
        } catch (err) {
          $('#updateMsg').textContent = 'Không kiểm tra được: ' + err.message;
          a.disabled = false;
        }
      }
    });
  }

  // ---------- events ----------
  function selectedItem() {
    return S.items.find((i) => i.id === S.selected);
  }

  function bind() {
    $('#brandMark').outerHTML = markSvg(28);
    hydrateIcons();
    $('#openNav').innerHTML = icon('menu');
    $('#closeNav').innerHTML = icon('close');
    $('#openNav').addEventListener('click', () => $('#sidebar').classList.add('open'));
    $('#closeNav').addEventListener('click', () => $('#sidebar').classList.remove('open'));
    window.addEventListener('hashchange', () => setView(location.hash.slice(1)));

    $('#addLink').addEventListener('click', () => addLinkDialog());
    $('#pauseAll').addEventListener('click', (e) => vdm.call(e.currentTarget.dataset.mode === 'resume' ? 'resumeAll' : 'pauseAll'));
    $('#tabs').addEventListener('click', (e) => {
      const b = e.target.closest('[data-tab]');
      if (!b) return;
      S.tab = b.dataset.tab;
      renderDownloads();
    });
    $('#typeFilter').addEventListener('change', (e) => { S.type = e.target.value; renderDownloads(); });
    $('#search').addEventListener('input', (e) => {
      S.query = e.target.value;
      if (S.view !== 'downloads') location.hash = 'downloads';
      renderDownloads();
    });

    $('#rows').addEventListener('click', (e) => {
      const tr = e.target.closest('tr');
      if (!tr) return;
      const item = S.items.find((i) => i.id === tr.dataset.id);
      if (!item) return;
      const b = e.target.closest('[data-act]');
      if (b) {
        e.stopPropagation();
        if (b.dataset.act === 'menu') openMenu(b, item);
        else act(b.dataset.act, item);
        return;
      }
      if (tr.classList.contains('item')) {
        S.selected = S.selected === item.id ? null : item.id;
        renderDownloads();
      }
    });
    $('#rows').addEventListener('dblclick', (e) => {
      const tr = e.target.closest('tr.item');
      const item = tr && S.items.find((i) => i.id === tr.dataset.id);
      if (item && item.status === 'completed' && !e.target.closest('button')) act('open', item);
    });

    $('#browserList').addEventListener('click', (e) => {
      const b = e.target.closest('[data-bact]');
      if (!b) return;
      const name = b.dataset.b;
      const def = BROWSERS.find((x) => x.name === name);
      switch (b.dataset.bact) {
        case 'toggle':
          if (openSteps.has(name)) openSteps.delete(name); else openSteps.add(name);
          renderBrowsers();
          break;
        case 'page': vdm.call('browserPage', { browser: name, page: def.page }); break;
        case 'folder': vdm.call('extensionFolder', { browser: name }); break;
        case 'copy': vdm.call('copyText', { text: b.dataset.text }); b.lastChild.textContent = 'Đã sao chép'; break;
        case 'xpi': vdm.call('installFirefox'); break;
      }
    });

    bindSettings();

    document.addEventListener('click', (e) => {
      if (!e.target.closest('.menu') && !e.target.closest('[data-act="menu"]') && $('#layer .menu')) closeLayer();
    });
    document.addEventListener('keydown', (e) => {
      const typing = /INPUT|TEXTAREA|SELECT/.test(document.activeElement.tagName);
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); $('#search').focus(); return; }
      if ($('#layer').children.length) return;
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'v' && !typing) {
        e.preventDefault();
        if (S.view !== 'downloads') location.hash = 'downloads';
        addLinkDialog();
        return;
      }
      if (typing || S.view !== 'downloads') return;
      const list = sorted(visibleItems().filter((TABS.find((t) => t.id === S.tab) || TABS[0]).test));
      const k = list.findIndex((i) => i.id === S.selected);
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        const next = list[Math.max(0, Math.min(list.length - 1, k + (e.key === 'ArrowDown' ? 1 : -1)))];
        if (next) { S.selected = next.id; renderDownloads(); rowCache.get(next.id).tr.scrollIntoView({ block: 'nearest' }); }
      }
      const item = selectedItem();
      if (!item) return;
      if (e.key === 'Escape') { S.selected = null; renderDownloads(); }
      if (e.key === 'Enter' && item.status === 'completed') act('open', item);
      if (e.key === ' ') { e.preventDefault(); act(item.status === 'downloading' || item.status === 'queued' ? 'pause' : 'resume', item); }
      if (e.key === 'Delete') act(item.status === 'completed' ? 'remove' : 'remove', item);
    });
  }

  function applyState(state) {
    S.items = state.items || [];
    S.folder = state.folder || S.folder;
    S.browsers = state.browsers || S.browsers;
    const update = state.update || null;
    if (JSON.stringify(update) !== JSON.stringify(S.update)) {
      S.update = update;
      renderUpdate();
      refreshAppUpdate();
    }
    const remembered = state.remembered || {};
    if (JSON.stringify(remembered) !== JSON.stringify(S.remembered)) {
      S.remembered = remembered;
      if ($('#remembered')) $('#remembered').innerHTML = rememberedHtml();
    }
    if (S.selected && !S.items.some((i) => i.id === S.selected)) S.selected = null;
    renderShell();
    if (S.view === 'downloads') renderDownloads();
    else if (S.view === 'browsers') renderBrowsers();
  }

  async function start() {
    bind();
    vdm.on('state', applyState);
    vdm.on('settings', (s) => { S.settings = s; if (S.view === 'settings') renderSettings(); });
    vdm.on('show', (view) => { if (view) location.hash = view; });
    const init = await vdm.call('ready');
    if (init.platform === 'mac') {
      W = WORDS.mac;
      document.documentElement.dataset.platform = 'mac';
      document.querySelectorAll('[data-key="mod"]').forEach((k) => { k.textContent = '⌘'; });
      document.querySelectorAll('[data-key="alt"]').forEach((k) => { k.textContent = 'Option'; });
    }
    S.settings = init.settings;
    S.tools = init.tools;
    S.extension = init.extension || null;
    const params = new URLSearchParams(location.search);
    if (params.get('select')) S.selected = params.get('select');
    applyState(init.state);
    setView(location.hash.slice(1) || 'downloads');
  }

  document.addEventListener('DOMContentLoaded', start);
})();
