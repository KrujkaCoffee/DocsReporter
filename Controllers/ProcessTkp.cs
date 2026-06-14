using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using Microsoft.Extensions.Options;
using DocsApi.Configurations;
using DocsApi.Data;
using DocsApi.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Linq;

[Route("api/process_tkp")]
[ApiController]
public class ProcessTkpController : ControllerBase
{
    private readonly AppDbContext2 _context2;
    private readonly AppDbContext3 _context3;
    private readonly ProcessTkpRefs _proccessTkpRefs;
    public ProcessTkpController(AppDbContext2 context2, AppDbContext3 context3, IOptions<ProcessTkpRefs> proccessTkpRefs)
    {
        _context2 = context2;
        _context3 = context3;
        _proccessTkpRefs = proccessTkpRefs.Value;
    }
    private async Task<List<ProccessTkpResponse>> SafeQueryAsync(DbContext context, string query, List<SqlParameter> parameters)
    {
        try
        {
            return await context.Set<ProccessTkpResponse>()
                .FromSqlRaw(query, parameters.ToArray())
                .ToListAsync();
        }
        catch (Exception ex)
        {
            return new List<ProccessTkpResponse>();
        }

    }
    
    private async Task<List<StagesByDirResponse>> SafeQueryAsyncTkpSchemas(DbContext context, string query, SqlParameter parameter)
    {
        try
        {
            return await context.Set<StagesByDirResponse>()
                .FromSqlRaw(query, parameter)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            return new List<StagesByDirResponse>();
        }

    }
    private async Task<List<TkpFoldersDBResponse>> SafeQueryAsyncTkpFolder(DbContext context, string query)
    {
        try
        {
            return await context.Set<TkpFoldersDBResponse>()
                .FromSqlRaw(query)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            return new List<TkpFoldersDBResponse>();
        }
    }

