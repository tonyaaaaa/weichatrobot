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

The group configuration page will:

1. Load registered groups from the existing
   `GET /api/admin/worktool/groups` endpoint.
2. Present those groups by name in a selector.
3. Keep the selected group's system-generated `id` as internal UI state.
4. Use that `id` for configuration GET and PUT requests.
5. Disable configuration actions when no registered group is selected.

The page will not add name-based backend routes, generate GUIDs in the browser,
or permit a free-form group identifier.

## Data Flow

1. An administrator registers an existing group through the existing backend
   flow.
2. The backend creates or finds the group profile and returns its generated
   internal ID.
3. The configuration page lists registered groups.
4. Selecting a group loads its configuration by internal ID.
5. Saving sends the configuration to the same GUID-constrained backend route.

## Error Handling

- An empty registered-group list shows an actionable empty state directing the
  administrator to register a group first.
- A list or configuration request failure leaves saving disabled and displays
  an error notice.
- Changing the selected group reloads configuration before editing or saving.

## Testing

Frontend component tests will prove that:

- registered groups render by name while their GUID is used for API calls;
- selecting `技术群` never sends `技术群` as the route identifier;
- no free-form GUID input remains;
- saving stays disabled when no group is available or selected;
- loading failures are visible and do not enable saving.

Existing backend GUID route tests remain unchanged because the backend
contract is correct.
