using API.DB;
using API.Models.DTO.Reports;
using API.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace API.Controllers;

[ApiController]
[EnableRateLimiting("fixed")]
[Route("api/reports")]
[Authorize(Roles = "Accountant")]
public class ReportsController : Controller
{
    private readonly _1135InventorySystemContext db;
    private readonly ISystemSettingsService settings;

    public ReportsController(_1135InventorySystemContext db, ISystemSettingsService settings)
    {
        this.db = db;
        this.settings = settings;
    }

    [HttpGet("inventory-summary")]
    public async Task<ActionResult<List<InventorySummaryResponse>>> GetInventorySummary([FromQuery] int? employeeId,
        [FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
    {
        var query = db.Inventoryrecords.AsQueryable();

        if (employeeId.HasValue)
            query = query.Where(x => x.EmployeeId == employeeId.Value);

        if (startDate.HasValue)
            query = query.Where(x => x.InventoryDate >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(x => x.InventoryDate <= endDate.Value);

        var result = await query.GroupBy(x => x.EmployeeId)
            .Select(g => new InventorySummaryResponse
            {
                EmployeeId = g.Key,
                TotalRecords = g.Count(),
                MissingCount = g.Count(x => x.IsPresent == false),
                LastInventory = g.Max(x => x.InventoryDate)
            }).ToListAsync();

        return Ok(result);
    }

    [HttpGet("missing-equipment")]
    public async Task<ActionResult<List<MissingEquipmentResponse>>> GetMissingEquipment(
        [FromQuery] bool? includeResolved = false)
    {
        bool includeMissingFromSettings = await settings.GetSettingValueAsBoolAsync("IncludeMissingEquipment");

        bool includeResolvedFinal = includeResolved ?? includeMissingFromSettings;

        List<Inventoryrecord> records;

        if (!includeResolvedFinal)
        {
            records = await db.Inventoryrecords.Where(r => r.InventoryDate == db.Inventoryrecords
                    .Where(x => x.EquipmentId == r.EquipmentId).Max(x => x.InventoryDate)).Where(r => !r.IsPresent)
                .ToListAsync();
        }
        else
        {
            records = await db.Inventoryrecords.Where(r => !r.IsPresent).ToListAsync();
        }

        var result = records.Select(x => new MissingEquipmentResponse
        {
            EquipmentId = x.EquipmentId,
            EmployeeId = x.EmployeeId,
            InventoryDate = x.InventoryDate,
            Location = x.Location,
            Comments = x.Comments
        });

        return Ok(result);
    }

    [HttpGet("equipment-status")]
    public async Task<ActionResult<List<EquipmentStatusResponse>>> GetEquipmentStatusDistribution()
    {
        var result = await db.Equipment.Where(x => x.IsActive != false).GroupBy(x => x.Status)
            .Select(x => new EquipmentStatusResponse
            {
                Status = x.Key,
                Count = x.Count()
            }).ToListAsync();

        return Ok(result);
    }

    [HttpGet("assignment-history")]
    public async Task<ActionResult<List<AssignmentHistoryReportResponse>>> GetAssignmentHistory(
        [FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
    {
        var query = db.Assignmenthistories.AsQueryable();

        if (startDate.HasValue)
            query = query.Where(x => x.AssignmentDate >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(x => x.AssignmentDate <= endDate.Value);

        var history = await query.Include(x => x.Equipment)
            .Include(x => x.NewUser).Include(x => x.AssignedByAccountant)
            .Select(x => new AssignmentHistoryReportResponse
            {
                Id = x.Id,
                EquipmentName = x.Equipment.Name,
                Action = x.Action,
                PreviousUserId = x.PreviousUserId,
                NewUser = x.NewUser.FullName,
                Accountant = x.AssignedByAccountant.FullName,
                AssignmentDate = x.AssignmentDate,
                Reason = x.Reason
            }).OrderByDescending(x => x.AssignmentDate).ToListAsync();

        return Ok(history);
    }

    [HttpPost("export")]
    public async Task<ActionResult> ExportReport([FromBody] ExportReportRequest request)
    {
        var reportData = await GetInventorySummaryAsync(request.EmployeeId, request.StartDate, request.EndDate);

        var defaultFormat = await settings.GetSettingValueAsync("DefaultReportFormat");
        if (string.IsNullOrEmpty(request.Type))
            request.Type = defaultFormat;

        if (request.Type == "PDF")
            return GeneratePdf(reportData, request);

        if (request.Type == "Excel")
            return GenerateExcel(reportData, request);

        return BadRequest("Поддерживаемые типы: PDF, Excel");
    }


    private ActionResult GeneratePdf(InventoryReportResponse data, ExportReportRequest request)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Grey.Darken4));

                page.Header().Element(c => PdfHeader(c, request, data));
                page.Content().Element(c => PdfContent(c, data));
                page.Footer().AlignCenter().PaddingTop(10).Text(t =>
                {
                    t.Span("Страница ").FontSize(9).FontColor(Colors.Grey.Darken1);
                    t.CurrentPageNumber();
                    t.Span(" из ").FontSize(9).FontColor(Colors.Grey.Darken1);
                    t.TotalPages();
                });
            });
        });

