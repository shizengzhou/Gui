import type { Metadata } from 'next';
import ConfigZh from '@/docs/config.zh.mdx';
import ConfigEn from '@/docs/config.en.mdx';

export const metadata: Metadata = { title: '配置' };

// Prose lives in docs/config.{zh,en}.mdx; the notes and syntax-highlighted
// code block are shared MDX components (components/DocBlocks.tsx).
export default function ConfigPage() {
  return (
    <section className="docs-section mdx-body">
      <div className="lang-zh"><ConfigZh /></div>
      <div className="lang-en"><ConfigEn /></div>
    </section>
  );
}
