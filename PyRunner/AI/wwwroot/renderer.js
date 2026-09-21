(() => {
  'use strict';
  const host = document.getElementById('messages');
  let labels = { copy: '', retry: '', codeFile: '', viewDiff: '', apply: '', discard: '' };
  let userPinned = false;
  let lastCount = 0;

  window.addEventListener('scroll', () => {
    userPinned = document.documentElement.scrollHeight - window.scrollY - window.innerHeight > 90;
  }, { passive: true });

  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }

  function actionButton(text, action, payload) {
    const button = node('button', '', text);
    button.type = 'button';
    button.addEventListener('click', () => chrome.webview.postMessage({ type: action, ...payload }));
    return button;
  }

  function appendHighlighted(parent, source) {
    const pattern = /(#.*$)|("""[\s\S]*?"""|'''[\s\S]*?'''|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')|\b(\d+(?:\.\d+)?)\b|\b(def|class|return|raise|try|except|finally|if|elif|else|for|while|in|is|not|and|or|import|from|as|with|async|await|yield|lambda|True|False|None)\b|\b([A-Za-z_]\w*)(?=\s*\()/gm;
    let index = 0;
    for (const match of source.matchAll(pattern)) {
      if (match.index > index) parent.append(document.createTextNode(source.slice(index, match.index)));
      const kind = match[1] ? 'comment' : match[2] ? 'string' : match[3] ? 'number' : match[4] ? 'keyword' : 'call';
      parent.append(node('span', `tok-${kind}`, match[0]));
      index = match.index + match[0].length;
    }
    if (index < source.length) parent.append(document.createTextNode(source.slice(index)));
  }

  function codeBlock(language, filename, code) {
    const block = node('section', 'code-block');
    const header = node('div', 'code-header');
    header.append(node('span', 'filename', filename || labels.codeFile));
    header.append(node('span', 'language', language || 'text'));
    header.append(actionButton(labels.copy, 'copy', { text: code }));
    block.append(header);
    const scroll = node('div', 'code-scroll');
    const pre = node('pre');
    code.replace(/\r\n?/g, '\n').split('\n').forEach((line, i) => {
      const row = node('div', 'code-line');
      row.append(node('span', 'line-no', String(i + 1)));
      const body = node('span', 'line-code');
      if ((language || '').toLowerCase().startsWith('py')) appendHighlighted(body, line);
      else body.textContent = line;
      row.append(body); pre.append(row);
    });
    scroll.append(pre); block.append(scroll); return block;
  }

  function markdown(text) {
    const fragment = document.createDocumentFragment();
    const lines = String(text || '').replace(/\r\n?/g, '\n').split('\n');
    let paragraph = [];
    const flush = () => {
      if (!paragraph.length) return;
      fragment.append(node('p', '', paragraph.join('\n'))); paragraph = [];
    };
    for (let i = 0; i < lines.length; i++) {
      const fence = lines[i].match(/^```([\w+-]*)?(?:\s+(.+))?$/);
      if (fence) {
        flush(); const body = [];
        while (++i < lines.length && !/^```\s*$/.test(lines[i])) body.push(lines[i]);
        fragment.append(codeBlock(fence[1] || 'text', fence[2] || '', body.join('\n'))); continue;
      }
      const heading = lines[i].match(/^(#{1,3})\s+(.+)$/);
      if (heading) { flush(); fragment.append(node(`h${heading[1].length}`, '', heading[2])); continue; }
      if (/^[-*]\s+/.test(lines[i])) {
        flush(); const list = node('ul');
        while (i < lines.length && /^[-*]\s+/.test(lines[i])) {
          list.append(node('li', '', lines[i].replace(/^[-*]\s+/, ''))); i++;
        }
        i--; fragment.append(list); continue;
      }
      if (!lines[i].trim()) { flush(); continue; }
      paragraph.push(lines[i]);
    }
    flush(); return fragment;
  }

  function renderMessage(message) {
    if (message.kind === 'error' || message.kind === 'cancelled' || message.kind === 'analysisstatus') {
      const status = node('div', message.kind === 'analysisstatus' ? 'status' : message.kind);
      if (message.kind === 'analysisstatus') status.append(node('span', 'status-icon', '✓'));
      status.append(document.createTextNode(message.content));
      if (message.kind === 'error') status.append(actionButton(labels.retry, 'retry', {}));
      return status;
    }
    const article = node('article', `message ${message.role === 'user' ? 'user' : 'assistant'}`);
    if (message.role !== 'user') {
      const copy = actionButton(labels.copy, 'copy', { text: message.content });
      copy.className = 'copy-message';
      article.append(copy);
    }
    const body = node('div', 'body'); body.append(markdown(message.content)); article.append(body);
    article.append(node('div', 'meta', message.time)); return article;
  }

  function renderProposal(proposal) {
    const card = node('section', 'proposal');
    const summary = node('div', 'proposal-summary');
    summary.append(node('span', 'proposal-icon'));
    const copy = node('div', 'proposal-copy');
    copy.append(node('strong', '', proposal.title));
    copy.append(node('span', '', proposal.summary));
    summary.append(copy); card.append(summary);
    const actions = node('div', 'proposal-actions');
    actions.append(node('span', 'added', `+${proposal.added}`));
    actions.append(node('span', 'deleted', `−${proposal.deleted}`));
    const discard = actionButton(labels.discard, 'discard', {}); discard.className = 'proposal-discard';
    actions.append(discard);
    actions.append(actionButton(labels.viewDiff, 'view-diff', {}));
    const apply = actionButton(labels.apply, 'apply', {}); apply.className = 'proposal-apply';
    actions.append(apply); card.append(actions);
    return card;
  }

  chrome.webview.addEventListener('message', event => {
    const payload = event.data;
    if (!payload || payload.type !== 'render' || !Array.isArray(payload.messages)) return;
    labels = payload.labels || labels;
    document.body.className = payload.theme === 'light' ? 'light' : 'dark';
    document.documentElement.style.setProperty('--surface', payload.surface || 'transparent');
    const wasPinned = userPinned;
    const content = payload.messages.map(renderMessage);
    if (payload.proposal) content.push(renderProposal(payload.proposal));
    host.replaceChildren(...content);
    if (!wasPinned || payload.messages.length > lastCount && payload.messages.at(-1)?.role === 'user')
      window.scrollTo({ top: document.documentElement.scrollHeight, behavior: 'instant' });
    lastCount = payload.messages.length;
  });
})();
