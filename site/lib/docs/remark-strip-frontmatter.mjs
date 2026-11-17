// Replaces a leading YAML frontmatter block with the page header it describes.
// @next/mdx runs without remark-frontmatter, so a `--- ... ---` header (the
// upstream Ghostty reference keeps one) would otherwise be parsed as a thematic
// break plus a paragraph and show up as body text. The source file stays
// verbatim; `title` and `description` become <DocHead>, like Ghostty's own docs
// page renders them.
export default function remarkStripFrontmatter() {
  return (tree, file) => {
    const source = String(file ?? '');
    const bom = source.charCodeAt(0) === 0xfeff ? 1 : 0;
    const match = /^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/.exec(source.slice(bom));
    if (!match) return;

    const end = bom + match[0].length;
    tree.children = tree.children.filter((node) => {
      const offset = node.position?.start?.offset;
      return typeof offset !== 'number' || offset >= end;
    });

    const field = (name) => {
      const line = new RegExp('^' + name + ':\\s*(.*)$', 'm').exec(match[1]);
      return line ? line[1].trim().replace(/^['"]|['"]$/g, '') : '';
    };
    const title = field('title');
    const description = field('description');
    if (!title && !description) return;

    const attributes = [];
    if (title) attributes.push({ type: 'mdxJsxAttribute', name: 'title', value: title });
    if (description) attributes.push({ type: 'mdxJsxAttribute', name: 'subtitle', value: description });

    tree.children.unshift({
      type: 'mdxJsxFlowElement',
      name: 'DocHead',
      attributes,
      children: [],
    });
  };
}
