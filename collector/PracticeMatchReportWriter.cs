using System.Globalization;
using ClosedXML.Excel;

namespace Dota2MmrCollector;

internal static class PracticeMatchReportWriter
{
    private static readonly string[] PlayerHeaders =
    [
        "Account ID", "玩家名", "胜", "负", "总场数", "胜率",
    ];

    public static PracticeAggregation WriteWorkbook(
        string path,
        PracticeCollectionOutput collection)
    {
        var settings = new PracticeReportSettings(
            collection.Period.FromDate,
            collection.Period.ToDateInclusive,
            collection.Period.TimeZoneId,
            null);
        var aggregation = PracticeMatchAggregator.Build(collection.Matches, settings);

        using var workbook = new XLWorkbook();
        var playerSheet = workbook.Worksheets.Add("玩家统计");
        WritePlayerSheet(playerSheet, collection, aggregation);
        var pairSheet = workbook.Worksheets.Add("组队胜率");
        WritePairSheet(pairSheet, collection, aggregation);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp.xlsx";
        try
        {
            workbook.SaveAs(temporary);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return aggregation;
    }

    private static void WritePlayerSheet(
        IXLWorksheet worksheet,
        PracticeCollectionOutput collection,
        PracticeAggregation aggregation)
    {
        const int headerRow = 6;
        ApplySheetDefaults(worksheet, XLColor.FromHtml("#1F4E78"));
        WriteHeading(worksheet, "A2:F2", "练习比赛玩家统计");
        worksheet.Cell("A3").Value = PeriodText(collection);
        worksheet.Cell("A4").Value = CoverageText(collection, aggregation);
        worksheet.Range("A3:F3").Merge();
        worksheet.Range("A4:F4").Merge();
        worksheet.Range("A3:F4").Style.Font.Italic = true;
        worksheet.Range("A3:F4").Style.Font.FontColor = XLColor.FromHtml("#595959");

        WriteHeaders(worksheet, headerRow, PlayerHeaders);
        for (var index = 0; index < aggregation.Players.Count; index++)
        {
            var item = aggregation.Players[index];
            var row = headerRow + 1 + index;
            if (item.AccountId is uint accountId)
            {
                worksheet.Cell(row, 1).SetValue(accountId.ToString());
            }
            worksheet.Cell(row, 2).SetValue(item.PlayerName);
            worksheet.Cell(row, 3).SetValue(item.Wins);
            worksheet.Cell(row, 4).SetValue(item.Losses);
            worksheet.Cell(row, 5).SetValue(item.TotalMatches);
            if (item.WinRate is double winRate)
            {
                worksheet.Cell(row, 6).SetValue(winRate);
            }
        }

        var lastRow = headerRow + aggregation.Players.Count;
        var table = worksheet.Range(headerRow, 1, lastRow, PlayerHeaders.Length)
            .CreateTable("PracticePlayers");
        table.Theme = XLTableTheme.TableStyleMedium2;
        table.ShowAutoFilter = true;
        worksheet.SheetView.FreezeRows(headerRow);
        worksheet.Column(1).Style.NumberFormat.Format = "@";
        if (aggregation.Players.Count > 0)
        {
            worksheet.Range(headerRow + 1, 3, lastRow, 5).Style.NumberFormat.Format = "#,##0";
            worksheet.Range(headerRow + 1, 6, lastRow, 6).Style.NumberFormat.Format = "0.0%";
        }
        worksheet.Column(1).Width = 18;
        worksheet.Column(2).Width = 30;
        worksheet.Columns(3, 5).Width = 11;
        worksheet.Column(6).Width = 12;
        WriteSourceNotes(worksheet, "H2");
    }

    private static void WritePairSheet(
        IXLWorksheet worksheet,
        PracticeCollectionOutput collection,
        PracticeAggregation aggregation)
    {
        const int headerRow = 6;
        var players = aggregation.Players;
        var lastColumn = Math.Max(2, players.Count + 1);
        ApplySheetDefaults(worksheet, XLColor.FromHtml("#4472C4"));
        WriteHeading(
            worksheet,
            worksheet.Range(2, 1, 2, lastColumn),
            "练习比赛组队矩阵");
        worksheet.Cell("A3").Value = PeriodText(collection);
        worksheet.Cell("A4").Value =
            "行列交叉处为共同场数/胜率（例如 3/66.7%）；包含仅共同出场 1 场的组合。" +
            "红色较低、黄色约 50%、绿色较高；空白表示没有共同出场，— 表示同一玩家。";
        worksheet.Range(3, 1, 3, lastColumn).Merge();
        worksheet.Range(4, 1, 4, lastColumn).Merge();
        worksheet.Range(3, 1, 4, lastColumn).Style.Font.Italic = true;
        worksheet.Range(3, 1, 4, lastColumn).Style.Font.FontColor =
            XLColor.FromHtml("#595959");
        worksheet.Row(4).Height = 34;
        worksheet.Cell("A4").Style.Alignment.WrapText = true;

        var pairsByKey = aggregation.Pairs.ToDictionary(
            pair => PairKey(pair.PlayerAKey, pair.PlayerBKey),
            StringComparer.Ordinal);
        WriteMatrixHeaders(worksheet, headerRow, players);
        WritePairMatrix(worksheet, headerRow, players, pairsByKey);

        worksheet.SheetView.FreezeRows(headerRow);
        worksheet.SheetView.FreezeColumns(1);
        worksheet.Column(1).Width = 30;
        for (var column = 2; column <= lastColumn; column++)
        {
            worksheet.Column(column).Width = 14;
        }
    }

    private static void WriteMatrixHeaders(
        IXLWorksheet worksheet,
        int headerRow,
        IReadOnlyList<PracticePlayerSummary> players)
    {
        worksheet.Cell(headerRow, 1).Value = "玩家";
        for (var index = 0; index < players.Count; index++)
        {
            var label = MatrixLabel(players[index]);
            worksheet.Cell(headerRow, index + 2).Value = label;
            worksheet.Cell(headerRow + index + 1, 1).Value = label;
        }

        var lastColumn = Math.Max(2, players.Count + 1);
        var header = worksheet.Range(headerRow, 1, headerRow, lastColumn);
        var rowHeaders = players.Count == 0
            ? worksheet.Range(headerRow, 1, headerRow, 1)
            : worksheet.Range(headerRow + 1, 1, headerRow + players.Count, 1);
        foreach (var range in new[] { header, rowHeaders })
        {
            range.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Font.Bold = true;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorderColor = XLColor.White;
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#D9E2F3");
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            range.Style.Alignment.WrapText = true;
        }
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        rowHeaders.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        worksheet.Row(headerRow).Height = 58;
        for (var index = 0; index < players.Count; index++)
        {
            worksheet.Row(headerRow + index + 1).Height = 34;
        }
    }

    private static void WritePairMatrix(
        IXLWorksheet worksheet,
        int headerRow,
        IReadOnlyList<PracticePlayerSummary> players,
        IReadOnlyDictionary<string, PracticePairSummary> pairsByKey)
    {
        for (var rowIndex = 0; rowIndex < players.Count; rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < players.Count; columnIndex++)
            {
                var cell = worksheet.Cell(headerRow + rowIndex + 1, columnIndex + 2);
                StyleMatrixCell(cell);
                if (rowIndex == columnIndex)
                {
                    StyleMatrixDiagonal(cell);
                    continue;
                }

                if (!pairsByKey.TryGetValue(
                        PairKey(players[rowIndex].PlayerKey, players[columnIndex].PlayerKey),
                        out var pair)
                    || pair.WinRate is not double winRate)
                {
                    continue;
                }

                var percentage = (winRate * 100).ToString("0.0", CultureInfo.InvariantCulture);
                cell.SetValue($"{pair.TotalMatches}/{percentage}%");
                cell.Style.Fill.BackgroundColor = WinRateFill(winRate);
            }
        }
    }

