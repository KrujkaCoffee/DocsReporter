/* Выполнить ТОЛЬКО в собственной БД репортера, не в T-FLEX.
   Создаёт только отсутствующие политики. Не изменяет отключения и настройки администратора.
   Синхронизация ParameterGroups для выбранного источника должна быть выполнена ранее.
   Начальный профиль этого этапа проверен только для tdocs / SRV-TDOCS / TFlexDOCsOne. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @SourceCode nvarchar(100) = N'tdocs';
DECLARE @RoleCode nvarchar(100) = N'ProjectCardViewer';
IF OBJECT_ID(N'app.TflexGroup', N'U') IS NULL OR OBJECT_ID(N'app.TflexRelation', N'U') IS NULL
    THROW 51000, N'Это не подготовленная БД репортера: отсутствуют app.TflexGroup/app.TflexRelation.', 1;
DECLARE @SourceId int = (SELECT SourceId FROM app.Source WHERE Code = @SourceCode AND IsEnabled = 1);
DECLARE @RoleId int = (SELECT AppRoleId FROM app.AppRole WHERE Code = @RoleCode);
IF @SourceId IS NULL OR @RoleId IS NULL THROW 51001, N'Источник или роль не найдены.', 1;
IF (SELECT COUNT(*) FROM app.TflexGroup WHERE SourceId = @SourceId AND [Type] = 1
    AND TableName IN (N'Kartochka_proekta', N'Nomenclature', N'Files')) <> 3
    THROW 51002, N'Сначала синхронизируйте метаданные выбранного источника. Ожидаются три однозначных справочника.', 1;
IF (SELECT COUNT(*) FROM app.TflexRelation WHERE SourceId = @SourceId
    AND RelationTable IN (N'Link_891_403_1', N'Link_403_16_1', N'Link_403_16_1_1', N'Link_403_16_2')) <> 4
    THROW 51003, N'В каталоге не найдены четыре однозначные связи. Начальные настройки не созданы.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    INSERT INTO app.GroupAccessPolicy
        (SourceId, AppRoleId, GroupId, TableName, CanSeeInMenu, CanSearch, CanOpenCard, CanExport,
         MaxObjectDepth, MaxFileTreeDepth, MaxRowsPerPage, MaxRelatedObjects, IsEnabled)
    SELECT @SourceId, @RoleId, g.GroupId, g.TableName, 1, 1, 1, 0, 2, 4, 100, 500, 1
    FROM app.TflexGroup g
    WHERE g.SourceId = @SourceId AND g.[Type] = 1
      AND g.TableName IN (N'Kartochka_proekta', N'Nomenclature', N'Files')
      AND NOT EXISTS (SELECT 1 FROM app.GroupAccessPolicy p WITH (UPDLOCK, HOLDLOCK)
        WHERE p.SourceId = @SourceId AND p.AppRoleId = @RoleId AND p.GroupId = g.GroupId);
    DECLARE @AddedGroups int = @@ROWCOUNT;
    INSERT INTO app.RelationAccessPolicy
        (SourceId, AppRoleId, RelationGroupId, RelationTable, DisplayName, CategoryCode, CategoryTitle,
         CanTraverse, ShowInCard, ShowInTree, MaxDepth, MaxItems, DirectionMode, IsEnabled)
    SELECT @SourceId, @RoleId, r.RelationGroupId, r.RelationTable, r.RelationCaption,
        CASE r.RelationTable WHEN N'Link_891_403_1' THEN N'product'
          WHEN N'Link_403_16_1' THEN N'rkd_cad' WHEN N'Link_403_16_1_1' THEN N'rkd_pdf' ELSE N'rkd_dxf' END,
        CASE r.RelationTable WHEN N'Link_891_403_1' THEN N'Изделие'
          WHEN N'Link_403_16_1' THEN N'РКД CAD' WHEN N'Link_403_16_1_1' THEN N'РКД PDF' ELSE N'РКД DXF' END,
        1, 1, 1, 1, 500, N'Both', 1
    FROM app.TflexRelation r
    WHERE r.SourceId = @SourceId
      AND r.RelationTable IN (N'Link_891_403_1', N'Link_403_16_1', N'Link_403_16_1_1', N'Link_403_16_2')
      AND NOT EXISTS (SELECT 1 FROM app.RelationAccessPolicy p WITH (UPDLOCK, HOLDLOCK)
        WHERE p.SourceId = @SourceId AND p.AppRoleId = @RoleId AND p.RelationTable = r.RelationTable);
    DECLARE @AddedRelations int = @@ROWCOUNT;
    COMMIT;
    SELECT @SourceCode AS [Источник], @RoleCode AS [Роль], @AddedGroups AS [Добавлено политик справочников],
        @AddedRelations AS [Добавлено политик связей], N'Существующие политики не изменялись' AS [Примечание];
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    THROW;
END CATCH;
