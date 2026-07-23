import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('global button styles', () => {
  const styles = readFileSync(join(process.cwd(), 'src', 'styles.css'), 'utf8');

  it('does not override Element Plus button hover colors', () => {
    expect(styles).toContain('button:not(.el-button):hover:not(:disabled)');
    expect(styles).not.toContain('button:hover:not(:disabled)');
  });
});
