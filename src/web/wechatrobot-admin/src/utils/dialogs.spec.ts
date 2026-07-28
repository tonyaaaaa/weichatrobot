import { beforeEach, describe, expect, it, vi } from 'vitest';

const { confirm, prompt } = vi.hoisted(() => ({
  confirm: vi.fn(),
  prompt: vi.fn()
}));

vi.mock('element-plus', async importOriginal => ({
  ...(await importOriginal<typeof import('element-plus')>()),
  ElMessageBox: { confirm, prompt }
}));

import { confirmAction, promptAction } from './dialogs';

describe('Element Plus dialog actions', () => {
  beforeEach(() => {
    confirm.mockReset();
    prompt.mockReset();
  });

  it('normalizes confirmation and cancellation', async () => {
    confirm.mockResolvedValueOnce('confirm').mockRejectedValueOnce('cancel');
    await expect(confirmAction('继续吗？')).resolves.toBe(true);
    await expect(confirmAction('继续吗？')).resolves.toBe(false);
  });

  it('uses dangerous styling when requested', async () => {
    confirm.mockResolvedValue('confirm');
    await confirmAction('确认删除？', { danger: true, confirmButtonText: '确认删除' });
    expect(confirm).toHaveBeenCalledWith('确认删除？', '请确认', expect.objectContaining({
      type: 'warning',
      confirmButtonClass: 'el-button--danger',
      confirmButtonText: '确认删除'
    }));
  });

  it('returns prompt text or null when cancelled', async () => {
    prompt.mockResolvedValueOnce({ value: '12' }).mockRejectedValueOnce({ action: 'close' });
    await expect(promptAction('请输入位置')).resolves.toBe('12');
    await expect(promptAction('请输入位置')).resolves.toBeNull();
  });
});
