import type { Metadata } from 'next';
import AboutZh from '@/docs/about.zh.mdx';
import AboutEn from '@/docs/about.en.mdx';

export const metadata: Metadata = { title: '关于' };

// Both languages render into the DOM; globals.css shows one based on
// <html data-lang>. The rehype plugin scopes ids/anchors per language so the
// two copies never collide. All prose lives in docs/about.{zh,en}.mdx.
export default function AboutPage() {
  return (
    <section className="docs-section mdx-body">
      <div className="lang-zh"><AboutZh /></div>
      <div className="lang-en"><AboutEn /></div>
    </section>
  );
}