    [NonAction]
    public async Task<ActionResult<IEnumerable<ProccessTkpResponse>>> SendQueryOnAllServers(string query, List<SqlParameter> parameters)
    {
        try
        {
            var result2 = await SafeQueryAsync(_context2, query, parameters);
            foreach (var item in result2)
            {
                item.СсылкаДокс_proc = $"{_proccessTkpRefs.srvTdocs}OpenReferenceWindow/?refId=1307&objId={item.ID_proc}";
                if (item.ID_card is int)
                {
                    item.СсылкаДокс_card = $"{_proccessTkpRefs.srvTdocs}OpenPropertiesInNewWindow/?refId=891&objId={item.ID_card}";
                }
            }

            var result3 = await SafeQueryAsync(_context3, query, parameters);
            foreach (var item in result3)
            {
                item.СсылкаДокс_proc = $"{_proccessTkpRefs.srvDocs}OpenReferenceWindow/?refId=1307&objId={item.ID_proc}";
                if (item.ID_card is int)
                {
                    item.СсылкаДокс_card = $"{_proccessTkpRefs.srvDocs}OpenPropertiesInNewWindow/?refId=891&objId={item.ID_card}";
                }
            }

            var allResults = result2.Concat(result3);
            return Ok(allResults);
        }
        catch (Exception ex)
        {
            return StatusCode(503, "Ошибка при доступе к базе данных. Пожалуйста, попробуйте позже.");
        }

    }
    [HttpGet("armatura/")]
    public async Task<ActionResult<IEnumerable<ProccessTkpResponse>>> GetProccessTkpArmatura()
    {
        string query = $@"
SELECT 
kp.s_ObjectID as ""ID_card"",
kp.s_Guid as ""UUID_card"",
tkp.s_ObjectID as ""ID_proc"",
tkp.s_Guid as ""UUID_proc"",
kp.Shifr_izdeliya as ""ШифрИзделия_card"",
kp.Nomer_proekta as ""НомерПроекта_card"",
kp.Nomer_pozitsii as ""НомерПозиции_card"",
kp.Name as ""Наименование_card"",
kp.s_CreationDate as ""ДатаСоздания_card"",
k.Nazvanie_varianta as ""НазваниеВарианта_card"",
users_otv.FullName ""Ответственный_proc"",
tkp.Kommentariy as ""Комментарий_proc"",
tkp.Name as ""Наименование_proc"",
parent.Name as ""Наименование_папки_proc"",
et.Name as ""Этап_proc"",
users_isp.FullName as ""Исполнитель_proc"",
process.Data_zapuska as ""ДатаЗапуска_proc"", 
process.Status as ""Статус_proc"",
process.Zhelaemaya_data as ""ЖелаемаяДата_proc"",
process.Kod_RS as ""КодРС_proc"",
'' as ""СсылкаДокс_proc"",
'' as ""СсылкаДокс_card"",
'' as ""Папка_proc"",
'' as ""Схема_proc""
FROM TFlexDOCsOne.dbo.Protsessy_TKP_2 tkp
LEFT JOIN TFlexDOCsOne.dbo.Kartochka_proekta_Protses_1 kpp ON tkp.s_ObjectID = kpp.MasterID 
LEFT JOIN TFlexDOCsOne.dbo.Kartochka_proekta kp ON  kp.s_ObjectID = kpp.SlaveID  and kp.s_ActualVersion = 1 and kp.s_Deleted = 0 
LEFT JOIN TFlexDOCsOne.dbo.Kartochka k ON k.s_ObjectID = kp.s_ObjectID and k.s_ActualVersion = 1 and k.s_Deleted  = 0
LEFT JOIN TFlexDOCsOne.dbo.Link_1706_1735 link_type ON link_type.MasterID = tkp.s_ObjectID 
LEFT JOIN TFlexDOCsOne.dbo.Link_1706_1697_1 link_stage ON link_stage.MasterID = tkp.s_ObjectID 
LEFT JOIN TFlexDOCsOne.dbo.Tipy_TKP_2 tt ON tt.s_ObjectID = link_type.SlaveID 
LEFT JOIN TFlexDOCsOne.dbo.Etapy_TKP_2 et ON et.s_ObjectID = link_stage.SlaveID 
LEFT JOIN TFlexDOCsOne.dbo.Tekushchiy_ispolnitel_Pro_1 tip ON tip.MasterID = tkp.s_ObjectID 
LEFT JOIN TFlexDOCsOne.dbo.Users users_isp ON users_isp.s_ObjectID = tip.SlaveID and users_isp.s_ActualVersion = 1 and users_isp.s_Deleted  = 0
LEFT JOIN TFlexDOCsOne.dbo.Otvetstvennyy_ProtsessTKP opt ON opt.MasterID = tkp.s_ObjectID 
LEFT JOIN TFlexDOCsOne.dbo.Users users_otv ON users_otv.s_ObjectID = opt.SlaveID  and users_otv.s_ActualVersion = 1 and users_otv.s_Deleted  = 0
LEFT JOIN TFlexDOCsOne.dbo.Protsess_3 process ON process.s_ObjectID = tkp.s_ObjectID and process.s_ActualVersion = 1 and process.s_Deleted  = 0
left JOIN TFlexDOCsOne.dbo.Link_1706_891 link_papk ON link_papk.MasterID = tkp.s_ParentID 
LEFT JOIN TFlexDOCsOne.dbo.Papka papka ON papka.s_ObjectID = link_papk.SlaveID and papka.s_ActualVersion = 1 and papka.s_Deleted = 0
left join Protsessy_TKP_2 parent ON parent.s_ObjectID = tkp.s_ParentID and parent.s_ActualVersion = 1 and parent.s_Deleted = 0
where parent.Name = 'Арматура литая' and tkp.s_ActualVersion = 1 and tkp.s_Deleted = 0
";
        var db_result = await _context2.ProccessTkpObj.FromSqlRaw(query).ToListAsync();
        foreach (var item in db_result)
        {
            item.СсылкаДокс_proc = $"{_proccessTkpRefs.srvTdocs}OpenReferenceWindow/?refId=1307&objId={item.ID_proc}";
            if (item.ID_card is int)
            {
                item.СсылкаДокс_card = $"{_proccessTkpRefs.srvTdocs}OpenPropertiesInNewWindow/?refId=891&objId={item.ID_card}";
            }
        }
        return Ok(db_result);
    }
    [HttpPost("all/")]
    public async Task<ActionResult<IEnumerable<ProccessTkpResponse>>> GetProccessTkpAll([FromBody] ProcessTkpRequest request)
    {
        string[] activeStatuses = ["Новый", "В очереди", "В работе"];
        string[] notActiveStatuses = ["Завершён", "Отменён"];


        var folders = request.Folders;
        var schemas = request.Schemas;
   
        var activeStatus = request.procActiveStatus;
        string wherePostfix = "";
        var parameters = new List<SqlParameter>();
        if (activeStatus != null) {
            var statuses = activeStatus == true ? activeStatuses : notActiveStatuses;
            string inClauseFolder = string.Join(",", statuses.Select((v, index) =>
            {
                var paramName = $"@status_{index}";
                parameters.Add(new SqlParameter(paramName, v));
                return paramName;
            }));
            wherePostfix += $" AND process.Status IN ({inClauseFolder})";
        };
        if (folders != null && folders.Count > 0) {
            string inClauseFolder = string.Join(",", folders.Select((v, index) =>
            {
                var paramName = $"@folder_{index}";
                parameters.Add(new SqlParameter(paramName, v));
                return paramName;
            }));
            wherePostfix += $" AND parent.s_Guid IN ({inClauseFolder})";
        }
        if (schemas != null && schemas.Count > 0) {
            string inClauseSchemas = string.Join(",", schemas.Select((v, index) =>
            {
                var paramName = $"@shemas_{index}";
                parameters.Add(new SqlParameter(paramName, v));
                return paramName;
            }));
            wherePostfix += $" AND schemas.s_Guid IN ({inClauseSchemas})";
        }
        string query = $@"
SELECT 
parent.Name as ""Папка_proc"",
schemas.Name as ""Схема_proc"",
kp.s_ObjectID as ""ID_card"",
kp.s_Guid as ""UUID_card"",
tkp.s_ObjectID as ""ID_proc"",
tkp.s_Guid as ""UUID_proc"",
kp.Shifr_izdeliya as ""ШифрИзделия_card"",
kp.Nomer_proekta as ""НомерПроекта_card"",
kp.Nomer_pozitsii as ""НомерПозиции_card"",
kp.Name as ""Наименование_card"",
kp.s_CreationDate as ""ДатаСоздания_card"",
k.Nazvanie_varianta as ""НазваниеВарианта_card"",
users_otv.FullName ""Ответственный_proc"",
tkp.Kommentariy as ""Комментарий_proc"",
tkp.Name as ""Наименование_proc"",
parent.Name as ""Наименование_папки_proc"",
et.Name as ""Этап_proc"",
users_isp.FullName as ""Исполнитель_proc"",
process.Data_zapuska as ""ДатаЗапуска_proc"", 
process.Status as ""Статус_proc"",
process.Zhelaemaya_data as ""ЖелаемаяДата_proc"",
process.Kod_RS as ""КодРС_proc"",
'' as ""СсылкаДокс_proc"",
'' as ""СсылкаДокс_card""
FROM Protsessy_TKP_2 tkp
LEFT JOIN Kartochka_proekta_Protses_1 kpp ON tkp.s_ObjectID = kpp.MasterID 
LEFT JOIN Kartochka_proekta kp ON  kp.s_ObjectID = kpp.SlaveID  and kp.s_ActualVersion = 1 and kp.s_Deleted = 0 
LEFT JOIN Kartochka k ON k.s_ObjectID = kp.s_ObjectID and k.s_ActualVersion = 1 and k.s_Deleted  = 0
LEFT JOIN Link_1706_1735 link_type ON link_type.MasterID = tkp.s_ObjectID 
LEFT JOIN Link_1706_1697_1 link_stage ON link_stage.MasterID = tkp.s_ObjectID 
LEFT JOIN Tipy_TKP_2 tt ON tt.s_ObjectID = link_type.SlaveID 
LEFT JOIN Etapy_TKP_2 et ON et.s_ObjectID = link_stage.SlaveID 
LEFT JOIN Tekushchiy_ispolnitel_Pro_1 tip ON tip.MasterID = tkp.s_ObjectID 
LEFT JOIN Users users_isp ON users_isp.s_ObjectID = tip.SlaveID and users_isp.s_ActualVersion = 1 and users_isp.s_Deleted  = 0
LEFT JOIN Otvetstvennyy_ProtsessTKP opt ON opt.MasterID = tkp.s_ObjectID 
LEFT JOIN Users users_otv ON users_otv.s_ObjectID = opt.SlaveID  and users_otv.s_ActualVersion = 1 and users_otv.s_Deleted  = 0
LEFT JOIN Protsess_3 process ON process.s_ObjectID = tkp.s_ObjectID and process.s_ActualVersion = 1 and process.s_Deleted  = 0
left JOIN Link_1706_891 link_papk ON link_papk.MasterID = tkp.s_ParentID 
LEFT JOIN Papka papka ON papka.s_ObjectID = link_papk.SlaveID and papka.s_ActualVersion = 1 and papka.s_Deleted = 0
left join Protsessy_TKP_2 parent ON parent.s_ObjectID = tkp.s_ParentID and parent.s_ActualVersion = 1 and parent.s_Deleted = 0
left join Link_1706_1697_2 link_schema ON parent.s_ObjectID = link_schema.MasterID 
left join Etapy_TKP_2 schemas ON schemas.s_ObjectID = link_schema.SlaveID  and schemas.s_ActualVersion = 1 and schemas.s_Deleted = 0
where tkp.s_ActualVersion = 1 and tkp.s_Deleted = 0 {wherePostfix}
";
        return await SendQueryOnAllServers(query, parameters);
    }
    [HttpGet("stage/all/")]
    public async Task<ActionResult<IEnumerator<StagesByDirResponse>>> GetFolderSchemas(string folderGuid)
    {
        var parameter = new SqlParameter("@s_guid", folderGuid);
        string query = $@"
            select et.s_ObjectID as id, et.s_Guid as uuid, et.Name as name
            from Etapy_TKP_2 as et
            left join Link_1706_1697_2 as link_schema ON link_schema.SlaveID = et.s_ObjectID 
            left join Protsessy_TKP_2 as parent ON parent.s_ObjectID = link_schema.MasterID 
            left join Protsessy_TKP_2 as tkp ON tkp.s_ParentID = parent.s_ObjectID and tkp.s_ActualVersion = 1 and tkp.s_Deleted = 0
            WHERE et.s_ParentID = 0 AND parent.s_Guid = {parameter}
            GROUP BY et.s_ObjectID, et.Name, et.s_Guid
            HAVING COUNT(tkp.s_ObjectID) > 0
        ";
        var result2 = await SafeQueryAsyncTkpSchemas(_context2, query, parameter); // server tdocs
        var result3 = await SafeQueryAsyncTkpSchemas(_context3, query, parameter); // server docs
        return Ok(result2.Concat(result3));
    }
    [HttpGet("tkp-folders/all/")]
    public async Task<ActionResult<IEnumerator<TkpFoldersDBResponse>>> GetTkpFolders()
    {
        var parameters = new List<SqlParameter>();
        string query = $@"
            SELECT parent.s_ObjectID AS id, parent.s_Guid as uuid, parent.Name as name
            FROM Protsessy_TKP_2 parent
            LEFT JOIN Protsessy_TKP_2 tkp ON tkp.s_ParentID = parent.s_ObjectID AND tkp.s_ActualVersion = 1 AND tkp.s_Deleted = 0
            WHERE parent.s_ParentID = 0 AND parent.s_ActualVersion = 1 AND parent.s_Deleted = 0
            GROUP BY parent.s_ObjectID, parent.s_Guid, parent.Name
            HAVING COUNT(tkp.s_ObjectID) > 0
        ";
        var result2 = await SafeQueryAsyncTkpFolder(_context2, query); // server tdocs
        var result3 = await SafeQueryAsyncTkpFolder(_context3, query); // server docs
        return Ok(result2.Concat(result3));
    }
}