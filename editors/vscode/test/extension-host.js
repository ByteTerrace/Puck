const assert = require('node:assert/strict');
const vscode = require('vscode');

exports.run = async function () {
    const extension = vscode.extensions.getExtension('ByteTerrace.vscode-puck');
    assert.ok(extension, 'Extension is installed in the development host');
    await extension.activate();
    const document = await vscode.workspace.openTextDocument({ language: 'puck', content: 'schema: "puck.world.def.v1"\n\nhost {\nwidth:1280\n}\n' });
    await vscode.window.showTextDocument(document);
    const completions = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, new vscode.Position(1, 0));
    assert.ok(completions.items.some(item => item.label === 'compareState'), 'Server completion reaches the editor');
    const hover = await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, new vscode.Position(0, 2));
    assert.ok(hover.length > 0, 'Server hover reaches the editor');
    const edits = await vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', document.uri, { tabSize: 4, insertSpaces: true });
    assert.ok(edits.length > 0, 'Server formatting reaches the editor');
    await vscode.commands.executeCommand('puck.restartLanguageServer');
    const afterRestart = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, new vscode.Position(1, 0));
    assert.ok(afterRestart.items.length > 0, 'Restart resynchronizes open documents');
    const hoverDocument = await vscode.workspace.openTextDocument({ language: 'puck', content: '// Number of seats.\nlet seats = 4\ncount: seats\nvalues: range(0, seats)\n' });
    await vscode.window.showTextDocument(hoverDocument);
    const variableHover = await vscode.commands.executeCommand('vscode.executeHoverProvider', hoverDocument.uri, new vscode.Position(2, 8));
    assert.ok(variableHover.some(card => card.contents.some(content => content.value.includes('let seats = 4') && content.value.includes('Number of seats.'))), 'Variable declaration and comments reach the popup');
    const functionHover = await vscode.commands.executeCommand('vscode.executeHoverProvider', hoverDocument.uri, new vscode.Position(3, 10));
    assert.ok(functionHover.some(card => card.contents.some(content => content.value.includes('range(start, count)'))), 'Function signature reaches the popup');
    const courtyard = await vscode.workspace.openTextDocument(require('node:path').resolve(__dirname, '../../../src/Puck.World/Assets/worlds/moth-courtyard.puck'));
    await vscode.window.showTextDocument(courtyard);
    const limestoneStart = courtyard.getText().indexOf('name: "weathered-limestone"');
    assert.ok(limestoneStart >= 0);
    for (const [token, expected] of [['roughness:', 'GGX roughness'], ['frequency:', 'lattice frequency'], ['exponent:', 'generalizing exponent'], ['material:', 'palette slot'], ['blend:', 'blend op']]) {
        const offset = courtyard.getText().indexOf(token, limestoneStart);
        assert.ok(offset >= 0);
        const cards = await vscode.commands.executeCommand('vscode.executeHoverProvider', courtyard.uri, courtyard.positionAt(offset + 1));
        assert.ok(cards.some(card => card.contents.some(content => content.value.includes(expected))), `Courtyard hover explains ${token}`);
    }
    assert.ok(!vscode.languages.getDiagnostics(courtyard.uri).some(diagnostic => diagnostic.code === 'PUCK035'), 'Editor URI resolves the courtyard basis on disk');
    const layoutSource = 'stations [{ index: 0, p [0, 0, 0], yaw: 0 }]';
    const layoutDocument = await vscode.workspace.openTextDocument({language:'puck',content:layoutSource});
    const layoutEditor = await vscode.window.showTextDocument(layoutDocument);
    assert.equal(layoutEditor.options.tabSize, 2, 'Puck defaults to two spaces in this uncustomized test profile');
    for (const [tabSize, insertSpaces, indent] of [[2, true, '  '], [4, true, '    '], [4, false, '\t']]) {
        const layoutEdits = await vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', layoutDocument.uri, {tabSize, insertSpaces});
        let formatted = layoutSource;
        for (const edit of [...layoutEdits].sort((a,b)=>layoutDocument.offsetAt(b.range.start)-layoutDocument.offsetAt(a.range.start))) {
            formatted = formatted.slice(0,layoutDocument.offsetAt(edit.range.start)) + edit.newText + formatted.slice(layoutDocument.offsetAt(edit.range.end));
        }
        assert.equal(formatted.replace(/\r\n/g, '\n'), `stations [\n${indent}{\n${indent}${indent}index: 0,\n${indent}${indent}p [0, 0, 0],\n${indent}${indent}yaw: 0\n${indent}}\n]\n`);
    }
    const plainDocument = await vscode.languages.setTextDocumentLanguage(layoutDocument, 'plaintext');
    await vscode.languages.setTextDocumentLanguage(plainDocument, 'puck');
    console.log('Puck IntelliSense extension-host checks passed.');
};
