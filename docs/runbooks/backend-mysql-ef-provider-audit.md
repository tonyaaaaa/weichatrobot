# 后端 MySQL EF Provider 风险审计

## 规则

- `ReplaceTracked`：nullable setter、可空捕获值或已知 Provider 高风险表达式，必须改为跟踪实体更新。
- `KeepAtomic`：保留单条条件更新所需的非空 CAS；必须加入精确测试白名单并由行为测试覆盖。
- `RemoveGuidContains`：运行时 Guid 集合进入 EF SQL，必须改为 `GuidBatchQuery`。
- 每次修改后重新运行 `BackendProviderCompatibilityContractTests`；不能通过扩大白名单隐藏失败。

## Bulk mutation 清单

| Path | Method | Ordinal | Operation | Classification | Reason |
| --- | --- | ---: | --- | --- | --- |
| `src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs` | `UpsertRobotAsync` | 1 | Update | ReplaceTracked | nullable setter or nullable capture |
| `src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs` | `UpsertRobotAsync` | 2 | Update | ReplaceTracked | nullable setter or nullable capture |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `EvaluateInboundPolicyAsync` | 1 | Update | ReplaceTracked | review with adjacent terminal transition |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `PersistNoReplyTerminalAsync` | 1 | Update | ReplaceTracked | nullable session fields |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `LeaseForProcessingAsync` | 1 | Update | KeepAtomic | message claim CAS |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `LeaseForProcessingAsync` | 2 | Update | KeepAtomic | message claim CAS |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `LeaseForProcessingAsync` | 3 | Update | KeepAtomic | session lease CAS |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `RenewLeaseAsync` | 1 | Update | ReplaceTracked | nullable/lease review |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `ReleaseLeaseAsync` | 1 | Update | ReplaceTracked | clears lease fields |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `PersistAnswerAndEnqueueAsync` | 1 | Update | ReplaceTracked | terminal transition review |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `PersistAnswerAndEnqueueAsync` | 2 | Update | ReplaceTracked | clears lease fields |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `PersistAnswerAndEnqueueAsync` | 3 | Update | ReplaceTracked | terminal transition review |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `PersistAnswerAndEnqueueAsync` | 4 | Update | ReplaceTracked | clears lease fields |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `PersistAnswerAndEnqueueAsync` | 5 | Update | ReplaceTracked | terminal transition review |
| `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | `ClearGroupSessionsAsync` | 1 | Update | ReplaceTracked | nullable clear timestamp review |
| `src/server/WechatRobot.Infrastructure/Groups/EfGroupLifecycleStore.cs` | `TryUpdateAsync` | 1 | Update | ReplaceTracked | nullable deleted timestamp |
| `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeCandidatePublishProcessor.cs` | `ProcessAsync` | 1 | Update | KeepAtomic | candidate publish CAS |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `LeaseNextAsync` | 1 | Update | KeepAtomic | index lease CAS |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `RenewLeaseAsync` | 1 | Update | KeepAtomic | lease renewal CAS |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `ActivateVersionAsync` | 1 | Update | KeepAtomic | activation claim CAS |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `ActivateVersionAsync` | 2 | Update | ReplaceTracked | document activation transition |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `ActivateVersionAsync` | 3 | Update | ReplaceTracked | version activation transition |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `ActivateVersionAsync` | 4 | Update | ReplaceTracked | candidate publish transition |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `ActivateVersionAsync` | 5 | Update | ReplaceTracked | old version nullable metadata |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `ActivateVersionAsync` | 6 | Update | ReplaceTracked | clears job lease fields |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `DisableCoreAsync` | 1 | Update | ReplaceTracked | clears active version |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `DisableCoreAsync` | 2 | Update | ReplaceTracked | version disable transition |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `DisableCoreAsync` | 3 | Update | ReplaceTracked | clears job lease owner |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `CompleteCleanupAsync` | 1 | Update | ReplaceTracked | clears cleanup lease fields |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `LeaseNextJobAsync` | 1 | Update | KeepAtomic | durable job claim CAS |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `CompleteJobAsync` | 1 | Update | ReplaceTracked | clears lease/error fields |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `DeferJobAsync` | 1 | Update | ReplaceTracked | clears lease fields |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `RenewJobLeaseAsync` | 1 | Update | KeepAtomic | durable job lease renewal |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `FailJobAsync` | 1 | Update | ReplaceTracked | retry transition clears lease |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `FailJobAsync` | 2 | Update | ReplaceTracked | dead-letter transition clears lease |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `LeaseNextSendCommandAsync` | 1 | Update | ReplaceTracked | stale guard cleanup |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `LeaseNextSendCommandAsync` | 2 | Update | ReplaceTracked | stale send recovery |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `LeaseNextSendCommandAsync` | 3 | Update | ReplaceTracked | guard release review |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `LeaseNextSendCommandAsync` | 4 | Update | KeepAtomic | robot guard claim CAS |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `LeaseNextSendCommandAsync` | 5 | Update | KeepAtomic | send command claim CAS |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `EnsureSendEnabledAsync` | 1 | Update | ReplaceTracked | blocked transition clears lease |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `MarkSendDispatchingAsync` | 1 | Update | KeepAtomic | external dispatch claim CAS |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `MarkSendDeliveryUnknownAsync` | 1 | Update | ReplaceTracked | clears lease fields |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `MarkSendRejectedAsync` | 1 | Update | ReplaceTracked | clears lease fields |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `MarkSendAcceptedAsync` | 1 | Update | ReplaceTracked | clears completion/reconciliation/lease fields |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `MarkSendAcceptedAsync` | 2 | Update | ReplaceTracked | Guid-batched memory recall update |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `FailSendCommandAsync` | 1 | Update | ReplaceTracked | retry clears lease fields |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `FailSendCommandAsync` | 2 | Update | ReplaceTracked | terminal failure clears lease fields |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `RenewSendLeasesAsync` | 1 | Update | KeepAtomic | send command lease renewal CAS |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `RenewSendLeasesAsync` | 2 | Update | KeepAtomic | robot guard lease renewal CAS |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `ReleaseRobotGuardAsync` | 1 | Update | ReplaceTracked | clears guard fields |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `UpdateRelatedMessageStateAsync` | 1 | Update | KeepAtomic | non-null message state update |
| `src/server/WechatRobot.Infrastructure/Persistence/EfHandoffStore.cs` | `TransitionAsync` | 1 | Update | ReplaceTracked | tracked handoff concurrency token transition |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `MarkUploadedAsync` | 1 | Update | ReplaceTracked | clears job lease fields |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `MarkUploadedAsync` | 2 | Update | ReplaceTracked | document transition review |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `MarkUploadedAsync` | 3 | Update | ReplaceTracked | clears failure reason |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `MarkUploadedAsync` | 4 | Update | ReplaceTracked | parse job unblock transition |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `MarkFailedAsync` | 1 | Update | ReplaceTracked | clears job lease fields |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `MarkFailedAsync` | 2 | Update | ReplaceTracked | document failure transition |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `MarkFailedAsync` | 3 | Update | ReplaceTracked | version failure transition |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `RequestPhysicalDeleteCoreAsync` | 1 | Update | ReplaceTracked | clears active version |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `RequestPhysicalDeleteCoreAsync` | 2 | Update | ReplaceTracked | version disable transition |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `RequestPhysicalDeleteCoreAsync` | 3 | Update | ReplaceTracked | clears durable job leases |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `RequestPhysicalDeleteCoreAsync` | 4 | Update | ReplaceTracked | clears index job owner |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `TryRequeuePhysicalCleanupAsync` | 1 | Update | ReplaceTracked | clears completion and lease fields |
| `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | `TryRequeuePhysicalCleanupAsync` | 1 | Delete | ReplaceTracked | replace with tracked dead-letter removal in same transaction |
| `src/server/WechatRobot.Worker/Jobs/WorkToolGroupOperationWorker.cs` | `ProcessOnceAsync` | 1 | Update | ReplaceTracked | timeout completion review |
| `src/server/WechatRobot.Worker/Jobs/WorkToolGroupOperationWorker.cs` | `ProcessOnceAsync` | 2 | Update | ReplaceTracked | clears expired dispatch lease |
| `src/server/WechatRobot.Worker/Jobs/WorkToolGroupOperationWorker.cs` | `ProcessOnceAsync` | 3 | Update | KeepAtomic | queued command claim CAS |
| `src/server/WechatRobot.Worker/Jobs/WorkToolGroupOperationWorker.cs` | `MarkAcceptedAsync` | 1 | Update | ReplaceTracked | clears result/completion/lease fields |
| `src/server/WechatRobot.Worker/Jobs/WorkToolGroupOperationWorker.cs` | `CompleteAsync` | 1 | Update | ReplaceTracked | nullable result and completion fields |
| `src/server/WechatRobot.Worker/Jobs/WorkToolGroupReconciliationWorker.cs` | `ProcessOnceAsync` | 1 | Update | KeepAtomic | reconciliation claim CAS |
| `src/server/WechatRobot.Worker/Jobs/WorkToolGroupReconciliationWorker.cs` | `ProcessOnceAsync` | 2 | Update | ReplaceTracked | nullable next attempt |
| `src/server/WechatRobot.Worker/Jobs/WorkToolGroupReconciliationWorker.cs` | `CompleteAsync` | 1 | Update | ReplaceTracked | nullable next attempt/group ID |

总计：74 项，`Unreviewed` 为 0。分类在对应模块的 red-green 修复中可以收紧，但不能未经测试改为 `KeepAtomic`。

## Runtime Guid 查询清单

| Path | Identifier | Classification | Replacement |
| --- | --- | --- | --- |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `expectedVersionIds` | RemoveGuidContains | `GuidBatchQuery` |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `documentIds` | RemoveGuidContains | `GuidBatchQuery` |
| `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | `newVersionIds` | RemoveGuidContains | `GuidBatchQuery` |
| `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | `memoryIds` | RemoveGuidContains | `GuidBatchQuery` |

以下模式已核对为安全且不替换：`taggedVersionIds.Contains` 是 SQL 子查询，不是运行时 Guid 集合；对 `historyRows`、`durableRows`、`groupBindings`、`chunkBindings`、字典和 HashSet 的 `Contains` 均在数据加载后于内存执行。
