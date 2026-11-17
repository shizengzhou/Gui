// The exported site uses trailing slashes and, on GitHub Pages, lives under a
// base path (/Gui). usePathname() and window.location.pathname return that
// deployed form, while the navigation tables are written as "/docs/config", so
// both have to be stripped before comparing.
const basePath = (process.env.NEXT_PUBLIC_BASE_PATH ?? '').replace(/\/+$/, '');

export function currentPath(pathname: string | null | undefined): string {
  let path = pathname || '/';
  if (basePath && (path === basePath || path.startsWith(basePath + '/'))) {
    path = path.slice(basePath.length) || '/';
  }
  return path.length > 1 ? path.replace(/\/+$/, '') : path;
}