    private static void StyleMatrixCell(IXLCell cell)
    {
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#D9E2F3");
    }

    private static void StyleMatrixDiagonal(IXLCell cell)
    {
        cell.Value = "—";
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E7E6E6");
        cell.Style.Font.FontColor = XLColor.FromHtml("#7F7F7F");
    }

    private static string MatrixLabel(PracticePlayerSummary player) =>
        player.AccountId is uint accountId
            ? $"{player.PlayerName}\n{accountId}"
            : player.PlayerName;

    private static string PairKey(string first, string second) =>
        string.CompareOrdinal(first, second) <= 0
            ? first + "\u001f" + second
            : second + "\u001f" + first;

    private static XLColor WinRateFill(double winRate)
    {
        var bounded = Math.Clamp(winRate, 0, 1);
        return bounded <= 0.5
            ? InterpolateColor("#F4CCCC", "#FFF2CC", bounded * 2)
            : InterpolateColor("#FFF2CC", "#D9EAD3", (bounded - 0.5) * 2);
    }

    private static XLColor InterpolateColor(string startHex, string endHex, double position)
    {
        var start = ParseRgb(startHex);
        var end = ParseRgb(endHex);
        var bounded = Math.Clamp(position, 0, 1);
        var red = (int)Math.Round(start.Red + (end.Red - start.Red) * bounded);
        var green = (int)Math.Round(start.Green + (end.Green - start.Green) * bounded);
        var blue = (int)Math.Round(start.Blue + (end.Blue - start.Blue) * bounded);
        return XLColor.FromHtml($"#{red:X2}{green:X2}{blue:X2}");
    }

