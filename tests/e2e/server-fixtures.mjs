export const ids = {
  document: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  version: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
  chunk: 'cccccccc-cccc-cccc-cccc-cccccccccccc'
};

export function createInitialState() {
  return {
    documentIndexed: false,
    approvedChunks: 0,
    externalProviderCalls: 0,
    workToolRequests: 0,
    ruleKinds: { include: [], exclude: [] },
    handoffState: 'WaitingHuman',
    handoffVersion: 1,
    candidateStatus: 'pending',
    finalAnswer: '由人工确认的安全答案。',
    robot: {
      id: 'robot-e2e', name: 'E2E 机器人', robotReference: 'configured',
      hasWorkToolRobotId: true, isEnabled: true, sendRateLimitPerMinute: 50,
      updatedAtUtc: '2026-07-23T00:00:00Z'
    },
    model: {
      id: 'model-e2e', name: 'e2e-chat', provider: 'fake-local', configurationType: 'chat',
      baseUrl: 'http://127.0.0.1:4178/__fake/chat', model: 'safe-chat-v1',
      timeoutSeconds: 5, maxRetries: 0, isEnabled: true, isDefault: true,
      hasApiKey: true, lastFour: '1234'
    }
  };
}

export function page(items, currentPage = 1, pageSize = 20, total = items.length) {
  return { items, total, page: currentPage, pageSize };
}

export function groupConfiguration() {
  return {
    id: 'group-e2e', name: '技术部',
    rules: {
      include: [{ id: 'rule-include', pattern: '^技术部', patternKind: 'regex', ignoreCase: true }],
      exclude: [{ id: 'rule-exclude', pattern: '禁用', patternKind: 'contains', ignoreCase: true }]
    },
    boundTagIds: ['11111111-1111-1111-1111-111111111111'],
    allowedTagIds: ['11111111-1111-1111-1111-111111111111'],
    availableTags: [{ id: '11111111-1111-1111-1111-111111111111', name: '安全测试', isGlobalPublic: false, isEnabled: true, isBound: true }],
    tagVisibility: 'any-bound-tag-or-global-public',
    context: {
      configured: {},
      effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true }
    },
    clearedContextSessions: 0
  };
}

export function auditPage(_state, currentPage, pageSize) {
  const first = {
    id: 'audit-e2e', groupProfileId: 'group-e2e', workToolMessageId: 'recorded-e2e-message',
    question: '如何重置密码？', answer: '请使用安全重置页面。', decision: 'Answer',
    createdAtUtc: '2026-07-23T00:00:00Z', sources: ['安全手册'],
    evidence: [{ documentId: 'safe-document', chunkId: 'safe-chunk', title: '安全手册' }],
    inputSummary: { promptTemplateVersion: 'grounded-v2' },
    send: { status: 'completed', attemptCount: 1 },
    handoff: {
      state: 'Resolved', reasonCode: 'explicit_transfer', pauseScope: 'Group',
      transitions: [{ sequence: 1, fromState: 'AIActive', toState: 'WaitingHuman', reasonCode: 'explicit_transfer' }]
    },
    knowledgeCandidate: { status: 'approved_pending_index' }
  };
  const second = {
    id: 'audit-e2e-page-2', groupProfileId: 'group-e2e', workToolMessageId: 'recorded-e2e-message-2',
    question: '第二页审计问题', answer: '第二页安全回答。', decision: 'Answer',
    createdAtUtc: '2026-07-22T23:00:00Z', sources: [], evidence: [],
    inputSummary: { promptTemplateVersion: 'grounded-v2' }, send: { status: 'completed', attemptCount: 1 },
    handoff: null, knowledgeCandidate: null
  };
  return page(currentPage === 1 ? [first] : currentPage === 2 ? [second] : [], currentPage, pageSize, 21);
}
