# WorkTool sender identity limitation

The official WorkTool QA callback currently documents `receivedName` as the sender's display name and does not provide a stable sender identifier. See the official [message callback specification](https://doc.worktool.ymdyes.cn/doc-861677), request parameters `receivedName` and `messageId`.

`receivedName` must never be used as an external-user ID or a per-sender session key: names are neither unique nor stable. The internal callback contract therefore keeps `receivedName` as `SenderDisplayName` and exposes a separate optional connector extension, `connectorStableSenderId`.

- Official public WorkTool callbacks leave `connectorStableSenderId` null.
- Group-shared context continues to use the group session.
- Per-sender context reuses history only when a connector supplies a validated stable ID.
- Without a stable ID, each inbound message receives a unique stateless session. The current question is still answered, but no prior sender history is loaded. The retrieval audit records `stable_sender_id_unavailable`.

If a future WorkTool contract adds an official immutable sender ID, map it to `connectorStableSenderId` only after validating its stability and length; do not change the display-name semantics.
