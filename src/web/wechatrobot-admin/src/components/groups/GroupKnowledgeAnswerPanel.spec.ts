import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import GroupKnowledgeAnswerPanel from './GroupKnowledgeAnswerPanel.vue';

describe('GroupKnowledgeAnswerPanel', () => {
  it('shows the complete answer chain and real model capability warning', () => {
    const wrapper = mount(GroupKnowledgeAnswerPanel, {
      props: {
        availableTags: [
          { id: 'tag-1', name: '售后知识', isGlobalPublic: false, isEnabled: true, isBound: true },
          { id: 'tag-2', name: '公共知识', isGlobalPublic: true, isEnabled: true, isBound: false }
        ],
        boundTagIds: ['tag-1'],
        answerFallback: {
          webSearchEnabled: true,
          modelKnowledgeFallbackEnabled: true,
          webSearchShowSources: false,
          webSearchResultCount: 5,
          webSearchRecency: 'NoLimit',
          webSearchDomainFilter: null,
          webSearchContentSize: 'Medium',
          finalNoEvidencePolicy: 'InsufficientEvidence'
        },
        defaultChatModel: {
          isConfigured: true,
          configurationName: 'glm',
          connectionStatus: 'Succeeded',
          webSearchMode: 'None',
          canUseWebSearch: false,
          unavailableReason: 'unsupported'
        }
      }
    });

    expect(wrapper.findAll('[data-testid="answer-step"]').map(item => item.text())).toEqual([
      expect.stringContaining('先查知识库'),
      expect.stringContaining('未命中时继续尝试'),
      expect.stringContaining('仍无可靠答案时')
    ]);
    expect(wrapper.text()).toContain('glm');
    expect(wrapper.text()).toContain('将跳过联网搜索');
    expect(wrapper.text()).toContain('搜索结果数量');
    expect(wrapper.text()).toContain('公共知识');
    expect(wrapper.text()).toContain('全局公开');
  });

  it('describes an unconfigured web search mode as not enabled instead of unsupported', () => {
    const wrapper = mount(GroupKnowledgeAnswerPanel, {
      props: {
        availableTags: [],
        boundTagIds: [],
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
          configurationName: 'glm',
          connectionStatus: 'Succeeded',
          webSearchMode: 'None',
          canUseWebSearch: false,
          unavailableReason: 'not_enabled'
        }
      }
    });

    expect(wrapper.text()).toContain('尚未启用 Web Search');
    expect(wrapper.text()).not.toContain('不支持 Web Search');
  });
});
