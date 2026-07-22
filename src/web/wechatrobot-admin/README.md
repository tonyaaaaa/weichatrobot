# NewsAgent 管理后台

## Authentication storage boundary

The browser stores the short-lived bearer access token in `localStorage` so a refresh can
hydrate the user through `GET /api/auth/me`. This is not an XSS protection boundary: a
same-origin script injection can read local storage. Production deployment must enforce a
strict Content Security Policy, avoid untrusted inline scripts, and keep third-party scripts
off the administrative origin. The client sends the bearer token only to the configured or
same-origin `/api` endpoint. Server-side authorization remains authoritative; this MVP does
not introduce a cookie-based authentication flow.

## Type checking

`npm run typecheck` runs two checks:

1. TypeScript 7.0.2 performs `.ts` validation.
2. `vue-tsc` performs Vue SFC script/template validation through the isolated
   `typescript-vue` 5.9.3 compatibility alias.

The alias is required because TypeScript 7.0.2 uses its native compiler package layout and
does not expose `typescript/lib/tsc`, which current `vue-tsc` requires. The planned TypeScript
7.0.2 runtime/tooling pin remains unchanged.