        using var stream = new MemoryStream();
        document.GeneratePdf(stream);

        return File(stream.ToArray(), "application/pdf",
            $"InventoryReport_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf");
    }

    private void PdfHeader(IContainer container, ExportReportRequest request, InventoryReportResponse data)
    {
        container.PaddingBottom(12).Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text("Отчёт по инвентаризации")
                        .FontSize(18).SemiBold().FontColor(Colors.Black);

                    left.Item().Text($"Период: {request.StartDate:dd.MM.yyyy} — {request.EndDate:dd.MM.yyyy}")
                        .FontSize(10).FontColor(Colors.Grey.Darken1);

                    left.Item().Text($"Сотрудник: {data.EmployeeName}")
                        .FontSize(10).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(150).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text(DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm"))
                        .FontSize(9).FontColor(Colors.Grey.Darken1);

                    right.Item().AlignRight().Text("Сформировано (UTC)")
                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });

            col.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
        });
    }

    private void PdfContent(IContainer container, InventoryReportResponse data)
    {
        container.Column(col =>
        {
            col.Item().PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Component(new KpiCard(data.TotalEquipment, "Всего единиц", Colors.Blue.Medium));
                row.Spacing(10);
                row.RelativeItem().Component(new KpiCard(data.InventoriedCount, "В наличии", Colors.Green.Medium));
                row.Spacing(10);
                row.RelativeItem().Component(new KpiCard(data.MissingCount, "Отсутствует", Colors.Red.Medium));
            });

            col.Item().PaddingTop(14).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(90);   
                    columns.RelativeColumn();     
                    columns.ConstantColumn(95);   
                    columns.ConstantColumn(70);   
                    columns.ConstantColumn(85);   
                });

                table.Header(header =>
                {
                    header.Cell().Element(Th).Text("Инв. номер");
                    header.Cell().Element(Th).Text("Наименование");
                    header.Cell().Element(Th).Text("Состояние");
                    header.Cell().Element(Th).Text("Наличие");
                    header.Cell().Element(Th).Text("Дата");
                });

                int index = 0;
                foreach (var item in data.Details)
                {
                    var even = index % 2 == 0;

                    table.Cell().Element(c => Td(c, even)).Text(item.InventoryNumber);
                    table.Cell().Element(c => Td(c, even)).Text(item.EquipmentName);
                    table.Cell().Element(c => Td(c, even)).Text(item.EquipmentCondition);

                    table.Cell().Element(c => Td(c, even)).Text(t =>
                    {
                        t.Span(item.IsPresent ? "Да" : "Нет")
                            .SemiBold()
                            .FontColor(item.IsPresent ? Colors.Green.Medium : Colors.Red.Medium);
                    });

                    table.Cell().Element(c => Td(c, even)).Text(item.InventoryDate.ToString("dd.MM.yyyy"));

                    index++;
                }
            });

            col.Item().PaddingTop(10)
                .Text("Примечание: отчёт формируется на основе записей инвентаризации за выбранный период.")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }

    private static IContainer Th(IContainer c) =>
        c.Background(Colors.Grey.Lighten3)
         .PaddingVertical(6).PaddingHorizontal(6)
         .BorderBottom(1).BorderColor(Colors.Grey.Lighten1)
         .DefaultTextStyle(t => t.SemiBold().FontColor(Colors.Grey.Darken4))
         .AlignMiddle();

    private static IContainer Td(IContainer c, bool even) =>
        c.Background(even ? Colors.White : Colors.Grey.Lighten5)
         .PaddingVertical(6).PaddingHorizontal(6)
         .BorderBottom(1).BorderColor(Colors.Grey.Lighten2)
         .AlignMiddle();

    internal class KpiCard : IComponent
    {
        private readonly int value;
        private readonly string label;
        private readonly Color accent;

        public KpiCard(int value, string label, Color accent)
        {
            this.value = value;
            this.label = label;
            this.accent = accent;
        }

        public void Compose(IContainer container)
        {
            container
                .Border(1).BorderColor(Colors.Grey.Lighten2)
                .Background(Colors.White)
                .Padding(12)
                .Row(row =>
                {
                    row.ConstantItem(4).Background(accent).Height(42);
                    row.Spacing(10);

                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(value.ToString())
                            .FontSize(18).SemiBold().FontColor(Colors.Black);

                        col.Item().Text(label)
                            .FontSize(9).FontColor(Colors.Grey.Darken1);
                    });
                });
        }
    }


    private ActionResult GenerateExcel(InventoryReportResponse data, ExportReportRequest request)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Отчёт");

        ws.Cell("A1").Value = "Отчёт по инвентаризации";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 16;

        ws.Range("A1:G1").Merge();
        ws.Range("A1:G1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        ws.Cell("A2").Value = "Период:";
        ws.Cell("B2").Value = $"{request.StartDate:dd.MM.yyyy} — {request.EndDate:dd.MM.yyyy}";
        ws.Cell("A3").Value = "Сотрудник:";
        ws.Cell("B3").Value = data.EmployeeName;

        ws.Range("A2:A3").Style.Font.Bold = true;
        ws.Range("A2:B3").Style.Font.FontColor = XLColor.FromArgb(55, 65, 81); 
        ws.Range("A2:B3").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        ws.Cell("D2").Value = "Всего";
        ws.Cell("E2").Value = data.TotalEquipment;

        ws.Cell("D3").Value = "В наличии";
        ws.Cell("E3").Value = data.InventoriedCount;

        ws.Cell("F3").Value = "Отсутствует";
        ws.Cell("G3").Value = data.MissingCount;

        ws.Range("D2:D3").Style.Font.Bold = true;
        ws.Range("F3:F3").Style.Font.Bold = true;

        ws.Range("E2:E2").Style.Font.Bold = true;
        ws.Range("E3:E3").Style.Font.Bold = true;
        ws.Range("G3:G3").Style.Font.Bold = true;

        ws.Range("D2:G3").Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range("D2:G3").Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        ws.Range("D2:G3").Style.Border.OutsideBorderColor = XLColor.FromArgb(229, 231, 235);
        ws.Range("D2:G3").Style.Border.InsideBorderColor = XLColor.FromArgb(229, 231, 235);
        ws.Range("D2:G3").Style.Fill.BackgroundColor = XLColor.FromArgb(249, 250, 251);

        ws.Cell("E3").Style.Font.FontColor = XLColor.DarkGreen;
        ws.Cell("G3").Style.Font.FontColor = XLColor.DarkRed;

        string[] headers = { "Инв. номер", "Наименование", "Категория", "Состояние", "Наличие", "Дата", "Локация" };

        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(5, i + 1);
            cell.Value = headers[i];
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(243, 244, 246); 
            cell.Style.Font.FontColor = XLColor.FromArgb(31, 41, 55);        
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorderColor = XLColor.FromArgb(209, 213, 219);
        }

        int row = 6;
        bool alternate = false;

        foreach (var item in data.Details)
        {
            if (alternate)
                ws.Row(row).Style.Fill.BackgroundColor = XLColor.FromArgb(249, 250, 251); 

            ws.Cell(row, 1).Value = item.InventoryNumber;
            ws.Cell(row, 2).Value = item.EquipmentName;
            ws.Cell(row, 3).Value = item.Category;
            ws.Cell(row, 4).Value = item.EquipmentCondition;

            var presence = ws.Cell(row, 5);
            presence.Value = item.IsPresent ? "Да" : "Нет";
            presence.Style.Font.Bold = true;
            presence.Style.Font.FontColor = item.IsPresent ? XLColor.DarkGreen : XLColor.DarkRed;

            ws.Cell(row, 6).Value = item.InventoryDate;
            ws.Cell(row, 6).Style.DateFormat.Format = "dd.MM.yyyy";

            ws.Cell(row, 7).Value = item.Location;

            ws.Range(row, 1, row, headers.Length).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            ws.Range(row, 1, row, headers.Length).Style.Border.BottomBorderColor = XLColor.FromArgb(229, 231, 235);

            alternate = !alternate;
            row++;
        }

        ws.Range(5, 1, row - 1, headers.Length).SetAutoFilter();
        ws.Columns().AdjustToContents();

        ws.SheetView.FreezeRows(5);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return File(stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"InventoryReport_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx");
    }


    private async Task<InventoryReportResponse> GetInventorySummaryAsync(int? employeeId, DateTime startDate, DateTime endDate)
    {
        var query = db.Inventoryrecords
            .Include(x => x.Equipment)
            .Include(x => x.Employee)
            .Where(x => x.InventoryDate >= startDate && x.InventoryDate <= endDate);

        if (employeeId.HasValue)
            query = query.Where(x => x.EmployeeId == employeeId.Value);

        var details = await query.Select(x => new InventoryReportDetail
        {
            InventoryNumber = x.Equipment.InventoryNumber,
            EquipmentName = x.Equipment.Name,
            Category = x.Equipment.Category,
            EquipmentCondition = x.EquipmentCondition,
            IsPresent = x.IsPresent,
            InventoryDate = x.InventoryDate,
            Location = x.Location
        }).ToListAsync();

        var employeeName = employeeId.HasValue
            ? (await db.Users.Where(u => u.Id == employeeId.Value)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync())
            : "Все";

        return new InventoryReportResponse
        {
            EmployeeName = employeeName,
            TotalEquipment = details.Count,
            InventoriedCount = details.Count(d => d.IsPresent),
            MissingCount = details.Count(d => !d.IsPresent),
            Details = details
        };
    }
}
