import type { Localized } from '@/lib/types';

// Renders both languages; globals.css shows one based on <html data-lang>.
// No hooks, so this works inside server components too.
export function T({ zh, en }: Localized) {
  return (
    <>
      <span className="lang-zh">{zh}</span>
      <span className="lang-en">{en}</span>
    </>
  );
}
