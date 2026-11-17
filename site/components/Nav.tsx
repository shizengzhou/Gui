'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { T } from '@/components/T';
import { Icon } from '@/components/Icons';
import LangToggle from '@/components/LangToggle';
import DocsSearch from '@/components/DocsSearch';
import { currentPath } from '@/lib/current-path';

// Plain <img> src is not rewritten by basePath the way next/link and next/image are.
const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? '';

export default function Nav() {
  const pathname = currentPath(usePathname());
  const inDocs = pathname.startsWith('/docs');
  const onDownload = pathname === '/docs/download';

  return (
    <header className="nav">
      <div className={'wrap nav-inner' + (inDocs ? ' docs' : '')}>
        <Link className="brand" href="/" aria-label="Gui home">
          <img className="brand-mark" src={`${basePath}/app-icon.png`} alt="" width={30} height={30} aria-hidden="true" />
          <span className="brand-name">Gui</span>
        </Link>
        <nav className="nav-links" aria-label="Primary">
          <Link href="/docs/download" className={'nav-link' + (onDownload ? ' active' : '')}>
            <T zh="下载" en="Download" />
          </Link>
          <Link href="/docs" className={'nav-link' + (inDocs && !onDownload ? ' active' : '')}>
            <T zh="文档" en="Docs" />
          </Link>
        </nav>
        <div className="nav-right">
          {inDocs ? <DocsSearch /> : null}
          <LangToggle />
          <a
            className="icon-link"
            href="https://github.com/shizengzhou/Gui"
            aria-label="GitHub repository"
            title="GitHub"
          >
            <Icon name="github" />
          </a>
        </div>
      </div>
    </header>
  );
}
