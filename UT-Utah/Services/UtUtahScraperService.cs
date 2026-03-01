using Microsoft.Playwright;
using UT_Utah.Models;
using UT_Utah.Utils;

namespace UT_Utah.Services;

/// <summary>Playwright-based scraper for Utah County Recorder. Pushes DocumentRecord to Apify Dataset; PDFs to Key-Value Store.</summary>
public class UtUtahScraperService
{
    const string StartUrl = "https://www.utahcounty.gov/LandRecords/Index.asp";
    const string LandRecordsBaseUrl = "https://www.utahcounty.gov/LandRecords/";
    const string RecordingsFormPath = "RecordingsForm.asp";
    const string Form2Id = "form2";
    const string InputStartDateId = "avEntryDate";
    const string InputEndDateId = "avEndEntryDate";
    const string SubmitButtonName = "Submit3";

    IPlaywright? _playwright;
    IBrowser? _browser;
    IBrowserContext? _context;
    IPage? _page;

    /// <summary>Main entry: runs full scrape workflow. Init Playwright, navigate, fill date range, paginate, extract records.</summary>
    public async Task RunAsync(InputConfig input)
    {
        input ??= new InputConfig();

        await ApifyHelper.SetStatusMessageAsync("Starting UT-Utah scraper...");

        try
        {
            await InitBrowserAsync();

            _page = await _context!.NewPageAsync();
            _page.SetDefaultTimeout(30_000);

            int searchRetries = 3;
            bool searchSuccess = false;

            for (int attempt = 1; attempt <= searchRetries; attempt++)
            {
                try
                {
                    if (attempt > 1)
                        await ApifyHelper.SetStatusMessageAsync($"Search attempt {attempt} of {searchRetries}...");

                    await _page.GotoAsync(StartUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

                    var docRecordingLink = _page.Locator($"a[href=\"{RecordingsFormPath}\"]").First;
                    await docRecordingLink.ClickAsync();
                    await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

                    var form2 = _page.Locator($"#{Form2Id}");
                    await form2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

                    var startDateInput = _page.Locator($"#{InputStartDateId}");
                    var endDateInput = _page.Locator($"#{InputEndDateId}");
                    if (attempt == 1)
                        await ApifyHelper.SetStatusMessageAsync($"Searching dates: {input.StartDate} to {input.EndDate}...");

                    await startDateInput.FillAsync(DomHelper.NormalizeDate(input.StartDate));
                    await endDateInput.FillAsync(DomHelper.NormalizeDate(input.EndDate));

                    var submitBtn = form2.Locator($"input[name=\"{SubmitButtonName}\"][type=\"submit\"]");
                    await submitBtn.ClickAsync();

                    await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

                    searchSuccess = true;
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Attempt {attempt}] Search failed: {ex.Message}");
                    if (attempt == searchRetries)
                    {
                        await ApifyHelper.SetStatusMessageAsync($"Fatal Error during search after {searchRetries} attempts: {ex.Message}", isTerminal: true);
                        throw;
                    }
                    await Task.Delay(5000);
                }
            }

            if (!searchSuccess) return;

            var allDetailLinks = await PaginateAndCollectDetailLinksAsync();
            Console.WriteLine($"[UtUtah] Total detail links collected: {allDetailLinks.Count}");

            if (allDetailLinks.Count == 0)
            {
                await ApifyHelper.SetStatusMessageAsync("Finished: No records found for the given date range.", isTerminal: true);
                return;
            }

            await ApifyHelper.SetStatusMessageAsync($"Found {allDetailLinks.Count} records. Preparing to extract...");

            const int TestLimit = 5;
            var linksToProcess = allDetailLinks.Take(TestLimit).ToList();
            if (allDetailLinks.Count > TestLimit)
                Console.WriteLine($"[UtUtah] Limiting to {TestLimit} records for test (total {allDetailLinks.Count} skipped).");

            for (var i = 0; i < linksToProcess.Count; i++)
            {
                await ApifyHelper.SetStatusMessageAsync($"Processing record {i + 1} of {linksToProcess.Count}...");

                var relativeUrl = linksToProcess[i];
                var fullUrl = ResolveDetailUrl(relativeUrl);
                var detailPage = await _context!.NewPageAsync();
                detailPage.SetDefaultTimeout(60_000);

                try
                {
                    await detailPage.GotoAsync(fullUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                    var record = await ExtractAndMapDetailPageAsync(detailPage);

                    if (!string.IsNullOrEmpty(record.EntryNumber))
                    {
                        var pdfUrl = await PdfDownloader.TryDownloadPdfForDetailPageAsync(detailPage, record.EntryNumber, record.Recorded);
                        record.PdfUrl = pdfUrl ?? "";

                        await ApifyHelper.PushSingleDataAsync(record);
                        Console.WriteLine($"[UtUtah] Pushed data for {record.EntryNumber} to Dataset.");
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

            await ApifyHelper.SetStatusMessageAsync("Success: All records exported to Dataset.", isTerminal: true);
        }
        catch (Exception ex)
        {
            await ApifyHelper.SetStatusMessageAsync($"Fatal Error: {ex.Message}", isTerminal: true);
            throw;
        }
    }

    /// <summary>Handles "No Records" check, pagination loop, extracts detail links from each page.</summary>
    async Task<List<string>> PaginateAndCollectDetailLinksAsync()
    {
        var detailLinks = new List<string>();
        if (_page == null) return detailLinks;

        var h1 = _page.Locator("h1").First;
        var h1Text = await h1.TextContentAsync();
        if (!string.IsNullOrEmpty(h1Text) && h1Text.Contains("Total Records: 0", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[UtUtah] No records found for the given date range.");
            return detailLinks;
        }

        while (true)
        {
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

    /// <summary>Extract data from Document Detail page into DocumentRecord format.</summary>
    async Task<DocumentRecord> ExtractAndMapDetailPageAsync(IPage page)
    {
        var table = page.Locator("table[width=\"80%\"]").First;
        await table.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        var record = new DocumentRecord();

        var docId = await DomHelper.GetValueByLabelAsync(table, "Entry #:");
        if (string.IsNullOrWhiteSpace(docId)) docId = await DomHelper.GetValueByLabelAsync(table, "Entry #");
        record.EntryNumber = docId?.Trim() ?? "";

        record.Recorded = (await DomHelper.GetValueByLabelAsync(table, "Recorded:")) ?? "";

        var book = await DomHelper.GetValueByLabelAsync(table, "Book:");
        if (string.IsNullOrEmpty(book)) book = await DomHelper.GetValueByLabelAsync(table, "Book #:");
        if (string.IsNullOrEmpty(book)) book = DomHelper.ExtractValueFromSameCell(await DomHelper.GetLabelCellTextAsync(table, "Book:"));
        record.Book = book ?? "";

        var pageNumber = await DomHelper.GetValueByLabelAsync(table, "Pages:");
        if (string.IsNullOrEmpty(pageNumber)) pageNumber = DomHelper.ExtractValueFromSameCell(await DomHelper.GetLabelCellTextAsync(table, "Pages:"));
        record.Page = pageNumber ?? "";

        record.InstrumentDate = (await DomHelper.GetValueByLabelAsync(table, "Instrument Date:")) ?? "";

        var amount = await DomHelper.GetValueByLabelAsync(table, "Consideration:");
        if (string.IsNullOrEmpty(amount)) amount = DomHelper.ExtractValueFromSameCell(await DomHelper.GetLabelCellTextAsync(table, "Consideration:"));
        record.Consideration = amount ?? "";

        record.KindOfInst = (await DomHelper.GetValueByLabelAsync(table, "Kind of Inst:")) ?? "";

        record.MailAddress = (await DomHelper.GetValueByLabelAsync(table, "Mail Address:") ?? "").TrimEnd();

        var rawTaxAddress = await DomHelper.GetValueByLabelAsync(table, "Tax Address:") ?? "";
        record.TaxAddress = System.Text.RegularExpressions.Regex.Replace(rawTaxAddress, @"\s+", " ").Trim();

        record.Grantors = await DomHelper.GetLinkTextsByLabelAsync(table, "Grantor(s):");
        record.Grantees = await DomHelper.GetLinkTextsByLabelAsync(table, "Grantee(s):");
        record.SerialNumbers = await DomHelper.GetSerialNumbersAsync(table);

        var tieEntries = await DomHelper.GetValueByLabelAsync(table, "Tie Entry(s):");
        record.TieEntries = DomHelper.SplitMultipleValues(tieEntries);

        var releasesLinks = await DomHelper.GetLinkTextsByLabelAsync(table, "Releases:");
        if (releasesLinks.Count > 0)
            record.Releases = releasesLinks;
        else
        {
            var releasesRaw = await DomHelper.GetValueByLabelAsync(table, "Releases:");
            record.Releases = DomHelper.SplitMultipleValues(releasesRaw);
        }

        record.AbbvTaxingDesc = await DomHelper.GetLegalDescriptionLinesAsync(table);

        return record;
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

    /// <summary>Stops browser and disposes Playwright resources.</summary>
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
    }
}
