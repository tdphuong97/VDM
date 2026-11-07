// The extension folder people pick in "Load unpacked": everything the manifests name is there, flat.
//   node --test extension/test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, existsSync } from 'node:fs';

const src = new URL('../src/', import.meta.url);

test('no subfolder in the extension folder, so the picker leaves only the folder itself to choose', () => {
  // Up to 1.3.1 an icons folder was all the picker showed inside, and people chose it: "manifest missing".
  const folders = readdirSync(src, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name);
  assert.deepEqual(folders, []);
});

for (const name of ['manifest.json', 'manifest.firefox.json']) {
  test(`every file ${name} names is in the folder`, () => {
    const m = JSON.parse(readFileSync(new URL(name, src), 'utf8'));
    const files = [
      ...Object.values(m.icons || {}),
      ...Object.values((m.action || m.browser_action || {}).default_icon || {}),
      (m.action || m.browser_action || {}).default_popup,
      m.background && m.background.service_worker,
      ...((m.background && m.background.scripts) || []),
      ...(m.content_scripts || []).flatMap((c) => [...(c.js || []), ...(c.css || [])]),
    ].filter(Boolean);
    for (const f of files) assert.ok(existsSync(new URL(f, src)), `${name} names ${f}, which is missing`);
  });
}

test('the popup\'s pictures are in the folder', () => {
  const html = readFileSync(new URL('popup.html', src), 'utf8');
  for (const [, f] of html.matchAll(/src="([^":]+)"/g)) assert.ok(existsSync(new URL(f, src)), `popup.html names ${f}`);
});
