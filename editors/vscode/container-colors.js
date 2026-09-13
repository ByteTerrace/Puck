// Skip strings, quoted identifiers, and comments before collecting structural delimiters.
function collectDelimiterOffsets(text) {
    const arrays = [];
    const objects = [];
    const tokens = /\/\/[^\r\n]*|\/\*[\s\S]*?(?:\*\/|$)|"(?:\\[\s\S]|[^"\\])*(?:"|$)|`(?:\\[\s\S]|[^`\\])*(?:`|$)|[\[\]{}]/g;
    for (const match of text.matchAll(tokens)) {
        if (match[0].length !== 1) continue;
        if (match[0] === '[' || match[0] === ']') arrays.push(match.index);
        if (match[0] === '{' || match[0] === '}') objects.push(match.index);
    }
    return { arrays, objects };
}

function registerContainerColors(vscode, context) {
    const arrays = vscode.window.createTextEditorDecorationType({ color: new vscode.ThemeColor('puck.arrayDelimiter') });
    const objects = vscode.window.createTextEditorDecorationType({ color: new vscode.ThemeColor('puck.objectDelimiter') });
    const cache = new WeakMap();
    let timer;
    function refresh() {
        for (const editor of vscode.window.visibleTextEditors) {
            const document = editor.document;
            if (document.languageId !== 'puck') continue;
            if (!vscode.workspace.getConfiguration('puck', document.uri).get('highlighting.containerColors', true)) {
                editor.setDecorations(arrays, []);
                editor.setDecorations(objects, []);
                continue;
            }
            let entry = cache.get(document);
            if (!entry || entry.version !== document.version) {
                const offsets = collectDelimiterOffsets(document.getText());
                const ranges = positions => positions.map(offset => new vscode.Range(document.positionAt(offset), document.positionAt(offset + 1)));
                entry = { version: document.version, arrays: ranges(offsets.arrays), objects: ranges(offsets.objects) };
                cache.set(document, entry);
            }
            editor.setDecorations(arrays, entry.arrays);
            editor.setDecorations(objects, entry.objects);
        }
    }
    function schedule() {
        clearTimeout(timer);
        timer = setTimeout(refresh, 50);
    }
    context.subscriptions.push(arrays, objects,
        vscode.window.onDidChangeVisibleTextEditors(refresh),
        vscode.workspace.onDidChangeTextDocument(event => {
            if (event.document.languageId === 'puck') schedule();
        }),
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('puck.highlighting.containerColors')) refresh();
        }),
        { dispose() { clearTimeout(timer); } });
    refresh();
}

module.exports = { collectDelimiterOffsets, registerContainerColors };
