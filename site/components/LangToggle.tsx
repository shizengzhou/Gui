'use client';

export default function LangToggle() {
  function toggle() {
    const doc = document.documentElement;
    const next = doc.dataset.lang === 'en' ? 'zh' : 'en';
    doc.dataset.lang = next;
    doc.lang = next === 'en' ? 'en' : 'zh-CN';
    try {
      localStorage.setItem('gui-lang', next);
    } catch (e) {
      /* storage unavailable */
    }
  }

  return (
    <button type="button" className="lang-toggle" onClick={toggle} aria-label="Switch language">
      <span className="lang-zh">EN</span>
      <span className="lang-en">中</span>
    </button>
  );
}
