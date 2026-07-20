/**
 * Generates the app icon set from a single vector design using sharp.
 * Run with: node scripts/make-icons.js
 */
const sharp = require('sharp');
const path = require('path');

const ASSETS = path.join(__dirname, '..', 'assets');

const ACCENT_A = '#5b8cff';
const ACCENT_B = '#6a4cff';
const BG_DARK = '#0d0f14';

// Play triangle centered in a 1024 box, with rounded joins via stroke.
function playMark(color = '#ffffff', scale = 1) {
  const cx = 512;
  const cy = 512;
  const pts = [
    [cx - 92 * scale, cy - 182 * scale],
    [cx - 92 * scale, cy + 182 * scale],
    [cx + 208 * scale, cy],
  ];
  const d = `M${pts[0][0]} ${pts[0][1]} L${pts[1][0]} ${pts[1][1]} L${pts[2][0]} ${pts[2][1]} Z`;
  return `<path d="${d}" fill="${color}" stroke="${color}" stroke-width="${52 * scale}" stroke-linejoin="round"/>`;
}

const gradientDef = `
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="${ACCENT_A}"/>
      <stop offset="1" stop-color="${ACCENT_B}"/>
    </linearGradient>
  </defs>`;

// Full icon: rounded gradient tile + white play mark.
function iconSvg() {
  return `<svg width="1024" height="1024" viewBox="0 0 1024 1024" xmlns="http://www.w3.org/2000/svg">
    ${gradientDef}
    <rect width="1024" height="1024" rx="224" fill="url(#g)"/>
    ${playMark('#ffffff', 1)}
  </svg>`;
}

// Transparent mark, scaled down to sit in an adaptive-icon safe zone.
function markSvg(color, scale) {
  return `<svg width="1024" height="1024" viewBox="0 0 1024 1024" xmlns="http://www.w3.org/2000/svg">
    ${playMark(color, scale)}
  </svg>`;
}

// Solid gradient background (adaptive-icon background layer).
function bgSvg() {
  return `<svg width="1024" height="1024" viewBox="0 0 1024 1024" xmlns="http://www.w3.org/2000/svg">
    ${gradientDef}
    <rect width="1024" height="1024" fill="url(#g)"/>
  </svg>`;
}

async function render(svg, size, file) {
  const out = path.join(ASSETS, file);
  await sharp(Buffer.from(svg)).resize(size, size).png().toFile(out);
  console.log('wrote', file, `${size}x${size}`);
}

(async () => {
  await render(iconSvg(), 1024, 'icon.png');
  await render(iconSvg(), 196, 'favicon.png');
  await render(markSvg('#ffffff', 0.62), 1024, 'splash-icon.png');
  await render(markSvg('#ffffff', 0.62), 512, 'android-icon-foreground.png');
  await render(bgSvg(), 512, 'android-icon-background.png');
  await render(markSvg('#ffffff', 0.62), 432, 'android-icon-monochrome.png');
  console.log('done');
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
