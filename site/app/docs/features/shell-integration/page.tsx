import type { Metadata } from 'next';
import ShellIntegrationZh from '@/docs/features-shell-integration.zh.mdx';
import ShellIntegrationEn from '@/docs/features-shell-integration.en.mdx';

export const metadata: Metadata = { title: 'Shell 集成' };

export default function FeatureShellIntegrationPage() {
  return (
    <section className="docs-section mdx-body">
      <div className="lang-zh"><ShellIntegrationZh /></div>
      <div className="lang-en"><ShellIntegrationEn /></div>
    </section>
  );
}
