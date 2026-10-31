// Where the on-page download button goes: dragged positions are kept, but never off the video or screen.
//   node --test extension/test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const ctx = vm.createContext({ URL, globalThis: {} });
ctx.globalThis = ctx;
vm.runInContext(readFileSync(new URL('../src/shared.js', import.meta.url), 'utf8'), ctx);
const { placeOnVideo, placeInCorner, offsetFromVideo, offsetFromCorner, BUTTON_SIZES } = ctx.VDM;
const viewport = { width: 1440, height: 900 };
const video = { left: 100, top: 80, right: 1060, bottom: 620 };
const button = { width: 150, height: 32 };

test('without a drag the button sits 12px inside the top-right corner of the video', () => {
  assert.deepEqual({ ...placeOnVideo(video, button, { dx: 0, dy: 0 }, viewport) }, { left: 1060 - 150 - 12, top: 92 });
});

test('a dragged button keeps its place relative to the video', () => {
  const at = placeOnVideo(video, button, { dx: -400, dy: 300 }, viewport);
  assert.deepEqual({ ...at }, { left: 898 - 400, top: 392 });
  // The same drag on a video that moved down the page moves with it.
  const lower = placeOnVideo({ ...video, top: 280, bottom: 820 }, button, { dx: -400, dy: 300 }, viewport);
  assert.equal(lower.top, 592);
});

test('a button dragged too far stays on the video and on the screen', () => {
  const far = placeOnVideo(video, button, { dx: -5000, dy: 5000 }, viewport);
  assert.equal(far.left, video.left + 4);
  assert.equal(far.top, video.bottom - button.height - 4);
  const offscreen = placeOnVideo({ left: 100, top: -300, right: 1060, bottom: 240 }, button, { dx: 0, dy: 0 }, viewport);
  assert.ok(offscreen.top >= 8, 'top stays on screen');
});

test('dragging records the offset that placeOnVideo gives back', () => {
  const off = offsetFromVideo(video, button, { left: 300, top: 400 });
  assert.deepEqual({ ...placeOnVideo(video, button, off, viewport) }, { left: 300, top: 400 });
});

test('the song button keeps its distance from the bottom-right corner of the window', () => {
  assert.deepEqual({ ...placeInCorner(button, { right: 24, bottom: 112 }, viewport) }, { left: 1440 - 150 - 24, top: 900 - 32 - 112 });
  const off = offsetFromCorner(button, { left: 40, top: 60 }, viewport);
  assert.deepEqual({ ...placeInCorner(button, off, viewport) }, { left: 40, top: 60 });
  const lost = placeInCorner(button, { right: -900, bottom: 5000 }, viewport);
  assert.ok(lost.left <= 1440 - 150 - 8 && lost.top >= 8);
});

test('three sizes, the middle one being today\'s button', () => {
  assert.deepEqual(Object.keys(BUTTON_SIZES), ['small', 'medium', 'large']);
  assert.equal(BUTTON_SIZES.medium.height, 32);
  assert.ok(BUTTON_SIZES.small.height < 32 && BUTTON_SIZES.large.height > 32);
});
