using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using Microsoft.Extensions.Options;
using DocsApi.Configurations;
using DocsApi.Data;
using DocsApi.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Runtime;

[Route("api/files/")]
[ApiController]
public class FileController : ControllerBase
{
    private readonly AppDbContext1 _context1;
    private readonly AppDbContext2 _context2;
    private readonly AppDbContext3 _context3;
    private readonly FileSettings _fileSettings;
    public FileController(IOptions<FileSettings> fileSettings, AppDbContext1 context1, AppDbContext2 context2, AppDbContext3 context3)
    {
        _fileSettings = fileSettings.Value;
        _context1 = context1;
        _context2 = context2;
        _context3 = context3;
    }

    private void AddResultsWithSource(IEnumerable<FileObjectId> results, string source, List<FileObjectIdWithSource> output)
    {
        output.AddRange(results.Select(r => new FileObjectIdWithSource
        {
            s_ObjectID = r.s_ObjectID,
            s_Version = r.s_Version,
            Name = r.Name,
            NomenId = r.NomenId,
            Source = source
        }));
    }


    [HttpGet("objectid")]
    [SwaggerOperation(
        Summary = "Файл по srvName(номер сервера), folder( id объекта File в Docs) и filename(номер ревизии)",
        Description = @"
        Принимает два get параметра folder и filename
        Если файл найден возвращает бинарное содержимое
        Если файл не найден 404
        Пример:
        http://test.ru/api/files/objectid?folder=2&filename=4")
        ]
    public async Task<ActionResult<Dictionary<string, string?>>> GetFileByObjectId(string srvName, string folder, string fileName)
    {
        // if наименование папки и наименование файла переданы
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(fileName))
        {
            return BadRequest("Folder and file name must be provided.");
        }

        // Формируем полный путь из C:\test + 4 + 3
        // Где 4 имя папки, а 3 имя файла
        if (_fileSettings.SrvPaths.TryGetValue(srvName, out var _basePath))
        {
            var filePath = Path.Combine(_basePath, folder, fileName);

            // Проверка на существование файла
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("File not found.");
            }

            // Чтение файла и возврат его клиенту
            var fileBytes = System.IO.File.ReadAllBytes(filePath);
            return File(fileBytes, "application/octet-stream", fileName);
        }
        else {
            return NotFound("File not found.");
        }


    }

    [HttpPost("file/objectid/")]
    [SwaggerOperation(
    Summary = "Возвращает массив объктов json с атрибутами s_ObjectID, s_Version, name",
    Description = "Принимает строку в формате json 'КТ.2108001.23.08' Возвращает массив вида   {s_ObjectID: 98791, s_Version: 1,\r\n    \"name\": \"КТ.2108001.23.08 СБ Полукольцо наружное.grb\"\r\n  },\r\n  {\r\n    \"s_ObjectID\": 98840,\r\n    \"s_Version\": 1,\r\n    \"name\": \"КТ.2108001.23.08 СБ Полукольцо наружное.pdf\"\r\n  }")
    ]
    public async Task<ActionResult<IEnumerable<FileObjectId>>> GetObjectIdByNameNomeclature([FromBody] string value)
    {
        var parameters = new List<SqlParameter>();
        parameters.Add(new SqlParameter("@value", value));
        string query = $@"
            select f.s_ObjectID, f.s_Version, f.Name, n.s_ObjectID as NomenId
            from Nomenclature n 
            INNER JOIN Documents d ON d.s_NomenclatureObjectID = n.s_ObjectID 
            INNER JOIN DocumentFiles df ON df.MasterID = d.s_ObjectID 
            INNER JOIN Files f ON f.s_ObjectID = df.SlaveID 
            where n.Denotation = @value AND n.s_ActualVersion = 1 AND d.s_ActualVersion = 1 AND f.s_ActualVersion = 1 AND df.DeletedChangelistID = 0
        ";
        var result1 = await _context1.FileObjectId.FromSqlRaw(query, parameters.ToArray()).ToListAsync();
        var result2 = await _context2.FileObjectId.FromSqlRaw(query, parameters.ToArray()).ToListAsync();
        var result3 = await _context3.FileObjectId.FromSqlRaw(query, parameters.ToArray()).ToListAsync();
        var results = new List<FileObjectIdWithSource>();
        AddResultsWithSource(result1, "srv-docs-pkb", results);
        AddResultsWithSource(result2, "srv-tdocs", results);
        AddResultsWithSource(result3, "srv-docs", results);
        //var allResults = result1.Concat(result2).Concat(result3);
        return Ok(results);
    }
}