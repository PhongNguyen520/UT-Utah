using System.IO;
using Microsoft.Playwright;

namespace UT_Utah.Services;

/// <summary>PDF download via Document Image Viewer popup for Utah County records.</summary>
public static class PdfDownloader
{
    /// <summary>Sanitize string for use as file name (replace invalid chars with underscore).</summary>
    public static string SanitizeFileName(string documentNumber)
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

    /// <summary>Parses RecordingDate (e.g. "2/25/2026 9:46:02 AM") to (YYYY, MM) for path.</summary>
    public static (string year, string month) ParseRecordingDateForPath(string? recordingDate)
    {
        if (string.IsNullOrWhiteSpace(recordingDate)) return ("0000", "01");
        var datePart = recordingDate.Trim().Split(' ')[0];
        if (string.IsNullOrEmpty(datePart)) return ("0000", "01");
        if (DateTime.TryParse(datePart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
            return (dt.Year.ToString(), dt.Month.ToString("D2"));
        return ("0000", "01");
    }

    /// <summary>Opens Document Image Viewer popup, downloads PDF, uploads to Apify Key-Value Store. Returns PDF URL or null.</summary>
    public static async Task<string?> TryDownloadPdfForDetailPageAsync(IPage detailPage, string documentNumber, string recordingDate)
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
}
