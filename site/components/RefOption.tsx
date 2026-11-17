import type { ReactNode } from 'react';
import { T } from '@/components/T';

// One entry of the option reference. The heading, the body and the note all come from the
// generated MDX (docs/config-reference.{en,zh}.mdx), which follows Ghostty's own document;
// this component only draws the chrome around them: the anchor, the types, the Gui badge.

const kindLabels: Record<string, { zh: string; en: string }> = {
  Bool: { zh: '布尔', en: 'boolean' },
  Integer: { zh: '整数', en: 'integer' },
  Float: { zh: '浮点', en: 'number' },
  Number: { zh: '数字或百分比', en: 'number or %' },
  Color: { zh: '颜色', en: 'colour' },
  Text: { zh: '文本', en: 'text' },
  Enum: { zh: '枚举', en: 'enum' },
  List: { zh: '可重复', en: 'repeatable' },
};

const platformLabels: Record<string, { zh: string; en: string }> = {
  MacOS: { zh: '仅 macOS', en: 'macOS only' },
  Linux: { zh: '仅 Linux', en: 'Linux only' },
};

// Notes and one-liners are plain text with occasional `code` spans.
function inline(value: string): ReactNode {
  return value.split(/(`[^`]+`)/g).map((part, index) =>
    part.length > 2 && part.startsWith('`') && part.endsWith('`') ? (
      <code key={index}>{part.slice(1, -1)}</code>
    ) : (
      <span key={index}>{part}</span>
    ),
  );
}

export function RefOption({
  lang,
  name,
  kind,
  platform,
  note,
  children,
}: {
  lang: 'zh' | 'en';
  name: string;
  kind: string;
  platform: string;
  note?: string;
  children?: ReactNode;
}) {
  return (
    <section className="ref-opt" id={`${lang}-${name}`}>
      <h2 className="ref-name">
        <a className="ref-anchor" href={`#${lang}-${name}`} aria-label="Link to this option">#</a>
        <code>{name}</code>
        {kindLabels[kind] ? <span className="ref-kind"><T {...kindLabels[kind]} /></span> : null}
        {platform !== 'All' && platformLabels[platform] ? (
          <span className="ref-platform"><T {...platformLabels[platform]} /></span>
        ) : null}
      </h2>

      {children ? <div className="ref-body">{children}</div> : null}

      {note ? (
        <div className="ref-note">
          <span className="ref-gui-tag">Gui</span>
          {inline(note)}
        </div>
      ) : null}
    </section>
  );
}

/** A heading inside the document, currently only the point where Gui's own keys start. */
export function RefSection({ id, label }: { id: string; label: string }) {
  return <h2 className="ref-section" id={id}>{label}</h2>;
}
