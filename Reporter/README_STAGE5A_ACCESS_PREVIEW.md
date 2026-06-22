# Stage 5A — identity and T-FLEX access preview

This stage is intentionally diagnostic. It does not yet use
`xAccessRights` to hide cards or files.

## Endpoints

- `GET /api/reporter/security/me?sources=tdocs,docs,pow`
- `GET /api/reporter/security/sources/{sourceCode}/identity`
- `GET /api/reporter/security/sources/{sourceCode}/references/{referenceId}/preview?objectId={s_ObjectID}`

## UI

Open `/reporter/security.html` or click **Доступ** in the reporter header.

The page shows:

- current Windows/debug identity and reporter roles;
- match against `Users` + `UserParameters` by SID, Login,
  or `N_77BD1F9D7F42CDB`;
- recursive membership from `UsersHierarchy`;
- effective reporter policy from `app.GroupAccessPolicy`;
- raw rows from `xAccessRights`;
- names from `AccessGroups`;
- `AccessGroups` key is detected from the live database because its
  numeric identity/PK is omitted from the supplied dump;
- command states from `AccessGroupCommands`.

## Optional debug identity

Until Windows Auth is enabled, add this under `Reporter`
in configuration:

```json
{
  "SecurityMode": "Preview",
  "DebugWindowsLogin": "DOMAIN\\a.a.fedorov",
  "DebugWindowsSid": "S-1-5-21-...",
  "SecurityMaxHierarchyDepth": 16,
  "SecurityMaxRightsRows": 500
}
```

Do not configure a debug identity in production.

## Why enforcement is deferred

`UsersHierarchy` is safe to interpret as a membership graph.
`xAccessRights` contains several access types and command groups,
but precedence and allow/deny semantics must be verified before
the table is used as an authorization decision.

Stage 5B should enable enforcement only after representative users
and objects are compared with the native DOCs UI.
