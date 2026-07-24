const REDACTED = '[已移除秘密值]';

function isBlockedKey(key: string): boolean {
  const normalized = key.replace(/[^a-z0-9]/gi, '').toLowerCase();
  return normalized === 'authorization' ||
    normalized === 'cookie' ||
    normalized === 'session' ||
    normalized === 'pwd' ||
    normalized === 'passwd' ||
    normalized === 'passphrase' ||
    normalized === 'token' ||
    normalized.endsWith('authorization') ||
    normalized.endsWith('credential') ||
    normalized.endsWith('credentials') ||
    normalized.endsWith('password') ||
    normalized.endsWith('privatekey') ||
    normalized.endsWith('secretkey') ||
    normalized.endsWith('accesskey') ||
    normalized.endsWith('apikey') ||
    normalized.endsWith('secret') ||
    normalized.endsWith('token') ||
    normalized.endsWith('sessionid') ||
    normalized.endsWith('sessionkey') ||
    normalized.endsWith('cookieheader');
}

function redactText(value: string): string {
  return value
    .replace(/-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----/gi, REDACTED)
    .replace(/(\bAuthorization\s*[:=]\s*)[^,;"'\r\n]+/gi, `$1${REDACTED}`)
    .replace(/(\bCookie\s*[:=]\s*)[^,;"'\r\n]+/gi, `$1${REDACTED}`)
    .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]+/gi, `Bearer ${REDACTED}`)
    .replace(/\bAKIA[A-Z0-9]{16}\b/g, REDACTED)
    .replace(/\b(?:glpat-|ghp_|github_pat_)[A-Za-z0-9_-]{8,}\b/gi, REDACTED)
    .replace(/\bsk-[A-Za-z0-9_-]{4,}\b/gi, REDACTED)
    .replace(/(\b(?:credential|token|password|passwd|pwd|passphrase|secret|private[_\s-]?key|access[_\s-]?key|secret[_\s-]?key|api[_\s-]?key|cookie|session)\s*[:=]\s*)[^\s,;"'&]+/gi, `$1${REDACTED}`)
    .replace(/([?&](?:access_token|refresh_token|api_key|token|password|passwd|pwd|passphrase|secret|private_key|access_key|secret_key|cookie|session)=)[^&#\s"']+/gi, `$1${REDACTED}`);
}

function sanitize(value: unknown, seen: WeakSet<object>): unknown {
  if (typeof value === 'string') return redactText(value);
  if (!value || typeof value !== 'object') return value;
  if (seen.has(value)) return '[已移除循环引用]';
  seen.add(value);
  if (Array.isArray(value)) return value.map(entry => sanitize(entry, seen));
  return Object.fromEntries(Object.entries(value).map(([key, entry]) => [
    key,
    isBlockedKey(key) ? REDACTED : sanitize(entry, seen)
  ]));
}

export function safeEvidence(value: unknown): string {
  let normalized = value;
  if (typeof value === 'string') {
    try {
      normalized = JSON.parse(value);
    } catch {
      return redactText(value);
    }
  }
  if (!normalized || typeof normalized !== 'object') return redactText(String(normalized ?? '—'));
  return redactText(JSON.stringify(sanitize(normalized, new WeakSet<object>()), null, 2));
}

export function safeEvidenceText(value: unknown): string {
  return redactText(String(value ?? '—'));
}
