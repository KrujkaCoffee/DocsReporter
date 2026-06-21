# Stage 3 — Reporter UI

Добавлен статический read-only интерфейс для просмотра карточек проектов поверх API Stage 1/2.

## Маршруты

```text
/reporter
/reporter/index.html
```

Корень приложения редиректит на:

```text
/reporter/index.html
```

## Что использует UI

```text
GET /api/reporter/ui/sources
GET /api/reporter/sources/{sourceCode}/project-cards/search?query=...&page=1&pageSize=50
GET /api/reporter/sources/{sourceCode}/project-cards/{objectId}/full-card?depth=2&fileDepth=4
```

`/api/reporter/ui/sources` сделан без `[Authorize]`, чтобы интерфейс можно было гонять до возврата WinAuth. Позднее его можно закрыть общей авторизацией.

## Что отображается

```text
1. Источник / сервер.
2. Поиск по карточкам проектов.
3. Список найденных карточек.
4. Read-only карточка проекта.
5. Основные поля карточки.
6. Файловые категории и дерево файлов.
7. Связи объекта.
8. Technical trace для диагностики.
```

## Визуальный принцип

Интерфейс не копирует T-FLEX DOCs, но сохраняет знакомую логику:

```text
левая панель  → справочники / режим
центр         → поиск и список
правая часть  → свойства, связи, файлы
```

Цветовая схема сделана в графитовом/конструкторском стиле с оранжевым акцентом и цветными заголовками параметров.

## Что не включено в Stage 3

```text
- полноценная админка;
- включение WinAuth;
- тонкие права T-FLEX;
- редактирование;
- общий backend endpoint для multi-source поиска.
```
