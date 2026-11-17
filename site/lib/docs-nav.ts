export type DocsNavChild = {
  href: string;
  zh: string;
  en: string;
  external?: boolean;
  children?: DocsNavChild[];
};
export type DocsNavItem = DocsNavChild;

// The documentation tree, in sidebar order, nested the way ghostty.org nests it:
// Config and Keybindings are folders whose first child is the folder's own
// overview page. The sidebar and the breadcrumbs both read this, so a new page
// only has to be added in one place.
export const docsNav: DocsNavItem[] = [
  { href: '/docs', zh: '关于', en: 'About' },
  { href: '/docs/download', zh: '下载', en: 'Download' },
  {
    href: 'https://github.com/shizengzhou/Gui/releases',
    zh: '发布说明',
    en: 'Release Notes',
    external: true,
  },
  {
    href: '/docs/config',
    zh: '配置',
    en: 'Config',
    children: [
      { href: '/docs/config', zh: '概览', en: 'Overview' },
      { href: '/docs/config/reference', zh: '选项参考', en: 'Option Reference' },
      {
        href: '/docs/config/keybind',
        zh: '按键绑定',
        en: 'Keybindings',
        children: [
          { href: '/docs/config/keybind', zh: '概览', en: 'Overview' },
          { href: '/docs/config/keybind/sequence', zh: '触发序列', en: 'Trigger Sequences' },
          { href: '/docs/config/keybind/reference', zh: '动作参考', en: 'Action Reference' },
        ],
      },
    ],
  },
  {
    href: '/docs/features',
    zh: '功能',
    en: 'Features',
    children: [
      { href: '/docs/features', zh: '概览', en: 'Overview' },
      { href: '/docs/features/theme', zh: '配色主题', en: 'Color Theme' },
      { href: '/docs/features/shell-integration', zh: 'Shell 集成', en: 'Shell Integration' },
    ],
  },
];
