(() => {
  'use strict';
  const protocolVersion = 1;
  const maximumTextLength = 2 * 1024 * 1024;
  let editor = null;
  let dirty = false;

  function send(payload) {
    window.chrome.webview.postMessage(payload);
  }

  function applyTheme(theme) {
    document.documentElement.dataset.theme = theme === 'light' ? 'light' : 'dark';
  }

  window.addEventListener('keydown', event => {
    if ((event.ctrlKey || event.metaKey) && !event.shiftKey && event.key.toLowerCase() === 's') {
      event.preventDefault();
      send({ type: 'saveRequest', version: protocolVersion });
    }
  });

  window.chrome.webview.addEventListener('message', event => {
    let message;
    try { message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data; }
    catch { return; }
    if (!message || typeof message.type !== 'string' || message.version !== protocolVersion) return;

    if (message.type === 'loadDocument') {
      if (typeof message.text !== 'string' || message.text.length > maximumTextLength) return;
      editor?.destroy();
      applyTheme(message.theme);
      editor = window.PyRunnerCodeMirror.create(document.getElementById('editor'), {
        text: message.text,
        readOnly: message.readOnly === true,
        onState(state) {
          if (state.changed) dirty = true;
          send({ type: 'state', version: protocolVersion, dirty, line: state.line, column: state.column });
        },
      });
      dirty = message.dirty === true;
      send({ type: 'state', version: protocolVersion, dirty, line: 1, column: 1 });
      return;
    }

    if (!editor) return;
    switch (message.type) {
      case 'requestText':
        if (typeof message.requestId === 'string')
          send({ type: 'text', version: protocolVersion, requestId: message.requestId, text: editor.getText() });
        break;
      case 'requestAiContext': {
        if (typeof message.requestId !== 'string') break;
        const context = editor.getAiContext();
        send({ type: 'aiContext', version: protocolVersion, requestId: message.requestId,
          text: context.text, selectionStart: context.selectionStart,
          selectionEnd: context.selectionEnd, isSelection: context.isSelection });
        break;
      }
      case 'applyAiCandidate': {
        if (typeof message.requestId !== 'string' || typeof message.expected !== 'string' ||
            typeof message.candidate !== 'string' || !Number.isSafeInteger(message.start) ||
            !Number.isSafeInteger(message.end)) break;
        const applied = editor.replaceChecked(message.start, message.end, message.expected, message.candidate);
        if (applied) dirty = true;
        send({ type: 'aiApplyResult', version: protocolVersion, requestId: message.requestId, applied });
        break;
      }
      case 'markClean': dirty = false; break;
      case 'undo': editor.undo(); break;
      case 'redo': editor.redo(); break;
      case 'focus': editor.focus(); break;
      case 'theme': applyTheme(message.theme); break;
    }
  });

  window.addEventListener('beforeunload', () => { editor?.destroy(); editor = null; });
  send({ type: 'ready', version: protocolVersion });
})();
