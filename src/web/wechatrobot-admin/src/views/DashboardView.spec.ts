import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import type { DashboardApi, DashboardSummary } from '../api/dashboard';
import DashboardView from './DashboardView.vue';

function summary(): DashboardSummary {
  return {
    checkedAtUtc: '2026-07-25T07:00:00Z',
    robots: {
      total: 3,
      enabled: 2,
      reachable: 1,
      online: 1,
      messageCallbackConfigured: 1,
      commandResultCallbackConfigured: 1,
      failedChecks: 1
    },
    knowledge: {
      documents: 8,
      versions: 12,
      pendingCandidates: 4,
      failedTasks: 2
    },
    operations: {
      durableJobs: { pending: 5, completed: 20 },
      sendCommands: { retrying: 3, completed: 18 },
      deadLetters: 1
    },
    readiness: {
      status: 'failed',
      components: [
        { name: 'MySQL', status: 'healthy', required: true },
        {
          name: 'Qdrant',
          status: 'failed',
          required: true,
          detail: 'unavailable'
        }
      ]
    }
  };
}

function mountView(api: DashboardApi) {
  return mount(DashboardView, { props: { api } });
}

describe('DashboardView', () => {
  it('shows a loading state until the aggregate request completes', async () => {
    const pending = new Promise<DashboardSummary>(() => {});
    const wrapper = mountView({ getSummary: vi.fn(() => pending) });

    expect(wrapper.get('[aria-label="正在加载工作台"]').attributes('aria-label'))
      .toBe('正在加载工作台');
  });

  it('renders database counts, queue states, readiness and Beijing check time', async () => {
    const wrapper = mountView({
      getSummary: vi.fn().mockResolvedValue(summary())
    });
    await flushPromises();

    expect(wrapper.get('[data-testid="robot-total"]').text()).toBe('3');
    expect(wrapper.get('[data-testid="knowledge-documents"]').text()).toBe('8');
    expect(wrapper.get('[data-testid="pending-candidates"]').text()).toBe('4');
    expect(wrapper.get('[data-testid="dead-letters"]').text()).toBe('1');
    expect(wrapper.text()).toContain('pending');
    expect(wrapper.text()).toContain('5');
    expect(wrapper.text()).toContain('retrying');
    expect(wrapper.text()).toContain('Qdrant');
    expect(wrapper.text()).toContain('unavailable');
    expect(wrapper.text()).toContain('2026/07/25 15:00（北京时间）');
    expect(wrapper.text()).toContain('部分检查失败');
  });

  it('keeps successful counts visible when readiness is failed', async () => {
    const wrapper = mountView({
      getSummary: vi.fn().mockResolvedValue(summary())
    });
    await flushPromises();

    expect(wrapper.get('[data-testid="knowledge-versions"]').text()).toBe('12');
    expect(wrapper.get('[data-testid="readiness-status"]').text()).toContain('异常');
  });

  it('offers retry after the aggregate request fails', async () => {
    const getSummary = vi.fn()
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce(summary());
    const wrapper = mountView({ getSummary });
    await flushPromises();

    expect(wrapper.text()).toContain('工作台数据加载失败');
    await wrapper.get('[data-testid="retry-dashboard"]').trigger('click');
    await flushPromises();

    expect(getSummary).toHaveBeenCalledTimes(2);
    expect(wrapper.get('[data-testid="robot-total"]').text()).toBe('3');
  });
});
