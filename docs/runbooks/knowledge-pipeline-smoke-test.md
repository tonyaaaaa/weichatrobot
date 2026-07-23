# Knowledge pipeline OCR smoke test

Scanned PDFs use Alibaba Cloud OCR `RecognizeGeneral`; there is no local Python OCR service.
The renderer still produces bounded PNG pages locally, and the Worker submits each page as
the official SDK binary body.

Set only the dedicated credentials before starting the Worker:

```powershell
$env:ALIBABA_CLOUD_OCR_ACCESS_KEY_ID = '<dedicated RAM access key id>'
$env:ALIBABA_CLOUD_OCR_ACCESS_KEY_SECRET = '<dedicated RAM access key secret>'
```

The RAM user is expected to have `AliyunOCRFullAccess`. The application neither provisions
IAM nor grants OSS permissions. Do not put these values in appsettings or logs.

Routine tests never call Alibaba Cloud. The real one-image acceptance test additionally
requires `$env:RUN_ALIYUN_OCR_E2E = '1'`; leave it unset unless a paid call is explicitly
approved. Readiness exposes only configured state and sanitized failures.
