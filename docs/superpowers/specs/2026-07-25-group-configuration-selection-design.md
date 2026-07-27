# Group Configuration Selection Design

## Problem

The group configuration page exposes the internal group profile GUID as a
free-form input. Administrators can therefore enter a human-readable group
name such as `技术群`. The client sends that name to
`PUT /api/groups/{id}/configuration`, while the backend route accepts only a
GUID, so ASP.NET Core returns `404 Not Found` before the endpoint runs.

## Design

The backend remains the sole creator of `GroupProfileEntity.Id`. The existing
group registration flow continues to create a group profile whose `Id` is
initialized with `Guid.NewGuid()`.

Group management will use two routes:

1. `/groups` is the registered-group list.
2. `/groups/:id/configuration` is the configuration detail for one group.

The list loads registered groups from the existing
`GET /api/admin/worktool/groups` endpoint. That response will be extended with
the robot name, group enabled state, and group update time so each row can
display the group name, robot, status, and last update. The primary row action
is `配置`, which navigates with the system-generated group ID. A page-level
`群操作` action links to the existing registration and group-operation page.

The detail page reads the ID only from the route. It displays the group name,
provides a `返回群列表` action, and preserves the existing matching-rule,
knowledge-tag, context-policy, preview, clear-context, and save features. The
internal GUID is never presented as an editable field.

The UI will not add name-based backend routes, generate GUIDs in the browser,
or permit a free-form group identifier.

## Data Flow

1. An administrator registers an existing group through the existing backend
   flow.
2. The backend creates or finds the group profile and returns its generated
   internal ID.
3. The group list renders the registered group by name.
4. Clicking `配置` navigates to `/groups/{id}/configuration`.
5. The detail page loads and saves configuration with that route ID.

## Error Handling

- An empty group list shows an actionable empty state directing the
  administrator to register a group first.
- A list request failure displays an error state without rendering stale rows.
- A missing, malformed, or unknown detail route ID shows `群不存在或已删除`,
  provides a return-to-list action, and does not render an editable form.
- A configuration request failure keeps saving disabled and displays an error
  notice.

## Testing

Frontend route and component tests will prove that:

- `/groups` renders registered groups by name;
- clicking the `技术群` row's `配置` action navigates with its generated GUID;
- `/groups/:id/configuration` uses the route GUID for GET and PUT requests;
- neither page contains a free-form GUID input;
- empty and failed list states are visible;
- an invalid or unknown group ID does not render an editable form;
- configuration loading failures are visible and do not enable saving.

Existing backend GUID route tests remain unchanged because the backend
contract is correct.
