// HTTP media file serving with Range support — what makes <video> seeking
// work. Kept free of Electron imports so it can be unit-tested in plain Node.

const fs = require('fs');
const path = require('path');

const MEDIA_MIME = {
  '.mp4': 'video/mp4',
  '.m4v': 'video/mp4',
  '.mkv': 'video/x-matroska',
  '.webm': 'video/webm',
  '.mov': 'video/quicktime',
  '.avi': 'video/x-msvideo',
  '.wmv': 'video/x-ms-wmv',
  '.flv': 'video/x-flv',
  '.mpg': 'video/mpeg',
  '.mpeg': 'video/mpeg',
  '.ogv': 'video/ogg',
  '.3gp': 'video/3gpp',
  '.ts': 'video/mp2t',
};

/**
 * Serve a local media file with HTTP Range support.
 * `u` is a parsed URL with `src` (absolute file path) and `token` params;
 * requests whose token doesn't match `expectedToken` are rejected.
 */
function serveMedia(u, req, res, expectedToken) {
  if (u.searchParams.get('token') !== expectedToken) {
    res.writeHead(403);
    return res.end('Forbidden');
  }
  const src = u.searchParams.get('src') || '';
  let st;
  try {
    st = fs.statSync(src);
  } catch {
    res.writeHead(404);
    return res.end('Not found');
  }
  if (!st.isFile()) {
    res.writeHead(404);
    return res.end('Not found');
  }

  const headers = {
    'Content-Type': MEDIA_MIME[path.extname(src).toLowerCase()] || 'application/octet-stream',
    'Accept-Ranges': 'bytes',
    // Needed so thumbnail canvas capture isn't tainted in dev (cross-port origin).
    'Access-Control-Allow-Origin': '*',
    'Cache-Control': 'no-store',
  };

  const m = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range || '');
  if (m && (m[1] || m[2])) {
    const start = m[1] ? parseInt(m[1], 10) : Math.max(0, st.size - parseInt(m[2], 10));
    const end = m[1] && m[2] ? Math.min(parseInt(m[2], 10), st.size - 1) : st.size - 1;
    if (Number.isNaN(start) || start > end || start >= st.size) {
      res.writeHead(416, { 'Content-Range': `bytes */${st.size}` });
      return res.end();
    }
    res.writeHead(206, {
      ...headers,
      'Content-Range': `bytes ${start}-${end}/${st.size}`,
      'Content-Length': end - start + 1,
    });
    fs.createReadStream(src, { start, end }).pipe(res);
  } else {
    res.writeHead(200, { ...headers, 'Content-Length': st.size });
    fs.createReadStream(src).pipe(res);
  }
}

module.exports = { serveMedia, MEDIA_MIME };
