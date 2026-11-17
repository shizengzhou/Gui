import type { Metadata } from 'next';
import ThemeZh from '@/docs/features-theme.zh.mdx';
import ThemeEn from '@/docs/features-theme.en.mdx';

export const metadata: Metadata = { title: '配色主题' };

export default function FeatureThemePage() {
  return (
    <section className="docs-section mdx-body">
      <div className="lang-zh"><ThemeZh /></div>
      <div className="lang-en"><ThemeEn /></div>
    </section>
  );
}
