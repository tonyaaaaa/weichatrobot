# Alibaba Cloud OCR replacement report

## Status

DONE_WITH_CONCERNS

The PaddleOCR/FastAPI provider was replaced by Alibaba Cloud OCR
`RecognizeGeneral` using `AlibabaCloud.SDK.Ocr-api20210707` 3.1.3. The scanned
PDF renderer, page leasing, persistence, and job-boundary recovery remain in
place. No real Alibaba Cloud request was made.

## Implementation

- Preserved the batch-shaped application `IOcrClient` and kept Alibaba SDK
  types inside Infrastructure.
- Added `AliyunOcrOptions`, `IAliyunOcrProvider`,
  `AlibabaSdkOcrProvider`, `AliyunOcrResponseParser`, and `AliyunOcrClient`.
- The SDK adapter sends the existing rendered page bytes through
  `RecognizeGeneralRequest.Body`; it does not create OSS/public URLs and does
  not implement signing.
- Validates supported image signatures, 10 MB maximum, dimensions 16..8191,
  and aspect ratio below 50 before provider invocation. The renderer's PNG
  output is treated as the explicit primary invariant; JPG/JPEG, BMP, GIF,
  TIFF, and WebP signatures are also accepted.
- Parses `Data.content` and ordered `prism_wordsInfo`; integer percentage
  probabilities are converted to 0..1 confidence. Non-empty content is the
  fallback when words are absent.
- Empty/malformed `Data` maps to the existing invalid-response error without
  exposing raw JSON.
- Retries throttling, service-unavailable/503, and algorithm timeout up to
  three total attempts per page. Other provider errors are not retried.
  Provider/algorithm timeout maps to the existing timeout error.
- Caller cancellation is honored before pages, between pages, and while
  awaiting the SDK task.
- Structured logs contain action, duration, page, attempt, provider code, and
  RequestId. RequestId remains in logs because widening the persisted result
  contract would require an unrelated migration. Logs do not include
  credentials, image bytes, or provider JSON.
- Worker configuration is fixed to Aliyun / RecognizeGeneral /
  `ocr-api.cn-hangzhou.aliyuncs.com`, 30 seconds, and three attempts.
  Credentials are read only from
  `ALIBABA_CLOUD_OCR_ACCESS_KEY_ID` and
  `ALIBABA_CLOUD_OCR_ACCESS_KEY_SECRET`.
- Readiness now reports only sanitized configured/unavailable state rather
  than probing or exposing a provider response.
- Removed `src/ocr-service`, the OCR Compose service, obsolete private HTTP
  client/policy/options, Paddle/Python commands, and loopback OCR settings.
- Added a conditionally skipped real acceptance test gated by
  `RUN_ALIYUN_OCR_E2E=1` plus both credentials. Its fixture is generated
  locally in memory and it never prints provider data.

## TDD evidence

### RED

Command:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~Ocr"
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore --filter "FullyQualifiedName~AliyunOcr"
```

Expected failure observed before production implementation:

```text
CS0246: AliyunOcrOptions could not be found
CS0103: AliyunOcrResponseParser does not exist in the current context
Build failed, exit code 1
```

### GREEN

Focused xUnit v3 commands:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore -- --filter-class '*Ocr*'
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*AliyunOcrClientTests'
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*AliyunOcrAcceptanceTests' --filter-class '*HealthTests'
```

Result:

```text
Contract: 9 passed, 0 failed
Unit: 17 passed, 0 failed
Integration focused: 17 passed, 0 failed, 1 skipped
Real Alibaba Cloud OCR acceptance: skipped (no opt-in; no paid call)
```

## Required verification

```text
dotnet build WechatRobot.slnx -warnaserror
PASS: 0 warnings, 0 errors

dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore
PASS: 31 passed, 0 failed

dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
PASS: 171 passed, 0 failed

docker compose config
PASS: topology contains MySQL and Qdrant only; no OCR container

git diff --check
PASS
```

The full IntegrationTests project ran twice and each run reported the same two
unrelated failures:

- `RagReplyPipelineTests.Fake_grounded_reply...`: expected one record, found two.
- `DocumentUploadTests.Failed_upload...`: expected no leased parse job, found
  another leased parse job.

Running those exact two tests together in isolation passed 2/2. No changed OCR
code is on either failing path, so no unrelated product/test-isolation change
was made. The integration project otherwise reported 130 passed and 3 expected
real-provider skips.

## Self-review

- Confirmed no Paddle, private OCR endpoint, OCR Compose service, or Python OCR
  runtime reference remains outside assertions that enforce their absence.
- Confirmed the package version, action, endpoint, timeout, attempts,
  credential names, and real-test gate match the brief.
- Confirmed no AccessKey values or raw provider payloads are logged.
- Confirmed changes are confined to the `codex/aliyun-ocr` worktree.
- No subagents were spawned.

## Concern

The pre-existing full integration suite has repeatable suite-level
state/isolation failures described above, although both failing tests pass in
isolation and all OCR-focused integration coverage passes.

