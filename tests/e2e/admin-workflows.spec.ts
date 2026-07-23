import { expect, test, type Page } from '@playwright/test';

const users = {
  admin: { email: 'admin@e2e.local', password: 'Safe-E2E-Admin-1!' },
  knowledge: { email: 'knowledge@e2e.local', password: 'Safe-E2E-Knowledge-1!' },
  human: { email: 'human@e2e.local', password: 'Safe-E2E-Human-1!' }
};

async function reset(page: Page): Promise<void> {
  await page.request.post('/__e2e/reset');
}

async function login(page: Page, user: keyof typeof users): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('邮箱').fill(users[user].email);
  await page.getByLabel('密码').fill(users[user].password);
  await page.getByRole('button', { name: '登录' }).click();
  await expect(page.getByRole('heading', { name: '工作台' })).toBeVisible();
}

test.beforeEach(async ({ page }) => {
  await reset(page);
  page.on('request', request => {
    const target = new URL(request.url());
    expect(['127.0.0.1', 'localhost']).toContain(target.hostname);
    expect(target.hostname).not.toContain('worktool');
  });
});

test('login enforces role-specific navigation and route guards', async ({ page }) => {
  await login(page, 'knowledge');
  await expect(page.getByRole('link', { name: '知识库', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: '群管理' })).toHaveCount(0);
  await page.goto('/groups');
  await expect(page).toHaveURL('/');

  await page.evaluate(() => localStorage.clear());
  await login(page, 'human');
  await expect(page.getByRole('link', { name: '人工转接' })).toBeVisible();
  await expect(page.getByRole('link', { name: '知识库', exact: true })).toHaveCount(0);
});

test('admin updates fake model settings and previews group rules', async ({ page }) => {
  await login(page, 'admin');
  await page.getByRole('link', { name: '模型配置' }).click();
  await expect(page.getByText('••••1234')).toBeVisible();
  await page.locator('#model-e2e-chat').fill('safe-chat-v2');
  await page.getByTestId('save-e2e-chat').click();
  await expect(page.getByText('e2e-chat 已保存；浏览器未保留明文密钥。')).toBeVisible();
  await page.getByTestId('test-e2e-chat').click();
  await expect(page.getByText('e2e-chat 连接测试成功。')).toBeVisible();

  await page.getByRole('link', { name: '群管理' }).click();
  await page.getByLabel('群配置 ID').fill('group-e2e');
  await page.getByRole('button', { name: '读取配置' }).click();
  await page.getByPlaceholder('每行一个已知群名称').fill('技术部\n技术部-禁用');
  await page.getByTestId('preview-rules').click();
  await expect(page.getByText('技术部：将匹配')).toBeVisible();
  await expect(page.getByText('技术部-禁用：已排除')).toBeVisible();
});

test('knowledge operator uploads, approves chunks, queues indexing, and sees honest audit boundary', async ({ page }) => {
  await login(page, 'knowledge');
  await page.getByRole('link', { name: '知识库', exact: true }).click();
  await page.locator('#knowledge-file').setInputFiles({
    name: 'safe-e2e.md',
    mimeType: 'text/markdown',
    buffer: Buffer.from('# Safe E2E\nAPI seeded acceptance document.')
  });
  await page.getByTestId('upload-document').click();
  await expect(page.getByText('已上传 safe-e2e.md，当前状态：preview_ready。')).toBeVisible();
  await page.getByTestId('open-document-detail').click();
  await expect(page.getByTestId('text-cccccccc-cccc-cccc-cccc-cccccccccccc')).toHaveValue('API seeded acceptance document.');
  page.once('dialog', dialog => dialog.accept());
  await page.getByTestId('approve-previews').click();
  await expect(page.getByText('分段已批准，可以提交索引。')).toBeVisible();
  await page.locator('#index-tag-ids').fill('11111111-1111-1111-1111-111111111111');
  await page.getByTestId('queue-index').click();
  await expect(page.getByText('索引任务已排队。')).toBeVisible();

  await page.getByRole('link', { name: '会话审计' }).click();
  await expect(page.getByText('后端暂未提供会话审计查询 API', { exact: true })).toBeVisible();
  const evidence = await page.request.get('/__e2e/evidence');
  expect(await evidence.json()).toMatchObject({
    documentIndexed: true,
    approvedChunks: 1,
    externalProviderCalls: 0,
    workToolRequests: 0
  });
});

test('human resolves a handoff and knowledge operator approves the resulting answer', async ({ page }) => {
  await login(page, 'human');
  await page.getByRole('link', { name: '人工转接' }).click();
  await page.getByTestId('handoff-handoff-e2e').click();
  await expect(page.getByText('用户明确要求人工')).toBeVisible();
  await page.getByTestId('assignee').fill('22222222-2222-2222-2222-222222222222');
  page.once('dialog', dialog => dialog.accept());
  await page.getByTestId('assign-handoff').click();
  await expect(page.getByText('转接已分配。')).toBeVisible();
  await page.getByTestId('final-answer').fill('由人工确认的安全答案。');
  page.once('dialog', dialog => dialog.accept());
  await page.getByTestId('resolve-handoff').click();
  await expect(page.getByText('转接已解决，答案等待知识审核。')).toBeVisible();

  await page.evaluate(() => localStorage.clear());
  await login(page, 'knowledge');
  await page.getByRole('link', { name: '知识审核' }).click();
  await page.getByTestId('candidate-candidate-e2e').click();
  await expect(page.locator('#revised-answer')).toHaveValue('由人工确认的安全答案。');
  await page.locator('#candidate-tags').fill('11111111-1111-1111-1111-111111111111');
  page.once('dialog', dialog => dialog.accept());
  await page.getByTestId('approve-candidate').click();
  await expect(page.getByText('审核已提交，状态：approved_pending_index')).toBeVisible();
});
