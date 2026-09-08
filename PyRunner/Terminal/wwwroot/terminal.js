// PyRunner 终端前端：xterm.js ⇄ WebView2 PostMessage ⇄ 后端 ConPTY
const terminalThemes = {
  dark: {
    background: '#0f1215',
    foreground: '#d9dee4',
    cursor: '#67cea0',
    cursorAccent: '#0f1215',
    selectionBackground: '#245440',
    yellow: '#e6c07b',
    brightYellow: '#ffd479',
    red: '#ff8f8f',
    green: '#67cea0',
    blue: '#86b7e8',
  },
  light: {
    background: '#f6f8fa',
    foreground: '#20262e',
    cursor: '#2f9e70',
    cursorAccent: '#f6f8fa',
    selectionBackground: '#dcefe7',
    yellow: '#8a5b00',
    brightYellow: '#a06a00',
    red: '#b42332',
    green: '#27865e',
    blue: '#245e91',
  },
};

const initialThemeName = document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
const term = new Terminal({
  cursorBlink: true,
  fontSize: 13,
  lineHeight: 1.7,
  fontFamily: 'Cascadia Mono, Consolas, monospace',
  theme: terminalThemes[initialThemeName],
  minimumContrastRatio: 4.5,
});
const fitAddon = new FitAddon.FitAddon();
term.loadAddon(fitAddon);
const terminalElement = document.getElementById('terminal');
term.open(terminalElement);
fitAddon.fit();

function sendToHost(msg) {
  window.chrome.webview.postMessage(msg);
}

// 后端 PostWebMessageAsString 发送的是 JSON 字符串，e.data 为 string，需要 JSON.parse。
// 畸形消息（解析/解码失败）静默丢弃，不向外抛异常。
window.chrome.webview.addEventListener('message', (e) => {
  let msg;
  try {
    msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
  } catch {
    return;
  }
  if (msg.type === 'output') {
    try {
      const bytes = Uint8Array.from(atob(msg.data), (c) => c.charCodeAt(0));
      term.write(bytes);
    } catch {
      // 非法 base64，丢弃
    }
  } else if (msg.type === 'exit') {
    const message = typeof msg.message === 'string' ? msg.message : '';
    term.write('\r\n\x1b[33m[' + message + ']\x1b[0m\r\n');
  } else if (msg.type === 'clear') {
    // 运行结束自动清空：清除滚动区并将光标移回左上角。
    term.clear();
    term.write('\x1b[2J\x1b[H');
  } else if (msg.type === 'requestSelection') {
    sendToHost({ type: 'selection', data: term.getSelection() || '' });
  } else if (msg.type === 'theme') {
    const themeName = msg.theme === 'light' ? 'light' : 'dark';
    document.documentElement.dataset.theme = themeName;
    term.options.theme = terminalThemes[themeName];
  } else if (msg.type === 'connected') {
    // 后端就绪：同步当前终端尺寸，保证 ConPTY 与 xterm 行列一致
    sendToHost({ type: 'resize', cols: term.cols, rows: term.rows });
  }
});

term.onData((data) => {
  sendToHost({ type: 'input', data });
});

term.onResize(({ cols, rows }) => {
  sendToHost({ type: 'resize', cols, rows });
});

// 在 DOM 捕获阶段接管终端剪贴板快捷键，阻止 Chromium 的原生 paste/copy
// 以及 xterm 将 Ctrl+Shift+C 继续翻译为 ETX。普通 Ctrl+C 不拦截，仍用于中断 Python。
document.addEventListener('keydown', (event) => {
  if (!event.ctrlKey || !event.shiftKey) return;

  const key = (event.key || '').toLowerCase();
  if (key !== 'c' && key !== 'v') return;

  event.preventDefault();
  event.stopImmediatePropagation();
  if (key === 'c') {
    sendToHost({ type: 'copyRequest', data: term.getSelection() || '' });
  } else {
    sendToHost({ type: 'pasteRequest' });
  }
}, true);

// 某些 WebView2 版本即使 keydown 已取消仍会派发 paste；在终端子树捕获并拦截，
// 粘贴内容只由宿主读取系统剪贴板后写入当前 ConPTY 一次。
document.addEventListener('paste', (event) => {
  if (!terminalElement.contains(event.target)) return;
  event.preventDefault();
  event.stopImmediatePropagation();
}, true);

// xterm 级处理作为浏览器事件未到达 document 时的后备防线。
term.attachCustomKeyEventHandler((event) => {
  if (!event.ctrlKey || !event.shiftKey) return true;

  const key = (event.key || '').toLowerCase();
  return key !== 'c' && key !== 'v';
});

// Chromium 默认菜单已由宿主关闭；右键在终端区域直接粘贴到当前会话。
terminalElement.addEventListener('contextmenu', (event) => {
  event.preventDefault();
  term.focus();
  sendToHost({ type: 'pasteRequest' });
});

window.addEventListener('resize', () => {
  fitAddon.fit();
});

sendToHost({ type: 'ready' });
