import { expect, test, type Page } from '@playwright/test';

const users = {
  admin: { email: 'admin@e2e.local', password: 'Safe-E2E-Admin-1!' },
  knowledge: { email: 'knowledge@e2e.local', password: 'Safe-E2E-Knowledge-1!' }
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

async function loginToken(page: Page, user: keyof typeof users): Promise<string> {
  const response = await page.request.post('/api/auth/login', { data: users[user] });
  expect(response.ok()).toBeTruthy();
  return (await response.json()).accessToken as string;
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
  await login(page, 'admin');
  for (const [path, heading] of [['/robots', '机器人设置'], ['/groups', '已登记群'], ['/knowledge/documents', '知识文档'],
    ['/fixed-replies', '固定回复模板'], ['/knowledge/private-ingests', '私聊知识入库'],
    ['/audit', '会话审计']] as const) {
    await page.goto(path);
    await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
  }

  await page.evaluate(() => localStorage.clear());
  await login(page, 'knowledge');
  await expect(page.getByRole('link', { name: '知识库', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: '群管理' })).toHaveCount(0);
  for (const path of ['/groups', '/fixed-replies', '/knowledge/private-ingests', '/robots']) {
    await page.goto(path);
    await expect(page).toHaveURL('/');
  }
  await page.goto('/audit');
  await expect(page.getByRole('heading', { name: '会话审计' })).toBeVisible();

  const adminToken = await loginToken(page, 'admin');
  const knowledgeToken = await loginToken(page, 'knowledge');
  const get = (path: string, token: string) => page.request.get(path, { headers: { Authorization: `Bearer ${token}` } });
  const paths = ['/api/admin/model-configurations', '/api/audit/conversations', '/api/knowledge/candidates/'];
  const expected = {
    admin: [200, 200, 200],
    knowledge: [403, 200, 200]
  };
  for (const [role, token] of Object.entries({ admin: adminToken, knowledge: knowledgeToken })) {
    for (let index = 0; index < paths.length; index += 1)
      expect((await get(paths[index], token)).status()).toBe(expected[role as keyof typeof expected][index]);
  }
});

test('admin updates robot and fake model settings and previews every group rule mode', async ({ page }) => {
  await login(page, 'admin');
  await page.getByRole('link', { name: '机器人设置' }).click();
  await page.getByLabel('机器人名称').fill('安全验收机器人');
  await page.getByLabel('发送限流').fill('40');
  await page.getByRole('button', { name: '保存设置', exact: true }).click();
  await expect(page.getByText('机器人设置已保存。')).toBeVisible();

  await page.getByRole('link', { name: '模型配置' }).click();
  await page.getByTestId('create-model').click();
  await page.getByLabel('配置名称').fill('E2E 对话模型');
  await page.getByLabel('接口地址').fill('http://127.0.0.1:4178/__fake/chat');
  await page.getByLabel('模型名称').fill('safe-chat-v1');
  await page.getByTestId('model-save').click();
  const modelId = '33333333-3333-3333-3333-333333333333';
  const modelCard = page.getByTestId(`model-card-${modelId}`);
  await expect(modelCard).toContainText('E2E 对话模型');
  await expect(modelCard).toContainText('待测试');
  await page.getByTestId(`test-${modelId}`).click();
  await expect(modelCard).toContainText('测试成功');
  await page.getByTestId(`enable-${modelId}`).click();
  await expect(modelCard).toContainText('已启用');
  await page.getByTestId(`default-${modelId}`).click();
  await expect(modelCard).toContainText('默认');
  await page.getByTestId(`edit-${modelId}`).click();
  await page.getByLabel('配置名称').fill('E2E 对话模型（已改名）');
  await page.getByTestId('model-save').click();
  await expect(page.getByTestId(`model-card-${modelId}`)).toContainText('E2E 对话模型（已改名）');
  await page.reload();
  await expect(page.getByTestId(`model-card-${modelId}`)).toContainText('已启用');
  await expect(page.getByTestId(`model-card-${modelId}`)).toContainText('默认');

  await page.getByRole('link', { name: '群管理' }).click();
  await page.getByTestId('configure-group').click();
  await page.getByRole('tab', { name: '高级设置' }).click();
  await page.getByText('匹配规则', { exact: true }).click();
  await page.getByTestId('add-exact-include').click();
  await page.getByLabel('include-1-模式').fill('技术部');
  await page.getByTestId('add-contains-include').click();
  await page.getByLabel('include-2-模式').fill('技术');
  await page.getByText('保存前预览', { exact: true }).click();
  await page.getByLabel('已知群名称').fill('技术部\n技术部-禁用');
  await page.getByTestId('preview-rules').click();
  await expect(page.getByText('技术部：将匹配')).toBeVisible();
  await expect(page.getByText('技术部-禁用：已排除')).toBeVisible();
  const ruleEvidence = await page.request.get('/__e2e/evidence');
  expect(await ruleEvidence.json()).toMatchObject({
    ruleKinds: { include: ['contains', 'exact', 'regex'], exclude: ['contains'] }
  });
});

test('knowledge operator uploads and reads sanitized audit evidence', async ({ page }) => {
  await login(page, 'knowledge');
  await page.getByRole('link', { name: '知识库', exact: true }).click();
  await page.locator('#knowledge-file').setInputFiles({
    name: 'safe-e2e.md',
    mimeType: 'text/markdown',
    buffer: Buffer.from('# Safe E2E\nAPI seeded acceptance document.')
  });
  await page.getByTestId('upload-document').click();
  await expect(page.getByText('已上传 safe-e2e.md，当前状态：preview_ready。')).toBeVisible();
  await page.getByRole('link', { name: '会话审计' }).click();
  await expect(page.getByText('安全手册', { exact: true })).toBeVisible();
  await expect(page.getByText('请使用安全重置页面。')).toBeVisible();
  await expect(page.getByText('grounded-v2')).toBeVisible();
  await expect(page.getByText('completed')).toBeVisible();
  await expect(page.getByText('approved_pending_index')).toBeVisible();
  await page.locator('.el-pagination .btn-next').click();
  await expect(page.getByText('第二页审计问题')).toBeVisible();
  await expect(page.getByText('安全手册', { exact: true })).toHaveCount(0);
  const evidence = await page.request.get('/__e2e/evidence');
  expect(await evidence.json()).toMatchObject({
    externalProviderCalls: 0,
    workToolRequests: 0
  });
});
