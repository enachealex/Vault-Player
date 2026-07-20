// Preload: exposes a minimal, typed desktop bridge to the renderer.
// The renderer's videoFolder service detects `window.desktopBridge` and routes
// folder picking / media streaming through Electron instead of the browser's
// File System Access API — giving us real file paths and ffmpeg conversion.

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('desktopBridge', {
  pickFolder: () => ipcRenderer.invoke('vp-pick-folder'),
  getLastFolder: () => ipcRenderer.invoke('vp-get-last-folder'),
  reopenLast: () => ipcRenderer.invoke('vp-reopen-last'),
  prepareMedia: (p, startAt) => ipcRenderer.invoke('vp-prepare-media', p, startAt),
  streamUrl: (p, t) => ipcRenderer.invoke('vp-stream-url', p, t),
  directUrl: (p) => ipcRenderer.invoke('vp-direct-url', p),
  readText: (p) => ipcRenderer.invoke('vp-read-text', p),
  onPrepareProgress: (cb) => {
    const handler = (_event, data) => cb(data);
    ipcRenderer.on('vp-prepare-progress', handler);
    return () => ipcRenderer.removeListener('vp-prepare-progress', handler);
  },
});
