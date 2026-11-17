'use client';

import { useState } from 'react';
import type { ReactNode } from 'react';
import { T } from '@/components/T';

export default function CodeBlock({ raw, children }: { raw: string; children: ReactNode }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      if (!navigator.clipboard?.writeText) throw new Error('no clipboard api');
      await navigator.clipboard.writeText(raw);
    } catch (e) {
      const ta = document.createElement('textarea');
      ta.value = raw;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      try {
        document.execCommand('copy');
      } catch (_) {
        /* clipboard unavailable */
      }
      ta.remove();
    }
    setCopied(true);
    setTimeout(() => setCopied(false), 1600);
  }

  return (
    <div className="code-window">
      <div className="code-bar">
        <span className="code-dots" aria-hidden="true"><i /><i /><i /></span>
        <span className="code-name">%APPDATA%\gui\config</span>
        <button type="button" className={'copy-btn' + (copied ? ' copied' : '')} onClick={copy}>
          {copied ? (
            <>
              <span className="lang-zh">已复制</span>
              <span className="lang-en">Copied</span>
            </>
          ) : (
            <T zh="复制" en="Copy" />
          )}
        </button>
      </div>
      <pre className="code"><code>{children}</code></pre>
    </div>
  );
}