## Review fix follow-up

Commit follow-up addresses every Important review item and the RequestId
normalization item:

- `AlibabaSdkOcrProvider` now copies the bounded request into an SDK-owned
  stream. Caller cancellation still returns immediately via `WaitAsync`, while
  the owned stream is disposed only after the non-cancellable SDK 3.1.3 task
  finishes. The SDK operation unavoidably continues in the background because
  this SDK version exposes no cancellation token; this is now explicit and no
  disposed request stream is exposed to it.
- SDK exceptions now normalize `Code`, numeric `StatusCode`, timeout semantics,
  and `RequestId` from direct properties, `DataResult`, or exception data.
  Normalized HTTP 503 participates in the three-attempt retry policy.
- Runtime/SDK timeouts map through the adapter to the existing client timeout
  semantics. `AlgorithmTimeOut` remains a three-attempt provider retry and
  maps to timeout only after exhaustion.
- The startup validation error now names the exact required OCR endpoint.

Covering files:

- `tests/server/WechatRobot.UnitTests/Knowledge/AlibabaSdkOcrProviderTests.cs`
  covers in-flight cancellation/owned-stream lifetime, status 503 and
  RequestId normalization, runtime/SDK timeout normalization, and client
  behavior through the real adapter seam.
- `tests/server/WechatRobot.UnitTests/Knowledge/AliyunOcrClientTests.cs` covers
  normalized-status retry and normalized-timeout mapping at the client
  contract.
- `tests/server/WechatRobot.ContractTests/Knowledge/OcrClientContractTests.cs`
  continues to cover response parsing.

Review-fix RED:

```text
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*AlibabaSdkOcrProviderTests' --filter-class '*AliyunOcrClientTests'
CS0246: IAlibabaOcrSdkInvoker could not be found
CS0246: AlibabaSdkRawResponse could not be found
Build failed, exit code 1
```

Review-fix GREEN:

```text
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*AlibabaSdkOcrProviderTests' --filter-class '*AliyunOcrClientTests'
PASS: 25 passed, 0 failed

dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore -- --filter-class '*Ocr*'
PASS: 9 passed, 0 failed
```

No Alibaba Cloud request was made during the review fixes or verification.

## Final whole-branch review fixes

- Retryable provider failures no longer retry immediately. `AliyunOcrClient`
  now uses injectable delay and jitter seams. Positive provider `Retry-After`
  guidance wins and is capped at 30 seconds; otherwise retries use bounded
  exponential delay starting at 200 ms with up to 50% jitter. Caller
  cancellation is passed to the delay.
- `AlibabaSdkOcrProvider` normalizes `RetryAfter` / `Retry-After` values from
  SDK exception properties, `DataResult`, or headers and carries the parsed
  delay on `AliyunOcrProviderException`.
- `Ocr:Endpoint` remains configurable. The default is Hangzhou, while startup
  accepts safe Alibaba OCR hostnames shaped as
  `ocr-api.<region>.aliyuncs.com`. Schemes, credentials, paths, ports,
  malformed labels, and arbitrary hosts are rejected.
- Worker startup calls the same `AliyunOcrOptions.Validate` method covered by
  contract tests, including a successful Shanghai-region override.
- Parameterized client tests now prove provider invocation for PNG, JPEG,
  BMP, GIF, both TIFF byte orders, and WebP signatures.

Covering tests:

- `tests/server/WechatRobot.UnitTests/Knowledge/AliyunOcrClientTests.cs`
  verifies Retry-After precedence/capping, fallback backoff/jitter, delay
  cancellation plumbing, and every accepted image signature.
- `tests/server/WechatRobot.UnitTests/Knowledge/AlibabaSdkOcrProviderTests.cs`
  verifies SDK Retry-After normalization.
- `tests/server/WechatRobot.ContractTests/Knowledge/OcrTopologyConfigurationTests.cs`
  verifies the default and the safe configurable endpoint policy.

Final-review RED:

```text
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*AlibabaSdkOcrProviderTests' --filter-class '*AliyunOcrClientTests'
CS0246: IAliyunOcrDelay and IAliyunOcrJitter could not be found

dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore -- --filter-class '*OcrTopologyConfigurationTests'
CS0117: AliyunOcrOptions does not contain IsAllowedEndpoint
```

Final-review GREEN:

```text
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*AlibabaSdkOcrProviderTests' --filter-class '*AliyunOcrClientTests'
PASS: 36 passed, 0 failed

dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore -- --filter-class '*OcrTopologyConfigurationTests'
PASS: 11 passed, 0 failed
```

No real Alibaba Cloud call was made.

Fresh final verification:

```text
dotnet build WechatRobot.slnx -warnaserror
PASS: 0 warnings, 0 errors

dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore -- --filter-class '*Ocr*'
PASS: 19 passed, 0 failed

dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*AlibabaSdkOcrProviderTests' --filter-class '*AliyunOcrClientTests'
PASS: 36 passed, 0 failed

git diff --check
PASS
```
