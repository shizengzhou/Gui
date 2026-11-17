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
  file: string;
  size: string;
  url: string;
};
