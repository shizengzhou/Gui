import type { NextConfig } from 'next';
import createMDX from '@next/mdx';
import { createRequire } from 'node:module';

// Resolve local remark/rehype plugin paths for the MDX loader, the same way
// ghostty-org/website does in its next.config.mjs.
const require = createRequire(import.meta.url);
const docAnchorsPlugin = require.resolve('./lib/docs/rehype-doc-anchors.mjs');
const stripFrontmatterPlugin = require.resolve('./lib/docs/remark-strip-frontmatter.mjs');

const withMDX = createMDX({
  extension: /\.mdx?$/,
  options: {
    remarkPlugins: [stripFrontmatterPlugin, 'remark-gfm'],
    rehypePlugins: [docAnchorsPlugin],
  },
});

const nextConfig: NextConfig = {
  reactStrictMode: true,
  pageExtensions: ['ts', 'tsx', 'js', 'jsx', 'md', 'mdx'],
};

export default withMDX(nextConfig);
