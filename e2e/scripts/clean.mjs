// A fresh database and index for every run: the tests make their own users, but a stale run directory would carry old ones over.
import { rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

rmSync(join(dirname(fileURLToPath(import.meta.url)), '..', '.run'), { recursive: true, force: true });
