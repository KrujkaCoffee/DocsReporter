/* Выполнять вручную ТОЛЬКО в отдельной БД репортера.
   Не меняет данные или назначения прав T-FLEX. Повторный запуск не очищает журнал. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
 OR OBJECT_ID(N'app.Source',N'U') IS NULL
 OR OBJECT_ID(N'dbo.ParameterGroups',N'U') IS NOT NULL
    THROW 51080,N'Выбрана не отдельная подготовленная БД репортера.',1;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'app.ReporterSecuritySchema',N'U') IS NULL
        CREATE TABLE app.ReporterSecuritySchema(Id int NOT NULL PRIMARY KEY CHECK(Id=1),Version int NOT NULL);
    IF EXISTS(SELECT 1 FROM app.ReporterSecuritySchema WHERE Id=1 AND Version<>1)
        THROW 51081,N'Неизвестная версия схемы журнала.',1;
    IF OBJECT_ID(N'app.ReporterAuditEvent',N'U') IS NULL
    BEGIN
        CREATE TABLE app.ReporterAuditEvent
        (
            AuditId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReporterAuditEvent PRIMARY KEY,
            StartedUtc datetime2(3) NOT NULL CONSTRAINT DF_ReporterAuditEvent_Started DEFAULT SYSUTCDATETIME(),
            ReadyUtc datetime2(3) NULL,
            FinishedUtc datetime2(3) NULL,
            RequestId nvarchar(100) NOT NULL,
            ActorLogin nvarchar(255) NULL,
            ActorSid nvarchar(184) NULL,
            Operation nvarchar(80) NOT NULL,
            ResourceJson nvarchar(max) NOT NULL,
            ResourceLabel nvarchar(512) NULL,
            Phase nvarchar(32) NOT NULL,
            HttpStatus int NULL,
            VisibleItemCount int NULL,
            DecisionsJson nvarchar(max) NULL,
            FailureCode nvarchar(100) NULL
        );
        CREATE INDEX IX_ReporterAuditEvent_ActorTime ON app.ReporterAuditEvent(ActorSid,StartedUtc) INCLUDE(Operation,HttpStatus);
        CREATE INDEX IX_ReporterAuditEvent_Request ON app.ReporterAuditEvent(RequestId);
    END;
    IF COL_LENGTH(N'app.ReporterAuditEvent',N'DecisionsJson') IS NULL
       OR COL_LENGTH(N'app.ReporterAuditEvent',N'RequestId') IS NULL
       OR COL_LENGTH(N'app.ReporterAuditEvent',N'ReadyUtc') IS NULL
       OR COL_LENGTH(N'app.ReporterAuditEvent',N'ActorSid') IS NULL
       OR COL_LENGTH(N'app.ReporterAuditEvent',N'Phase') IS NULL
        THROW 51082,N'Существующая таблица журнала несовместима. Автоматического изменения нет.',1;
    IF NOT EXISTS(SELECT 1 FROM app.ReporterSecuritySchema WHERE Id=1)
        INSERT INTO app.ReporterSecuritySchema(Id,Version) VALUES(1,1);
    COMMIT;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK;
    THROW;
END CATCH;
SELECT N'Схема строгого журнала готова. Права DOCs не менялись.' AS [Результат];
