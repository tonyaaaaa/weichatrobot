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
$env:WORKTOOL_E2E_BASE_URL = 'https://<confirmed-worktool-host>/'
$env:WORKTOOL_E2E_ROBOT_ID = '<secret>'
$env:WORKTOOL_E2E_CALLBACK_SECRET = '<secret>'
$env:WORKTOOL_E2E_TARGET_GROUP = '技术部'
$env:WORKTOOL_E2E_TARGET_CONFIRMED = '技术部'
$env:WORKTOOL_E2E_AUDIT_IDS_JSON = '{"noAtReply":"<guid>","duplicateCallback":"<guid>","allowedTags":"<guid>","disallowedTags":"<guid>","noVisibleSource":"<guid>","explicitTransfer":"<guid>","employeeNotification":"<guid>","aiPause":"<guid>","humanResolution":"<guid>","approval":"<guid>","laterSemanticRetrieval":"<guid>"}'
```

Then run only the ordinary category:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-trait 'Category=RealWorkToolAcceptance' --filter-not-trait 'Category=RealWorkToolGroupMutation'
```

The test skips unless every variable exists, the target and confirmation both equal `技术部`, and the base URL is HTTPS. It performs a connection probe and emits only the UTC execution time, target name, and supplied sanitized audit GUIDs.

Manually exercise and record one audit GUID for each key in `WORKTOOL_E2E_AUDIT_IDS_JSON`:

- a group question without `@` receives one reply;
- the duplicate callback produces no second effective reply;
- allowed and disallowed tag scopes behave differently;
- the group reply contains no visible source citation while authorized audit evidence does;
- an explicit transfer request notifies the employee and pauses AI;
- the human resolves the case and AI is restored only at the intended step;
- the human answer is approved;
- a later semantically similar question retrieves the approved answer.

Clear the environment variables when finished:

```powershell
Remove-Item Env:RUN_WORKTOOL_E2E, Env:WORKTOOL_E2E_BASE_URL, Env:WORKTOOL_E2E_ROBOT_ID, Env:WORKTOOL_E2E_CALLBACK_SECRET, Env:WORKTOOL_E2E_TARGET_GROUP, Env:WORKTOOL_E2E_TARGET_CONFIRMED, Env:WORKTOOL_E2E_AUDIT_IDS_JSON
```

## Separate type 206/207 mutation gate

Creating or modifying a group is not part of ordinary `技术部` acceptance. It requires a second approval, a disposable new group name, confirmed member identifiers, and these independent variables:

```powershell
$env:RUN_WORKTOOL_GROUP_MUTATION_E2E = '1'
$env:WORKTOOL_GROUP_MUTATION_BASE_URL = 'https://<confirmed-worktool-host>/'
$env:WORKTOOL_GROUP_MUTATION_ROBOT_ID = '<secret>'
$env:WORKTOOL_GROUP_MUTATION_NEW_GROUP = '<approved-disposable-group>'
$env:WORKTOOL_GROUP_MUTATION_RENAMED_GROUP = '<approved-renamed-group>'
$env:WORKTOOL_GROUP_MUTATION_ANNOUNCEMENT = '<approved-non-sensitive-text>'
$env:WORKTOOL_GROUP_MUTATION_MEMBER_IDS = '<confirmed-id-1>,<confirmed-id-2>'
$env:WORKTOOL_GROUP_MUTATION_TARGET_CONFIRMED = '<approved-disposable-group>'
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-trait 'Category=RealWorkToolGroupMutation'
```

This test executes command 206 and then command 207. Never set this gate merely to make a skipped test run. Clear every `WORKTOOL_GROUP_MUTATION_*` value immediately afterward.
