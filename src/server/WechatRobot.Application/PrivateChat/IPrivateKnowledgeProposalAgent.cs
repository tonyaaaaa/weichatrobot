namespace WechatRobot.Application.PrivateChat;

public interface IPrivateKnowledgeProposalAgent
{
    Task<IReadOnlyList<ProposedKnowledgeItem>> ProposeAsync(
        string sourceText,
        CancellationToken cancellationToken);
}
