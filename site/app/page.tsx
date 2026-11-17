import Link from 'next/link';
import { T } from '@/components/T';
import { Icon } from '@/components/Icons';

export default function Home() {
  return (
    <main className="home">
      <section className="hero wrap">
        <div className="hero-copy">
          <div className="eyebrow">
            <span className="dot" />
            <T zh="Windows 上的终端模拟器" en="A terminal emulator for Windows" />
          </div>
          <h1 className="hero-title">Gui<span className="caret">_</span></h1>
          <p className="hero-lede">
            <T
              zh="Gui 是一个快速、功能丰富的 Windows 终端模拟器：平台原生界面、GPU 加速渲染，解析内核来自 Ghostty。"
              en="Gui is a fast, feature-rich terminal emulator for Windows, with platform-native UI, GPU-accelerated rendering, and Ghostty's VT engine under the hood."
            />
          </p>
          <div className="hero-actions">
            <Link className="btn btn-primary" href="/docs/download">
              <Icon name="download" />
              <T zh="下载" en="Download" />
            </Link>
            <Link className="btn btn-ghost" href="/docs">
              <Icon name="book" />
              <T zh="查看文档" en="Read the docs" />
            </Link>
          </div>
          <div className="hero-meta">
            <span>Windows 10 1809+</span><span className="sep">·</span>
            <span>x64</span><span className="sep">·</span>
            <span>MIT</span><span className="sep">·</span>
          </div>
        </div>
      </section>
    </main>
  );
}
