import type { Metadata } from 'next';
import ActionReferenceZh from '@/docs/keybind-reference.zh.mdx';
import ActionReferenceEn from '@/docs/keybind-reference.en.mdx';

export const metadata: Metadata = { title: '按键动作参考' };

export default function KeybindReferencePage() {
  return (
    <section className="docs-section mdx-body">
      <div className="lang-zh"><ActionReferenceZh /></div>
      <div className="lang-en"><ActionReferenceEn /></div>
    </section>
  );
}
