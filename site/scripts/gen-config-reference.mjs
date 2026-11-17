// Generates lib/config-options.json from the application's own schema so the
// website's option reference cannot drift from the build it documents.
// Run from site/: npm run gen:config
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const source = resolve(root, '../Gui/Gui/Term/ConfigSchema.cs');
const target = resolve(root, 'lib/config-options.json');

const text = readFileSync(source, 'utf8');
const entry = /new\("([^"]+)",\s*ConfigKind\.(\w+),\s*ConfigPlatform\.(\w+),\s*"((?:[^"\\]|\\.)*)"\)/g;
const applicationAt = text.indexOf('Application =');

const options = [];
let match;
while ((match = entry.exec(text)) !== null) {
  options.push({
    name: match[1],
    kind: match[2],
    platform: match[3],
    summary: match[4].replace(/\\"/g, '"'),
    own: match.index > applicationAt,
  });
}

if (options.length < 100) {
  throw new Error(`only ${options.length} options parsed from ${source}; the schema format probably changed`);
}

writeFileSync(target,
  JSON.stringify({ source: 'Gui/Gui/Term/ConfigSchema.cs', options }, null, 2).replace(/\n/g, '\r\n') + '\r\n');
console.log(`${options.length} options -> ${target}`);
