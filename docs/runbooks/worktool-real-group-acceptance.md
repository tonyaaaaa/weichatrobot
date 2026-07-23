# WorkTool real-group acceptance

This runbook is an explicitly opt-in acceptance procedure. The default test suite uses only loopback API-seeded data and fake provider boundaries. It must not contact WorkTool, send a group message, or execute command 206/207.

## Safety prerequisites

Before setting any opt-in variable:

1. A human operator confirms that `技术部` is the intended existing acceptance group and records that approval outside Git.
2. Confirm the robot is manually invited to the existing group. WorkTool cannot make a robot join an arbitrary existing group.
3. Confirm Enterprise WeChat permissions for reading group messages, replying, and notifying the named employee.
4. Confirm every external participant in the group expects the test and the test window.
5. Observe WorkTool, Android/BlueStacks, and Enterprise WeChat account-risk controls. Stop immediately on a warning, verification challenge, unusual throttling, or unexpected recipient.
6. Use non-sensitive test questions and do not copy credentials, personal data, callback secrets, robot IDs, member IDs, or message bodies into evidence committed to Git.

## Ordinary existing-group gate

Set these values only in the process that will run the test:

```powershell
$env:RUN_WORKTOOL_E2E = '1'
$env:WORKTOOL_E2E_CONNECTION_STRING = '<local-secret-mysql-connection>'
$env:WORKTOOL_E2E_ROBOT_ID = '<secret>'
$env:WORKTOOL_E2E_CALLBACK_SECRET = '<secret>'
$env:WORKTOOL_E2E_TARGET_GROUP = '技术部'
$env:WORKTOOL_E2E_TARGET_CONFIRMED = '技术部'
$env:WORKTOOL_E2E_EVIDENCE_JSON = '{"fromUtc":"<UTC ISO-8601>","toUtc":"<UTC ISO-8601>","noAtMessageId":"<external message id>","duplicateMessageId":"<external message id posted twice>","allowedTagMessageId":"<external message id>","disallowedTagMessageId":"<external message id>","transferMessageId":"<external message id>","laterSemanticMessageId":"<external message id>","allowedTagId":"<expected enabled tag UUID>","disallowedTagId":"<forbidden tag UUID>","forbiddenDocumentId":"<active document UUID>","forbiddenVersionId":"<active version UUID>","forbiddenChunkId":"<approved chunk UUID>","disallowedProbeQuestion":"<exact non-sensitive probe question>","disallowedExpectedDecision":"InsufficientEvidence"}'
```

Then run only the ordinary category:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-trait 'Category=RealWorkToolAcceptance' --filter-not-trait 'Category=RealWorkToolGroupMutation'
```

The test skips unless every variable exists and the target and confirmation both equal `技术部`. It does not trust supplied audit GUIDs. It hashes the supplied callback secret and compares it to the selected robot, binds the expected WorkTool robot and `技术部` group to the same database identities, and queries durable messages, jobs, retrieval audits, send commands, handoffs, transitions, reviews, and later retrieval evidence inside the supplied UTC window. Assertion failures use stable categories only; provider messages, URLs, headers, credentials, and identifiers are not printed. Successful output contains only condition names, UTC timestamps, and audit/entity GUIDs retrieved by the verifier.

Manually exercise the following actions during the UTC window and record their WorkTool external message IDs in `WORKTOOL_E2E_EVIDENCE_JSON`:

- a group question without `@` receives one completed reply; its durable callback payload must record `WasMentioned=false`;
- post the duplicate callback/message twice; the verifier requires exactly one inbound message;
- allowed and disallowed tag scopes behave differently. Before the run, create one unique, approved, active probe chunk whose `Question` exactly equals `disallowedProbeQuestion`, bind it only to `disallowedTagId`, and confirm that `技术部` is not bound to that tag. The production audit honestly records `scoped_zero_hits`, the applied allowed-tag filter, the allowed tag IDs, and zero visible results; it does not claim that every zero-result query was caused by tag scope. The real verifier establishes that causal conclusion only by combining those audit facts with the explicitly configured document/version/chunk/tag/question metadata, exact callback text, completed processing and send, and the expected non-answer decision;
- the group reply contains no visible source citation while authorized audit evidence does;
- an explicit transfer request notifies the employee and pauses AI;
- the human resolves the case and AI is restored only at the intended step;
- the human answer is approved;
- a later semantically similar question retrieves the approved answer.

The verifier fails if any record is missing, outside the UTC window, associated with a different robot/group, or not correlated to the expected tag, handoff, notification, approval, and published knowledge version.

Clear the environment variables when finished:

```powershell
Remove-Item Env:RUN_WORKTOOL_E2E, Env:WORKTOOL_E2E_CONNECTION_STRING, Env:WORKTOOL_E2E_ROBOT_ID, Env:WORKTOOL_E2E_CALLBACK_SECRET, Env:WORKTOOL_E2E_TARGET_GROUP, Env:WORKTOOL_E2E_TARGET_CONFIRMED, Env:WORKTOOL_E2E_EVIDENCE_JSON
```

## Separate type 206/207 mutation gate

Creating or modifying a group is not part of ordinary `技术部` acceptance. It requires a second approval, a disposable new group name, confirmed member identifiers, and these independent variables:

```powershell
$env:RUN_WORKTOOL_GROUP_MUTATION_E2E = '1'
$env:WORKTOOL_GROUP_MUTATION_API_BASE_URL = 'https://<confirmed-api-host>/'
$env:WORKTOOL_GROUP_MUTATION_BEARER_TOKEN = '<short-lived-admin-token>'
$env:WORKTOOL_GROUP_MUTATION_CONNECTION_STRING = '<local-secret-mysql-connection>'
$env:WORKTOOL_GROUP_MUTATION_ROBOT_CONFIG_ID = '<confirmed internal robot UUID>'
$env:WORKTOOL_GROUP_MUTATION_NEW_GROUP = '<approved-disposable-group>'
$env:WORKTOOL_GROUP_MUTATION_RENAMED_GROUP = '<approved-renamed-group>'
$env:WORKTOOL_GROUP_MUTATION_ANNOUNCEMENT = '<approved-non-sensitive-text>'
$env:WORKTOOL_GROUP_MUTATION_MEMBER_IDS = '<confirmed-id-1>,<confirmed-id-2>'
$env:WORKTOOL_GROUP_MUTATION_TARGET_CONFIRMED = '<approved-disposable-group>'
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-trait 'Category=RealWorkToolGroupMutation'
```

This test calls the audited backend preview/execute flow for command 206 and then command 207. Each execute response returns its newly created audit ID; the test queries only those two IDs and requires the exact command number, successful status, robot configuration, and sanitized group identifier for each operation. Concurrent unrelated 206/207 rows cannot satisfy the check. It never prints the bearer token, provider response, URL, robot ID, member IDs, or command payload. Never set this gate merely to make a skipped test run. Clear every `WORKTOOL_GROUP_MUTATION_*` value immediately afterward.
