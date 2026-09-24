import {
  autocompletion,
  type Completion,
  CompletionContext,
  type CompletionResult,
} from "@codemirror/autocomplete";
import { sql } from "@codemirror/lang-sql";
import { lineNumbers } from "@codemirror/view";
import { EditorView, minimalSetup } from "codemirror";
import { useEffect, useEffectEvent, useRef } from "react";
import { puckEditorTheme } from "../ui/codemirror";
import classes from "./SqlEditor.module.css";

const KEYWORDS = [
  "SELECT",
  "FROM",
  "WHERE",
  "GROUP BY",
  "ORDER BY",
  "HAVING",
  "LIMIT",
  "JOIN",
  "LEFT JOIN",
  "ON",
  "AS",
  "AND",
  "OR",
  "NOT",
  "IN",
  "BETWEEN",
  "DISTINCT",
  "DESC",
  "ASC",
  "UNION ALL",
  "WITH",
  "CASE",
  "WHEN",
  "THEN",
  "ELSE",
  "END",
];
const FUNCTIONS = [
  "read_parquet",
  "read_csv_auto",
  "read_json_auto",
  "count",
  "avg",
  "sum",
  "min",
  "max",
  "coalesce",
  "round",
  "cast",
  "lower",
  "upper",
  "strftime",
];

export interface SqlFileCompletion {
  label: string;
  url: string;
}

/** A SQL editor over DuckDB's dialect that completes keywords, readers, and the user's own file URLs. */
export default function SqlEditor({
  files,
  onChange,
  value,
}: {
  files: SqlFileCompletion[];
  onChange: (value: string) => void;
  value: string;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const viewRef = useRef<EditorView>(null);
  // The editor outlives renders; these read the latest props from its own callbacks.
  const currentFiles = useEffectEvent(() => files);
  const emitChange = useEffectEvent((text: string) => onChange(text));

  useEffect(() => {
    if (!containerRef.current) {
      return;
    }

    const completionSource = (
      context: CompletionContext,
    ): CompletionResult | null => {
      const word = context.matchBefore(/[\w./'-]*/);

      if (!word || (word.from === word.to && !context.explicit)) {
        return null;
      }

      const options: Completion[] = [
        ...KEYWORDS.map((keyword) => ({ label: keyword, type: "keyword" })),
        ...FUNCTIONS.map((name) => ({
          apply: `${name}(`,
          label: name,
          type: "function",
        })),
        ...currentFiles().map((file) => ({
          apply: `'${file.url}'`,
          detail: "your file",
          label: file.label,
          type: "text",
        })),
      ];

      return { from: word.from, options };
    };
    const view = new EditorView({
      doc: value,
      extensions: [
        minimalSetup,
        lineNumbers(),
        sql(),
        autocompletion({ override: [completionSource] }),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) {
            emitChange(update.state.doc.toString());
          }
        }),
        EditorView.contentAttributes.of({ "aria-label": "SQL query" }),
        puckEditorTheme,
      ],
      parent: containerRef.current,
    });

    viewRef.current = view;

    return () => {
      viewRef.current = null;
      view.destroy();
    };
    // The editor is created once; value sync happens below.
  }, []);
  useEffect(() => {
    const view = viewRef.current;

    if (view && view.state.doc.toString() !== value) {
      view.dispatch({
        changes: { from: 0, insert: value, to: view.state.doc.length },
      });
    }
  }, [value]);

  return <div className={classes.editor} ref={containerRef} />;
}
