// Shared by the background worker, the content script and the popup.
var VDM = (function () {
  const api = globalThis.browser || globalThis.chrome;

  // Sites whose video pages yt-dlp understands; the page address is all VDM needs.
  const SITES = [
    { host: /(^|\.)youtube(-nocookie)?\.com$/, page: /^\/(watch|shorts\/|embed\/|live\/)/ },
    { host: /(^|\.)youtu\.be$/, page: /^\/[\w-]{11}/ },
    { host: /(^|\.)tiktok\.com$/, page: /\/video\/\d+/ },
    // A video, reel or share link, or a post (a post with a video is how it shows in the feed).
    { host: /(^|\.)facebook\.com$/, page: /^\/(watch(\/live)?\/?\?(.*&)?v=\d|[^/]+\/videos\/|reel\/\d|share\/[vr]\/|[^/]+\/posts\/|groups\/[^/]+\/(posts|permalink)\/\w|(permalink|story|video)\.php\?)/ },
    { host: /(^|\.)fb\.watch$/, page: /./ },
    { host: /(^|\.)instagram\.com$/, page: /^\/([\w.]+\/)?(reel|reels|p|tv)\/[\w-]+/ },
    { host: /(^|\.)(twitter|x)\.com$/, page: /\/status\/\d+/ },
    { host: /(^|\.)vimeo\.com$/, page: /^\/(\d+|video\/\d+)/ },
    { host: /(^|\.)dailymotion\.com$/, page: /^\/(video|embed\/video)\// },
    { host: /(^|\.)bilibili\.(com|tv)$/, page: /\/video\// },
    { host: /(^|\.)twitch\.tv$/, page: /\/(videos\/\d+|clip\/)|^\/\w+\/clip\// },
    { host: /(^|\.)reddit\.com$/, page: /\/comments\// },
  ];

  function videoPage(url) {
    try {
      const u = new URL(url);
      return SITES.some((s) => s.host.test(u.hostname) && s.page.test(u.pathname + u.search));
    } catch (e) {
      return false;
    }
  }

  // ---------- social feeds: many videos on one page, each belonging to its own post ----------
  const SOCIAL = /(^|\.)(facebook\.com|instagram\.com|twitter\.com|x\.com|tiktok\.com)$/;

  /** www., m. and web.facebook.com are one site, and so are twitter.com and x.com. */
  function siteOf(hostname) {
    const base = hostname.split('.').slice(-2).join('.');
    return base === 'twitter.com' ? 'x.com' : base;
  }

  // Facebook numbers the posts of a feed; Instagram, X and TikTok wrap each one in <article>.
  function isPostEdge(el) {
    return el.tagName === 'ARTICLE' || el.getAttribute('role') === 'article' || el.getAttribute('aria-posinset') != null;
  }

  /** The video page a link points to on the page's own site, without tracking parameters; else null. */
  function linkTo(a, page) {
    const href = a.getAttribute('href');
    if (!href || href.startsWith('#') || /^javascript:/i.test(href)) return null;
    let u;
    try { u = new URL(href, page.href); } catch (e) { return null; }
    if (siteOf(u.hostname) !== siteOf(page.hostname) || !videoPage(u.href)) return null;
    const keep = new URLSearchParams();
    for (const k of ['v', 'story_fbid', 'id', 'fbid']) if (u.searchParams.has(k)) keep.set(k, u.searchParams.get(k));
    u.search = keep.toString();
    u.hash = '';
    return u.href;
  }

  function tiktokHandle(a, page) {
    try {
      const m = /^\/@([\w.-]+)\/?$/.exec(new URL(a.getAttribute('href'), page.href).pathname);
      return m && m[1];
    } catch (e) {
      return null;
    }
  }

  /**
   * The address of the post a feed video belongs to: the nearest link to a video page around it,
   * looking no further than the post. TikTok names the video in its player's id instead.
   */
  function postUrl(video, pageUrl) {
    let page;
    try { page = new URL(pageUrl); } catch (e) { return null; }
    if (!SOCIAL.test(page.hostname)) return null;
    let tiktok = null;
    for (let el = video, depth = 0; el && depth < 40; el = el.parentElement, depth++) {
      if (depth > 0 && el.querySelectorAll('video').length > 1) break; // past the post, into the feed
      const player = /^xgwrapper-\d+-(\d{15,})$/.exec(el.id || '');
      if (player) tiktok = player[1];
      const links = el.tagName === 'A' ? [el, ...el.querySelectorAll('a[href]')] : el.querySelectorAll('a[href]');
      for (const a of links) {
        if (tiktok) {
          const handle = tiktokHandle(a, page);
          if (handle) return `https://www.tiktok.com/@${handle}/video/${tiktok}`;
        } else {
          const url = linkTo(a, page);
          if (url) return url;
        }
      }
      if (isPostEdge(el)) break;
    }
    return tiktok ? `https://www.tiktok.com/@/video/${tiktok}` : null;
  }

  // Music sites whose player bar shows the song; the name of the file comes from here.
  const PLAYERS = [
    { host: /(^|\.)nhaccuatui\.com$/, title: '.music-player-wrap .music-info .name', artist: '.music-player-wrap .music-info .artist' },
  ];

  function trackTitle(hostname, doc) {
    const player = PLAYERS.find((p) => p.host.test(hostname));
    const text = (sel) => {
      const el = sel && doc.querySelector(sel);
      return el ? el.textContent.replace(/\s+/g, ' ').trim() : '';
    };
    return player ? { title: text(player.title), artist: text(player.artist) } : { title: '', artist: '' };
  }

  function isYouTube(url) {
    try { return /(^|\.)(youtube(-nocookie)?\.com|youtu\.be)$/.test(new URL(url).hostname); } catch (e) { return false; }
  }

  // ---------- the on-page button: three sizes, and where a drag left it ----------
  const BUTTON_SIZES = {
    small: { height: 26, font: 12, icon: 14, pad: 9 },
    medium: { height: 32, font: 14, icon: 16, pad: 12 },
    large: { height: 40, font: 16, icon: 20, pad: 15 },
  };
  const INSET = 12;  // from the video's corner when never dragged
  const EDGE = 4;    // a dragged button stays this far inside the video
  const SCREEN = 8;  // and this far inside the window
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

  /** On a video: the drag is kept as an offset from the top-right corner, so it follows the video. */
  function placeOnVideo(video, button, offset, viewport) {
    let left = clamp(video.right - button.width - INSET + (offset.dx || 0), video.left + EDGE, video.right - button.width - EDGE);
    let top = clamp(video.top + INSET + (offset.dy || 0), video.top + EDGE, video.bottom - button.height - EDGE);
    left = clamp(left, SCREEN, viewport.width - button.width - SCREEN);
    top = clamp(top, SCREEN, viewport.height - button.height - SCREEN);
    return { left, top };
  }

  function offsetFromVideo(video, button, at) {
    return { dx: at.left - (video.right - button.width - INSET), dy: at.top - (video.top + INSET) };
  }

  /** For a song: kept as a distance from the window's bottom-right corner, above most player bars. */
  function placeInCorner(button, offset, viewport) {
    return {
      left: clamp(viewport.width - button.width - offset.right, SCREEN, viewport.width - button.width - SCREEN),
      top: clamp(viewport.height - button.height - offset.bottom, SCREEN, viewport.height - button.height - SCREEN),
    };
  }

  function offsetFromCorner(button, at, viewport) {
    return { right: viewport.width - button.width - at.left, bottom: viewport.height - button.height - at.top };
  }

  const DEFAULT_BUTTON = { size: 'medium', video: { dx: 0, dy: 0 }, audio: { right: 24, bottom: 112 } };

  return {
    api, videoPage, postUrl, isYouTube, trackTitle,
    BUTTON_SIZES, DEFAULT_BUTTON, placeOnVideo, offsetFromVideo, placeInCorner, offsetFromCorner,
  };
})();
