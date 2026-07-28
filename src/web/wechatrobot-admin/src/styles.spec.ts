import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('global button styles', () => {
  const styles = readFileSync(join(process.cwd(), 'src', 'styles.css'), 'utf8');
  const entrypoint = readFileSync(join(process.cwd(), 'src', 'main.ts'), 'utf8');

  it('loads the Element Plus message-box layout used by global confirmations', () => {
    expect(entrypoint).toContain(
      "import 'element-plus/es/components/message-box/style/css';"
    );
  });

  it('does not override Element Plus button hover colors', () => {
    expect(styles).toContain(
      'button:not([class^="el-"]):not([class*=" el-"]):hover:not(:disabled)'
    );
    expect(styles).not.toContain('button:hover:not(:disabled)');
  });

  it('keeps native checkbox and radio controls compact instead of stretching them as text inputs', () => {
    const style = document.createElement('style');
    style.textContent = styles;
    document.head.append(style);

    for (const type of ['checkbox', 'radio']) {
      const input = document.createElement('input');
      input.type = type;
      document.body.append(input);

      const computed = getComputedStyle(input);
      expect(computed.width).toBe('1.25rem');
      expect(computed.minHeight).toBe('1.25rem');

      input.remove();
    }

    style.remove();
  });
});
