using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace UT_Utah.Utils;

/// <summary>DOM extraction and parsing utilities for Utah County scraper.</summary>
public static class DomHelper
{
    /// <summary>Convert YYYY-MM-DD to MM/DD/YYYY; otherwise return trimmed input.</summary>
    public static string NormalizeDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var s = value.Trim();
        if (s.Length == 10 && s[4] == '-' && s[7] == '-')
        {
            var parts = s.Split('-');
            if (parts.Length == 3 && parts[0].Length == 4 && parts[1].Length == 2 && parts[2].Length == 2)
                return $"{parts[1]}/{parts[2]}/{parts[0]}";
        }
        return s;
    }

    /// <summary>Finds td containing label text, returns the following-sibling td[1] text; otherwise empty.</summary>
    public static async Task<string?> GetValueByLabelAsync(ILocator table, string label)
    {
        var cell = table.Locator($"xpath=.//td[contains(., {XPathString(label.Trim())})]/following-sibling::td[1]");
        var n = await cell.CountAsync();
        if (n == 0) return null;
        var text = await cell.First.TextContentAsync();
        return text?.Trim();
    }

    /// <summary>Gets the text of the td that contains the label (for same-cell value extraction).</summary>
    public static async Task<string?> GetLabelCellTextAsync(ILocator table, string label)
    {
        var cell = table.Locator($"xpath=.//td[contains(., {XPathString(label.Trim())})]");
        var n = await cell.CountAsync();
        if (n == 0) return null;
        return await cell.First.TextContentAsync();
    }

    public static string XPathString(string s)
    {
        if (s.Contains('\'')) return "\"" + s.Replace("\"", "\\\"") + "\"";
        return "'" + s + "'";
    }

    /// <summary>Extracts value from same cell when label and value are together (e.g. "Consideration:$0.00" or "Pages: 9").</summary>
    public static string? ExtractValueFromSameCell(string? cellText)
    {
        if (string.IsNullOrWhiteSpace(cellText)) return null;
        var idx = cellText.IndexOf(':');
        if (idx < 0) return cellText.Trim();
        return cellText[(idx + 1)..].Trim();
    }

    /// <summary>Gets all &lt;a&gt; tag inner texts in the value cell for a label (e.g. Grantor(s):, Grantee(s):).</summary>
    public static async Task<List<string>> GetLinkTextsByLabelAsync(ILocator table, string label)
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

    /// <summary>Serial Number(s): extract from &lt;a&gt; tags in value cell, or split cell text by comma/newline.</summary>
    public static async Task<List<string>> GetSerialNumbersAsync(ILocator table)
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

    public static List<string> SplitMultipleValues(string? raw)
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
    public static async Task<List<string>> GetLegalDescriptionLinesAsync(ILocator table)
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
            var s = Regex.Replace(line.Trim(), @"\s+", " ");
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return list;
    }
}
