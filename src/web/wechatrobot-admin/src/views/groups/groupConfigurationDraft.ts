import type {
  AnswerFallbackSettings,
  ContextOverrides,
  GroupConfiguration,
  GroupRule
} from '../../api/groups';

export interface GroupConfigurationDraft {
  includeRules: GroupRule[];
  excludeRules: GroupRule[];
  boundTagIds: string[];
  context: ContextOverrides;
  answerFallback: AnswerFallbackSettings;
}

export function createGroupConfigurationDraft(configuration: GroupConfiguration): GroupConfigurationDraft {
  return {
    includeRules: configuration.rules.include.map(rule => ({ ...rule })),
    excludeRules: configuration.rules.exclude.map(rule => ({ ...rule })),
    boundTagIds: [...configuration.boundTagIds],
    context: { ...configuration.context.configured },
    answerFallback: { ...configuration.answerFallback }
  };
}

export function groupConfigurationDraftSignature(draft: GroupConfigurationDraft): string {
  return JSON.stringify({
    ...draft,
    boundTagIds: [...draft.boundTagIds].sort(),
    includeRules: draft.includeRules.map(rule => ({ ...rule })),
    excludeRules: draft.excludeRules.map(rule => ({ ...rule }))
  });
}
