import type { ReactNode } from 'react';
import { T } from '@/components/T';

// The two callouts Ghostty's reference uses. Kept deliberately plain: a coloured edge and a
// short label, the same weight as the surrounding prose.
function Callout({ kind, children }: { kind: 'warning' | 'note'; children: ReactNode }) {
  return (
    <div className={`callout callout-${kind}`}>
      <span className="callout-label">
        {kind === 'warning' ? <T zh="警告" en="Warning" /> : <T zh="说明" en="Note" />}
      </span>
      <div className="callout-body">{children}</div>
    </div>
  );
}

export function Warning({ children }: { children?: ReactNode }) {
  return <Callout kind="warning">{children}</Callout>;
}

export function Note({ children }: { children?: ReactNode }) {
  return <Callout kind="note">{children}</Callout>;
}
