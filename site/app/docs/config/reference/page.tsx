import type { Metadata } from 'next';
import ReferenceZh from '@/docs/config-reference.zh.mdx';
import ReferenceEn from '@/docs/config-reference.en.mdx';

export const metadata: Metadata = { title: '选项参考' };

// The English page is Ghostty's own document rendered verbatim; the YAML
// frontmatter is dropped by lib/docs/remark-strip-frontmatter.mjs. The Chinese
// page keeps the RefOption format so Gui's own notes stay visible. The
// on-this-page rail is the shared PageToc in app/docs/layout.tsx.
export default function ConfigReferencePage() {
  return (
    <section className="docs-section mdx-body reference-page">
      <div className="reference-main">
        <div className="lang-zh"><ReferenceZh /></div>
        <div className="lang-en"><ReferenceEn /></div>
      </div>
    </section>
  );
}
