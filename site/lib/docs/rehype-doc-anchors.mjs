// The zh and en MDX files are both rendered into the DOM (CSS shows one at a
// time), so every id and in-page link must be scoped by language or they
// collide. This plugin also gives headings stable ids and a "#" anchor, the
// same way ghostty.org/docs/about does, and localizes GFM footnote labels.
//
// It runs on hast, so it sees both markdown elements (`element`) and JSX
// written inside the MDX source (`mdxJsxFlowElement` / `mdxJsxTextElement`).
// All prefixing is idempotent: values already carrying the tag are left alone.

function walk(node, fn) {
  if (!node || typeof node !== 'object') return;
  fn(node);
  if (Array.isArray(node.children)) {
    for (const child of node.children) walk(child, fn);
  }
}

function textOf(node) {
  if (!node) return '';
  if (node.type === 'text') return node.value || '';
  if (Array.isArray(node.children)) return node.children.map(textOf).join('');
  return '';
}

function slugify(value) {
  return (
    value
      .toLowerCase()
      .trim()
      .replace(/[^\p{L}\p{N}\s-]/gu, '')
      .replace(/\s+/g, '-')
      .replace(/-+/g, '-')
      .replace(/^-|-$/g, '') || 'section'
  );
}

// Attributes whose values reference an id elsewhere in the document.
const IDREF_ATTRS = ['ariaDescribedBy', 'ariaLabelledBy', 'headers', 'htmlFor', 'for'];

function tagValue(value, tag) {
  return value.startsWith(tag) ? value : tag + value;
}

// `props` is a hast properties object; mutate it in place.
function scopeProperties(props, tag) {
  if (!props) return;
  if (typeof props.id === 'string') props.id = tagValue(props.id, tag);
  if (typeof props.href === 'string' && props.href.startsWith('#')) {
    props.href = '#' + tagValue(props.href.slice(1), tag);
  }
  for (const key of IDREF_ATTRS) {
    if (typeof props[key] === 'string') {
      props[key] = props[key]
        .split(/\s+/)
        .map((token) => (token ? tagValue(token, tag) : token))
        .join(' ');
    }
  }
}

// MDX JSX attributes are an array of {type:'mdxJsxAttribute', name, value}.
function scopeJsxAttributes(attributes, tag) {
  if (!Array.isArray(attributes)) return;
  const names = new Set(['id', 'href', ...IDREF_ATTRS]);
  const fake = {};
  const picked = [];
  for (const attr of attributes) {
    if (attr.type === 'mdxJsxAttribute' && typeof attr.value === 'string' && names.has(attr.name)) {
      fake[attr.name] = attr.value;
      picked.push(attr);
    }
  }
  scopeProperties(fake, tag);
  for (const attr of picked) attr.value = fake[attr.name];
}

export default function rehypeDocAnchors() {
  return (tree, file) => {
    const source = (file && (file.path || (file.history && file.history[0]))) || '';
    const match = source.match(/\.(zh|en)\.mdx?$/i);
    const tag = (match ? match[1] : 'doc') + '-';
    const footnoteLabel = match && match[1] === 'zh' ? '脚注' : 'Footnotes';

    const used = new Set();
    let headingCount = 0;

    // 1. Markdown headings: assign a language-scoped id and append a "#" link.
    //    Skip remark-gfm's auto-generated "Footnotes" label heading.
    walk(tree, (node) => {
      if (node.type !== 'element' || !/^h[2-4]$/.test(node.tagName)) return;
      const props = node.properties || {};
      const className = Array.isArray(props.className) ? props.className : [];
      if (props.id === 'footnote-label' || className.includes('sr-only')) {
        if (textOf(node) === 'Footnotes') node.children = [{ type: 'text', value: footnoteLabel }];
        return;
      }
      let slug = slugify(textOf(node));
      if (used.has(slug)) {
        let n = 2;
        while (used.has(slug + '-' + n)) n++;
        slug = slug + '-' + n;
      }
      used.add(slug);
      headingCount += 1;
      const id = tag + slug;
      node.properties = props;
      props.id = id;
      props.className = ['about-section', ...(headingCount === 1 ? ['about-section-first'] : []), ...className];
      node.children = node.children || [];
      node.children.push({
        type: 'element',
        tagName: 'a',
        properties: { className: ['anchor'], href: '#' + id, ariaLabel: 'Link to this section' },
        children: [{ type: 'text', value: '#' }],
      });
    });

    // 2. Scope ids/hash-hrefs on markdown elements and on MDX JSX attributes.
    walk(tree, (node) => {
      if (node.type === 'element') scopeProperties(node.properties, tag);
      if (node.type === 'mdxJsxFlowElement' || node.type === 'mdxJsxTextElement') {
        scopeJsxAttributes(node.attributes, tag);
      }
    });
  };
}
