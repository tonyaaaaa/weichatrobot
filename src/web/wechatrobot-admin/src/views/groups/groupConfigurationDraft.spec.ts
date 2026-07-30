import { describe, expect, it } from 'vitest';
import type { GroupConfiguration } from '../../api/groups';
import { createGroupConfigurationDraft, groupConfigurationDraftSignature } from './groupConfigurationDraft';

const configuration = {
  id: 'group-1',
  name: '技术群',
  identity: {
    robotName: '默认机器人',
    registrationSource: 'WorkToolImport',
    state: 'enabled',
    isEnabled: true,
    stateVersion: 1
  },
  rules: { include: [], exclude: [] },
  boundTagIds: ['tag-b', 'tag-a'],
  allowedTagIds: [],
  availableTags: [],
  tagVisibility: 'any-bound-tag-or-global-public',
  context: {
    configured: {},
    effective: {
      senderIsolated: false,
      historyTurns: 6,
      idleTimeoutMinutes: 30,
      tokenCap: 3000,
      summaryEnabled: true,
      includeBotHistory: true
    }
  },
  answerFallback: {
    webSearchEnabled: false,
    modelKnowledgeFallbackEnabled: false,
    webSearchShowSources: false,
    webSearchResultCount: 5,
    webSearchRecency: 'NoLimit',
    webSearchDomainFilter: null,
    webSearchContentSize: 'Medium',
    finalNoEvidencePolicy: 'InsufficientEvidence'
  },
  defaultChatModel: {
    isConfigured: true,
    configurationName: '默认模型',
    connectionStatus: 'Succeeded',
    webSearchMode: 'None',
    canUseWebSearch: false,
    unavailableReason: 'unsupported'
  },
  memorySummary: {
    activeGroupMemoryCount: 0,
    activeMemberMemoryCount: 0,
    pendingCandidateCount: 0,
    pendingOrRunningJobCount: 0
  },
  clearedContextSessions: 0,
  configurationVersion: 4
} satisfies GroupConfiguration;

describe('groupConfigurationDraft', () => {
  it('clones only editable configuration and ignores read-only response fields', () => {
    const draft = createGroupConfigurationDraft(configuration);
    draft.boundTagIds.push('tag-c');

    expect(configuration.boundTagIds).toEqual(['tag-b', 'tag-a']);
    expect(draft).not.toHaveProperty('identity');
    expect(draft).not.toHaveProperty('configurationVersion');
  });

  it('normalizes tag order when calculating dirty state', () => {
    const first = createGroupConfigurationDraft(configuration);
    const second = createGroupConfigurationDraft(configuration);
    second.boundTagIds.reverse();

    expect(groupConfigurationDraftSignature(first)).toBe(groupConfigurationDraftSignature(second));
    second.answerFallback.modelKnowledgeFallbackEnabled = true;
    expect(groupConfigurationDraftSignature(first)).not.toBe(groupConfigurationDraftSignature(second));
  });
});
