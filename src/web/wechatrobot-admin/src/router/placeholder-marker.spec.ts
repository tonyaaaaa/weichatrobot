import { readFileSync, readdirSync } from 'node:fs';
import { extname, join } from 'node:path';
import { describe, expect, it } from 'vitest';

function productionSources(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return productionSources(path);
    if (!['.ts', '.vue'].includes(extname(path)) || path.endsWith('.spec.ts')) return [];
    return [path];
  });
}

describe('production source placeholder gate', () => {
  it('scans the actual src tree for the removed task placeholder marker', () => {
    const marker = ['Task', 'Placeholder', 'View'].join('');
    const offenders = productionSources(join(process.cwd(), 'src'))
      .filter(path => readFileSync(path, 'utf8').includes(marker));

    expect(offenders).toEqual([]);
  });
});
