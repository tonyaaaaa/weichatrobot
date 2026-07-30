import { describe, expect, it } from 'vitest';
import { routes } from '../router';
import { pageCompleteness } from './pageCompleteness';

describe('page completeness inventory', () => {
  it('covers every non-public routed administration page', () => {
    const routedNames = routes
      .flatMap(route => route.children ?? [])
      .map(route => String(route.name))
      .sort();

    expect(Object.keys(pageCompleteness).sort()).toEqual(routedNames);
    expect(Object.values(pageCompleteness)).not.toContain('unchecked');
    expect(pageCompleteness['system-settings']).toBe('blocked-no-contract');
  });
});
