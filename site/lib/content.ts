import type { Feature, Extra, Download } from '@/lib/types';

const RELEASES_API = 'https://api.github.com/repos/shizengzhou/Gui/releases/latest';
const LATEST = 'https://github.com/shizengzhou/Gui/releases/latest/download';

export const features: Feature[] = [
  {
    icon: 'window',
    title: { zh: '原生 WinUI 3 界面', en: 'Native WinUI 3 UI' },
    desc: {
      zh: '自绘标题栏、标签栏与窗格，跟随系统亮暗；支持亚克力透明与背景模糊，透明度作用于整个窗口而非叠加。',
      en: 'A self-drawn title bar, tab rail and panes that follow the system light/dark setting, with acrylic transparency and background blur applied to the whole window at once.',
    },
  },
  {
    icon: 'bolt',
    title: { zh: 'GPU 加速渲染', en: 'GPU-accelerated rendering' },
    desc: {
      zh: 'Win2D 与 DirectWrite 每帧只重绘脏区域；内置 JetBrains Mono Nerd Font，进程内注册，Powerline 字形开箱即用。',
      en: 'Win2D and DirectWrite repaint only dirty cells each frame; the JetBrains Mono Nerd Font is bundled and registered for the process, so Powerline glyphs just work.',
    },
  },
  {
    icon: 'chip',
    title: { zh: 'Ghostty 的 VT 内核', en: "Ghostty's VT engine" },
    desc: {
      zh: '由 libghostty-vt 解析 ConPTY 字节流，终端行为对齐 Ghostty，而不是另写一套解析器。',
      en: 'libghostty-vt parses the ConPTY byte stream, so terminal behaviour tracks Ghostty instead of a home-grown parser.',
    },
  },
  {
    icon: 'sliders',
    title: { zh: '熟悉的配置格式', en: 'A familiar config' },
    desc: {
      zh: '%APPDATA%\\gui\\config 就是 Ghostty 格式：全部键位可识别，内置主题，支持 light: / dark: 条件式条目。',
      en: '%APPDATA%\\gui\\config speaks Ghostty: every key is recognised, bundled themes, and light:/dark: conditional entries.',
    },
  },
  {
    icon: 'columns',
    title: { zh: '标签页与分屏', en: 'Tabs and split panes' },
    desc: {
      zh: '拖动窗格标题即可重排分屏，拖动边界即可调整比例且重建后仍保留；缩放模式只显示当前窗格。',
      en: 'Drag a pane header to rearrange splits, drag a boundary to resize it (ratios survive a rebuild); zoom shows one pane full-bleed.',
    },
  },
  {
    icon: 'keyboard',
    title: { zh: '中文输入法与 Shell Integration', en: 'IME and shell integration' },
    desc: {
      zh: '组字串像 Windows Terminal 一样实时显示在光标处；OSC 133 / OSC 7 提示标记驱动工作目录、标题与命令完成通知。',
      en: 'Composition is drawn live at the cursor cell like Windows Terminal; OSC 133 / OSC 7 prompt marks drive cwd, titles and command-finish notices.',
    },
  },
];

export const extras: Extra[] = [
  { icon: 'search', text: { zh: '命令面板与增量搜索（覆盖回滚）', en: 'Command palette & incremental search (scrollback included)' } },
  { icon: 'link', text: { zh: 'OSC 8 超链接、OSC 52 剪贴板、Kitty 剪贴板协议', en: 'OSC 8 hyperlinks, OSC 52 clipboard, kitty clipboard protocol' } },
  { icon: 'keyboard', text: { zh: 'Kitty 键盘协议与鼠标报告', en: 'Kitty keyboard protocol & mouse reporting' } },
  { icon: 'bolt', text: { zh: 'OSC 7501 程序状态徽标', en: 'OSC 7501 program-status badge' } },
];

const downloads: Download[] = [
  {
    primary: true,
    icon: 'download',
    title: { zh: '完整安装包', en: 'Full installer' },
    desc: {
      zh: '自包含，随包携带 .NET 8 运行时，机器上什么都不用装。',
      en: 'Self-contained. Ships the .NET 8 runtime, so nothing else needs to be installed.',
    },
    file: 'Gui-0.1.1-x64-setup.exe',
    size: '≈ 63 MB · x64',
    url: `${LATEST}/Gui-0.1.1-x64-setup.exe`,
  },
  {
    primary: false,
    icon: 'bolt',
    title: { zh: 'Lite', en: 'Lite' },
    desc: {
      zh: '框架依赖，不带 .NET 8 运行时；安装时会检查，缺失时给出下载入口。',
      en: 'Framework-dependent. Leaves out the .NET 8 runtime; the setup checks for it and links to the download if missing.',
    },
    file: 'Gui-0.1.1-x64-lite-setup.exe',
    size: '≈ 31 MB · x64 · .NET 8',
    url: `${LATEST}/Gui-0.1.1-x64-lite-setup.exe`,
  },
];

type GitHubAsset = { name: string; size: number; browser_download_url: string };
type GitHubRelease = { assets?: GitHubAsset[] };

// Resolve the installer links from the latest GitHub release so they keep working
// when a new version changes the asset filenames. `downloads` above is the fallback
// used when the API is unavailable (rate-limited, offline build, network error).
// `revalidate` caches the response for an hour instead of hitting the API per request.
export async function getDownloads(): Promise<Download[]> {
  try {
    const res = await fetch(RELEASES_API, {
      headers: {
        Accept: 'application/vnd.github+json',
        'User-Agent': 'gui-site',
      },
      next: { revalidate: 3600 },
    });
    if (!res.ok) return downloads;

    const release = (await res.json()) as GitHubRelease;
    const assets = release.assets ?? [];
    const full = assets.find((a) => a.name.toLowerCase().endsWith('-x64-setup.exe'));
    const lite = assets.find((a) => a.name.toLowerCase().endsWith('-lite-setup.exe'));

    return downloads.map((d) => {
      const asset = d.file.includes('-lite-') ? lite : full;
      if (!asset) return d;
      const mib = Math.round(asset.size / 1048576);
      return {
        ...d,
        file: asset.name,
        size: d.size.replace(/^≈ \d+ MB/, `≈ ${mib} MB`),
        url: asset.browser_download_url,
      };
    });
  } catch {
    return downloads;
  }
}
