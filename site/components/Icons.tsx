import type { ReactNode } from 'react';
import type { IconName } from '@/lib/types';

const paths: Record<IconName, ReactNode> = {
  window: (
    <>
      <rect x="2.5" y="3.5" width="19" height="17" rx="2" />
      <path d="M2.5 8.5h19M6 6h.01M9 6h.01" />
      <path d="M6.5 12.5l2.5 2-2.5 2M11.5 16.5h5" />
    </>
  ),
  bolt: <path d="M13 2 4.5 13.5H11l-1 8.5 8.5-11.5H12z" />,
  chip: (
    <>
      <rect x="6" y="6" width="12" height="12" rx="1.5" />
      <path d="M9.5 2v4M14.5 2v4M9.5 18v4M14.5 18v4M2 9.5h4M2 14.5h4M18 9.5h4M18 14.5h4" />
    </>
  ),
  sliders: (
    <>
      <path d="M4 6h16M4 12h16M4 18h16" />
      <circle cx="9" cy="6" r="2" />
      <circle cx="15" cy="12" r="2" />
      <circle cx="8" cy="18" r="2" />
    </>
  ),
  columns: (
    <>
      <rect x="2.5" y="3.5" width="19" height="17" rx="2" />
      <path d="M13 3.5v17M2.5 9h7.5" />
    </>
  ),
  keyboard: (
    <>
      <rect x="2" y="5" width="20" height="14" rx="2" />
      <path d="M6 9h.01M10 9h.01M14 9h.01M18 9h.01M6 13h.01M10 13h.01M14 13h.01M18 13h.01M8 16.5h8" />
    </>
  ),
  search: (
    <>
      <circle cx="10.5" cy="10.5" r="6.5" />
      <path d="M15.5 15.5 21 21" />
    </>
  ),
  link: (
    <>
      <path d="M10 13.5a4 4 0 0 0 5.7.3l3-3a4 4 0 0 0-5.7-5.7l-1.7 1.7" />
      <path d="M14 10.5a4 4 0 0 0-5.7-.3l-3 3a4 4 0 0 0 5.7 5.7l1.7-1.7" />
    </>
  ),
  github: (
    <path d="M12 2.2a9.8 9.8 0 0 0-3.1 19.1c.5.1.7-.2.7-.5v-1.9c-2.8.6-3.4-1.2-3.4-1.2-.5-1.2-1.1-1.5-1.1-1.5-.9-.6.1-.6.1-.6 1 0 1.6 1 1.6 1 .9 1.6 2.4 1.1 3 .9.1-.7.4-1.1.7-1.4-2.2-.3-4.6-1.1-4.6-4.9 0-1.1.4-2 1-2.7-.1-.3-.4-1.3.1-2.7 0 0 .8-.3 2.7 1a9.4 9.4 0 0 1 4.9 0c1.9-1.3 2.7-1 2.7-1 .5 1.4.2 2.4.1 2.7.6.7 1 1.6 1 2.7 0 3.8-2.4 4.6-4.6 4.9.4.3.7 1 .7 2v3c0 .3.2.6.7.5A9.8 9.8 0 0 0 12 2.2z" />
  ),
  download: <path d="M12 3v12M7.5 10.5 12 15l4.5-4.5M4 20h16" />,
  arrow: <path d="M5 12h14M13 6l6 6-6 6" />,
  book: (
    <>
      <path d="M4 4.5A2.5 2.5 0 0 1 6.5 2H20v17H6.5A2.5 2.5 0 0 0 4 21.5zM4 4.5v17" />
      <path d="M8 7h8M8 11h6" />
    </>
  ),
  home: (
    <>
      <path d="M3 10.5 12 3l9 7.5" />
      <path d="M5.5 9.5V21h13V9.5" />
    </>
  ),
};

export function Icon({ name, className = 'ico' }: { name: IconName; className?: string }) {
  const filled = name === 'github';
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      aria-hidden="true"
      fill={filled ? 'currentColor' : 'none'}
      stroke={filled ? 'none' : 'currentColor'}
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      {paths[name]}
    </svg>
  );
}
