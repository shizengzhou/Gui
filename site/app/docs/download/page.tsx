import type { Metadata } from 'next';
import DownloadZh from '@/docs/download.zh.mdx';
import DownloadEn from '@/docs/download.en.mdx';

export const metadata: Metadata = { title: '下载' };

// The async DownloadCards component inside the MDX resolves installer links
// from the GitHub API, so this page needs no data fetching of its own.
export default function DownloadPage() {
  return (
    <section className="docs-section mdx-body">
      <div className="lang-zh"><DownloadZh /></div>
      <div className="lang-en"><DownloadEn /></div>
    </section>
  );
}
