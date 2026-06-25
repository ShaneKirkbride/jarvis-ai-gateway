import assert from 'node:assert/strict';
import { GatewayClient, GatewayClientError, normalizeBaseUrl } from '../src/gatewayClient';

async function run(): Promise<void> {
  assert.equal(normalizeBaseUrl(' https://gateway.example/v1/ '), 'https://gateway.example/v1');
  assert.equal(normalizeBaseUrl('https://gateway.example'), 'https://gateway.example/v1');
  assert.equal(normalizeBaseUrl('https://<gateway>/v1'), undefined);

  const modelsClient = new GatewayClient({
    baseUrl: 'https://gateway.example/v1',
    apiKey: 'jrvs_test',
    fetchImpl: async () => new Response(JSON.stringify({ data: [{ id: 'jarvis2-chat', display_name: 'Jarvis 2' }, { bad: true }] }), { status: 200 })
  });
  const models = await modelsClient.listModels();
  assert.deepEqual(models, [{ id: 'jarvis2-chat', display_name: 'Jarvis 2', provider: undefined, supports_streaming: undefined }]);

  const chatClient = new GatewayClient({
    baseUrl: 'https://gateway.example',
    apiKey: 'jrvs_test',
    fetchImpl: async (_url, init) => {
      assert.equal((init?.headers as Record<string, string>).Authorization, 'Bearer jrvs_test');
      assert.match(String(init?.body), /jarvis2-chat/);
      return new Response(JSON.stringify({ choices: [{ message: { content: 'ok' } }] }), { status: 200 });
    }
  });
  assert.equal(await chatClient.completeChat('jarvis2-chat', [{ role: 'user', content: 'hello' }]), 'ok');

  const failingClient = new GatewayClient({
    baseUrl: 'https://gateway.example',
    apiKey: 'jrvs_test',
    fetchImpl: async () => new Response(JSON.stringify({ error: { message: 'denied' } }), { status: 403 })
  });
  await assert.rejects(() => failingClient.listModels(), (error) => error instanceof GatewayClientError && error.statusCode === 403 && error.message === 'denied');
}

run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
