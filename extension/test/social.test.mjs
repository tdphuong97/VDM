// Social feeds: which post a playing video belongs to, so VDM gets that video and not the feed.
//   node --test extension/test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const ctx = vm.createContext({ URL, URLSearchParams, globalThis: {} });
ctx.globalThis = ctx;
vm.runInContext(readFileSync(new URL('../src/shared.js', import.meta.url), 'utf8'), ctx);
const { videoPage, postUrl } = ctx.VDM;

// Just enough of the DOM for postUrl: parents, ids, attributes and the two queries it makes.
class El {
  constructor(tag, attrs = {}, ...children) {
    this.tagName = tag.toUpperCase();
    this.attrs = attrs;
    this.id = attrs.id || '';
    this.parentElement = null;
    this.children = [];
    for (const c of children) { c.parentElement = this; this.children.push(c); }
  }
  getAttribute(name) { return name in this.attrs ? this.attrs[name] : null; }
  *all() { for (const c of this.children) { yield c; yield* c.all(); } }
  querySelectorAll(sel) {
    if (sel === 'video') return [...this.all()].filter((e) => e.tagName === 'VIDEO');
    if (sel === 'a[href]') return [...this.all()].filter((e) => e.tagName === 'A' && e.getAttribute('href') != null);
    throw new Error('selector not faked: ' + sel);
  }
}
const h = (tag, attrs, ...children) => new El(tag, attrs, ...children);
const nest = (depth, inner) => { let el = inner; for (let i = 0; i < depth; i++) el = h('div', {}, el); return el; };

test('facebook video, reel, share and post addresses count as video pages; the feed and the watch tab do not', () => {
  for (const url of [
    'https://www.facebook.com/watch/?v=1234567890',
    'https://www.facebook.com/watch?ref=x&v=1234567890',
    'https://www.facebook.com/vtv24news/videos/1234567890/',
    'https://www.facebook.com/reel/1234567890',
    'https://www.facebook.com/share/v/1AbCdEf/',
    'https://www.facebook.com/share/r/1AbCdEf/',
    'https://www.facebook.com/vtv24news/posts/pfbid02xYz',
    'https://www.facebook.com/groups/123/posts/456/',
    'https://www.facebook.com/groups/123/permalink/456/',
    'https://www.facebook.com/permalink.php?story_fbid=456&id=123',
    'https://m.facebook.com/story.php?story_fbid=456&id=123',
    'https://fb.watch/abcDEF/',
  ]) assert.equal(videoPage(url), true, url);
  for (const url of ['https://www.facebook.com/', 'https://www.facebook.com/watch/', 'https://www.facebook.com/vtv24news', 'https://www.facebook.com/groups/123/'])
    assert.equal(videoPage(url), false, url);
});

test('instagram posts count with or without the account in front', () => {
  assert.equal(videoPage('https://www.instagram.com/p/C9xYz12AbCd/'), true);
  assert.equal(videoPage('https://www.instagram.com/someone/reel/C9xYz12AbCd/'), true);
  assert.equal(videoPage('https://www.instagram.com/'), false);
  assert.equal(videoPage('https://www.instagram.com/someone/'), false);
});

test('a facebook feed video takes the link of its own post, without the tracking', () => {
  const video = h('video');
  const post = h('div', { 'aria-posinset': '2' },
    h('a', { href: 'https://www.facebook.com/vtv24news' }),
    h('a', { href: 'https://www.facebook.com/vtv24news/videos/1234567890/?__cft__[0]=AZX&__tn__=%2CO%2CP-R' }),
    nest(12, video));
  h('div', { role: 'feed' }, h('div', { 'aria-posinset': '1' }, h('a', { href: '/other/posts/pfbid0aaa' }), h('img')), post);
  assert.equal(postUrl(video, 'https://www.facebook.com/'), 'https://www.facebook.com/vtv24news/videos/1234567890/');
});

test('a facebook post link keeps only the parameters that name the post', () => {
  const video = h('video');
  h('div', { role: 'article' }, h('a', { href: '/permalink.php?story_fbid=456&id=123&__cft__[0]=AZX' }), nest(8, video));
  assert.equal(postUrl(video, 'https://www.facebook.com/groups/9/'), 'https://www.facebook.com/permalink.php?story_fbid=456&id=123');
});

test('the search for a link stops at the edge of the post', () => {
  const video = h('video');
  // This post has no usable link (facebook fills some in only on hover); the one above it does.
  h('div', { role: 'feed' },
    h('div', { 'aria-posinset': '1' }, h('a', { href: '/other/videos/999/' }), h('video')),
    h('div', { 'aria-posinset': '2' }, h('a', { href: '#' }), nest(6, video)));
  assert.equal(postUrl(video, 'https://www.facebook.com/'), null);
});

test('without markers, a container holding a second video is outside the post', () => {
  const video = h('video');
  h('div', {}, h('div', {}, h('a', { href: '/other/videos/999/' }), h('video')), h('div', {}, nest(4, video)));
  assert.equal(postUrl(video, 'https://www.facebook.com/'), null);
});

test('a link to another site inside a post is not the post', () => {
  const video = h('video');
  h('div', { role: 'article' }, h('a', { href: 'https://www.youtube.com/watch?v=Icq1mZFekTg' }), h('a', { href: '/page/posts/pfbid0abc' }), nest(3, video));
  assert.equal(postUrl(video, 'https://www.facebook.com/'), 'https://www.facebook.com/page/posts/pfbid0abc');
});

test('an instagram feed video takes its post link', () => {
  const video = h('video');
  h('article', {}, h('a', { href: '/someone/' }), nest(9, video), h('a', { href: '/p/C9xYz12AbCd/?img_index=1' }));
  assert.equal(postUrl(video, 'https://www.instagram.com/'), 'https://www.instagram.com/p/C9xYz12AbCd/');
});

test('an x timeline video takes the address of its tweet', () => {
  const video = h('video');
  h('article', { 'data-testid': 'tweet' },
    h('a', { href: '/vnexpress' }),
    h('a', { href: '/vnexpress/status/1851234567890123456' }, h('time')),
    nest(10, video));
  assert.equal(postUrl(video, 'https://x.com/home'), 'https://x.com/vnexpress/status/1851234567890123456');
});

test('a tiktok feed video is named by its player, with the account when the page shows it', () => {
  const video = h('video');
  h('article', {}, h('a', { href: '/@vtv24news' }), h('div', {}, h('div', { id: 'xgwrapper-0-7412345678901234567' }, h('div', {}, video))));
  assert.equal(postUrl(video, 'https://www.tiktok.com/foryou'), 'https://www.tiktok.com/@vtv24news/video/7412345678901234567');

  const lone = h('video');
  h('div', { id: 'xgwrapper-3-7412345678901234568' }, lone);
  assert.equal(postUrl(lone, 'https://www.tiktok.com/'), 'https://www.tiktok.com/@/video/7412345678901234568');
});

test('a tiktok preview inside a grid link takes that link', () => {
  const video = h('video');
  h('a', { href: 'https://www.tiktok.com/@someone/video/7412345678901234569' }, h('div', {}, video));
  assert.equal(postUrl(video, 'https://www.tiktok.com/@someone'), 'https://www.tiktok.com/@someone/video/7412345678901234569');
});

test('other sites are left to the usual rules', () => {
  const video = h('video');
  h('div', {}, h('a', { href: '/watch?v=Icq1mZFekTg' }), video);
  assert.equal(postUrl(video, 'https://www.youtube.com/'), null);
  assert.equal(postUrl(video, 'https://phim.example.vn/'), null);
});
