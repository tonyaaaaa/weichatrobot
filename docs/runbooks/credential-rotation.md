# Credential rotation

Rotate one credential at a time, keep the old value available only for rollback, and verify authenticated readiness plus a focused fake-provider operation before revoking it. Never paste credentials, callback tokens, robot IDs, Authorization headers, OSS signed URLs, or encrypted ciphertext into logs, issues, commits, or screenshots.

## Master encryption key

The `WECHATROBOT_MASTER_KEY_BASE64` value must decode to exactly 32 bytes. Because model provider settings are stored as ciphertext, changing this value without first decrypting and re-encrypting every protected row makes existing settings unreadable. Take a database backup, export protected settings through an approved offline rotation tool, deploy the new key, re-encrypt, verify, and only then destroy the old key. Do not perform a direct environment-variable swap.

## JWT signing key

Create a new high-entropy value of at least 32 characters, deploy it to API instances, restart the API, and require users to log in again. Existing bearer tokens become invalid. Verify anonymous endpoints remain limited to liveness, login, and the token-authenticated WorkTool callback. The `/` route and detailed readiness route require bearer authentication.

## MySQL, Qdrant, OCR, and OSS

- Rotate MySQL credentials in the database and secret store, update the connection string, restart API and Worker, then verify MySQL and Worker heartbeat are healthy.
- Rotate the Qdrant API key in Qdrant and the secret store together, restart services, and verify the Qdrant component.
- OCR has no public credential in the local topology; keep it private and verify its readiness endpoint.
- Rotate both OSS access key values, restart API and Worker, verify the OSS configuration component, upload only a harmless test document, then revoke the old key.

## WorkTool callback

Generate a new high-entropy callback token, persist only its SHA-256 hash for the selected robot, and use `scripts/update-worktool-callback.ps1` with a fake target first. For a real change, use `-Apply` only after checking the displayed token-redacted callback URL and entering the exact confirmation. Verify the new token with one harmless callback before invalidating the old route. Robot IDs and callback tokens must remain out of logs.
