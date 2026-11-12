/**
 * The Vault Movies icon — a rust tile with a white play mark — at every size
 * the web app and the Android app need. One drawing, rendered with sharp.
 *
 *   node v2/scripts/make-vault-icons.js
 *
 * Writes:
 *   v2/src/Rendezvous/watch/   icons the /watch web app and its manifest serve
 *   v2/mobile/assets/          app icon, adaptive-icon layers and splash for Android
 */
const sharp = require('sharp');
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const WEB = path.join(ROOT, 'src', 'Rendezvous', 'watch');
const MOBILE = path.join(ROOT, 'mobile', 'assets');

const RUST_A = '#E35A3C';
const RUST_B = '#B9381F';

const defs = `<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
  <stop offset="0" stop-color="${RUST_A}"/><stop offset="1" stop-color="${RUST_B}"/></linearGradient></defs>`;

// A play triangle with rounded corners, centred on (512,512), `s` = scale.
function play(s, color = '#fff') {
  const pts = [[512 - 92 * s, 512 - 182 * s], [512 - 92 * s, 512 + 182 * s], [512 + 208 * s, 512]];
  const d = `M${pts[0]} L${pts[1]} L${pts[2]} Z`.replace(/,/g, ' ');
  return `<path d="${d}" fill="${color}" stroke="${color}" stroke-width="${52 * s}" stroke-linejoin="round"/>`;
}
const svg = body => Buffer.from(
  `<svg width="1024" height="1024" viewBox="0 0 1024 1024" xmlns="http://www.w3.org/2000/svg">${defs}${body}</svg>`);

// Rounded tile, for places that show the icon as-is.
const tile = svg(`<rect width="1024" height="1024" rx="230" fill="url(#g)"/>${play(1)}`);
// Full bleed: the platform cuts its own shape (iOS home screen, maskable, legacy launcher).
const bleed = svg(`<rect width="1024" height="1024" fill="url(#g)"/>${play(0.82)}`);
// Android adaptive layers: the mark alone inside the 66% safe zone, and the colour behind it.
const fg = svg(play(0.62));
const bg = svg(`<rect width="1024" height="1024" fill="url(#g)"/>`);
const mono = svg(play(0.62, '#000'));
// Splash: the tile, small, centred; Expo puts it on the background colour.
const splash = svg(`<rect x="312" y="312" width="400" height="400" rx="90" fill="url(#g)"/>
  <g transform="translate(312 312) scale(.390625)">${play(1)}</g>`);

async function out(buf, dir, name, size) {
  fs.mkdirSync(dir, { recursive: true });
  await sharp(buf).resize(size, size).png().toFile(path.join(dir, name));
  console.log(path.relative(ROOT, path.join(dir, name)), size);
}

(async () => {
  await out(bleed, WEB, 'apple-touch-icon.png', 180);
  await out(tile, WEB, 'icon-192.png', 192);
  await out(tile, WEB, 'icon-512.png', 512);
  await out(bleed, WEB, 'icon-maskable-512.png', 512);
  await out(tile, WEB, 'favicon-64.png', 64);

  if (fs.existsSync(path.join(ROOT, 'mobile'))) {
    await out(bleed, MOBILE, 'icon.png', 1024);
    await out(fg, MOBILE, 'android-icon-foreground.png', 1024);
    await out(bg, MOBILE, 'android-icon-background.png', 1024);
    await out(mono, MOBILE, 'android-icon-monochrome.png', 1024);
    await out(splash, MOBILE, 'splash-icon.png', 1024);
  }
})();
