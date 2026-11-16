import { readFileSync } from 'node:fs';
import { parseEnv } from 'node:util';
import { spawnSync } from 'node:child_process';

const root = new URL('../', import.meta.url);
const config = parseEnv(readFileSync(new URL('.env', root), 'utf8'));
for (const key of ['POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD']) {
  if (!config[key]) throw new Error(`Set ${key} in the root .env file.`);
}
const port = config.POSTGRES_PORT || '5432';
if (!/^\d+$/.test(port) || Number(port) < 1 || Number(port) > 65535) {
  throw new Error('POSTGRES_PORT must be a valid TCP port.');
}
const quote = (value) => `"${value.replaceAll('"', '""')}"`;
const connectionString = `Host=localhost;Port=${port};Database=${quote(config.POSTGRES_DB)};Username=${quote(config.POSTGRES_USER)};Password=${quote(config.POSTGRES_PASSWORD)}`;
const result = spawnSync('dotnet', ['user-secrets', 'set', '--project', 'backend/ColoringApp.Api'], {
  cwd: root,
  input: JSON.stringify({ 'ConnectionStrings:DefaultConnection': connectionString }),
  encoding: 'utf8',
  stdio: ['pipe', 'pipe', 'pipe'],
});
if (result.error || result.status !== 0) {
  throw new Error('Could not configure .NET User Secrets. Check the installed SDK and local secret-store access.');
}
console.log('Configured ConnectionStrings:DefaultConnection from .env in .NET User Secrets.');
