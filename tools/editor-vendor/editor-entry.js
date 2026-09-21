import { basicSetup } from "codemirror";
import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { undo, redo } from "@codemirror/commands";
import { python } from "@codemirror/lang-python";

window.PyRunnerCodeMirror = {
  create(parent, options) {
    const listener = EditorView.updateListener.of(update => {
      if (!update.docChanged && !update.selectionSet) return;
      const head = update.state.selection.main.head;
      const line = update.state.doc.lineAt(head);
      options.onState({
        changed: update.docChanged,
        line: line.number,
        column: head - line.from + 1,
      });
    });
    const theme = EditorView.theme({
      "&": { height: "100%", fontSize: "13px" },
      ".cm-scroller": { fontFamily: "Cascadia Mono, Consolas, monospace", overflow: "auto" },
      ".cm-content": { caretColor: "#67cea0" },
      ".cm-gutters": { borderRight: "1px solid var(--border)" },
    });
    const state = EditorState.create({
      doc: options.text || "",
      extensions: [
        basicSetup,
        python(),
        theme,
        EditorView.editable.of(!options.readOnly),
        EditorState.readOnly.of(!!options.readOnly),
        listener,
      ],
    });
    const view = new EditorView({ state, parent });
    return {
      getText: () => view.state.doc.toString(),
      getAiContext() {
        const range = view.state.selection.main;
        const isSelection = range.from !== range.to;
        return {
          text: isSelection ? view.state.sliceDoc(range.from, range.to) : view.state.doc.toString(),
          selectionStart: isSelection ? range.from : 0,
          selectionEnd: isSelection ? range.to : view.state.doc.length,
          isSelection,
        };
      },
      replaceChecked(from, to, expected, candidate) {
        if (from < 0 || to < from || to > view.state.doc.length ||
            view.state.sliceDoc(from, to) !== expected) return false;
        view.dispatch({
          changes: { from, to, insert: candidate },
          selection: { anchor: from + candidate.length },
        });
        view.focus();
        return true;
      },
      setText(text) {
        view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: text || "" } });
      },
      undo: () => undo(view),
      redo: () => redo(view),
      focus: () => view.focus(),
      destroy: () => view.destroy(),
    };
  },
};
