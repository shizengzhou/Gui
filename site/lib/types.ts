export type Localized = { zh: string; en: string };

export type IconName =
  | 'window' | 'bolt' | 'chip' | 'sliders' | 'columns' | 'keyboard'
  | 'search' | 'link' | 'github' | 'download' | 'arrow' | 'book' | 'home';

export type Feature = { icon: IconName; title: Localized; desc: Localized };
export type Extra = { icon: IconName; text: Localized };
export type Download = {
  primary: boolean;
  icon: IconName;
  title: Localized;
  desc: Localized;
  // Tail of the release asset this card resolves, e.g. '-x64-lite-setup.exe'. One version
  // publishes one file per architecture and per runtime variant, so the whole tail is the
  // only thing that tells the two apart.
  suffix: string;
  file: string;
  size: string;
  url: string;
};
