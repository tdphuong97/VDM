// Runs background.js in a fake browser and checks which downloads it takes from the browser.
//   node --test extension/test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const src = new URL('../src/', import.meta.url);
const read = (f) => readFileSync(new URL(f, src), 'utf8');

function load() {
  const event = () => ({ addListener() {} });
  const chrome = {
    downloads: { onDeterminingFilename: event(), onCreated: event(), cancel: async () => {}, erase: async () => {}, download: async () => {} },
    webRequest: { onHeadersReceived: event() },
    tabs: { onUpdated: event(), onRemoved: event(), sendMessage: async () => {}, get: async () => ({}) },
    runtime: { onMessage: event(), onInstalled: event(), onStartup: event(), getManifest: () => ({ version: '1.3.0' }), reload() { chrome.runtime.reloads++; }, reloads: 0 },
    contextMenus: { onClicked: event(), removeAll() {}, create() {} },
    alarms: { onAlarm: event(), create() {} },
    storage: { local: { get: async () => ({}), set: async () => {} }, session: { get: async () => ({}), set: async () => {}, remove: async () => {} } },
    cookies: { getAll: async () => [] },
    action: { setTitle() {} },
  };
  chrome.storage.local.get = async (key) => ({ [key]: chrome.storage.saved[key] });
  chrome.storage.local.set = async (o) => { Object.assign(chrome.storage.saved, o); };
  chrome.storage.saved = {};
  const ctx = vm.createContext({
    chrome, navigator: { userAgent: 'Mozilla/5.0 Chrome/141.0' }, URL, URLSearchParams, AbortController, setTimeout, clearTimeout, console, atob,
    fetch: async () => { throw new Error('offline'); },
  });
  vm.runInContext(read('shared.js') + '\n;\n' + read('background.js'), ctx);
  return ctx;
}

