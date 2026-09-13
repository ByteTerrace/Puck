const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { registerContainerColors } = require('../container-colors');

test('language switches clear Puck decorations and switching back reapplies them', async () => {
    const events = {};
    const on = name => callback => { events[name] = callback; return {dispose() {}}; };
    const applied = new Map();
    const document = {languageId:'puck',version:1,uri:{},getText:()=> 'noise { values [1] }'};
    const editor = {document,setDecorations:(decoration,ranges)=>applied.set(decoration.id,ranges)};
    const context = {extensionPath:path.resolve(__dirname,'..'),subscriptions:[]};
    const vscode = {
        ThemeColor: class { constructor(id) { this.id = id; } },
        Range: class {},
        window: {visibleTextEditors:[editor],createTextEditorDecorationType:options=>({id:options.color.id,dispose(){}}),onDidChangeVisibleTextEditors:on('visible')},
        workspace: {
            getConfiguration:()=>({get:(_key,fallback)=>fallback}),
            onDidOpenTextDocument:on('open'),onDidCloseTextDocument:on('close'),
            onDidChangeTextDocument:on('change'),onDidChangeConfiguration:on('configuration')
        }
    };
    try {
        await registerContainerColors(vscode,context);
        assert.ok(applied.get('puck.arrayDelimiter').length > 0);
        editor.document = {...document, languageId:'plaintext'};
        events.close(document);
        events.open(editor.document);
        assert.ok([...applied.values()].every(ranges=>ranges.length===0));
        editor.document = {...document};
        events.open(editor.document);
        assert.ok(applied.get('puck.objectDelimiter').length > 0);
    } finally {
        for (const disposable of context.subscriptions) disposable.dispose();
    }
});
