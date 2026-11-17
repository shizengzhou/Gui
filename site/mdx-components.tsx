import type { MDXComponents } from 'mdx/types';
import * as DocBlocks from '@/components/DocBlocks';
import * as Callouts from '@/components/Callouts';
import { RefOption, RefSection } from '@/components/RefOption';

// @next/mdx looks up this hook for every MDX file. Exposing the shared blocks here lets the
// .mdx docs use <DocHead />, <ConfigBlocks /> and the reference's <RefOption> without
// importing anything.
export function useMDXComponents(components: MDXComponents): MDXComponents {
  return {
    ...components,
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
