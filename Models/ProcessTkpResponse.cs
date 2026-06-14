using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

public class ProccessTkpResponse
{
    [Key]
    public int? ID_card { get; set; } // kp.s_ObjectID

    public int ID_proc { get; set; } // tkp.s_ObjectID

    public string? ШифрИзделия_card { get; set; } // kp.Shifr_izdeliya

    public string? НомерПроекта_card { get; set; } // kp.Nomer_proekta

    public string? НомерПозиции_card { get; set; } // kp.Nomer_pozitsii

    public string? Наименование_card { get; set; } // kp.Name
    public DateTime? ДатаСоздания_card { get; set; } // kp.Name

    public string? НазваниеВарианта_card { get; set; } // k.Nazvanie_varianta

    public string? Ответственный_proc { get; set; } // users_otv.FullName

    public string? Комментарий_proc { get; set; } // tkp.Kommentariy

    public string? Наименование_proc { get; set; } // tkp.Name

    public string? Этап_proc { get; set; } // et.Name

    public string? Исполнитель_proc { get; set; } // users_isp.FullName

    public DateTime? ДатаЗапуска_proc { get; set; } // process.Data_zapuska

    public string? Статус_proc { get; set; } // process.Status

    public DateTime? ЖелаемаяДата_proc { get; set; } // process.Zhelaemaya_data

    public string? КодРС_proc { get; set; } // process.Kod_RS
    public string? СсылкаДокс_proc { get; set; }
    public string? СсылкаДокс_card { get; set; }
    public string? Наименование_папки_proc { get; set; }
    public string? Папка_proc { get; set; }
    public string? Схема_proc { get; set; }
    public Guid? UUID_card { get; set; }
    public Guid? UUID_proc { get; set; }

}


public class StagesByDirResponse
{
    public int id { get; set; }
    public Guid? uuid{ get; set; }
    public string? name{ get; set; }

}

public class TkpFoldersDBResponse
{
    public int id { get; set; }
    public Guid uuid { get; set; }
    public string name { get; set; }

}

public class ProcessTkpRequest
{
    public List<string>? Folders { get; set; }
    public List<string>? Schemas { get; set; }
    public bool? procActiveStatus { get; set; }
}