const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export interface ParsedKnowledgeTagIds {
  tagIds: string[];
  error: string;
}

export function parseKnowledgeTagIds(value: string, requiredMessage: string): ParsedKnowledgeTagIds {
  const values = value.split(/[\s,，]+/).filter(Boolean);
  if (values.length === 0) return { tagIds: [], error: requiredMessage };
  if (values.some(value => !guidPattern.test(value))) {
    return { tagIds: [], error: '标签 ID 必须是有效的 UUID，多个 ID 请用逗号分隔。' };
  }
  return { tagIds: [...new Set(values.map(value => value.toLowerCase()))], error: '' };
}
