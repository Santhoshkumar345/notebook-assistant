// Runs the browser tests against the stack as it would be deployed, not as it is developed:
//   node scripts/run.mjs production [playwright arguments...]
// The Angular app is built for production and served by the API itself (one origin, no dev server), the API runs outside Development behind a
// proxy that ends TLS (see proxy.mjs), and the same tests as the development run are executed, plus the ones about deployment (production.spec.ts).
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const [mode, ...rest] = process.argv.slice(2);

if (mode !== 'production') {
  console.error('usage: node scripts/run.mjs production [playwright arguments...]');
  process.exit(2);
}

function run(command, args, options) {
  const result = spawnSync(command, args, { stdio: 'inherit', shell: true, ...options });
  if (result.status !== 0) process.exit(result.status ?? 1);
}

run('node', [join(here, 'clean.mjs')]);
run('npx', ['ng', 'build'], { cwd: join(root, 'web') }); // the production configuration is the default
run('npx', ['playwright', 'test', '--project=e2e', ...rest], { cwd: join(here, '..'), env: { ...process.env, E2E_STACK: 'production' } });
