import * as vscode from 'vscode';
import { GatewayClient, GatewayClientError, normalizeBaseUrl } from './gatewayClient';

const secretKey = 'jarvis.developerApiKey';

export function activate(context: vscode.ExtensionContext): void {
  const statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  statusBar.text = 'Jarvis: not connected';
  statusBar.command = 'jarvis.refreshModels';
  statusBar.show();
  context.subscriptions.push(statusBar);

  context.subscriptions.push(vscode.commands.registerCommand('jarvis.configureApiKey', async () => {
    const apiKey = await vscode.window.showInputBox({
      title: 'Jarvis Developer API Key',
      prompt: 'Paste your jrvs_ developer API key. It will be stored in VS Code SecretStorage.',
      password: true,
      ignoreFocusOut: true,
      validateInput: (value) => value.startsWith('jrvs_') ? undefined : 'Jarvis developer keys start with jrvs_.'
    });

    if (apiKey) {
      await context.secrets.store(secretKey, apiKey);
      vscode.window.showInformationMessage('Jarvis API key saved securely.');
    }
  }));

  context.subscriptions.push(vscode.commands.registerCommand('jarvis.refreshModels', async () => {
    await refreshModels(context, statusBar);
  }));

  context.subscriptions.push(vscode.commands.registerCommand('jarvis.askSelection', async () => {
    await askAboutSelection(context, statusBar);
  }));
}

export function deactivate(): void { }

async function askAboutSelection(context: vscode.ExtensionContext, statusBar: vscode.StatusBarItem): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    vscode.window.showWarningMessage('Open a file and select text before asking Jarvis.');
    return;
  }

  const config = vscode.workspace.getConfiguration('jarvis');
  if (config.get<boolean>('sendSelectionOnly', true) && editor.selection.isEmpty) {
    vscode.window.showWarningMessage('Select the code or text to send to Jarvis. The example extension does not scan the workspace.');
    return;
  }

  const selectedText = editor.document.getText(editor.selection);
  if (!selectedText.trim()) {
    vscode.window.showWarningMessage('The selected text is empty.');
    return;
  }

  const maxSelectionCharacters = config.get<number>('maxSelectionCharacters', 8000);
  if (selectedText.length > maxSelectionCharacters) {
    vscode.window.showWarningMessage(`Selection is ${selectedText.length} characters; reduce it below jarvis.maxSelectionCharacters (${maxSelectionCharacters}).`);
    return;
  }

  if (config.get<boolean>('confirmBeforeSend', true)) {
    const answer = await vscode.window.showWarningMessage(
      `Send ${selectedText.length} selected characters to Jarvis? Gateway policy, redaction, and audit controls still apply.`,
      { modal: true },
      'Send'
    );
    if (answer !== 'Send') {
      return;
    }
  }

  const client = await createClient(context);
  if (!client) {
    return;
  }

  const model = config.get<string>('model', 'jarvis2-chat');
  await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Asking Jarvis...' }, async (_progress, token) => {
    const abort = new AbortController();
    token.onCancellationRequested(() => abort.abort());
    try {
      const content = await client.completeChat(model, [
        { role: 'system', content: 'You are a careful coding assistant. Answer concisely and call out uncertainty.' },
        { role: 'user', content: `Review this selected code or text:\n\n${selectedText}` }
      ], abort.signal);
      await showMarkdownResponse(content || '(Jarvis returned an empty response.)');
      await refreshModels(context, statusBar);
    } catch (error) {
      showGatewayError(error);
    }
  });
}

async function refreshModels(context: vscode.ExtensionContext, statusBar: vscode.StatusBarItem): Promise<void> {
  const client = await createClient(context, false);
  if (!client) {
    statusBar.text = 'Jarvis: configure key/base URL';
    return;
  }

  try {
    const models = await client.listModels();
    const configuredModel = vscode.workspace.getConfiguration('jarvis').get<string>('model', 'jarvis2-chat');
    const activeModel = models.find((model) => model.id === configuredModel);
    statusBar.text = activeModel ? `Jarvis: ${activeModel.display_name ?? activeModel.id}` : `Jarvis: ${configuredModel}`;
  } catch {
    statusBar.text = 'Jarvis: unavailable';
  }
}

async function createClient(context: vscode.ExtensionContext, showErrors = true): Promise<GatewayClient | undefined> {
  const config = vscode.workspace.getConfiguration('jarvis');
  const baseUrl = config.get<string>('baseUrl', '');
  const normalizedBaseUrl = normalizeBaseUrl(baseUrl);
  const apiKey = await context.secrets.get(secretKey);

  if (!normalizedBaseUrl) {
    if (showErrors) vscode.window.showWarningMessage('Configure jarvis.baseUrl with your gateway /v1 URL.');
    return undefined;
  }

  if (!apiKey) {
    if (showErrors) vscode.window.showWarningMessage('Run "Jarvis: Configure Developer API Key" before calling Jarvis.');
    return undefined;
  }

  return new GatewayClient({ baseUrl: normalizedBaseUrl, apiKey });
}

async function showMarkdownResponse(content: string): Promise<void> {
  const document = await vscode.workspace.openTextDocument({ content, language: 'markdown' });
  await vscode.window.showTextDocument(document, { preview: true });
}

function showGatewayError(error: unknown): void {
  if (error instanceof GatewayClientError) {
    vscode.window.showErrorMessage(error.statusCode ? `Jarvis error (${error.statusCode}): ${error.message}` : `Jarvis error: ${error.message}`);
    return;
  }

  vscode.window.showErrorMessage(error instanceof Error ? error.message : 'Jarvis request failed.');
}
