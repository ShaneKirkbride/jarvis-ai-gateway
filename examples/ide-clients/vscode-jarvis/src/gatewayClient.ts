export interface GatewayModel {
  id: string;
  display_name?: string;
  provider?: string;
  supports_streaming?: boolean;
}

export interface ChatMessage {
  role: 'system' | 'user' | 'assistant';
  content: string;
}

export interface GatewayClientOptions {
  baseUrl: string;
  apiKey: string;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
}

export class GatewayClientError extends Error {
  public constructor(message: string, public readonly statusCode?: number) {
    super(message);
    this.name = 'GatewayClientError';
  }
}

export class GatewayClient {
  private readonly fetchImpl: typeof fetch;
  private readonly timeoutMs: number;

  public constructor(private readonly options: GatewayClientOptions) {
    this.fetchImpl = options.fetchImpl ?? fetch;
    this.timeoutMs = options.timeoutMs ?? 60_000;
  }

  public async listModels(signal?: AbortSignal): Promise<GatewayModel[]> {
    const response = await this.send('/models', { method: 'GET', signal });
    const payload = await this.readJson(response);
    if (!Array.isArray(payload?.data)) {
      return [];
    }

    return payload.data
      .filter((item: unknown): item is GatewayModel => typeof (item as GatewayModel)?.id === 'string')
      .map((item: GatewayModel) => ({
        id: item.id,
        display_name: typeof item.display_name === 'string' ? item.display_name : undefined,
        provider: typeof item.provider === 'string' ? item.provider : undefined,
        supports_streaming: typeof item.supports_streaming === 'boolean' ? item.supports_streaming : undefined
      }));
  }

  public async completeChat(model: string, messages: ChatMessage[], signal?: AbortSignal): Promise<string> {
    if (!model.trim()) {
      throw new GatewayClientError('A Jarvis model must be configured.');
    }

    if (messages.length === 0) {
      throw new GatewayClientError('At least one message is required.');
    }

    const response = await this.send('/chat/completions', {
      method: 'POST',
      signal,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ model, messages, stream: false })
    });
    const payload = await this.readJson(response);
    const content = payload?.choices?.[0]?.message?.content;
    return typeof content === 'string' ? content : '';
  }

  private async send(path: string, init: RequestInit): Promise<Response> {
    const baseUrl = normalizeBaseUrl(this.options.baseUrl);
    if (!baseUrl) {
      throw new GatewayClientError('Configure jarvis.baseUrl before calling Jarvis.');
    }

    if (!this.options.apiKey.startsWith('jrvs_')) {
      throw new GatewayClientError('Configure a valid Jarvis developer API key.');
    }

    let response: Response;
    const timeout = createTimeoutSignal(this.timeoutMs, init.signal);
    try {
      response = await this.fetchImpl(`${baseUrl}${path}`, {
        ...init,
        signal: timeout.signal,
        headers: {
          Authorization: `Bearer ${this.options.apiKey}`,
          Accept: 'application/json',
          ...(init.headers ?? {})
        }
      });
    } catch (error) {
      throw new GatewayClientError(error instanceof Error ? error.message : 'Unable to reach Jarvis gateway.');
    } finally {
      timeout.dispose();
    }

    if (!response.ok) {
      const message = await safeReadGatewayError(response);
      throw new GatewayClientError(message, response.status);
    }

    return response;
  }

  private async readJson(response: Response): Promise<any> {
    try {
      return await response.json();
    } catch {
      throw new GatewayClientError('Jarvis returned an invalid JSON response.', response.status);
    }
  }
}

export function normalizeBaseUrl(value: string): string | undefined {
  const trimmed = value.trim().replace(/\/+$/, '');
  if (!trimmed || trimmed.includes('<gateway>')) {
    return undefined;
  }

  return trimmed.endsWith('/v1') ? trimmed : `${trimmed}/v1`;
}

async function safeReadGatewayError(response: Response): Promise<string> {
  try {
    const payload = await response.clone().json();
    const gatewayMessage = payload?.error?.message;
    if (typeof gatewayMessage === 'string' && gatewayMessage.trim()) {
      return gatewayMessage;
    }
  } catch {
    // Ignore malformed error bodies and return a generic message below.
  }

  return `Jarvis request failed with HTTP ${response.status}.`;
}


function createTimeoutSignal(timeoutMs: number, parent?: AbortSignal | null): { signal: AbortSignal; dispose: () => void } {
  const controller = new AbortController();
  const timeoutHandle = setTimeout(() => controller.abort(new Error('Jarvis request timed out.')), Math.max(1, timeoutMs));

  const abortFromParent = () => controller.abort(parent?.reason);
  if (parent?.aborted) {
    abortFromParent();
  } else {
    parent?.addEventListener('abort', abortFromParent, { once: true });
  }

  return {
    signal: controller.signal,
    dispose: () => {
      clearTimeout(timeoutHandle);
      parent?.removeEventListener('abort', abortFromParent);
    }
  };
}
