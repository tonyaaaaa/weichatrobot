import { describe, expect, it } from 'vitest';
import { formatBeijingTime } from './beijingTime';

describe('formatBeijingTime', () => {
  it('formats UTC timestamps using the shared Asia/Shanghai timezone', () => {
    expect(formatBeijingTime('2026-07-22T16:30:00Z')).toContain('2026');
    expect(formatBeijingTime('2026-07-22T16:30:00Z')).toContain('07');
    expect(formatBeijingTime('2026-07-22T16:30:00Z')).toContain('23');
    expect(formatBeijingTime('2026-07-22T16:30:00Z')).toContain('00:30');
  });
});