const bg = load();
const cfg = (capture) => ({ app: 'VDM', capture: { enabled: true, all: false, extensions: ['zip', 'iso'], minSizeKb: 0, ...capture } });
const item = (over) => ({ id: 1, url: 'https://files.example.vn/get', finalUrl: 'https://files.example.vn/get', filename: 'report.docx', fileSize: 250000, mime: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', ...over });

test('with "every file type" on, any file the browser downloads goes to VDM', () => {
  assert.equal(bg.shouldCatch(item(), cfg({ all: true })), true);
  assert.equal(bg.shouldCatch(item({ filename: 'photo.jpg', mime: 'image/jpeg' }), cfg({ all: true })), true);
  assert.equal(bg.shouldCatch(item({ filename: 'download', mime: 'application/octet-stream' }), cfg({ all: true })), true);
});

test('with it off, only the listed extensions go to VDM', () => {
  assert.equal(bg.shouldCatch(item(), cfg({ all: false })), false);
  assert.equal(bg.shouldCatch(item({ filename: 'backup.zip' }), cfg({ all: false })), true);
});

test('saving a web page stays with the browser, which saves its pictures too', () => {
  assert.equal(bg.shouldCatch(item({ filename: 'Trang chủ.html', mime: 'text/html' }), cfg({ all: true })), false);
  assert.equal(bg.shouldCatch(item({ filename: 'page.mhtml', mime: 'multipart/related' }), cfg({ all: true })), false);
});

test('links that only exist inside the page, private windows and tiny files stay with the browser', () => {
  assert.equal(bg.shouldCatch(item({ url: 'blob:https://app.example.vn/123', finalUrl: 'blob:https://app.example.vn/123' }), cfg({ all: true })), false);
  assert.equal(bg.shouldCatch(item({ incognito: true }), cfg({ all: true })), false);
  assert.equal(bg.shouldCatch(item({ fileSize: 10 * 1024 }), cfg({ all: true, minSizeKb: 100 })), false);
  assert.equal(bg.shouldCatch(item(), cfg({ all: true, enabled: false })), false);
});

test('a song file is told apart from a video file', () => {
  const headers = (type, len) => [{ name: 'Content-Type', value: type }, { name: 'Content-Length', value: String(len) }];
  assert.equal(bg.classify({ url: 'https://stream.nct.vn/a/b.mp3?st=x', responseHeaders: headers('audio/mpeg', 3634052) }), 'audio');
  assert.equal(bg.classify({ url: 'https://cdn.example.vn/clip.mp4', responseHeaders: headers('video/mp4', 9e6) }), 'file');
  assert.equal(bg.classify({ url: 'https://cdn.example.vn/live/master.m3u8', responseHeaders: headers('application/vnd.apple.mpegurl', 900) }), 'hls');
  assert.equal(bg.classify({ url: 'https://rr1.googlevideo.com/videoplayback', responseHeaders: headers('video/mp4', 9e6) }), null);
});

test('song names become safe file names', () => {
  assert.equal(bg.cleanName('Cô Bé Ơi - Tăng Duy Tân: "Live" a\\b/c'), 'Cô Bé Ơi - Tăng Duy Tân Live a b c');
});

test('the list understands IDM wildcards such as R0* for split RAR parts', () => {
  const list = cfg({ all: false, extensions: ['r0*', 'r1*', 'zip'] });
  assert.equal(bg.shouldCatch(item({ filename: 'game.r00' }), list), true);
  assert.equal(bg.shouldCatch(item({ filename: 'game.r17' }), list), true);
  assert.equal(bg.shouldCatch(item({ filename: 'game.r20' }), list), false);
  assert.equal(bg.shouldCatch(item({ filename: 'notes.rtf' }), list), false);
});

// Facebook and Instagram play a video as pieces of a file named by bytestart/byteend; Facebook's
// efg parameter (base64 JSON) says which video it is.
const efg = (json) => Buffer.from(json).toString('base64').replace(/=+$/, '');
const segment = (json, host = 'video.fsgn5-6.fna.fbcdn.net') =>
  `https://${host}/o1/v/t2/f2/m69/AQN.mp4?strext=1&_nc_cat=1&efg=${efg(json)}&_nc_ohc=x&bytestart=0&byteend=812345`;
const mp4 = (len) => [{ name: 'Content-Type', value: 'video/mp4' }, { name: 'Content-Length', value: String(len) }];

test('a piece of a facebook video is never sent as a file, but tells which video is playing', () => {
  const url = segment('{"vencode_tag":"dash_h264-basic-gen2_720p","video_id":1234567890123456789,"oil_urlgen_app_id":256}');
  assert.equal(bg.classify({ url, initiator: 'https://www.facebook.com', responseHeaders: mp4(2e6) }), 'post');
  // The id is longer than a JavaScript number holds exactly: every digit must survive.
  assert.equal(bg.facebookVideo(url), 'https://www.facebook.com/watch/?v=1234567890123456789');
});

test('pieces without a video id, and instagram pieces, are not files either', () => {
  const plain = segment('{"vencode_tag":"dash_baseline_1_v1"}');
  assert.equal(bg.classify({ url: plain, initiator: 'https://www.facebook.com', responseHeaders: mp4(2e6) }), null);
  const insta = segment('{"vencode_tag":"xpv_progressive","video_id":555}', 'scontent.cdninstagram.com');
  assert.equal(bg.classify({ url: insta, initiator: 'https://www.instagram.com', responseHeaders: mp4(2e6) }), null);
});

function capture() {
  const sent = [];
  bg.send = async (path, body) => { sent.push({ path, ...body }); return true; };
  return sent;
}
const feedTab = { id: 7, url: 'https://www.facebook.com/', title: '(3) Facebook' };

test('a feed video goes to VDM as the post the page shows it in', async () => {
  const sent = capture();
  await bg.saveTabMedia(7, []);
  await bg.downloadVideo(feedTab, feedTab.url, '(3) Facebook', 'blob:https://www.facebook.com/x', 'https://www.facebook.com/vtv24news/videos/1234567890/');
  assert.equal(sent.length, 1);
  assert.equal(sent[0].path, '/media');
  assert.equal(sent[0].url, 'https://www.facebook.com/vtv24news/videos/1234567890/');
  assert.equal(sent[0].pageUrl, 'https://www.facebook.com/vtv24news/videos/1234567890/');
  assert.equal(sent[0].title, null); // the feed's title is not the video's; yt-dlp names it
});

test('without a post link, the facebook video playing last is sent', async () => {
  const sent = capture();
  await bg.saveTabMedia(7, [
    { url: 'https://www.facebook.com/watch/?v=111', kind: 'post' },
    { url: 'https://www.facebook.com/watch/?v=222', kind: 'post' },
  ]);
  await bg.downloadVideo(feedTab, feedTab.url, '(3) Facebook', 'blob:https://www.facebook.com/x', null);
  assert.equal(sent[0].url, 'https://www.facebook.com/watch/?v=222');
});

test('on a video page, the page itself still wins', async () => {
  const sent = capture();
  const tab = { id: 8, url: 'https://www.youtube.com/watch?v=Icq1mZFekTg', title: 'Bài hát - YouTube' };
  await bg.saveTabMedia(8, []);
  await bg.downloadVideo(tab, tab.url, 'Bài hát - YouTube', null, null);
  assert.equal(sent[0].url, 'https://www.youtube.com/watch?v=Icq1mZFekTg');
  assert.equal(sent[0].title, 'Bài hát - YouTube');
});

test('versions compare by number', () => {
  assert.equal(bg.newerVersion('1.3.10', '1.3.9'), true);
  assert.equal(bg.newerVersion('1.3.0', '1.3.0'), false);
  assert.equal(bg.newerVersion('1.2.9', '1.3.0'), false);
});

test('after VDM updates itself, the extension loads its new files once', async () => {
  const runtime = bg.chrome.runtime;
  runtime.reloads = 0;
  await bg.followApp('1.3.0'); // same version: nothing to do
  assert.equal(runtime.reloads, 0);
  await bg.followApp('1.3.1');
  assert.equal(runtime.reloads, 1);
  // Still old after the reload (loaded from another folder): do not reload over and over.
  await bg.followApp('1.3.1');
  assert.equal(runtime.reloads, 1);
});
