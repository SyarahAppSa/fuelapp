using System.ComponentModel.DataAnnotations;
using ClosedXML.Excel;
using FuelUp.Api.Security;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FuelUp.Api.Controllers;

public record PunchRequest([Range(-90, 90)] double? Lat, [Range(-180, 180)] double? Lng, [StringLength(300)] string? Note);
public record AttendanceCorrection(DateTimeOffset CheckInAt, DateTimeOffset? CheckOutAt, [Required, StringLength(500, MinimumLength = 3)] string Reason);

/// <summary>
/// Attendance (check-in / check-out). Every signed-in user records his own attendance; times come from the server clock.
/// Viewing others requires Attendance:View, corrections require Attendance:Edit and a written reason (kept in the audit log).
/// </summary>
[ApiController, Route("api/attendance")]
public class AttendanceController(FuelUpDb db, ActorAccessor actor, AuditWriter audit) : ControllerBase
{
    private static readonly TimeSpan Forgotten = TimeSpan.FromHours(16);   // an open period older than this = check-out forgotten

    [HttpGet("me")]
    public async Task<IActionResult> Mine()
    {
        var a = await actor.GetAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-31);
        var rows = await db.Attendance.Where(x => x.UserId == a.UserId && x.CheckInAt > since).OrderByDescending(x => x.CheckInAt).ToListAsync();
        var open = rows.FirstOrDefault(x => x.CheckOutAt == null);
        var today = Ksa.Day(DateTimeOffset.UtcNow);
        return Ok(new
        {
            open = open is null ? null : Row(open, null),
            todayMinutes = rows.Where(x => x.Day == today).Sum(Minutes),
            records = rows.Select(r => Row(r, null)),
        });
    }

    [HttpPost("check-in")]
    public async Task<IActionResult> CheckIn(PunchRequest r)
    {
        var a = await actor.GetAsync();
        if (await db.Attendance.AnyAsync(x => x.UserId == a.UserId && x.CheckOutAt == null))
            throw new DomainException("لديك حضور مسجّل لم يُغلق بعد. سجّل الانصراف أولًا.");
        var now = DateTimeOffset.UtcNow;
        var rec = new AttendanceRecord { UserId = a.UserId, Day = Ksa.Day(now), CheckInAt = now, InLat = r.Lat, InLng = r.Lng, InIp = ClientInfo.Ip(HttpContext), Note = r.Note?.Trim() };
        db.Attendance.Add(rec);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })   // two taps at the same instant
        { throw new DomainException("لديك حضور مسجّل لم يُغلق بعد. سجّل الانصراف أولًا."); }
        return Ok(Row(rec, null));
    }

    [HttpPost("check-out")]
    public async Task<IActionResult> CheckOut(PunchRequest r)
    {
        var a = await actor.GetAsync();
        var rec = await db.Attendance.FirstOrDefaultAsync(x => x.UserId == a.UserId && x.CheckOutAt == null)
                  ?? throw new DomainException("لا يوجد حضور مفتوح. سجّل الحضور أولًا.");
        rec.CheckOutAt = DateTimeOffset.UtcNow; rec.OutLat = r.Lat; rec.OutLng = r.Lng; rec.OutIp = ClientInfo.Ip(HttpContext);
        if (!string.IsNullOrWhiteSpace(r.Note)) rec.Note = string.IsNullOrEmpty(rec.Note) ? r.Note.Trim() : $"{rec.Note} — {r.Note.Trim()}";
        await db.SaveChangesAsync();
        return Ok(Row(rec, null));
    }

    /// <summary>Team attendance for a period (default: this month), with totals per person. format=xlsx downloads Excel.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? userId, [FromQuery] string? format)
    {
        var a = await actor.GetAsync(); a.Require(Section.Attendance, PermissionLevel.View);
        var today = Ksa.Day(DateTimeOffset.UtcNow);
        var f = from ?? new DateOnly(today.Year, today.Month, 1); var t = to ?? today;
        if (t < f || t.DayNumber - f.DayNumber > 366) throw new DomainException("الفترة غير صحيحة (سنة كحد أقصى).");
        var q = db.Attendance.Where(x => x.Day >= f && x.Day <= t);
        if (userId is { } u) q = q.Where(x => x.UserId == u);
        var rows = await q.OrderByDescending(x => x.CheckInAt).ToListAsync();
        var names = await db.Users.Select(x => new { x.Id, x.DisplayName, x.Username }).ToDictionaryAsync(x => x.Id);
        string Name(Guid id) => names.TryGetValue(id, out var n) ? n.DisplayName : "—";
        var summary = rows.GroupBy(x => x.UserId).Select(g => new
        {
            userId = g.Key, name = Name(g.Key), username = names.GetValueOrDefault(g.Key)?.Username,
            days = g.Select(x => x.Day).Distinct().Count(), minutes = g.Sum(Minutes),
            open = g.Count(x => x.CheckOutAt == null), forgotten = g.Count(IsForgotten),
        }).OrderBy(x => x.name).ToList();

        if (format != "xlsx") return Ok(new { from = f, to = t, summary, records = rows.Select(r => Row(r, Name(r.UserId))) });

        a.Require(Section.Export, PermissionLevel.Edit);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("الحضور"); ws.RightToLeft = true;
        string[] head = ["الموظف", "اسم المستخدم", "التاريخ", "الحضور", "الانصراف", "المدة (ساعة)", "الحالة", "ملاحظات"];
        for (var i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
        var rr = 2;
        foreach (var r in rows)
        {
            object?[] v = [Name(r.UserId), names.GetValueOrDefault(r.UserId)?.Username, r.Day.ToString("yyyy-MM-dd"), Ksa.Text(r.CheckInAt), Ksa.Text(r.CheckOutAt),
                Math.Round(Minutes(r) / 60m, 2), Status(r), (r.Corrected ? "مُعدّل من المشرف. " : "") + r.Note];
            for (var i = 0; i < v.Length; i++) ws.Cell(rr, i + 1).Value = v[i] is decimal d ? d : XLCellValue.FromObject(v[i]?.ToString() ?? "");
            rr++;
        }
        var s2 = wb.AddWorksheet("الإجمالي"); s2.RightToLeft = true;
        string[] h2 = ["الموظف", "أيام الحضور", "إجمالي الساعات", "فترات مفتوحة", "انصراف منسي"];
        for (var i = 0; i < h2.Length; i++) s2.Cell(1, i + 1).Value = h2[i];
        var r2 = 2;
        foreach (var s in summary) { s2.Cell(r2, 1).Value = s.name; s2.Cell(r2, 2).Value = s.days; s2.Cell(r2, 3).Value = Math.Round(s.minutes / 60m, 2); s2.Cell(r2, 4).Value = s.open; s2.Cell(r2, 5).Value = s.forgotten; r2++; }
        foreach (var w in new[] { ws, s2 }) { w.Row(1).Style.Font.Bold = true; w.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EBECFC"); w.Columns().AdjustToContents(); w.SheetView.FreezeRows(1); }
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"attendance-{f:yyyyMMdd}-{t:yyyyMMdd}.xlsx");
    }

    /// <summary>Manager correction (forgotten check-out, wrong time). The previous values stay in the audit log.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Correct(Guid id, AttendanceCorrection r)
    {
        var a = await actor.GetAsync(); a.Require(Section.Attendance, PermissionLevel.Edit);
        var rec = await db.Attendance.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("سجل الحضور");
        var now = DateTimeOffset.UtcNow;
        if (r.CheckInAt > now || r.CheckOutAt > now) throw new DomainException("لا يمكن تسجيل وقت في المستقبل.");
        if (r.CheckOutAt is { } o && o <= r.CheckInAt) throw new DomainException("وقت الانصراف يجب أن يكون بعد وقت الحضور.");
        if (r.CheckOutAt is { } o2 && o2 - r.CheckInAt > TimeSpan.FromHours(24)) throw new DomainException("مدة الفترة الواحدة لا تتجاوز 24 ساعة.");
        var before = $"{Ksa.Text(rec.CheckInAt)} → {Ksa.Text(rec.CheckOutAt)}";
        (rec.CheckInAt, rec.CheckOutAt, rec.Day, rec.Corrected) = (r.CheckInAt, r.CheckOutAt, Ksa.Day(r.CheckInAt), true);
        audit.Write(a, "attendance", rec.Id, "تعديل حضور", r.Reason.Trim(), before, $"{Ksa.Text(rec.CheckInAt)} → {Ksa.Text(rec.CheckOutAt)}");
        await db.SaveChangesAsync();
        return Ok(Row(rec, null));
    }

    private static int Minutes(AttendanceRecord r) => r.CheckOutAt is { } o ? (int)Math.Round((o - r.CheckInAt).TotalMinutes) : 0;
    private static bool IsForgotten(AttendanceRecord r) => r.CheckOutAt == null && DateTimeOffset.UtcNow - r.CheckInAt > Forgotten;
    private static string Status(AttendanceRecord r) => r.CheckOutAt != null ? "مكتمل" : IsForgotten(r) ? "انصراف منسي" : "حاضر الآن";

    private static object Row(AttendanceRecord r, string? name) => new
    {
        r.Id, r.UserId, name, day = r.Day, r.CheckInAt, r.CheckOutAt, minutes = Minutes(r), status = Status(r),
        forgotten = IsForgotten(r), r.Corrected, r.Note, hasLocation = r.InLat != null,
    };
}
