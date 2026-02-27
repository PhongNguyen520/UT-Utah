using System.Globalization;
using System.IO;
using System.IO.Compression;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Playwright;
using UT_Utah.Models;

namespace UT_Utah.Services;

/// <summary>
/// Playwright-based scraper for Utah County Recorder.
/// Exports data into 5 CSV files: Header, Legal, Parcel, Name, Xref.
/// </summary>
public class UtUtahScraperService
{
    const string StartUrl = "https://www.utahcounty.gov/LandRecords/Index.asp";
    const string LandRecordsBaseUrl = "https://www.utahcounty.gov/LandRecords/";
    const string CountyId = "49049"; // FIPS code for Utah County
    const string RecordingsFormPath = "RecordingsForm.asp";
    const string Form2Id = "form2";
    const string InputStartDateId = "avEntryDate";
    const string InputEndDateId = "avEndEntryDate";
    const string SubmitButtonName = "Submit3";
    const string SubmitButtonValue = "  Search  ";

    IPlaywright? _playwright;
    IBrowser? _browser;
    IBrowserContext? _context;
    IPage? _page;

    /// <summary>
    /// Main entry: runs the full scrape workflow using ApifyHelper for storage and logging.
    /// Step 1: Initialization &amp; Navigation (init Playwright, go to start URL, open Recordings form, fill date range, submit).
    /// </summary>
    public async Task RunAsync(InputConfig input)
    {
        input ??= new InputConfig();

        // Step 6: Checkpoint / state — resume from last processed date if present
        try
        {
            var state = await ApifyHelper.GetValueAsync<StateModel>("STATE");
            if (state != null && !string.IsNullOrWhiteSpace(state.LastProcessedDate))
            {
                if (DateTime.TryParse(state.LastProcessedDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastDate))
                {
                    var resumeStart = lastDate.AddDays(1).ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                    input.StartDate = resumeStart;
                    Console.WriteLine($"[UtUtah] Resuming from checkpoint: StartDate set to {resumeStart} (last processed: {state.LastProcessedDate})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UtUtah] State load failed (continuing with full range): {ex.Message}");
        }

        // 1. Init Playwright and Browser
        await InitBrowserAsync();

        _page = await _context!.NewPageAsync();
        _page.SetDefaultTimeout(30_000);

        // 2. Navigate to Utah County Recorder start URL
        await _page.GotoAsync(StartUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // 3. Click "Document Recording Search" link (href="RecordingsForm.asp")
        var docRecordingLink = _page.Locator($"a[href=\"{RecordingsFormPath}\"]").First;
        await docRecordingLink.ClickAsync();
        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // 4. Locate form#form2 (action="RecordingsDate.asp"), fill Start Date and End Date
        var form2 = _page.Locator($"#{Form2Id}");
        await form2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        var startDateInput = _page.Locator($"#{InputStartDateId}");
        var endDateInput = _page.Locator($"#{InputEndDateId}");
        await startDateInput.FillAsync(NormalizeDate(input.StartDate));
        await endDateInput.FillAsync(NormalizeDate(input.EndDate));

        // 5. Click submit button inside #form2 (name="Submit3", value="  Search  ")
        var submitBtn = form2.Locator($"input[name=\"{SubmitButtonName}\"][type=\"submit\"]");
        await submitBtn.ClickAsync();

        // 6. Wait for the search results page to load completely
        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // ——— Step 2: Pagination & Extracting Record Links ———
        var allDetailLinks = await PaginateAndCollectDetailLinksAsync();
        Console.WriteLine($"[UtUtah] Total detail links collected: {allDetailLinks.Count}");

        // ——— Step 3: Scraping Data & Mapping ———
        var headers = new List<HeaderModel>();
        var names = new List<NameModel>();
        var legals = new List<LegalModel>();
        var parcels = new List<ParcelModel>();
        var xrefs = new List<XrefModel>();

        foreach (var relativeUrl in allDetailLinks.Take(10))
        {
            var fullUrl = ResolveDetailUrl(relativeUrl);

            // Create a brand new isolated page for every record
            var detailPage = await _context!.NewPageAsync();
            detailPage.SetDefaultTimeout(60_000);

            try
            {
                await detailPage.GotoAsync(fullUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                await ExtractAndMapDetailPageAsync(detailPage, headers, names, legals, parcels, xrefs);

                var lastHeader = headers.Count > 0 ? headers[^1] : null;
                if (lastHeader != null)
                {
                    await TryDownloadPdfForDetailPageAsync(detailPage, lastHeader.DocumentNumber, lastHeader.RecordingDate);
                }
            }
            catch (Exception ex) when (ex.Message.Contains("has been closed", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[UtUtah] Browser or page was closed; stopping loop.");
                try { await detailPage.CloseAsync(); } catch { }
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UtUtah] Error scraping detail page {fullUrl}: {ex.Message}");
            }
            finally
            {
                try { await detailPage.CloseAsync(); } catch { }
            }
        }

        Console.WriteLine($"[UtUtah] Step 3+4 done. Header={headers.Count}, Name={names.Count}, Legal={legals.Count}, Parcel={parcels.Count}, Xref={xrefs.Count}");

        // Step 5: Export CSVs, zip by date, cleanup, (optional) push to Apify
        await ExportAndZipDataAsync(headers, names, legals, parcels, xrefs);
    }

    /// <summary>
    /// Step 5: Groups data by RecordingDate, writes 5 CSVs per date into a temp folder,
    /// zips to Output/Data/{YYYY}/UT-Utah_{yyyy-MM-dd}.zip, then deletes the temp folder.
    /// </summary>
    async Task ExportAndZipDataAsync(
        List<HeaderModel> headers,
        List<NameModel> names,
        List<LegalModel> legals,
        List<ParcelModel> parcels,
        List<XrefModel> xrefs)
    {
        if (headers.Count == 0)
        {
            Console.WriteLine("[UtUtah] No headers to export; skipping Step 5.");
            return;
        }

        // 1. Group headers by normalized RecordingDate (yyyy-MM-dd)
        var dateGroups = headers
            .Select(h => new { Header = h, DateKey = TryParseRecordingDateKey(h.RecordingDate) })
            .Where(x => x.DateKey != null)
            .GroupBy(x => x.DateKey!, StringComparer.Ordinal)
            .ToList();

        foreach (var group in dateGroups)
        {
            var dateKey = group.Key; // e.g. "2026-02-26"
            var year = dateKey.Length >= 4 ? dateKey[..4] : DateTime.Now.Year.ToString();
            var docIdsForDate = new HashSet<string>(group.Select(x => x.Header.DocID), StringComparer.OrdinalIgnoreCase);

            // 2. Filter child lists by DocID
            var namesForDate = names.Where(n => docIdsForDate.Contains(n.DocID)).ToList();
            var legalsForDate = legals.Where(l => docIdsForDate.Contains(l.DocID)).ToList();
            var parcelsForDate = parcels.Where(p => docIdsForDate.Contains(p.DocID)).ToList();
            var xrefsForDate = xrefs.Where(x => docIdsForDate.Contains(x.DocID)).ToList();

            // 3. Temp folder and CSVs
            var tempDir = Path.Combine("Output", $"Temp_{dateKey}");
            Directory.CreateDirectory(tempDir);

            var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture);

            await WriteCsvAsync(Path.Combine(tempDir, "Header.csv"), group.Select(x => x.Header), csvConfig);
            await WriteCsvAsync(Path.Combine(tempDir, "Names.csv"), namesForDate, csvConfig);
            await WriteCsvAsync(Path.Combine(tempDir, "Legals.csv"), legalsForDate, csvConfig);
            await WriteCsvAsync(Path.Combine(tempDir, "Parcels.csv"), parcelsForDate, csvConfig);
            await WriteCsvAsync(Path.Combine(tempDir, "Xref.csv"), xrefsForDate, csvConfig);

            // 4. Zip: Output/Data/{YYYY}/UT-Utah_{yyyy-MM-dd}.zip
            var dataDir = Path.Combine("Output", "Data", year);
            Directory.CreateDirectory(dataDir);
            var zipPath = Path.Combine(dataDir, $"UT-Utah_{dateKey}.zip");
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tempDir, zipPath);
            Console.WriteLine($"[UtUtah] Created zip: {zipPath}");

            // 5. Cleanup temp folder
            Directory.Delete(tempDir, recursive: true);

            // 6. Apify: upload zip to Key-Value store (key = filename without extension)
            var zipKey = $"UT-Utah_{dateKey}";
            try
            {
                var zipBytes = await File.ReadAllBytesAsync(zipPath);
                await ApifyHelper.SaveKeyValueRecordAsync(zipKey, zipBytes, "application/zip");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UtUtah] Key-Value store upload failed for {zipKey}: {ex.Message}");
            }

            // 7. Update checkpoint state for this date (so we can resume later)
            try
            {
                await ApifyHelper.SetValueAsync("STATE", new StateModel { LastProcessedDate = dateKey });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UtUtah] State save failed for date {dateKey}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Parses RecordingDate (e.g. MM/DD/YYYY) to a date key "yyyy-MM-dd", or null if invalid.
    /// </summary>
    static string? TryParseRecordingDateKey(string? recordingDate)
    {
        if (string.IsNullOrWhiteSpace(recordingDate)) return null;
        if (DateTime.TryParse(recordingDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd");
        return null;
    }

    static async Task WriteCsvAsync<T>(string filePath, IEnumerable<T> records, CsvConfiguration config)
    {
        await using var writer = new StreamWriter(filePath);
        await using var csv = new CsvWriter(writer, config);
        await csv.WriteRecordsAsync(records);
    }

    /// <summary>
    /// Step 2: Handles "No Records" check, pagination loop, extracts detail links from each page.
    /// Assumes the page is already on the search results (RecordingsDate.asp).
    /// </summary>
    async Task<List<string>> PaginateAndCollectDetailLinksAsync()
    {
        var detailLinks = new List<string>();
        if (_page == null) return detailLinks;

        // 1. Check for "Total Records: 0" inside h1
        var h1 = _page.Locator("h1").First;
        var h1Text = await h1.TextContentAsync();
        if (!string.IsNullOrEmpty(h1Text) && h1Text.Contains("Total Records: 0", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[UtUtah] No records found for the given date range.");
            return detailLinks;
        }

        // 2. Pagination loop
        while (true)
        {
            // Wait for data table rows (tr[valign="top"]); header row has bgcolor="#000066" and no valign="top"
            var dataRows = _page.Locator("tr[valign=\"top\"]");
            await dataRows.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

            var count = await dataRows.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var row = dataRows.Nth(i);
                var firstCellLink = row.Locator("td").First.Locator("a[href]");
                var href = await firstCellLink.GetAttributeAsync("href");
                if (!string.IsNullOrWhiteSpace(href))
                {
                    detailLinks.Add(href.Trim());
                }
            }

            // 3. Look for "Next" button (exact text) in pagination area at bottom
            var nextLink = _page.Locator("a").GetByText("Next", new LocatorGetByTextOptions { Exact = true }).First;
            var nextVisible = await nextLink.IsVisibleAsync();
            if (!nextVisible)
                break;

            await nextLink.ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }

        return detailLinks;
    }

    static string ResolveDetailUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return LandRecordsBaseUrl;
        var s = url.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s;
        return LandRecordsBaseUrl.TrimEnd('/') + "/" + s.TrimStart('/');
    }

    const int PopupTimeoutMs = 15_000;
    const int DownloadTimeoutMs = 60_000;

    /// <summary>
    /// Step 4: Opens Document Image Viewer popup and handles the PDF download.
    /// Runs inside a fresh, isolated Page for every record to prevent state-leakage timeouts.
    /// </summary>
    async Task TryDownloadPdfForDetailPageAsync(IPage detailPage, string documentNumber, string recordingDate)
    {
        var docId = documentNumber;
        var (year, month) = ParseRecordingDateForPath(recordingDate);
        var safeFileName = SanitizeFileName(documentNumber) + ".pdf";
        var dir = Path.Combine("Output", "PDFs", year, month);
        Directory.CreateDirectory(dir);
        var fullPath = Path.Combine(dir, safeFileName);

        IPage? popup = null;
        var downloadTcs = new TaskCompletionSource<IDownload>();
        void OnDownload(object? sender, IDownload d) => downloadTcs.TrySetResult(d);

        try
        {
            detailPage.Download += OnDownload;

            // 1. Open BMI Web Viewer popup
            var popupTask = detailPage.WaitForPopupAsync(new PageWaitForPopupOptions { Timeout = 45_000 });
            await detailPage.Locator("input[value=\"Document Image Viewer\"]").First.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
            popup = await popupTask;
            popup.SetDefaultTimeout(60_000);

            popup.Download += OnDownload;

            await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            // Wait for document image so KnockoutJS bindings are active
            var firstDocImage = popup.Locator("img.lt-image").First;
            await firstDocImage.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
            await Task.Delay(2000);

            // 2. Open Hamburger menu
            var toolbar = popup.Locator("#Toolbar").First;
            await toolbar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

            var menuToggle = popup.Locator("#Toolbar a.dropdown-toggle[data-toggle=\"dropdown\"]").First;
            await menuToggle.ClickAsync();
            await Task.Delay(1500);

            // 3. Prepare to click "Download PDF"
            var downloadLink = popup.Locator("a[data-bind*=\"showPdf\"]").First;
            if (!await downloadLink.IsVisibleAsync())
                downloadLink = popup.Locator("a:has-text('Download PDF')").First;

            // 4. Click download and wait
            Console.WriteLine($"[UtUtah] Triggering download for {docId}...");
            await downloadLink.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

            var completed = await Task.WhenAny(downloadTcs.Task, Task.Delay(60_000));

            if (completed == downloadTcs.Task)
            {
                var download = await downloadTcs.Task;
                await download.SaveAsAsync(fullPath);
                Console.WriteLine($"[UtUtah] Successfully saved PDF: {fullPath}");
            }
            else
            {
                Console.WriteLine($"[UtUtah] PDF download timeout. No download event fired for DocID {docId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UtUtah] PDF download failed for DocID {docId}: {ex.Message}");
        }
        finally
        {
            detailPage.Download -= OnDownload;
            if (popup != null) popup.Download -= OnDownload;
            if (popup != null) try { await popup.CloseAsync(); } catch { }
        }
    }

    /// <summary>Parses RecordingDate (e.g. "2/25/2026 9:46:02 AM") to (YYYY, MM) for path.</summary>
    static (string year, string month) ParseRecordingDateForPath(string? recordingDate)
    {
        if (string.IsNullOrWhiteSpace(recordingDate)) return ("0000", "01");
        var datePart = recordingDate.Trim().Split(' ')[0];
        if (string.IsNullOrEmpty(datePart)) return ("0000", "01");
        if (DateTime.TryParse(datePart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
            return (dt.Year.ToString(), dt.Month.ToString("D2"));
        return ("0000", "01");
    }

    static string SanitizeFileName(string documentNumber)
    {
        if (string.IsNullOrEmpty(documentNumber)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(documentNumber.Length);
        foreach (var c in documentNumber)
        {
            if (Array.IndexOf(invalid, c) >= 0) sb.Append('_');
            else sb.Append(c);
        }
        return sb.ToString().Trim() is var s && s.Length > 0 ? s : "unknown";
    }

    /// <summary>
    /// Step 3: Extract data from Document Detail page and append to the five model lists.
    /// </summary>
    async Task ExtractAndMapDetailPageAsync(IPage page, List<HeaderModel> headers, List<NameModel> names, List<LegalModel> legals, List<ParcelModel> parcels, List<XrefModel> xrefs)
    {
        var table = page.Locator("table[width=\"80%\"]").First;
        await table.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var docId = await GetValueByLabelAsync(table, "Entry #:");
        if (string.IsNullOrWhiteSpace(docId)) docId = await GetValueByLabelAsync(table, "Entry #");
        docId = docId?.Trim() ?? "";
        if (string.IsNullOrEmpty(docId)) return;

        var countyId = CountyId;

        // HeaderModel
        var recordingDate = await GetValueByLabelAsync(table, "Recorded:");
        var pageNumber = await GetValueByLabelAsync(table, "Pages:");
        if (string.IsNullOrEmpty(pageNumber)) pageNumber = ExtractValueFromSameCell(await GetLabelCellTextAsync(table, "Pages:"));
        var documentDate = await GetValueByLabelAsync(table, "Instrument Date:");
        var amount = await GetValueByLabelAsync(table, "Consideration:");
        if (string.IsNullOrEmpty(amount)) amount = ExtractValueFromSameCell(await GetLabelCellTextAsync(table, "Consideration:"));
        var documentType = await GetValueByLabelAsync(table, "Kind of Inst:");
        var feesRemarks = await GetValueByLabelAsync(table, "Fees:");
        if (string.IsNullOrEmpty(feesRemarks)) feesRemarks = ExtractValueFromSameCell(await GetLabelCellTextAsync(table, "Fees:"));
        var mailAddress = (await GetValueByLabelAsync(table, "Mail Address:") ?? "").TrimEnd();
        var rawTaxAddress = await GetValueByLabelAsync(table, "Tax Address:") ?? "";
        var taxAddress = System.Text.RegularExpressions.Regex.Replace(rawTaxAddress, @"\s+", " ").Trim();

        headers.Add(new HeaderModel
        {
            CountyID = countyId,
            DocID = docId,
            DocumentNumber = docId,
            RecordingDate = recordingDate ?? "",
            PageNumber = pageNumber ?? "",
            DocumentDate = documentDate ?? "",
            Amount = amount ?? "",
            DocumentType = documentType ?? "",
            Remarks = feesRemarks ?? "",
            MailAddress = mailAddress ?? "",
            TaxAddress = taxAddress ?? ""
        });

        // NameModel: Grantor(s) = PartyType "1", Grantee(s) = PartyType "2", Sequence increment
        var sequence = 0;
        var grantors = await GetLinkTextsByLabelAsync(table, "Grantor(s):");
        foreach (var name in grantors)
        {
            sequence++;
            names.Add(new NameModel { CountyID = countyId, DocID = docId, PartyType = "1", PartyName = name, Sequence = sequence.ToString() });
        }
        var grantees = await GetLinkTextsByLabelAsync(table, "Grantee(s):");
        foreach (var name in grantees)
        {
            sequence++;
            names.Add(new NameModel { CountyID = countyId, DocID = docId, PartyType = "2", PartyName = name, Sequence = sequence.ToString() });
        }

        // ParcelModel: Serial Number(s)
        var serialNumbers = await GetSerialNumbersAsync(table);
        foreach (var parcelNum in serialNumbers)
        {
            parcels.Add(new ParcelModel { CountyID = countyId, DocID = docId, ParcelNumber = parcelNum, TaxAccountNumber = "" });
        }

        // XrefModel: Tie Entry(s) and Releases
        var tieEntries = await GetValueByLabelAsync(table, "Tie Entry(s):");
        var releases = await GetValueByLabelAsync(table, "Releases:");
        foreach (var xrefNum in SplitMultipleValues(tieEntries))
        {
            if (string.IsNullOrWhiteSpace(xrefNum)) continue;
            xrefs.Add(new XrefModel { CountyID = countyId, DocID = docId, XrefDocumentNumber = xrefNum.Trim() });
        }
        foreach (var xrefNum in SplitMultipleValues(releases))
        {
            if (string.IsNullOrWhiteSpace(xrefNum)) continue;
            xrefs.Add(new XrefModel { CountyID = countyId, DocID = docId, XrefDocumentNumber = xrefNum.Trim() });
        }

        // LegalModel: Abbv Taxing Desc* — strip warning note
        var legalDesc = await GetLegalDescriptionAsync(table);
        if (!string.IsNullOrWhiteSpace(legalDesc))
        {
            legals.Add(new LegalModel { CountyID = countyId, DocID = docId, LegalDescription = legalDesc });
        }
    }

    /// <summary>Finds td containing label text, returns the following-sibling td[1] text; otherwise empty.</summary>
    async Task<string?> GetValueByLabelAsync(ILocator table, string label)
    {
        var cell = table.Locator($"xpath=.//td[contains(., {XPathString(label.Trim())})]/following-sibling::td[1]");
        var n = await cell.CountAsync();
        if (n == 0) return null;
        var text = await cell.First.TextContentAsync();
        return text?.Trim();
    }

    /// <summary>Gets the text of the td that contains the label (for same-cell value extraction).</summary>
    async Task<string?> GetLabelCellTextAsync(ILocator table, string label)
    {
        var cell = table.Locator($"xpath=.//td[contains(., {XPathString(label.Trim())})]");
        var n = await cell.CountAsync();
        if (n == 0) return null;
        return await cell.First.TextContentAsync();
    }

    static string XPathString(string s)
    {
        if (s.Contains('\'')) return "\"" + s.Replace("\"", "\\\"") + "\"";
        return "'" + s + "'";
    }

    /// <summary>Extracts value from same cell when label and value are together (e.g. "Consideration:$0.00" or "Pages: 9").</summary>
    static string? ExtractValueFromSameCell(string? cellText)
    {
        if (string.IsNullOrWhiteSpace(cellText)) return null;
        var idx = cellText.IndexOf(':');
        if (idx < 0) return cellText.Trim();
        return cellText[(idx + 1)..].Trim();
    }

    /// <summary>Gets all &lt;a&gt; tag inner texts in the value cell for a label (e.g. Grantor(s):, Grantee(s):).</summary>
    async Task<List<string>> GetLinkTextsByLabelAsync(ILocator table, string label)
    {
        var list = new List<string>();
        var valueCell = table.Locator($"xpath=.//td[contains(., {XPathString(label.Trim())})]/following-sibling::td[1]");
        var n = await valueCell.CountAsync();
        if (n == 0) return list;
        var links = valueCell.First.Locator("a");
        var count = await links.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var text = await links.Nth(i).TextContentAsync();
            if (!string.IsNullOrWhiteSpace(text)) list.Add(text.Trim());
        }
        return list;
    }

    /// <summary>Serial Number(s): extract from &lt;a&gt; tags in value cell (e.g. SerialVersions.asp links), or split cell text by comma/newline.</summary>
    async Task<List<string>> GetSerialNumbersAsync(ILocator table)
    {
        var valueCell = table.Locator("xpath=.//td[contains(., 'Serial Number(s):')]/following-sibling::td[1]");
        var n = await valueCell.CountAsync();
        if (n == 0) return new List<string>();
        var links = valueCell.First.Locator("a");
        var linkCount = await links.CountAsync();
        if (linkCount > 0)
        {
            var list = new List<string>();
            for (var i = 0; i < linkCount; i++)
            {
                var text = await links.Nth(i).TextContentAsync();
                if (!string.IsNullOrWhiteSpace(text)) list.Add(text.Trim());
            }
            return list;
        }
        var raw = await GetValueByLabelAsync(table, "Serial Number(s):");
        return SplitMultipleValues(raw);
    }

    static List<string> SplitMultipleValues(string? raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var part in raw.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Trim();
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return list;
    }

    /// <summary>Abbv Taxing Desc*: extract value and remove "*Taxing description NOT FOR LEGAL DOCUMENTS".</summary>
    async Task<string?> GetLegalDescriptionAsync(ILocator table)
    {
        var valueCell = table.Locator("xpath=.//td[contains(., 'Abbv Taxing Desc')]/following-sibling::td[1]");
        var n = await valueCell.CountAsync();
        if (n == 0) return null;
        var text = await valueCell.First.InnerTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = text
            .Replace("*Taxing description NOT FOR LEGAL DOCUMENTS", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Taxing description NOT FOR LEGAL DOCUMENTS", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    static string NormalizeDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var s = value.Trim();
        // Accept MM/DD/YYYY; if user passes yyyy-MM-dd, convert to MM/dd/yyyy
        if (s.Length == 10 && s[4] == '-' && s[7] == '-')
        {
            var parts = s.Split('-');
            if (parts.Length == 3 && parts[0].Length == 4 && parts[1].Length == 2 && parts[2].Length == 2)
                return $"{parts[1]}/{parts[2]}/{parts[0]}";
        }
        return s;
    }

    async Task InitBrowserAsync()
    {
        _playwright = await Playwright.CreateAsync();
        var isApify = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APIFY_CONTAINER_PORT"));
        var browserArgs = new[]
        {
            "--no-default-browser-check",
            "--disable-dev-shm-usage",
            "--disable-gpu",
            "--no-sandbox",
            "--disable-software-rasterizer",
            "--disable-extensions",
            "--disable-background-networking",
            "--disable-default-apps",
            "--disable-sync",
            "--disable-translate",
            "--mute-audio",
            "--no-first-run",
            "--disable-renderer-backgrounding"
        };
        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "chrome",
                Headless = isApify,
                Timeout = 60_000,
                Args = browserArgs
            });
        }
        catch
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = isApify,
                Timeout = 60_000,
                Args = browserArgs
            });
        }
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });
    }

    /// <summary>
    /// Stops browser and disposes Playwright resources.
    /// </summary>
    public async Task StopAsync()
    {
        if (_page != null)
        {
            await _page.CloseAsync();
            _page = null;
        }
        if (_context != null)
        {
            await _context.CloseAsync();
            _context = null;
        }
        if (_browser != null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }
        _playwright?.Dispose();
        _playwright = null;
        await Task.CompletedTask;
    }
}
