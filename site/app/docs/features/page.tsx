import type { Metadata } from 'next';
import FeaturesZh from '@/docs/features.zh.mdx';
import FeaturesEn from '@/docs/features.en.mdx';

export const metadata: Metadata = { title: '功能' };

// Prose lives in docs/features.{zh,en}.mdx; the feature grid and extras list
// are shared MDX components (components/DocBlocks.tsx).
export default function FeaturesPage() {
  return (
    <section className="docs-section mdx-body">
      <div className="lang-zh"><FeaturesZh /></div>
      <div className="lang-en"><FeaturesEn /></div>
    </section>
  );
}
