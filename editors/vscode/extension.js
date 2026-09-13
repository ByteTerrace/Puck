const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

let client;
let pending = Promise.resolve();

async function restart() {
    if (client) {
        await client.dispose();
        client = undefined;
    }
    const configuration = vscode.workspace.getConfiguration('puck');
    client = new LanguageClient('puck', 'Puck Language Server', {
        command: configuration.get('server.command', 'puck'),
        // Omitting transport uses stdio without appending a --stdio argument.
        args: configuration.get('server.args', ['lsp'])
    }, {
        documentSelector: [
            { scheme: 'file', language: 'puck' },
            { scheme: 'untitled', language: 'puck' }
        ]
    });
    await client.start();
}

function queueRestart() {
    pending = pending.then(restart).catch(error => {
        void vscode.window.showErrorMessage(
            `Puck IntelliSense could not start: ${error.message}. Check puck.server.command and puck.server.args, then run Puck: Restart Language Server.`
        );
    });
    return pending;
}

async function activate(context) {
    require('./container-colors').registerContainerColors(vscode, context);
    context.subscriptions.push(
        vscode.commands.registerCommand('puck.restartLanguageServer', queueRestart),
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('puck.server')) {
                void queueRestart();
            }
        })
    );
    await queueRestart();
}

async function deactivate() {
    await pending;
    if (client) {
        await client.dispose();
        client = undefined;
    }
}

module.exports = { activate, deactivate };
