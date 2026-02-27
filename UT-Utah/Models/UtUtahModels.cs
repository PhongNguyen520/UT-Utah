namespace UT_Utah.Models;

/// <summary>
/// Header row for CSV export. One record per document header.
/// </summary>
public class HeaderModel
{
    public string CountyID { get; set; } = "";
    public string DocID { get; set; } = "";
    public string RecordingDate { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public string DocumentNumber { get; set; } = "";
    public string BookType { get; set; } = "";
    public string BookNumber { get; set; } = "";
    public string PageNumber { get; set; } = "";
    public string CourtType { get; set; } = "";
    public string CaseType { get; set; } = "";
    public string CaseNumber { get; set; } = "";
    public string Amount { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string DocumentDate { get; set; } = "";
    public string MailAddress { get; set; } = "";
    public string TaxAddress { get; set; } = "";
}

/// <summary>
/// Legal description row for CSV export.
/// </summary>
public class LegalModel
{
    public string CountyID { get; set; } = "";
    public string DocID { get; set; } = "";
    public string LegalDescription { get; set; } = "";
    public string AddressLine { get; set; } = "";
    public string Subdivision { get; set; } = "";
    public string Lot { get; set; } = "";
    public string Block { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Building { get; set; } = "";
    public string Section { get; set; } = "";
    public string Township { get; set; } = "";
    public string Range { get; set; } = "";
}

/// <summary>
/// Parcel/tax account row for CSV export.
/// </summary>
public class ParcelModel
{
    public string CountyID { get; set; } = "";
    public string DocID { get; set; } = "";
    public string ParcelNumber { get; set; } = "";
    public string TaxAccountNumber { get; set; } = "";
}

/// <summary>
/// Party name row for CSV export.
/// </summary>
public class NameModel
{
    public string CountyID { get; set; } = "";
    public string DocID { get; set; } = "";
    public string PartyType { get; set; } = "";
    public string PartyName { get; set; } = "";
    public string Sequence { get; set; } = "";
}

/// <summary>
/// Cross-reference document row for CSV export.
/// </summary>
public class XrefModel
{
    public string CountyID { get; set; } = "";
    public string DocID { get; set; } = "";
    public string XrefDocumentType { get; set; } = "";
    public string XrefDocumentNumber { get; set; } = "";
    public string XrefBookType { get; set; } = "";
    public string XrefBookNumber { get; set; } = "";
    public string XrefPageNumber { get; set; } = "";
    public string XrefDocumentNumber_2 { get; set; } = "";
}
