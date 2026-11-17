import Link from 'next/link';
import type { ReactNode } from 'react';
import type { Metadata } from 'next';
import DocsSidebar from '@/components/DocsSidebar';
import DocsCrumbs from '@/components/DocsCrumbs';
import PageToc from '@/components/PageToc';
import { T } from '@/components/T';

export const metadata: Metadata = {
  title: { default: '文档 — Gui', template: '%s — Gui 文档' },
};

export default function DocsLayout({ children }: { children: ReactNode }) {
  return (
    <>
      <div className="wrap docs-wrap">
        <DocsSidebar />
        <main className="docs-content">
          <div className="docs-page-grid">
            <div className="docs-page-main">
              <DocsCrumbs />
              {children}
            </div>
            <aside className="docs-toc-wrap"><PageToc /></aside>
          </div>
        </main>
      </div>
      <footer className="docs-footer wrap">
        <p className="disclaimer">
          <T
            zh="说明：Gui 与 Ghostty 项目没有隶属关系，也未获其背书。"
            en="Note: Gui is not affiliated with or endorsed by the Ghostty project."
          />
        </p>
        <p className="footer-small">MIT License · © 2026 Gui</p>
        <div className="docs-footer-links">
          <Link href="/"><T zh="首页" en="Home" /></Link>
          <a href="https://github.com/shizengzhou/Gui">GitHub</a>
        </div>
      </footer>
    </>
  );
}
