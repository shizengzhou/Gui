'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { T } from '@/components/T';
import { docsNav, type DocsNavChild } from '@/lib/docs-nav';
import { currentPath } from '@/lib/current-path';

// The trail down the tree: every folder on the way to the page, then the page.
function trailFor(items: DocsNavChild[], pathname: string): DocsNavChild[] | null {
  for (const item of items) {
    if (pathname === item.href) return [item];
    if (item.children) {
      const rest = trailFor(item.children, pathname);
      if (rest) return [item, ...rest];
    }
  }
  return null;
}

// Ghostty puts the trail above the page title: Docs / Config / Keybindings /
// Action Reference. The last entry is the current page and is not a link, and
// the docs landing page is the trail's own start.
export default function DocsCrumbs() {
  const pathname = currentPath(usePathname());
  const trail = (trailFor(docsNav, pathname) ?? []).filter((item) => item.href !== '/docs');

  if (trail.length === 0) return null;

  return (
    <nav className="crumbs" aria-label="Breadcrumb">
      <span className="crumb">
        <Link href="/docs"><T zh="文档" en="Docs" /></Link>
        <span className="crumb-sep" aria-hidden="true">/</span>
      </span>
      {trail.map((item, index) => {
        const current = index === trail.length - 1;
        return (
          <span className="crumb" key={`${item.href}-${index}`}>
            {current ? (
              <span className="crumb-current" aria-current="page"><T zh={item.zh} en={item.en} /></span>
            ) : (
              <Link href={item.href}><T zh={item.zh} en={item.en} /></Link>
            )}
            {current ? null : <span className="crumb-sep" aria-hidden="true">/</span>}
          </span>
        );
      })}
    </nav>
  );
}
