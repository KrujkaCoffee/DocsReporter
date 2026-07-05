# Stage 5B — source-scoped read access

Stage 5B adds a conservative access decision layer without changing the default permissive test mode.

## Why source-scoped

A Windows user can have different `Users.s_ObjectID` values on different T-FLEX DOCs databases. Runtime access checks therefore use the tuple:

```text
sourceCode + DocsUserObjectId + DocsUserGuid
sourceCode + ParameterGroups.PK + s_ObjectID
```

`DocsUserObjectId` is the local T-FLEX user/group ID for the selected database. `DocsUserGuid` is stored as an additional stable diagnostic identifier in `app.SourceUserMap`.

## SecurityMode

`Reporter:SecurityMode` now supports:

```text
Preview
  Default. Identity, hierarchy, xAccessRights and Stage 5B decision are calculated for diagnostics only.

AppPolicy
  Enforces app.GroupAccessPolicy and app.RelationAccessPolicy, but does not filter by xAccessRights.

TflexRead
  Enforces app policies and the Stage 5B xAccessRights read decision.

Disabled
  Disables /api/reporter/security endpoints. Reporter API stays permissive.
```

Useful options:

```json
{
  "Reporter": {
    "SecurityMode": "Preview",
    "DebugWindowsLogin": "DOMAIN\\user",
    "DebugWindowsSid": "S-1-5-21-...",
    "TflexReadCommandId": 1,
    "TflexDenyWhenNoReadRule": true,
    "TflexFilesReferenceId": 16,
    "TflexReadFilterFiles": true
  }
}
```

## What is enforced

When `SecurityMode=TflexRead`:

- project-card search is filtered by `ParameterGroups.PK = 891` and each card `s_ObjectID`;
- project-card full-card requires a positive decision for the selected card;
- relation targets are hidden when their own `ParameterGroups.PK + s_ObjectID` decision denies read;
- file tree nodes are filtered by `TflexFilesReferenceId` when `TflexReadFilterFiles=true`.

## Decision heuristic

The decision service reads `xAccessRights`, joins `AccessGroups`, and reads `AccessGroupCommands`.

- `TflexReadCommandId` defaults to `1` and is treated as the read/open command.
- `AccessGroupCommands.Enable = 1` for that command means `allow_read`.
- `AccessGroupCommands.Enable = 0` means `deny_read`.
- If commands are missing, names like `Просмотр`, `Редакторский`, `Административный`, `Полный`, `Без ограничений` are treated as read-capable.
- Names like `Запрещено всё` / `Без доступа` are treated as hard read-deny.

This is intentionally conservative and should be compared with real DOCs behavior before enabling `TflexRead` for all users.

## Diagnostics

Open:

```text
/reporter/security
```

The page now shows:

- Web/Windows identity;
- T-FLEX identity per source;
- UsersHierarchy chain;
- reporter app policy;
- raw xAccessRights rows;
- Stage 5B read decision and rules.

Use the page first in `Preview`, then in `AppPolicy`, and only then try `TflexRead`.
