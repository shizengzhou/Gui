// Generates lib/search-index.json from the docs/*.mdx sources so the Ctrl-K
// search dialog ships a static index instead of parsing MDX in the browser.
// Run from site/: npm run gen:search (also runs before dev and build).
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const docsDir = resolve(root, 'docs');
const target = resolve(root, 'lib/search-index.json');

// File stem -> page. The labels mirror lib/docs-nav.ts, which is the single
// source of the sidebar and breadcrumb text.
const pages = {
  about: { href: '/docs', zh: '关于', en: 'About' },
  download: { href: '/docs/download', zh: '下载', en: 'Download' },
  config: { href: '/docs/config', zh: '配置', en: 'Config' },
  'config-reference': { href: '/docs/config/reference', zh: '选项参考', en: 'Option Reference' },
  keybind: { href: '/docs/config/keybind', zh: '按键绑定', en: 'Keybindings' },
  'keybind-sequence': { href: '/docs/config/keybind/sequence', zh: '触发序列', en: 'Trigger Sequences' },
  'keybind-reference': { href: '/docs/config/keybind/reference', zh: '动作参考', en: 'Action Reference' },
  features: { href: '/docs/features', zh: '功能', en: 'Features' },
  'features-theme': { href: '/docs/features/theme', zh: '配色主题', en: 'Color Theme' },
  'features-shell-integration': { href: '/docs/features/shell-integration', zh: 'Shell 集成', en: 'Shell Integration' },
};

// Keep in sync with lib/docs/rehype-doc-anchors.mjs: headings h2-h4 get ids
// scoped by language, duplicates get -2, -3, ...
function slugify(value) {
  return (
    value
      .toLowerCase()
      .trim()
      .replace(/[^\p{L}\p{N}\s-]/gu, '')
      .replace(/\s+/g, '-')
      .replace(/-+/g, '-')
      .replace(/^-|-$/g, '') || 'section'
  );
}

function plainHeading(text) {
  return text
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/\*\*|__|\*|~~/g, '')
    .trim();
}

function plainBody(text) {
  return text
    .replace(/\{\/\*[\s\S]*?\*\/\}/g, ' ')
    .replace(/<[A-Za-z/][^>]*>/g, ' ')
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/^>\s?/gm, '')
    .replace(/^\s{0,3}[-*+]\s+/gm, '')
    .replace(/^\s{0,3}\d+\.\s+/gm, '')
    .replace(/^\s*\|?[\s:|-]+\|?\s*$/gm, ' ')
    .replace(/\|/g, ' ')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/\*\*|__|\*|~~/g, '')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/\s+/g, ' ')
    .trim();
}

// A section is the text between two headings; the intro before the first
// heading becomes the page-level entry.
function parseSections(source, lang) {
  const lines = source.split(/\r?\n/);
  const sections = [];
  const used = new Set();
  let current = { id: '', heading: '', lines: [] };
  let fence = '';

  for (const raw of lines) {
    const line = raw.trimEnd();
    const fenceMatch = line.match(/^\s*(```+|~~~+)/);
    if (fenceMatch) {
      if (!fence) {
        fence = fenceMatch[1][0];
        continue;
      }
      if (line.trim().startsWith(fence.repeat(3))) {
        fence = '';
        continue;
      }
    }
    if (fence) {
      current.lines.push(line);
      continue;
    }

    const heading = line.match(/^(#{1,6})\s+(.+?)\s*#*\s*$/);
    if (!heading) {
      current.lines.push(line);
      continue;
    }

    sections.push(current);
    const level = heading[1].length;
    const text = plainHeading(heading[2]);
    let id = '';
    if (level >= 2 && level <= 4) {
      let slug = slugify(text);
      if (used.has(slug)) {
        let n = 2;
        while (used.has(slug + '-' + n)) n++;
        slug = slug + '-' + n;
      }
      used.add(slug);
      id = lang + '-' + slug;
    }
    current = { id, heading: text, lines: [] };
  }

  sections.push(current);
  return sections;
}

const MAX_TEXT = 600;
const entries = [];

for (const file of readdirSync(docsDir)) {
  const match = file.match(/^(.*)\.(zh|en)\.mdx$/);
  if (!match) continue;
  const page = pages[match[1]];
  if (!page) continue;
  const lang = match[2];

  let source = readFileSync(join(docsDir, file), 'utf8').replace(/^\uFEFF/, '');
  source = source
    .replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '')
    .replace(/^import\s+.*$/gm, '')
    .replace(/^export\s+.*$/gm, '');

  const sections = parseSections(source, lang);
  for (const section of sections) {
    const text = plainBody(section.lines.join('\n'));
    if (!section.heading && !text) continue;
    entries.push({
      href: page.href,
      lang,
      id: section.id,
      page: { zh: page.zh, en: page.en },
      heading: section.heading || page[lang],
      text: text.slice(0, MAX_TEXT),
    });
  }
}

writeFileSync(target, JSON.stringify({ entries }) + '\r\n');
console.log(`${entries.length} search entries -> ${target}`);
