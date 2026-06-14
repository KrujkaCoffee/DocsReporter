using DocsApi.Models;
using DocsApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
//using System.Collections.Generic;
//using System.Threading.Tasks;
using Swashbuckle.AspNetCore.Annotations;

[Route("api/kod-erp")]
[ApiController]
public class ErpItemsController : ControllerBase
{
    private readonly AppDbContext1 _context1;
    private readonly AppDbContext2 _context2;
    private readonly AppDbContext3 _context3;

    public ErpItemsController(AppDbContext1 context1, AppDbContext2 context2, AppDbContext3 context3)
    {
        _context1 = context1;
        _context2 = context2;
        _context3 = context3;
    }
    private async Task<List<CodeErpDbResponse>> SafeQueryAsync(DbContext context, string query, List<SqlParameter> parameters)
    {
        try
        {
            return await context.Set<CodeErpDbResponse>()
                .FromSqlRaw(query, parameters.ToArray())
                .ToListAsync();
        }
        catch (Exception ex)
        {
            return new List<CodeErpDbResponse>();
        }

    }
    [NonAction]
    public async Task<ActionResult<Dictionary<string, string?>>> SendQueryOnAllServers(string query, List<SqlParameter> parameters)
    {



        try
        {
            var result1 = await SafeQueryAsync(_context1, query, parameters);
            var result2 = await SafeQueryAsync(_context2, query, parameters);
            var result3 = await SafeQueryAsync(_context3, query, parameters);

            var allResults = result1.Concat(result2).Concat(result3);
            var uniqueResults = allResults
                .GroupBy(item => item.Name.Trim())
                .ToDictionary(
                    item => item.Key,
                    item => item.First().Kod_ERP
                );
            return Ok(uniqueResults);
        }
        catch (Exception ex) {
            return StatusCode(503, "Ошибка при доступе к базе данных. Пожалуйста, попробуйте позже.");
        }

    }

    [HttpPost("mat/")]
    [SwaggerOperation(
        Summary = "Принимает список строк [string, ...] Возвращает объкт типа {НаименованиеМатериала: Kod_ERP, НаименованиеМатериала2: Kod_ERP2,...}",
        Description = @"
            Принимает массив строк в формате json 
            ['Круг В1-30 ГОСТ 2590-2006 / 45 ГОСТ 1050-2013', 'Лист Б-ПН-3 ГОСТ 19903-2015 / Ст3сп5 ГОСТ 16523-97'] 
            Возвращает {'Круг В1-30 ГОСТ 2590-2006 / 45 ГОСТ 1050-2013':'00-00045310', 'Винт с шестигранной головкой ГОСТ Р ИСО 4017 - M10x40-8.8-A6J': '00-00044520' }")
        ]
    public async Task<ActionResult<Dictionary<string, string?>>> GetErpItemsByMats([FromBody] List<string> values)
    {
        var parameters = new List<SqlParameter>();
        var inClause = string.Join(",", values.Select((v, index) =>
        {
            var paramName = $"@value{index}";
            parameters.Add(new SqlParameter(paramName, v));
            return paramName;
        }));
        var results = new List<CodeErpDbResponse>();
        string query = $@"
            select e.Kod_ERP , mn.Name, e.s_ObjectID
            from ERP_1 e
            left join MaterialNomenclature mn on mn.s_ObjectID = e.s_ObjectID
            WHERE mn.Name IN ({inClause}) AND mn.s_ActualVersion = 1 AND e.s_ActualVersion = 1
        ";
        return await SendQueryOnAllServers(query, parameters);
    }
    [HttpPost("mat/one/")]
    [SwaggerOperation(
        Summary = "Возвращает объкт {Материал: Kod_ERP}",
        Description = "Принимает строку в формате json 'Лист Б-ПН-3 ГОСТ 19903-2015 / Ст3сп5 ГОСТ 16523-97' Возвращает {'Лист Б-ПН-3 ГОСТ 19903-2015 / Ст3сп5 ГОСТ 16523-97':'00-00044520' }")
        ]
    public async Task<ActionResult<Dictionary<string, string?>>> GetErpItemByMat([FromBody] string value)
    {
        var parameters = new List<SqlParameter>();
        parameters.Add(new SqlParameter("@value", value));
        string query = $@"
        select e.Kod_ERP , mn.Name, e.s_ObjectID
from ERP_1 e
left join MaterialNomenclature mn on mn.s_ObjectID = e.s_ObjectID
WHERE mn.Name = @value  AND mn.s_ActualVersion = 1 AND e.s_ActualVersion = 1
        ";
        return await SendQueryOnAllServers(query, parameters);
    }

    [HttpPost("standart/")]
    [SwaggerOperation(
        Summary = "Принимает список строк [string, ...] Возвращает объкт типа {Стандартное изделие: Kod_ERP, Стандартное изделие2: Kod_ERP2,...}",
        Description = @"
            Принимает массив строк в формате json 
            ['Шайба C.12.01.016 ГОСТ 11371-78', 'Винт с шестигранной головкой ГОСТ Р ИСО 4017 - M10x40-8.8-A6J'] 
            Возвращает {'Шайба C.12.01.016 ГОСТ 11371-78':'00-00053234', 'Винт с шестигранной головкой ГОСТ Р ИСО 4017 - M10x40-8.8-A6J': '00-00052370' }")
        ]
    public async Task<ActionResult<Dictionary<string, string?>>> GetErpItemsByName([FromBody] List<string> values)
    {
        var parameters = new List<SqlParameter>();
        var inClause = string.Join(",", values.Select((v, index) =>
        {
            var paramName = $"@value{index}";
            parameters.Add(new SqlParameter(paramName, v));
            return paramName;
        }));
        var results = new List<CodeErpDbResponse>();
        string query = $@"
        SELECT e.Kod_ERP, n.Name, n.s_ObjectID
        FROM Nomenclature n 
        LEFT JOIN ERP e ON n.s_ObjectID = e.s_ObjectID
        WHERE n.Name IN ({inClause}) AND n.s_ActualVersion = 1 AND e.s_ActualVersion = 1
        ";
        return await SendQueryOnAllServers(query, parameters);

    }
    [HttpPost("standart/one/")]
    [SwaggerOperation(
        Summary = "Возвращает объкт {Стандартное изделие: Kod_ERP}", 
        Description = "Принимает строку в формате json 'Шайба C.12.01.016 ГОСТ 11371-78' Возвращает {'Шайба C.12.01.016 ГОСТ 11371-78':'00-00053234' }")
        ]
    public async Task<ActionResult<Dictionary<string, string?>>> GetErpItemByStandartName([FromBody] string value)
    {
        var parameters = new List<SqlParameter>();
        parameters.Add(new SqlParameter("@value", value));
        string query = $@"
        SELECT e.Kod_ERP, n.Name, n.s_ObjectID
        FROM Nomenclature n 
        LEFT JOIN ERP e ON n.s_ObjectID = e.s_ObjectID
        WHERE n.Name = @value AND n.s_ActualVersion = 1 AND e.s_ActualVersion = 1";
        return await SendQueryOnAllServers(query, parameters);
    }

}