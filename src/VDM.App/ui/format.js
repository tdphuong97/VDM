// Vietnamese number, size and time formatting, shared by the main window and the capture dialog.
(function () {
  const n1 = new Intl.NumberFormat('vi-VN', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const n0 = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 0 });
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];

  function bytes(b) {
    if (b == null || b < 0 || !isFinite(b)) return null;
    if (b < 1024) return n0.format(b) + ' B';
    let i = 0;
    let v = b;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return (i === 1 ? n0.format(v) : n1.format(v)) + ' ' + units[i];
  }

  // Splits "5,8 GB" into value and unit, so the unit can be set smaller and grey.
  function bytesParts(b) {
    const s = bytes(b);
    if (!s) return null;
    const k = s.lastIndexOf(' ');
    return { value: s.slice(0, k), unit: s.slice(k + 1) };
  }

  function speed(bps) {
    if (!bps || bps < 1) return null;
    return bytes(bps) + '/s';
  }

  function duration(sec, prefix) {
    if (sec == null || !isFinite(sec) || sec < 0) return null;
    sec = Math.round(sec);
    let s;
    if (sec < 60) s = sec + ' giây';
    else if (sec < 3600) {
      const m = Math.floor(sec / 60);
      const r = sec % 60;
      s = m + ' phút' + (m < 10 && r ? ' ' + r + ' giây' : '');
    } else {
      const h = Math.floor(sec / 3600);
      const m = Math.floor((sec % 3600) / 60);
      s = h + ' giờ' + (m ? ' ' + m + ' phút' : '');
    }
    return prefix ? prefix + ' ' + s : s;
  }

  // 4:51 or 1:02:03, for a video's length.
  function clock(sec) {
    if (!sec) return null;
    sec = Math.round(sec);
    const h = Math.floor(sec / 3600);
    const m = Math.floor((sec % 3600) / 60);
    const s = String(sec % 60).padStart(2, '0');
    return h ? h + ':' + String(m).padStart(2, '0') + ':' + s : m + ':' + s;
  }

  function hhmm(d) {
    return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
  }

  // "14:32", "hôm qua 09:10", "28/10".
  function when(isoString) {
    if (!isoString) return null;
    const d = new Date(isoString);
    const today = new Date();
    const start = new Date(today.getFullYear(), today.getMonth(), today.getDate());
    const diffDays = Math.floor((start - new Date(d.getFullYear(), d.getMonth(), d.getDate())) / 86400000);
    if (diffDays <= 0) return hhmm(d);
    if (diffDays === 1) return 'hôm qua ' + hhmm(d);
    return d.getDate() + '/' + (d.getMonth() + 1);
  }

  function percent(done, total) {
    if (!total || total <= 0) return null;
    return Math.min(100, Math.floor((done / total) * 100));
  }

  function host(url) {
    try { return new URL(url).hostname.replace(/^www\./, ''); } catch (e) { return ''; }
  }

  function siteName(item) {
    const site = item.media && item.media.site;
    if (site && site !== 'Generic') return site === 'Youtube' ? 'YouTube' : site;
    return host(item.referrer || item.pageUrl || item.url);
  }

  // What a file type is called in a sentence: "Lần sau tải tài liệu cũng lưu vào đây".
  function typeName(category) {
    return { video: 'video', audio: 'nhạc', archive: 'file nén', program: 'chương trình', document: 'tài liệu', image: 'ảnh' }[category] || 'file loại khác';
  }

  // Windows paths ignore case and a trailing slash.
  function sameFolder(a, b) {
    const norm = (p) => String(p || '').replace(/[\\/]+$/, '').toLowerCase();
    return norm(a) === norm(b);
  }

  window.fmt = { bytes, bytesParts, speed, duration, clock, when, hhmm, percent, host, siteName, typeName, sameFolder, n0, n1 };
})();
