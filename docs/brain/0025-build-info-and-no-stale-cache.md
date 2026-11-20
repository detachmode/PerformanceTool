# 0025. Show build info; don't serve stale cached builds

- Status: Accepted (user request); techniques are implementation choices
- Date: 2026-11-20

## What
- The page shows **build info**: the **commit** it was built from and the **date of the deploy**. Keep it small and calm
  (0016), e.g. in the top bar or footer, linking the commit to GitHub.
- The browser must **not keep using an old cached build** after a new deploy.

## Why
The user tests the UX on GitHub Pages after each merge. They need to see which build they're looking at, and they
need to get the newest one without fighting the browser cache.

## Consequences
- The Pages workflow (and local builds) inject the commit SHA and the build/deploy timestamp at publish time.
- Static assets get cache-busted (fingerprinted file names or version query strings). GitHub Pages doesn't allow
  custom HTTP cache headers, so `index.html` itself relies on GitHub's short cache plus in-page measures.
