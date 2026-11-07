// The on-page button in a fake page: how much work it does while the user just browses.
//   node --test extension/test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const read = (f) => readFileSync(new URL('../src/' + f, import.meta.url), 'utf8');

function element(tag) {
  const el = {
    tagName: tag.toUpperCase(), style: { setProperty() {} }, dataset: {}, hidden: false, textContent: '',
    offsetWidth: 150, offsetHeight: 32, parentElement: null, id: '',
    classList: { set: new Set(), add(c) { this.set.add(c); }, remove(c) { this.set.delete(c); }, toggle(c, on) { on ? this.set.add(c) : this.set.delete(c); } },
    addEventListener() {}, setAttribute() {}, getAttribute: () => null, appendChild() {},
    querySelector: () => element('span'), querySelectorAll: () => [],
    closest: () => null, getBoundingClientRect: () => ({ left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 }),
    attachShadow: () => element('root'),
  };
  return el;
}

/** A page with one big video, the content script loaded; counts how often it measures the page. */
function page() {
  const listeners = {};
  const timers = [];
  const counts = { measure: 0 };
  const video = element('video');
  video.currentSrc = 'https://cdn.example.vn/clip.mp4';
  video.getBoundingClientRect = () => { counts.measure++; return { left: 100, top: 80, right: 1060, bottom: 620, width: 960, height: 540 }; };
  const document = {
    body: element('body'), documentElement: element('html'), fullscreenElement: null,
    createElement: (t) => element(t),
    querySelectorAll: (sel) => (sel === 'video' ? [video] : []),
    querySelector: () => null,
    addEventListener: (type, fn) => { (listeners[type] ||= []).push(fn); },
  };
  const event = () => ({ addListener() {} });
  const chrome = {
    storage: { local: { get: async () => ({}), set: async () => {} }, onChanged: event() },
    runtime: { sendMessage: async () => null, onMessage: event() },
  };
  const ctx = vm.createContext({
    chrome, document, location: { href: 'https://news.example.vn/bai-viet', hostname: 'news.example.vn' },
    navigator: {}, innerWidth: 1440, innerHeight: 900, URL, URLSearchParams, JSON, Math, Date, Object,
    addEventListener: (type, fn) => { (listeners[type] ||= []).push(fn); },
    setInterval: (fn) => { timers.push(fn); return timers.length; },
    setTimeout: () => 0, clearTimeout() {}, requestAnimationFrame: () => 0,
  });
  ctx.window = ctx;
  ctx.globalThis = ctx;
  vm.runInContext(read('shared.js') + '\n;\n' + read('content.js'), ctx);
  const fire = (type, e) => (listeners[type] || []).forEach((fn) => fn(e || {}));
  return { counts, fire, tick: () => timers.forEach((fn) => fn()) };
}

test('after the button showed once, hiding it again does not loop', () => {
  const p = page();
  p.fire('mousemove', { clientX: 500, clientY: 300 }); // over the video: the button appears
  const realNow = Date.now;
  try {
    Date.now = () => realNow() + 10000; // the pointer left long ago: time to hide
    p.counts.measure = 0;
    assert.doesNotThrow(() => p.tick());
    assert.ok(p.counts.measure <= 4, `measured the page ${p.counts.measure} times in one tick`);
  } finally {
    Date.now = realNow;
  }
});
