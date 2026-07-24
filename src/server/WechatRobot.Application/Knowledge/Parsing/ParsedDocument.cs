namespace WechatRobot.Application.Knowledge.Parsing;

public sealed record ParsedDocument(IReadOnlyList<ParsedBlock> Blocks);

public sealed record ParsedBlock(
    string Text,
    int? PageNumber,
    IReadOnlyList<string> Headings,
    bool IsTable,
    int? TableRows,
    int? TableColumns);
