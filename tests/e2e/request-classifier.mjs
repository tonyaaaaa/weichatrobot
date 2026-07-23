export function classifyRequest(pathname) {
  const path = pathname.toLowerCase();
  if (path === '/wework' || path.startsWith('/wework/')
    || path === '/api/worktool' || path.startsWith('/api/worktool/')
    || path === '/api/admin/worktool' || path.startsWith('/api/admin/worktool/')) {
    return 'worktool';
  }
  if (path === '/v1' || path.startsWith('/v1/')
    || path === '/__fake/chat' || path.startsWith('/__fake/chat/')
    || path === '/__fake/embedding' || path.startsWith('/__fake/embedding/')
    || path === '/__fake/ocr' || path.startsWith('/__fake/ocr/')
    || path === '/__fake/oss' || path.startsWith('/__fake/oss/')) {
    return 'external-provider';
  }
  return 'application';
}
