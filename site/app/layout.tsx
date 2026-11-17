import './globals.css';
import type { ReactNode } from 'react';
import type { Metadata, Viewport } from 'next';
import localFont from 'next/font/local';
import Nav from '@/components/Nav';

// next/font/local emits the @font-face and its URL itself, so the asset picks up
// the Pages base path without hard-coding "/gui-term.woff" in the stylesheet.
const guiTerm = localFont({
  src: '../public/gui-term.woff',
  variable: '--font-gui-term',
  display: 'swap',
});

const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? '';

export const metadata: Metadata = {
  title: 'Gui — Windows 上的终端模拟器',
  description:
    'Gui 是一个快速、功能丰富的 Windows 终端模拟器：平台原生界面、GPU 加速渲染，解析内核来自 Ghostty。',
  icons: {
    icon: [
      { url: `${basePath}/favicon.ico`, sizes: 'any' },
      { url: `${basePath}/app-icon.png`, sizes: '256x256', type: 'image/png' },
    ],
  },
};

export const viewport: Viewport = {
  themeColor: '#0f0f11',
};

// Applied before paint so a returning EN visitor never sees a flash of Chinese.
const langScript = `try{var l=localStorage.getItem('gui-lang');if(l==='en'){var d=document.documentElement;d.dataset.lang='en';d.lang='en'}}catch(e){}`;

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="zh-CN" data-lang="zh" className={guiTerm.variable} suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: langScript }} />
      </head>
      <body>
        <Nav />
        {children}
      </body>
    </html>
  );
}
