using System.Globalization;
using System.IO;
using Microsoft.Playwright;
using UT_Utah.Models;

namespace UT_Utah.Services;

/// <summary>
/// Playwright-based scraper for Utah County Recorder.
/// Pushes each document as a DocumentRecord (JSON) to the Apify Dataset; PDFs go to Key-Value Store.
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

        // TEST: limit to 5 records for Apify testing
        const int TestLimit = 5;
        var linksToProcess = allDetailLinks.Take(TestLimit).ToList();
        if (allDetailLinks.Count > TestLimit)
            Console.WriteLine($"[UtUtah] Limiting to {TestLimit} records for test (total {allDetailLinks.Count} skipped).");

        // ——— Step 3: For each detail link, scrape one document, upload PDF to KV, push DocumentRecord to Dataset ———
        foreach (var relativeUrl in linksToProcess)
        {
            var fullUrl = ResolveDetailUrl(relativeUrl);
            var detailPage = await _context!.NewPageAsync();
            detailPage.SetDefaultTimeout(60_000);

            try
            {
                await detailPage.GotoAsync(fullUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                var record = await ExtractAndMapDetailPageAsync(detailPage);

                if (!string.IsNullOrEmpty(record.EntryNumber))
                {
                    var pdfUrl = await TryDownloadPdfForDetailPageAsync(detailPage, record.EntryNumber, record.Recorded);
                    record.PdfUrl = pdfUrl ?? "";

                    await ApifyHelper.PushSingleDataAsync(record);
                    Console.WriteLine($"[UtUtah] Pushed data for {record.EntryNumber} to Dataset.");

                    if (DateTime.TryParse(record.Recorded, CultureInfo.InvariantCulture, DateTimeStyles.None, out var recordDate))
                    {
                        try
                        {
                            await ApifyHelper.SetValueAsync("STATE", new StateModel { LastProcessedDate = recordDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[UtUtah] State save failed: {ex.Message}");
                        }
                    }
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
                Console.WriteLine($"[UtUtah] Error processing detail page {fullUrl}: {ex.Message}");
            }
            finally
            {
                try { await detailPage.CloseAsync(); } catch { }
            }
        }
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
    /// Step 4: Opens Document Image Viewer popup, downloads PDF, uploads to Apify Key-Value Store.
    /// Returns the public URL of the PDF in the KV store (or local path when not on Apify). Returns null on failure.
    /// </summary>
    async Task<string?> TryDownloadPdfForDetailPageAsync(IPage detailPage, string documentNumber, string recordingDate)
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
        string? pdfPublicUrl = null;

        try
        {
            detailPage.Download += OnDownload;

            var popupTask = detailPage.WaitForPopupAsync(new PageWaitForPopupOptions { Timeout = 45_000 });
            await detailPage.Locator("input[value=\"Document Image Viewer\"]").First.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
            popup = await popupTask;
            popup.SetDefaultTimeout(60_000);

            popup.Download += OnDownload;

            await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var firstDocImage = popup.Locator("img.lt-image").First;
            await firstDocImage.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
            await Task.Delay(2000);

            var toolbar = popup.Locator("#Toolbar").First;
            await toolbar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

            var menuToggle = popup.Locator("#Toolbar a.dropdown-toggle[data-toggle=\"dropdown\"]").First;
            await menuToggle.ClickAsync();
            await Task.Delay(1500);

            var downloadLink = popup.Locator("a[data-bind*=\"showPdf\"]").First;
            if (!await downloadLink.IsVisibleAsync())
                downloadLink = popup.Locator("a:has-text('Download PDF')").First;

            Console.WriteLine($"[UtUtah] Triggering download for {docId}...");
            await downloadLink.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

            var completed = await Task.WhenAny(downloadTcs.Task, Task.Delay(60_000));

            if (completed == downloadTcs.Task)
            {
                var download = await downloadTcs.Task;
                await download.SaveAsAsync(fullPath);
                Console.WriteLine($"[UtUtah] Successfully saved PDF: {fullPath}");

                var pdfBytes = await File.ReadAllBytesAsync(fullPath);
                var kvKey = SanitizeFileName(docId) + ".pdf";
                await ApifyHelper.SaveKeyValueRecordAsync(kvKey, pdfBytes, "application/pdf");
                pdfPublicUrl = ApifyHelper.GetRecordUrl(kvKey);
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

        return pdfPublicUrl;
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
    /// Step 3: Extract data from Document Detail page into the flat DocumentRecord format.
    /// </summary>
    async Task<DocumentRecord> ExtractAndMapDetailPageAsync(IPage page)
    {
        var table = page.Locator("table[width=\"80%\"]").First;
        await table.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var record = new DocumentRecord();

        var docId = await GetValueByLabelAsync(table, "Entry #:");
        if (string.IsNullOrWhiteSpace(docId)) docId = await GetValueByLabelAsync(table, "Entry #");
        record.EntryNumber = docId?.Trim() ?? "";

        record.Recorded = (await GetValueByLabelAsync(table, "Recorded:")) ?? "";

        var book = await GetValueByLabelAsync(table, "Book:");
        if (string.IsNullOrEmpty(book)) book = await GetValueByLabelAsync(table, "Book #:");
        if (string.IsNullOrEmpty(book)) book = ExtractValueFromSameCell(await GetLabelCellTextAsync(table, "Book:"));
        record.Book = book ?? "";

        var pageNumber = await GetValueByLabelAsync(table, "Pages:");
        if (string.IsNullOrEmpty(pageNumber)) pageNumber = ExtractValueFromSameCell(await GetLabelCellTextAsync(table, "Pages:"));
        record.Page = pageNumber ?? "";

        record.InstrumentDate = (await GetValueByLabelAsync(table, "Instrument Date:")) ?? "";

        var amount = await GetValueByLabelAsync(table, "Consideration:");
        if (string.IsNullOrEmpty(amount)) amount = ExtractValueFromSameCell(await GetLabelCellTextAsync(table, "Consideration:"));
        record.Consideration = amount ?? "";

        record.KindOfInst = (await GetValueByLabelAsync(table, "Kind of Inst:")) ?? "";

        record.MailAddress = (await GetValueByLabelAsync(table, "Mail Address:") ?? "").TrimEnd();

        var rawTaxAddress = await GetValueByLabelAsync(table, "Tax Address:") ?? "";
        record.TaxAddress = System.Text.RegularExpressions.Regex.Replace(rawTaxAddress, @"\s+", " ").Trim();

        record.Grantors = await GetLinkTextsByLabelAsync(table, "Grantor(s):");
        record.Grantees = await GetLinkTextsByLabelAsync(table, "Grantee(s):");
        record.SerialNumbers = await GetSerialNumbersAsync(table);

        var tieEntries = await GetValueByLabelAsync(table, "Tie Entry(s):");
        record.TieEntries = SplitMultipleValues(tieEntries);

        var releasesLinks = await GetLinkTextsByLabelAsync(table, "Releases:");
        if (releasesLinks.Count > 0)
            record.Releases = releasesLinks;
        else
        {
            var releasesRaw = await GetValueByLabelAsync(table, "Releases:");
            record.Releases = SplitMultipleValues(releasesRaw);
        }

        record.AbbvTaxingDesc = await GetLegalDescriptionLinesAsync(table);

        return record;
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

    /// <summary>Abbv Taxing Desc*: extract lines, remove warning line, each line becomes one list item.</summary>
    async Task<List<string>> GetLegalDescriptionLinesAsync(ILocator table)
    {
        var list = new List<string>();
        var valueCell = table.Locator("xpath=.//td[contains(., 'Abbv Taxing Desc')]/following-sibling::td[1]");
        var n = await valueCell.CountAsync();
        if (n == 0) return list;
        var text = await valueCell.First.InnerTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var cleaned = text
            .Replace("*Taxing description NOT FOR LEGAL DOCUMENTS", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Taxing description NOT FOR LEGAL DOCUMENTS", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return list;
        foreach (var line in cleaned.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = System.Text.RegularExpressions.Regex.Replace(line.Trim(), @"\s+", " ");
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return list;
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
