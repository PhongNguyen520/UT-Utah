using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UT_Utah.Models;

public class DocumentRecord
{
    [JsonPropertyName("Entry #")]
    public string EntryNumber { get; set; } = "";

    [JsonPropertyName("Recorded")]
    public string Recorded { get; set; } = "";

    [JsonPropertyName("Book")]
    public string Book { get; set; } = "";

    [JsonPropertyName("Page")]
    public string Page { get; set; } = "";

    [JsonPropertyName("Instrument Date")]
    public string InstrumentDate { get; set; } = "";

    [JsonPropertyName("Consideration")]
    public string Consideration { get; set; } = "";

    [JsonPropertyName("Kind of Inst")]
    public string KindOfInst { get; set; } = "";

    [JsonPropertyName("Mail Address")]
    public string MailAddress { get; set; } = "";

    [JsonPropertyName("Tax Address")]
    public string TaxAddress { get; set; } = "";

    [JsonPropertyName("Grantor(s)")]
    public List<string> Grantors { get; set; } = new();

    [JsonPropertyName("Grantee(s)")]
    public List<string> Grantees { get; set; } = new();

    [JsonPropertyName("Serial Number(s)")]
    public List<string> SerialNumbers { get; set; } = new();

    [JsonPropertyName("Tie Entry(s)")]
    public List<string> TieEntries { get; set; } = new();

    [JsonPropertyName("Releases")]
    public List<string> Releases { get; set; } = new();

    [JsonPropertyName("Abbv Taxing Desc")]
    public string AbbvTaxingDesc { get; set; } = "";

    [JsonPropertyName("PdfUrl")]
    public string PdfUrl { get; set; } = "";
}
