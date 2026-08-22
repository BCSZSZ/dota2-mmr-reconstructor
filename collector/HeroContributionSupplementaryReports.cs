using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace Dota2MmrCollector;

internal static class HeroContributionSupplementaryReports
{
    private static readonly string[] Headers =
    [
        "排名", "英雄", "Hero ID", "场次", "胜", "负", "胜率", "总MMR贡献", "场均贡献",
        "GC真实场次", "GC真实贡献", "拟合场次", "拟合贡献",
    ];

    private static readonly string[] ScopeNotes =
    [
        "按总 MMR 贡献从高到低排列，只统计该账号在曲线区间内实际使用过的英雄。",
        "GC 真实比赛使用服务器返回的 Rank Change；隐藏比赛使用端点约束后的拟合 Rank Change。",
        "校准、轨道切换或观测断层产生的 anchor_jump_before 不属于任何单场比赛，因此不归因给英雄。",
        "英雄英文名来自内置 OpenDota dotaconstants 快照；Hero ID 始终保留，未知新英雄不会丢失。",
    ];

    public static void WriteMarkdown(string path, HeroContributionReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Dota 2 MMR 英雄贡献统计");
        builder.AppendLine();
        builder.AppendLine($"- 账号 ID32：{report.AccountId}");
        builder.AppendLine($"- 区间：{report.StartUtc} 至 {report.EndUtc}");
        builder.AppendLine($"- 天梯比赛：{report.Matches:N0}");
        builder.AppendLine($"- 出现英雄：{report.Contributions.Count:N0}");
        builder.AppendLine($"- 总贡献：{Signed(report.TotalContribution)} MMR");
        builder.AppendLine($"- GC 真实贡献：{Signed(report.ActualContribution)} MMR");
        builder.AppendLine($"- 低置信度拟合贡献：{Signed(report.FittedContribution)} MMR");
        builder.AppendLine();
        builder.AppendLine("## 口径说明");
        builder.AppendLine();
        for (var index = 0; index < ScopeNotes.Length; index++)
        {
            builder.Append(index + 1).Append(". ").AppendLine(ScopeNotes[index]);
        }
        builder.AppendLine();
        builder.AppendLine("## 英雄贡献");
        builder.AppendLine();
        builder.Append('|').Append(string.Join('|', Headers.Select(EscapeMarkdown))).AppendLine("|");
        builder.Append('|').Append(string.Join('|', Headers.Select(_ => "---"))).AppendLine("|");
        for (var index = 0; index < report.Contributions.Count; index++)
        {
            var item = report.Contributions[index];
            var average = item.Matches == 0 ? 0 : item.TotalContribution / (double)item.Matches;
            var winRate = item.Matches == 0 ? 0 : item.Wins / (double)item.Matches;
            string[] fields =
            [
                (index + 1).ToString(CultureInfo.InvariantCulture),
                EscapeMarkdown(item.HeroName),
                item.HeroId.ToString(CultureInfo.InvariantCulture),
                item.Matches.ToString(CultureInfo.InvariantCulture),
                item.Wins.ToString(CultureInfo.InvariantCulture),
                item.Losses.ToString(CultureInfo.InvariantCulture),
                winRate.ToString("0.0%", CultureInfo.InvariantCulture),
                Signed(item.TotalContribution),
                Signed(average, "0.00"),
                item.ActualMatches.ToString(CultureInfo.InvariantCulture),
                Signed(item.ActualContribution),
                item.FittedMatches.ToString(CultureInfo.InvariantCulture),
                Signed(item.FittedContribution),
            ];
            builder.Append('|').Append(string.Join('|', fields)).AppendLine("|");
        }

        WriteTextAtomically(path, builder.ToString());
    }

    public static void WriteWorkbook(string path, HeroContributionReport report)
    {
        using var workbook = new XLWorkbook();
        var dataSheet = workbook.Worksheets.Add("英雄贡献");
        WriteContributionSheet(dataSheet, report);
        var notesSheet = workbook.Worksheets.Add("说明");
        WriteNotesSheet(notesSheet, report);

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
    }

