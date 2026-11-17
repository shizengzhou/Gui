'use client';

import type { CSSProperties } from 'react';
import { useEffect, useRef, useState } from 'react';
import { usePathname } from 'next/navigation';

type TocItem = { id: string; title: string; depth: number };

// A single heading is not worth a rail, the same threshold ghostty.org uses.
const MIN_ITEMS = 2;

// Read the headings of the copy the reader is looking at: both languages are in
// the DOM at once, and the ids are already scoped by language. Callout labels are
// also wrapped in .lang-xx spans, so pick the wrapper that actually holds headings.
function readItems(): TocItem[] {
  const lang = document.documentElement.dataset.lang === 'en' ? 'en' : 'zh';
  const headingSelector = 'h2.about-section, h3.about-section, h4.about-section';
  const root = Array.from(document.querySelectorAll<HTMLElement>(`.docs-content .mdx-body .lang-${lang}`))
    .find((element) => element.querySelector(headingSelector));
  if (!root) return [];

  return Array.from(root.querySelectorAll<HTMLElement>(headingSelector))
    .map((heading) => {
      const copy = heading.cloneNode(true) as HTMLElement;
      copy.querySelector('.anchor')?.remove();
      return {
        id: heading.id,
        title: (copy.textContent ?? '').trim(),
        depth: Number(heading.tagName.slice(1)),
      };
    })
    .filter((item) => item.id.length > 0 && item.title.length > 0);
}

// ghostty.org's sidecar: a sticky list of the headings on the page, one indent
// step per level, with the section currently under the header underlined.
export default function PageToc() {
  const pathname = usePathname();
  const [items, setItems] = useState<TocItem[]>([]);
  const [activeId, setActiveId] = useState('');
  const navRef = useRef<HTMLElement>(null);

  useEffect(() => {
    setItems(readItems());
    const html = document.documentElement;
    const observer = new MutationObserver(() => setItems(readItems()));
    observer.observe(html, { attributes: true, attributeFilter: ['data-lang'] });
    return () => observer.disconnect();
  }, [pathname]);

  // The section under the header is the last heading whose top has passed it.
  useEffect(() => {
    if (items.length === 0) return;
    let frame = 0;

    const update = () => {
      const line = 140;
      let current = items[0].id;
      for (const item of items) {
        const heading = document.getElementById(item.id);
        if (heading && heading.getBoundingClientRect().top <= line) current = item.id;
      }
      setActiveId(current);
    };

    const schedule = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(update);
    };

    update();
    window.addEventListener('scroll', schedule, { passive: true });
    window.addEventListener('resize', schedule);
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener('scroll', schedule);
      window.removeEventListener('resize', schedule);
    };
  }, [items]);

  // Keep the active row inside the rail when the list is taller than it.
  useEffect(() => {
    const nav = navRef.current;
    const row = nav?.querySelector<HTMLElement>('li.active');
    if (!nav || !row) return;

    const navBox = nav.getBoundingClientRect();
    const rowBox = row.getBoundingClientRect();
    if (rowBox.top < navBox.top) nav.scrollTop -= navBox.top - rowBox.top + 8;
    else if (rowBox.bottom > navBox.bottom) nav.scrollTop += rowBox.bottom - navBox.bottom + 8;
  }, [activeId]);

  if (items.length < MIN_ITEMS) return null;

  return (
    <nav ref={navRef} className="page-toc" aria-label="On this page">
      <ul>
        {items.map((item) => (
          <li
            key={item.id}
            className={item.id === activeId ? 'active' : undefined}
            style={{ '--depth': item.depth } as CSSProperties}
          >
            <a href={`#${item.id}`}>{item.title}</a>
          </li>
        ))}
      </ul>
    </nav>
  );
}
