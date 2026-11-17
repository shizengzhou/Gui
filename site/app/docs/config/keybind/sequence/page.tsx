import type { Metadata } from 'next';
import SequenceZh from '@/docs/keybind-sequence.zh.mdx';
import SequenceEn from '@/docs/keybind-sequence.en.mdx';

export const metadata: Metadata = { title: '触发序列' };

export default function KeybindSequencePage() {
  return (
    <section className="docs-section mdx-body">
      <div className="lang-zh"><SequenceZh /></div>
      <div className="lang-en"><SequenceEn /></div>
    </section>
  );
}
