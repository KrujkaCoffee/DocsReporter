# Этап 6 — мини-админка репортера

Добавлен read-only friendly admin shell для управления тем, что видно в веб-репортере:

- видимость справочников в меню;
- поиск и открытие карточек;
- глубина раскрытия объектов;
- глубина файлового дерева;
- лимиты строк и связанных объектов;
- включение/отключение связей;
- категории файловых/объектных связей;
- запуск синхронизации метаданных и начальной настройки из интерфейса.

Страница:

```text
/reporter/admin
```

API:

```text
GET  /api/reporter/admin/sources
GET  /api/reporter/admin/roles
GET  /api/reporter/admin/overview?sourceCode=tdocs&roleCode=ProjectCardViewer
GET  /api/reporter/admin/sources/{sourceCode}/groups?roleCode=ProjectCardViewer&query=...&enabledOnly=false
PUT  /api/reporter/admin/sources/{sourceCode}/groups/{groupId}/policy?roleCode=ProjectCardViewer
GET  /api/reporter/admin/sources/{sourceCode}/relations?roleCode=ProjectCardViewer&groupId=891&query=...&enabledOnly=false
PUT  /api/reporter/admin/sources/{sourceCode}/relations/{relationGroupId}/policy?roleCode=ProjectCardViewer
POST /api/reporter/admin/discovery/{sourceCode}/parameter-groups
POST /api/reporter/admin/bootstrap/project-card-viewer/{sourceCode}
```

## Доступ к админке

В режиме `Reporter:SecurityMode = Preview` и `Reporter:AdminAllowAnonymousInPreview = true` страница доступна без WinAuth для ручного тестирования.

В режимах `AppPolicy` и `TflexRead` admin endpoints требуют роль репортера:

- `ReportAdmin`
- `Developer`

Роли берутся из `app.AppUserRole` / `app.AppRole`.

## Что важно проверить

1. Открыть `/reporter/admin`.
2. Выбрать source и роль `ProjectCardViewer`.
3. Проверить счетчики groups/relations.
4. Найти `Kartochka_proekta` и изменить `MaxObjectDepth` / `MaxFileTreeDepth`.
5. Открыть `/reporter` и проверить, что effective depth изменился.
6. Найти `Link_891_16` или `DocumentFiles`, отключить связь и проверить файловые категории.
7. Вернуть связь обратно.

WinAuth намеренно не включался глобально на этом этапе.
