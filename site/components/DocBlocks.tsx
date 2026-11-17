import type { ReactNode } from 'react';
import { T } from '@/components/T';
import { Icon } from '@/components/Icons';
import CodeBlock from '@/components/CodeBlock';
import { features, extras, getDownloads } from '@/lib/content';

// Shared building blocks used from the .mdx files. MDX is the source of truth
// for the prose; these are the chrome and data-driven widgets around it.

export function DocHead({ kicker, title, subtitle }: { kicker?: string; title?: string; subtitle?: string }) {
  return (
    <div className="section-head">
      {kicker ? <span className="kicker">{kicker}</span> : null}
      {title ? <h2>{title}</h2> : null}
      {subtitle ? <p className="section-sub">{subtitle}</p> : null}
    </div>
  );
}

export function Thanks({ text }: { text: string }) {
  return <div className="thanks-note">{text}</div>;
}

export function Actions({ github, reference }: { github: string; reference?: string }) {
  return (
    <div className="thanks-actions">
      <a className="btn btn-primary" href="https://github.com/shizengzhou/Gui">
        <Icon name="github" />
        {github}
        <Icon name="arrow" className="ico ico-sm" />
      </a>
      {reference ? (
        <a className="text-link" href="https://ghostty.org/docs/about">
          {reference}
          <Icon name="arrow" className="ico ico-sm" />
        </a>
      ) : null}
    </div>
  );
}

export async function DownloadCards() {
  const downloads = await getDownloads();
  return (
    <div className="dl-grid">
      {downloads.map((d) => (
        <article className={'dl-card' + (d.primary ? ' dl-primary' : '')} key={d.file}>
          {d.primary && <div className="dl-badge"><T zh="推荐" en="RECOMMENDED" /></div>}
          <div className="dl-icon"><Icon name={d.icon} /></div>
          <h3><T {...d.title} /></h3>
          <p className="dl-desc"><T {...d.desc} /></p>
          <div className="dl-file">{d.file}</div>
          <div className="dl-size">{d.size}</div>
          <a className={'btn btn-block ' + (d.primary ? 'btn-primary' : 'btn-ghost')} href={d.url}>
            <Icon name="download" />
            <T zh="下载" en="Download" />
          </a>
        </article>
      ))}
    </div>
  );
}

const configLines: [string, string][][] = [
  [['comment', '# Gui · Ghostty format']],
  [['key', 'font-family'], ['eq', ' = '], ['string', 'JetBrainsMono Nerd Font']],
  [['key', 'font-size'], ['eq', ' = '], ['num', '12']],
  [['key', 'theme'], ['eq', ' = '], ['string', 'light:Gui Light,dark:Gui Dark']],
  [['key', 'window-theme'], ['eq', ' = '], ['string', 'system']],
  [['key', 'background-opacity'], ['eq', ' = '], ['num', '0.85']],
  [['key', 'background-blur'], ['eq', ' = '], ['num', 'true']],
  [['key', 'tab-position'], ['eq', ' = '], ['string', 'left']],
  [['key', 'shell-integration'], ['eq', ' = '], ['string', 'detect']],
  [['key', 'copy-on-select'], ['eq', ' = '], ['string', 'clipboard']],
];
const configRaw = configLines.map((line) => line.map(([, text]) => text).join('')).join('\n');

export function ConfigBlocks() {
  return (
    <div className="config-layout">
      <CodeBlock raw={configRaw}>
        {configLines.map((line, i) => (
          <span key={i}>
            {i > 0 ? '\n' : null}
            {line.map(([cls, text], j) => (
              <span className={'c-' + cls} key={j}>{text}</span>
            ))}
          </span>
        ))}
      </CodeBlock>
    </div>
  );
}

export function FeatureBlocks() {
  return (
    <>
      <div className="feature-grid">
        {features.map((f) => (
          <article className="feature" key={f.title.en}>
            <div className="feature-icon"><Icon name={f.icon} /></div>
            <h3><T {...f.title} /></h3>
            <p><T {...f.desc} /></p>
          </article>
        ))}
      </div>
      <div className="extras">
        <span className="extras-label"><T zh="还包括" en="ALSO INCLUDED" /></span>
        <ul className="extras-list">
          {extras.map((e) => (
            <li key={e.text.en}>
              <Icon name={e.icon} />
              <T {...e.text} />
            </li>
          ))}
        </ul>
      </div>
    </>
  );
}

// Re-export for MDX authors that want a plain wrapper.
export function Prose({ children }: { children: ReactNode }) {
  return <div className="mdx-prose">{children}</div>;
}
