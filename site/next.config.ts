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

// GitHub Pages serves a project site from https://<user>.github.io/<repo>/, so the
// build needs a base path. The Pages workflow passes it in; a local build has none.
const basePath = (process.env.NEXT_PUBLIC_BASE_PATH ?? '').replace(/\/+$/, '');

const nextConfig: NextConfig = {
  reactStrictMode: true,
  pageExtensions: ['ts', 'tsx', 'js', 'jsx', 'md', 'mdx'],
  // Static export: `next build` writes a ready-to-serve site to out/.
  output: 'export',
  // Pages serves directory indexes; emit docs/foo/index.html instead of docs/foo.html.
  trailingSlash: true,
  basePath: basePath || undefined,
};

export default withMDX(nextConfig);
