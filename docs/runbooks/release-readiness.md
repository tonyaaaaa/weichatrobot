# Release readiness evidence

This page records Task 18 automated acceptance only. It is not the final technical-department checklist and does not declare the whole branch or MVP complete.

## Reproducible default acceptance

`npm test --prefix tests/e2e` builds the real Vue production bundle, serves it from `127.0.0.1:4178`, and exercises it through Playwright 1.61.1. Test state is reset and seeded through `POST /__e2e/reset`. The controlled server supplies safe API records; only external provider boundaries are fake.

The browser suite rejects any request whose host is not `127.0.0.1` or `localhost`. The controlled API separately counts WorkTool-shaped requests and the final evidence assertion requires both `externalProviderCalls` and `workToolRequests` to equal zero.

| Design acceptance condition | Task 18 automated evidence | Real/manual status |
| --- | --- | --- |
| Document upload, chunk approval, indexing | Safe Markdown upload through built UI; approved chunk and queued index asserted | TXT, PDFs, DOCX remain covered by server suites; real OSS/OCR run pending |
| Exact/contains/regex/exclude group rules | API-seeded `技术部` preview and exclusion result asserted | Real target pending |
| Existing-group invitation and configuration | Runbook requires manual invitation and permission confirmation | Pending explicit approval |
| New external group and contacts | Separately gated command 206/207 test | Pending separate explicit approval |
| No-at answer using allowed knowledge/context | Sanitized recorded callback preserves `atMe=false`; server pipeline tests cover processing | Real target pending |
| No source in group; full authorized audit evidence | Browser asserts the UI honestly reports the absent audit-read API; safe handoff evidence is visible | Full audit-page real check pending backend read API and explicit run |
| Duplicate callback | Existing integration coverage plus real evidence key | Real target pending |
| Rate limit, retry, dead letter | Existing server suites and operations acceptance | Real observation pending |
| Transfer, employee notification, AI pause | API-seeded handoff reason, assignment, resolution, and transitions | Notification/real pause pending |
| Human answer approval and later retrieval | Browser resolves handoff and approves resulting candidate | Later real semantic retrieval pending |
| Three-role authorization | Knowledge and human navigation plus direct route guard assertions; server role tests remain authoritative | Final role/page matrix is outside Task 18 |

## Evidence handling

- Default artifacts contain only deterministic fixture IDs and fake credentials.
- Real runs may record UTC timestamps and audit GUIDs only.
- Never commit robot IDs, callback secrets, member IDs, Authorization headers, raw provider responses, or group message bodies.
- Failed Playwright traces and screenshots stay under ignored `tests/e2e/test-results/`; review and sanitize before sharing.

## Task 18 command matrix

Run from the repository root:

```powershell
dotnet test WechatRobot.slnx
npm ci --prefix src/web/wechatrobot-admin
npm run test --prefix src/web/wechatrobot-admin
npm run typecheck --prefix src/web/wechatrobot-admin
npm run build --prefix src/web/wechatrobot-admin
npm ci --prefix tests/e2e
npm test --prefix tests/e2e
docker compose config
```

The real WorkTool and Alibaba Cloud OCR categories are expected to report skipped in an ordinary run. OCR requires `RUN_ALIYUN_OCR_E2E=1` plus both dedicated Alibaba Cloud OCR credential variables; never enable it in routine verification.
