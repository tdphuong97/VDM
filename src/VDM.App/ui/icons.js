// One outline icon set (16px grid, 1.5px stroke), injected once as an SVG sprite.
(function () {
  const paths = {
    download: '<path d="M8 2v8M4.5 6.5 8 10l3.5-3.5M2.5 11v2.5h11V11"/>',
    browser: '<rect x="2" y="3" width="12" height="10" rx="2"/><path d="M2 6.5h12"/>',
    sliders: '<path d="M2.5 4.5H9M12 4.5h1.5M2.5 11.5H4M7 11.5h6.5"/><circle cx="10.5" cy="4.5" r="1.5"/><circle cx="5.5" cy="11.5" r="1.5"/>',
    search: '<circle cx="7" cy="7" r="4.5"/><path d="m10.5 10.5 3 3"/>',
    plus: '<path d="M8 3v10M3 8h10"/>',
    pause: '<path d="M5.5 3.5v9M10.5 3.5v9"/>',
    play: '<path d="M5 3.2v9.6L12.5 8z" stroke-linejoin="round"/>',
    retry: '<path d="M13 8a5 5 0 1 1-1.46-3.54"/><path d="M13 2.5v3h-3"/>',
    folder: '<path d="M2 4.5A1.5 1.5 0 0 1 3.5 3h3L8 4.5h4.5A1.5 1.5 0 0 1 14 6v5.5a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 2 11.5z"/>',
    more: '<circle cx="8" cy="3.5" r=".9" fill="currentColor"/><circle cx="8" cy="8" r=".9" fill="currentColor"/><circle cx="8" cy="12.5" r=".9" fill="currentColor"/>',
    file: '<path d="M4 1.5h5l3 3v10H4z"/><path d="M9 1.5v3h3"/>',
    video: '<rect x="1.5" y="3.5" width="13" height="9" rx="1.5"/><path d="m6.5 6 3.5 2-3.5 2z" stroke-linejoin="round"/>',
    audio: '<path d="M6 12V3.6l7-1.6v8.5"/><circle cx="4.5" cy="12" r="1.5"/><circle cx="11.5" cy="10.5" r="1.5"/>',
    archive: '<rect x="2.5" y="2" width="11" height="12" rx="1.5"/><path d="M8 2v1.5M8 5v1.5M8 8v1.5"/><rect x="6.75" y="10" width="2.5" height="2" rx=".5"/>',
    program: '<rect x="2" y="2.5" width="12" height="11" rx="1.5"/><path d="M2 5.5h12M5 8.5l1.5 1.5L5 11.5M8.5 11.5H11"/>',
    document: '<path d="M4 1.5h5l3 3v10H4z"/><path d="M9 1.5v3h3M6 8h4M6 10.5h4"/>',
    image: '<rect x="2" y="2.5" width="12" height="11" rx="1.5"/><path d="m2.5 11.5 3.5-3.5 3 3 1.5-1.5 3 3"/><circle cx="10.5" cy="5.5" r="1"/>',
    check: '<path d="m3.5 8.5 3 3 6-7"/>',
    close: '<path d="m4 4 8 8M12 4l-8 8"/>',
    copy: '<rect x="5.5" y="5.5" width="8.5" height="8.5" rx="1.5"/><path d="M10.5 5.5V3.5A1.5 1.5 0 0 0 9 2H3.5A1.5 1.5 0 0 0 2 3.5V9a1.5 1.5 0 0 0 1.5 1.5h2"/>',
    chevron: '<path d="m4 6 4 4 4-4"/>',
    external: '<path d="M9 2.5h4.5V7M13.5 2.5 8 8M12 9.5v3a1 1 0 0 1-1 1H3.5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h3"/>',
    menu: '<path d="M2.5 4h11M2.5 8h11M2.5 12h11"/>',
    link: '<path d="M7 9a3 3 0 0 0 4.24 0l2-2a3 3 0 0 0-4.24-4.24l-.75.75"/><path d="M9 7a3 3 0 0 0-4.24 0l-2 2A3 3 0 0 0 7 13.24l.75-.75"/>',
  };
  const symbols = Object.entries(paths)
    .map(([id, d]) => `<symbol id="i-${id}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round">${d}</symbol>`)
    .join('');
  const sprite = `<svg xmlns="http://www.w3.org/2000/svg" style="display:none" aria-hidden="true">${symbols}</svg>`;
  document.addEventListener('DOMContentLoaded', () => document.body.insertAdjacentHTML('afterbegin', sprite));

  window.icon = (id, cls) => `<svg class="ic${cls ? ' ' + cls : ''}" aria-hidden="true"><use href="#i-${id}"/></svg>`;

  // The mark: an arrow dropping into a bar cut into parts, the last part still filling.
  window.markSvg = (size) => `<svg class="mark" width="${size}" height="${size}" viewBox="0 0 24 24" aria-hidden="true">
    <path d="M12 2.5v10.5M7.5 8.6 12 13.1l4.5-4.5" fill="none" stroke="var(--accent)" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/>
    <rect x="2" y="16.5" width="6.2" height="5" rx="1.2" fill="var(--accent)"/>
    <rect x="8.9" y="16.5" width="6.2" height="5" rx="1.2" fill="var(--accent)"/>
    <rect x="15.8" y="16.5" width="6.2" height="5" rx="1.2" fill="var(--accent)" opacity=".35"/>
  </svg>`;
})();
