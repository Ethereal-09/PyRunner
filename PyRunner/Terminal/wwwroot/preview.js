(() => {
  const params = new URLSearchParams(location.search);
  const initial = Math.min(24, Math.max(10, Number.parseInt(params.get('fontSize') || '13', 10)));
  const term = new Terminal({ fontSize: initial, fontFamily: 'Cascadia Mono, Consolas, monospace', disableStdin: true,
    theme: { background: '#0f1215', foreground: '#d9dee4', green: '#67cea0' } });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit); term.open(document.getElementById('terminal')); fit.fit();
  term.write('\x1b[32mPyRunner\x1b[0m  Python  >>> print("Hello")');
  window.chrome.webview.addEventListener('message', event => {
    let message;
    try { message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data; }
    catch { return; }
    const size = Number(message?.fontSize);
    if (message?.type === 'fontSize' && Number.isInteger(size) && size >= 10 && size <= 24) {
      term.options.fontSize = size; requestAnimationFrame(() => fit.fit());
    }
  });
  window.addEventListener('resize', () => fit.fit());
})();
