export type PageCompletenessStatus = 'complete' | 'blocked-no-contract' | 'unchecked';

export const pageCompleteness: Record<string, PageCompletenessStatus> = {
  dashboard: 'complete',
  'knowledge-documents': 'complete',
  'knowledge-document-management': 'complete',
  'knowledge-document-detail': 'complete',
  'knowledge-tags': 'complete',
  'knowledge-review': 'complete',
  'private-knowledge-ingests': 'complete',
  'memory-center': 'complete',
  'fixed-replies': 'complete',
  'group-list': 'complete',
  'group-operations': 'complete',
  'group-configuration': 'complete',
  'group-context': 'complete',
  audit: 'complete',
  'administration-audit': 'complete',
  'send-queue': 'complete',
  'agent-diagnostics': 'complete',
  'model-settings': 'complete',
  'robot-settings': 'complete',
  users: 'complete',
  'system-settings': 'blocked-no-contract'
};
