'use client';

import { useState } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { T } from '@/components/T';
import { docsNav, type DocsNavChild } from '@/lib/docs-nav';

export default function DocsSidebar() {
  const pathname = usePathname();
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});

  // Nested the way ghostty.org draws the tree: folders keep a caret, their
  // children are indented underneath, and the folder's own overview page is
  // the first child rather than a separate entry.
  const renderNode = (item: DocsNavChild, depth: number) => {
    const inSection = pathname === item.href || pathname.startsWith(item.href + '/');
    const hasChildren = !!item.children?.length;
    const open = hasChildren && !collapsed[item.href];
    const active = !hasChildren && pathname === item.href;
    const labelClass = (depth === 0 ? 'side-link' : 'side-child')
      + (hasChildren ? (inSection ? ' open' : '') : active ? ' active' : '');

    return (
      <div className="side-group" key={`${item.href}-${depth}`}>
        {hasChildren ? (
          <div className="side-row">
            <Link href={item.href} className={labelClass}>
              <T zh={item.zh} en={item.en} />
            </Link>
            <button
              type="button"
              className="side-caret"
              aria-expanded={open}
              aria-label={`${item.zh} / ${item.en}`}
              onClick={() => setCollapsed((state) => ({ ...state, [item.href]: !state[item.href] }))}
            >
              <svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="m6 15 6-6 6 6" />
              </svg>
            </button>
          </div>
        ) : item.external ? (
          <a href={item.href} className={labelClass} target="_blank" rel="noreferrer">
            <T zh={item.zh} en={item.en} />
          </a>
        ) : (
          <Link href={item.href} className={labelClass}>
            <T zh={item.zh} en={item.en} />
          </Link>
        )}
        {open ? (
          <div className={'side-children' + (depth > 0 ? ' side-children-nested' : '')}>
            {item.children!.map((child) => renderNode(child, depth + 1))}
          </div>
        ) : null}
      </div>
    );
  };

  return (
    <aside className="docs-sidebar">
      <div className="side-label"><T zh="文档目录" en="DOCUMENTATION" /></div>
      <nav className="side-links" aria-label="Docs">
        {docsNav.map((item) => renderNode(item, 0))}
      </nav>
    </aside>
  );
}