    private static void WriteContributionSheet(
        IXLWorksheet worksheet,
        HeroContributionReport report)
    {
        for (var column = 0; column < Headers.Length; column++)
        {
            worksheet.Cell(1, column + 1).Value = Headers[column];
        }

        for (var index = 0; index < report.Contributions.Count; index++)
        {
            var item = report.Contributions[index];
            var row = index + 2;
            worksheet.Cell(row, 1).Value = index + 1;
            worksheet.Cell(row, 2).Value = item.HeroName;
            worksheet.Cell(row, 3).Value = item.HeroId;
            worksheet.Cell(row, 4).Value = item.Matches;
            worksheet.Cell(row, 5).Value = item.Wins;
            worksheet.Cell(row, 6).Value = item.Losses;
            worksheet.Cell(row, 7).Value = item.Matches == 0 ? 0 : item.Wins / (double)item.Matches;
            worksheet.Cell(row, 8).Value = item.TotalContribution;
            worksheet.Cell(row, 9).Value = item.Matches == 0
                ? 0
                : item.TotalContribution / (double)item.Matches;
            worksheet.Cell(row, 10).Value = item.ActualMatches;
            worksheet.Cell(row, 11).Value = item.ActualContribution;
            worksheet.Cell(row, 12).Value = item.FittedMatches;
            worksheet.Cell(row, 13).Value = item.FittedContribution;
        }

        var lastRow = report.Contributions.Count + 1;
        var table = worksheet.Range(1, 1, lastRow, Headers.Length)
            .CreateTable("HeroMmrContribution");
        table.Theme = XLTableTheme.TableStyleMedium2;
        worksheet.SheetView.FreezeRows(1);
        worksheet.Range(2, 7, lastRow, 7).Style.NumberFormat.Format = "0.0%";
        worksheet.Range(2, 8, lastRow, 8).Style.NumberFormat.Format = "+0;-0;0";
        worksheet.Range(2, 9, lastRow, 9).Style.NumberFormat.Format = "+0.00;-0.00;0.00";
        worksheet.Range(2, 11, lastRow, 11).Style.NumberFormat.Format = "+0;-0;0";
        worksheet.Range(2, 13, lastRow, 13).Style.NumberFormat.Format = "+0;-0;0";
        worksheet.Column(1).Width = 8;
        worksheet.Column(2).Width = 24;
        worksheet.Column(3).Width = 10;
        for (var column = 4; column <= 7; column++)
        {
            worksheet.Column(column).Width = 10;
        }
        for (var column = 8; column <= 13; column++)
        {
            worksheet.Column(column).Width = 15;
        }
        worksheet.RangeUsed()!.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void WriteNotesSheet(IXLWorksheet worksheet, HeroContributionReport report)
    {
        worksheet.Cell("A1").Value = "Dota 2 MMR 英雄贡献统计";
        worksheet.Range("A1:B1").Merge();
        worksheet.Cell("A1").Style.Font.Bold = true;
        worksheet.Cell("A1").Style.Font.FontSize = 16;

        WriteLabelValue(worksheet, 3, "账号 ID32", report.AccountId);
        WriteLabelValue(worksheet, 4, "区间", $"{report.StartUtc} 至 {report.EndUtc}");
        WriteLabelValue(worksheet, 5, "天梯比赛", report.Matches);
        WriteLabelValue(worksheet, 6, "出现英雄", report.Contributions.Count);
        WriteLabelValue(worksheet, 7, "总贡献", report.TotalContribution);
        WriteLabelValue(worksheet, 8, "GC 真实贡献", report.ActualContribution);
        WriteLabelValue(worksheet, 9, "低置信度拟合贡献", report.FittedContribution);
        worksheet.Range("B7:B9").Style.NumberFormat.Format = "+0;-0;0";

        worksheet.Cell("A11").Value = "口径说明";
        worksheet.Cell("A11").Style.Font.Bold = true;
        for (var index = 0; index < ScopeNotes.Length; index++)
        {
            worksheet.Cell(index + 12, 1).Value = index + 1;
            worksheet.Cell(index + 12, 2).Value = ScopeNotes[index];
        }
        worksheet.Column(1).Width = 18;
        worksheet.Column(2).Width = 100;
        worksheet.Column(2).Style.Alignment.WrapText = true;
        worksheet.RangeUsed()!.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
    }

    private static void WriteLabelValue(IXLWorksheet worksheet, int row, string label, object value)
    {
        worksheet.Cell(row, 1).Value = label;
        worksheet.Cell(row, 1).Style.Font.Bold = true;
        worksheet.Cell(row, 2).Value = XLCellValue.FromObject(value);
    }

    private static void WriteTextAtomically(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value, new UTF8Encoding(true));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal);

    private static string Signed(int value) =>
        value.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    private static string Signed(double value, string digits) =>
        value.ToString($"+{digits};-{digits};{digits}", CultureInfo.InvariantCulture);
}