    private static (int Red, int Green, int Blue) ParseRgb(string hex) =>
        (
            Convert.ToInt32(hex.Substring(1, 2), 16),
            Convert.ToInt32(hex.Substring(3, 2), 16),
            Convert.ToInt32(hex.Substring(5, 2), 16)
        );

    private static void ApplySheetDefaults(IXLWorksheet worksheet, XLColor tabColor)
    {
        worksheet.ShowGridLines = false;
        worksheet.TabColor = tabColor;
        worksheet.Style.Font.FontName = "Arial";
        worksheet.Style.Font.FontSize = 10;
        worksheet.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        worksheet.Row(1).Height = 8;
        worksheet.Row(2).Height = 24;
        worksheet.Rows(3, 4).Height = 20;
    }

    private static void WriteHeading(IXLWorksheet worksheet, string rangeAddress, string title) =>
        WriteHeading(worksheet, worksheet.Range(rangeAddress), title);

    private static void WriteHeading(IXLWorksheet worksheet, IXLRange range, string title)
    {
        range.Merge();
        range.FirstCell().Value = title;
        range.Style.Font.Bold = true;
        range.Style.Font.FontSize = 14;
        range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        range.Style.Border.BottomBorderColor = XLColor.FromHtml("#A6A6A6");
    }

    private static void WriteHeaders(IXLWorksheet worksheet, int row, IReadOnlyList<string> headers)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            worksheet.Cell(row, index + 1).Value = headers[index];
        }
        var header = worksheet.Range(row, 1, row, headers.Count);
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Font.Bold = true;
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        header.Style.Border.InsideBorderColor = XLColor.White;
        worksheet.Row(row).Height = 22;
    }

    private static string PeriodText(PracticeCollectionOutput collection) =>
        $"日期：{collection.Period.FromDate:yyyy-MM-dd} 至 " +
        $"{collection.Period.ToDateInclusive:yyyy-MM-dd}（{collection.Period.TimeZoneId}）";

    private static string CoverageText(
        PracticeCollectionOutput collection,
        PracticeAggregation aggregation) =>
        $"GC 种子 {collection.Gc.SeedMatches:N0} 场（" +
        (collection.Gc.Errors.Count == 0 ? "完成" : $"{collection.Gc.Errors.Count:N0} 项错误") +
        $"）；OpenDota 补充 " +
        $"{collection.OpenDota.MatchesAdded:N0} 场（" +
        (collection.OpenDota.Complete ? "完成" : "部分请求失败") +
        $"）；最终范围内 {aggregation.MatchesInPeriod:N0} 场，" +
        $"其中计分 {aggregation.ScoredMatches:N0} 场、未计分 {aggregation.UnscoredMatches:N0} 场。";

    private static void WriteSourceNotes(IXLWorksheet worksheet, string startCell)
    {
        var start = worksheet.Cell(startCell);
        start.Value = "数据来源";
        start.Style.Font.Bold = true;
        start.CellBelow().Value = "Valve Dota 2 Game Coordinator";
        start.CellBelow(2).Value = "OpenDota API：https://api.opendota.com/api";
        start.CellBelow(3).Value =
            "匿名账号按“比赛 + 槽位”分别列示，不跨比赛合并；机器人、教练和观战槽位不计。";
        for (var offset = 0; offset < 4; offset++)
        {
            worksheet.Range(
                start.Address.RowNumber + offset,
                start.Address.ColumnNumber,
                start.Address.RowNumber + offset,
                start.Address.ColumnNumber + 3).Merge();
        }
        worksheet.Columns(start.Address.ColumnNumber, start.Address.ColumnNumber + 3).Width = 16;
        worksheet.Row(start.Address.RowNumber + 3).Height = 32;
        var noteRange = worksheet.Range(
            start.Address.RowNumber,
            start.Address.ColumnNumber,
            start.Address.RowNumber + 3,
            start.Address.ColumnNumber + 3);
        noteRange.Style.Font.FontColor = XLColor.FromHtml("#595959");
        noteRange.Style.Alignment.WrapText = true;
    }
}
