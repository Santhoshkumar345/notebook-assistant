import { join, resolve } from 'node:path';

import { defineConfig } from '@playwright/test';

/**
 * Runs the real stack (the Angular app, the .NET API with a throwaway database, the Python service with real retrieval) on ports of its own,
 * so it never touches a development stack, and drives it with a browser that is already on the machine (Edge by default, E2E_BROWSER=chrome
 * for Chrome), so nothing is downloaded. The language model is a deterministic stand-in (ai-service/e2e_server.py) unless E2E_REAL_MODEL=1,
 * which the screenshot run turns on by itself so the pictures show real answers (it needs GEMINI_API_KEY in ai-service/.env).
 *
 * E2E_STACK=production (use `npm run test:production`) runs it as deployed instead: the web app built and served by the API itself, the API
 * outside Development with its own checks on, and a proxy in front that ends TLS, as a host's edge would. See scripts/run.mjs.
 */
const root = resolve(__dirname, '..');
const run = join(__dirname, '.run');
const python = process.platform === 'win32' ? join(root, 'ai-service', '.venv', 'Scripts', 'python.exe') : join(root, 'ai-service', '.venv', 'bin', 'python');

// Not a secret: it only guards a throwaway stack that listens on localhost for the length of a test run.
const SERVICE_KEY = 'e2e-only-service-key-0123456789abcdef';
const ports = { ai: 8010, api: 5257, web: 4210, apiBehindProxy: 5258, proxy: 4211 };
const production = process.env.E2E_STACK === 'production';
const webDist = join(root, 'web', 'dist', 'web', 'browser');

// What the browser opens, and what the tests that open their own browsers (support.ts) read.
const baseURL = `http://127.0.0.1:${production ? ports.proxy : ports.web}`;
process.env.E2E_BASE_URL = baseURL;
// The screenshot run always uses the real model, so the pictures show real answers.
const realModel = process.env.E2E_REAL_MODEL === '1' || process.argv.includes('--project=screenshots');

const aiService = realModel
  ? `"${python}" -m uvicorn app.api:app_from_env --factory --host 127.0.0.1 --port ${ports.ai}`
  : `"${python}" e2e_server.py "${join(run, 'chroma')}"`;

export default defineConfig({
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: true,
  workers: 2,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL,
    channel: process.env.E2E_BROWSER ?? 'msedge',
    trace: 'off', // a trace would record what was typed into the password fields
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'e2e', testDir: './tests' },
    { name: 'screenshots', testDir: './screenshots', fullyParallel: false },
  ],
  webServer: [aiServer(), ...(production ? productionServers() : developmentServers())],
});

function aiServer() {
  return {
    command: aiService,
    cwd: join(root, 'ai-service'),
    env: { AI_SERVICE_KEY: SERVICE_KEY, PORT: String(ports.ai), ...(realModel ? { AI_DATA_DIR: join(run, 'chroma') } : {}) },
    url: `http://127.0.0.1:${ports.ai}/health`, // answers 401 without the key, which is enough to know it is up
    timeout: 180_000,
    reuseExistingServer: false,
  };
}

/** The API's command line, shared by both stacks: a throwaway database, the AI service just started, no per-address limits to trip over. */
function apiCommand(environment: string, port: number, extra: string[]) {
  return [
    `dotnet run -e ASPNETCORE_ENVIRONMENT=${environment}`,
    ...extra,
    `--project "${join(root, 'api', 'src', 'NotebookApi', 'NotebookApi.csproj')}" --no-launch-profile`,
    `--urls http://127.0.0.1:${port}`,
    `--Database:Path="${join(run, 'notebook.db')}"`,
    `--AiService:BaseUrl=http://127.0.0.1:${ports.ai} --AiService:ApiKey=${SERVICE_KEY}`,
    '--RateLimits:AuthPerMinute=100000 --RateLimits:AskPerMinute=100000', // every test signs people up from one address
  ].join(' ');
}

function developmentServers() {
  return [
    {
      command: apiCommand('Development', ports.api, []),
      url: `http://127.0.0.1:${ports.api}/api/health`,
      timeout: 240_000,
      reuseExistingServer: false,
    },
    {
      command: `npx ng serve --port ${ports.web} --host 127.0.0.1`,
      cwd: join(root, 'web'),
      env: { API_TARGET: `http://127.0.0.1:${ports.api}` },
      url: `http://127.0.0.1:${ports.web}`,
      timeout: 240_000,
      reuseExistingServer: false,
    },
  ];
}

function productionServers() {
  return [
    {
      // Outside Development, so every production check is live: the Secure cookies, the refusal of plain http, the startup checks. Only the
      // proxy's address is believed when it says the request was https. The keys live next to the database, as they would on a volume.
      command: apiCommand('Production', ports.apiBehindProxy, [
        `-e ASPNETCORE_WEBROOT="${webDist}"`,
        // Registration open on purpose (the tests make their own accounts); a real deployment sets an invite code instead.
      ]) + [
        ' --Auth:OpenRegistration=true',
        `--DataProtection:KeysPath="${join(run, 'keys')}"`,
        '--ForwardedHeaders:Mode=TrustedProxies --ForwardedHeaders:KnownNetworks:0=127.0.0.0/8 --ForwardedHeaders:KnownNetworks:1=::1/128',
      ].join(' '),
      url: `http://127.0.0.1:${ports.apiBehindProxy}/healthz`,
      timeout: 240_000,
      reuseExistingServer: false,
    },
    {
      command: `node scripts/proxy.mjs ${ports.proxy} ${ports.apiBehindProxy}`,
      cwd: __dirname,
      url: `http://127.0.0.1:${ports.proxy}/healthz`,
      timeout: 60_000,
      reuseExistingServer: false,
    },
  ];
}
