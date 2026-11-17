import type { Metadata } from 'next';
import KeybindsZh from '@/docs/keybind.zh.mdx';
import KeybindsEn from '@/docs/keybind.en.mdx';

export const metadata: Metadata = { title: '按键绑定' };

export default function KeybindPage() {
  return (
    <section className="docs-section mdx-body">
      <div className="lang-zh"><KeybindsZh /></div>
      <div className="lang-en"><KeybindsEn /></div>
    </section>
  );
}
