import type { ComponentProps } from 'react';
import type { MDXComponents } from 'mdx/types';
import Link from 'next/link';
import * as DocBlocks from '@/components/DocBlocks';
import * as Callouts from '@/components/Callouts';
import { RefOption, RefSection } from '@/components/RefOption';

// Docs cross-link to root-absolute paths (/docs/...). A bare <a> would skip the
// site's basePath on GitHub Pages, so internal links go through next/link.
function DocsLink({ href, ...rest }: ComponentProps<'a'>) {
  if (href?.startsWith('/')) return <Link href={href} {...rest} />;
  return <a href={href} {...rest} />;
}

// @next/mdx looks up this hook for every MDX file. Exposing the shared blocks here lets the
// .mdx docs use <DocHead />, <ConfigBlocks /> and the reference's <RefOption> without
// importing anything.
export function useMDXComponents(components: MDXComponents): MDXComponents {
  return {
    ...components,
    a: DocsLink,
    DocHead: DocBlocks.DocHead,
    Note: Callouts.Note,
    Warning: Callouts.Warning,
    RefOption,
    RefSection,
    Thanks: DocBlocks.Thanks,
    Actions: DocBlocks.Actions,
    DownloadCards: DocBlocks.DownloadCards,
    ConfigBlocks: DocBlocks.ConfigBlocks,
    FeatureBlocks: DocBlocks.FeatureBlocks,
    Prose: DocBlocks.Prose,
  };
}
