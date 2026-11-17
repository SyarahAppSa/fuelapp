using ClosedXML.Excel;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api.Controllers;

public record ReportTable(string Title, string[] Columns, List<object?[]> Rows);
public record ReportResult(string Key, string Title, string Subtitle, List<ReportTable> Tables);

/// <summary>
/// The 14 custody reports. Each answers as JSON for the dashboard (tables of rows) or as Excel with ?format=xlsx (one sheet per table, RTL).
/// PDF is the dashboard's print view. Filters: from/to (Saudi dates), vehicleId, holderKind+holderId, catalogId, category, warehouseId, partId/q, days.
/// </summary>
[ApiController, Route("api/custody/reports"), Authorize]
public class CustodyReportsController(FuelUpDb db, ActorAccessor actor) : ControllerBase
{
    public static readonly (string Key, string Title, string Hint)[] All =
    [
        ("custody", "العهد الحالية لكل موظف", "الأصناف التي بعهدة كل سائق أو موظف وتاريخ استلامها وحالتها"),
        ("installed", "القطع المركّبة على كل شاحنة", "اللوحة والقطع وأرقامها التسلسلية وتواريخ التركيب"),
        ("history", "سجل حياة القطعة", "من الشراء والصرف والتركيب والنقل والإصلاح حتى الإتلاف"),
        ("issues", "صرف القطع", "القطعة والكمية والشاحنة والمستلم وأمر الصيانة والمعتمد"),
        ("replacements", "الاستبدال", "القطعة القديمة والجديدة وأرقامهما وسبب الاستبدال ومصير القديمة"),
        ("transfers", "نقل العهد والقطع", "بين الموظفين والشاحنات والمستودعات مع التسليم والقبول"),
        ("inventory", "المخزون والجرد", "الرصيد المسجل والفعلي والفروقات ومواقع التخزين"),
        ("shortages", "النواقص وإعادة الطلب", "الأصناف عند الحد الأدنى أو أقل والكمية المقترح شراؤها"),
        ("damaged", "التالف والمفقود", "القطعة وآخر حامل أو موقع والسبب ونتيجة المراجعة"),
        ("warranty", "الضمان والمطالبات", "القطع تحت الضمان والتي ينتهي ضمانها قريبًا ومطالبات الموردين"),
        ("life", "عمر القطع وتكرار الأعطال", "مدة الاستخدام والكيلومترات والساعات قبل الاستبدال"),
        ("costs", "تكلفة صيانة الشاحنات", "تكلفة القطع والأجور لكل شاحنة خلال فترة مع تفاصيل العمليات"),
        ("repair", "القطع تحت الإصلاح", "لدى الورش: تاريخ الإرسال والتكلفة والعودة المتوقعة"),
        ("clearance", "إخلاء العهدة", "ما سلّمه الموظف أو السائق وما بقي عليه عند النقل أو انتهاء الخدمة"),
    ];

    [HttpGet]
    public async Task<IActionResult> List()
    {
        (await actor.GetAsync()).Require(Section.Custody, PermissionLevel.View);
        return Ok(All.Select((r, i) => new { no = i + 1, r.Key, r.Title, r.Hint }));
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? vehicleId, [FromQuery] string? holderKind, [FromQuery] Guid? holderId,
        [FromQuery] Guid? catalogId, [FromQuery] string? category, [FromQuery] Guid? warehouseId, [FromQuery] Guid? partId, [FromQuery] string? q, [FromQuery] int? days, [FromQuery] string? format)
    {
        var a = await actor.GetAsync(); a.Require(Section.Custody, PermissionLevel.View);
        var meta = All.FirstOrDefault(x => x.Key == key);
        if (meta.Key is null) throw new NotFoundException("التقرير");
        var cost = a.Can(Section.Costs) || a.Can(Section.Custody, PermissionLevel.Edit);
        if (key == "costs" && !cost) throw new ForbiddenException();
        var today = Ksa.Day(DateTimeOffset.UtcNow);
        var f = from ?? today.AddMonths(-3); var t = to ?? today;
        if (t < f || t.DayNumber - f.DayNumber > 3700) throw new DomainException("الفترة غير صحيحة.");
        var F = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).ToUniversalTime();
        var T = new DateTimeOffset(t.ToDateTime(TimeOnly.MaxValue), TimeSpan.FromHours(3)).ToUniversalTime();
        var L = await CustodyLookups.LoadAsync(db);
        var period = $"الفترة {f:yyyy-MM-dd} — {t:yyyy-MM-dd}";
        var catIds = catalogId is { } cc ? [cc] : !string.IsNullOrWhiteSpace(category) ? L.Catalog.Values.Where(c => c.Category == category).Select(c => c.Id).ToHashSet() : null;
        bool CatOk(Guid id) => catIds is null || catIds.Contains(id);
        Holder? holder = holderId is { } hid ? await new CustodyService(db).HolderAsync(holderKind, hid) : null;

        var tables = new List<ReportTable>();
        var sub = "";
        switch (key)
        {
            case "custody":
            {
                var parts = await db.Parts.Where(p => p.Status == PartStatus.Issued && (holder == null || (p.HolderKind == holder.Kind && p.HolderId == holder.Id))).ToListAsync();
                var qty = await db.HolderBalances.Where(x => x.Qty > 0 && (holder == null || (x.HolderKind == holder.Kind && x.HolderId == holder.Id))).ToListAsync();
                var rows = parts.Where(p => CatOk(p.CatalogId)).Select(p => new object?[] { p.HolderRef, CustodyText.Holder(p.HolderKind), L.Code(p.CatalogId), L.Item(p.CatalogId), p.InternalNo, p.Serial, 1m, D(p.HolderSince), p.Condition ?? "" })
                    .Concat(qty.Where(x => CatOk(x.CatalogId)).Select(x => new object?[] { x.HolderName, CustodyText.Holder(x.HolderKind), L.Code(x.CatalogId), L.Item(x.CatalogId), "", "", x.Qty, D(x.Since), "كمية" }))
                    .OrderBy(r => r[0]).ToList();
                tables.Add(new("العهد الحالية", ["الحامل", "النوع", "رمز الصنف", "الصنف", "رقم القطعة", "الرقم التسلسلي", "الكمية", "تاريخ الاستلام", "الحالة"], rows));
                var per = rows.GroupBy(r => (string?)r[0]).Select(g => new object?[] { g.Key, g.Count(), g.Sum(r => (decimal)r[6]!) }).ToList();
                tables.Add(new("الإجمالي لكل حامل", ["الحامل", "عدد البنود", "إجمالي الكميات"], per));
                sub = holder is null ? $"{per.Count} حامل عهدة" : holder.Name;
                break;
            }
            case "installed":
            {
                var parts = await db.Parts.Where(p => p.Status == PartStatus.Installed && (vehicleId == null || p.VehicleId == vehicleId)).ToListAsync();
                var km = new Dictionary<Guid, decimal?>();
                var svc = new CustodyService(db);
                foreach (var v in parts.Select(p => p.VehicleId!.Value).Distinct()) km[v] = await svc.LastKmAsync(v);
                tables.Add(new("القطع المركّبة", ["اللوحة", "الفئة", "الصنف", "رقم القطعة", "الرقم التسلسلي", "الماركة/الموديل", "الموضع", "تاريخ التركيب", "عداد التركيب", "كم منذ التركيب", "نهاية الضمان"],
                    parts.Where(p => CatOk(p.CatalogId)).OrderBy(p => L.Plate(p.VehicleId)).ThenBy(p => L.Catalog.GetValueOrDefault(p.CatalogId)?.Category).ThenBy(p => p.Position)
                        .Select(p => new object?[] { L.Plate(p.VehicleId), L.Catalog.GetValueOrDefault(p.CatalogId)?.Category, L.Item(p.CatalogId), p.InternalNo, p.Serial, $"{p.Brand} {p.Model}".Trim(), p.Position,
                            D(p.InstalledAt), p.InstallKm, km[p.VehicleId!.Value] is { } lk && p.InstallKm is { } ik ? lk - ik : null, p.WarrantyEnd?.ToString("yyyy-MM-dd") }).ToList()));
                sub = $"{parts.Select(p => p.VehicleId).Distinct().Count()} شاحنة · {tables[0].Rows.Count} قطعة";
                break;
            }
            case "history":
            {
                Part? p = partId is { } pid ? await db.Parts.FirstOrDefaultAsync(x => x.Id == pid) : null;
                if (p is null && !string.IsNullOrWhiteSpace(q))
                {
                    var up = q.Trim().ToUpperInvariant().Replace(" ", "");
                    p = await db.Parts.FirstOrDefaultAsync(x => x.InternalNo == up || x.Serial == up);
                }
                if (p is null) throw new DomainException("حدد القطعة: الرقم الداخلي أو الرقم التسلسلي.");
                var moves = await db.PartMovements.Where(m => m.PartId == p.Id).OrderBy(m => m.At).ToListAsync();
                var nos = await db.Parts.Where(x => moves.Select(m => m.RelatedPartId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.InternalNo);
                tables.Add(new($"سجل القطعة {p.InternalNo}", ["التاريخ", "الحركة", "من", "إلى", "الشاحنة", "أمر الصيانة", "السبب / المصير", "القطعة المقابلة", "العداد (كم)", "الساعات", "التكلفة", "بواسطة", "المعتمد"],
                    moves.Select(m => new object?[] { D(m.At), CustodyText.Move(m.Type), L.From(m), L.To(m), L.Plate(m.VehicleId ?? m.FromVehicleId), L.Wo(m.WorkOrderId),
                        Join(m.Reason, m.OldPartFate is { } fa ? "المصير: " + CustodyText.Fates.GetValueOrDefault(fa, fa) : null, m.Note), m.RelatedPartId is { } r ? nos.GetValueOrDefault(r) : null, m.Km, m.Hours,
                        cost ? m.Cost : null, L.User(m.ByUserId), L.User(m.ApprovedBy) }).ToList()));
                var life = CustodyController.LifePeriods(moves, p, L);
                tables.Add(new("فترات التركيب", ["الشاحنة", "الموضع", "من", "إلى", "الأيام", "كم", "ساعات", "سبب الفك"], life.Select(x => new object?[] { x.Plate, x.Position, D(x.From), D(x.To), x.Days, x.Km, x.Hours, x.EndReason }).ToList()));
                sub = $"{p.InternalNo} · {L.Item(p.CatalogId)}{(p.Serial is null ? "" : " · " + p.Serial)} · الحالة الآن: {CustodyText.Status[p.Status]} ({L.Location(p)})";
                break;
            }
            case "issues":
            {
                var moves = await db.PartMovements.Where(m => m.At >= F && m.At <= T && (m.Type == "issue" || m.Type == "stock-issue" || ((m.Type == "install" || m.Type == "replace-in") && m.FromWarehouseId != null))).OrderBy(m => m.At).ToListAsync();
                var nos = await Nos(moves.Select(m => m.PartId));
                var rows = moves.Where(m => CatOk(m.CatalogId) && (vehicleId == null || m.VehicleId == vehicleId) && (holder == null || (m.HolderKind == holder.Kind && m.HolderId == holder.Id)))
                    .Select(m => new object?[] { D(m.At), CustodyText.Move(m.Type), L.Code(m.CatalogId), L.Item(m.CatalogId), m.PartId is { } pid ? nos.GetValueOrDefault(pid) : "", m.Qty, L.Plate(m.VehicleId), m.HolderRef,
                        L.Wo(m.WorkOrderId), m.Reason, L.User(m.ApprovedBy ?? m.ByUserId), L.User(m.ByUserId), cost ? m.Cost : null }).ToList();
                tables.Add(new("صرف القطع", ["التاريخ", "النوع", "رمز الصنف", "الصنف", "رقم القطعة", "الكمية", "الشاحنة", "المستلم", "أمر الصيانة", "سبب الصرف", "المعتمد", "سجّلها", "التكلفة"], rows));
                sub = $"{period} · {rows.Count} عملية صرف";
                break;
            }
            case "replacements":
            {
                var moves = await db.PartMovements.Where(m => m.At >= F && m.At <= T && (m.Type == "replace-out" || m.Type == "remove") && (vehicleId == null || m.FromVehicleId == vehicleId)).OrderBy(m => m.At).ToListAsync();
                var nos = await db.Parts.Where(x => moves.Select(m => m.PartId).Contains(x.Id) || moves.Select(m => m.RelatedPartId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => (x.InternalNo, x.Serial));
                string No(Guid? id) => id is { } x && nos.TryGetValue(x, out var n) ? n.InternalNo : "";
                string Sn(Guid? id) => id is { } x && nos.TryGetValue(x, out var n) ? n.Serial ?? "" : "";
                var rows = moves.Where(m => CatOk(m.CatalogId)).Select(m => new object?[] { D(m.At), L.Plate(m.FromVehicleId), m.Position, L.Item(m.CatalogId), No(m.PartId), Sn(m.PartId),
                    m.Type == "replace-out" ? No(m.RelatedPartId) : "(فك بدون بديل)", Sn(m.RelatedPartId), m.Reason, m.OldPartFate is { } fa ? CustodyText.Fates.GetValueOrDefault(fa, fa) : "", m.Km, m.Hours, L.Wo(m.WorkOrderId), L.User(m.ByUserId) }).ToList();
                tables.Add(new("الاستبدال", ["التاريخ", "الشاحنة", "الموضع", "الصنف", "القطعة القديمة", "تسلسلي القديمة", "القطعة الجديدة", "تسلسلي الجديدة", "سبب الاستبدال", "مصير القديمة", "العداد (كم)", "الساعات", "أمر الصيانة", "بواسطة"], rows));
                sub = $"{period} · {rows.Count} عملية";
                break;
            }
            case "transfers":
            {
                var list = await db.CustodyTransfers.Where(x => x.RequestedAt >= F && x.RequestedAt <= T).OrderBy(x => x.RequestedAt).ToListAsync();
                var nos = await Nos(list.Select(x => x.PartId));
                var rows = list.Where(x => CatOk(x.CatalogId) && (holder == null || (x.FromKind == holder.Kind && x.FromId == holder.Id) || (x.ToKind == holder.Kind && x.ToId == holder.Id))
                        && (vehicleId == null || (x.FromKind == "vehicle" && x.FromId == vehicleId) || (x.ToKind == "vehicle" && x.ToId == vehicleId)))
                    .Select(x => new object?[] { x.No, D(x.RequestedAt), x.Kind switch { "issue" => "صرف عهدة", "qty-issue" => "صرف كمية", "qty-transfer" => "نقل كمية", _ => "نقل عهدة" }, L.Item(x.CatalogId),
                        x.PartId is { } pid ? nos.GetValueOrDefault(pid) : "", x.Qty, $"{CustodyText.Holder(x.FromKind)}: {x.FromName}", $"{CustodyText.Holder(x.ToKind)}: {x.ToName}", x.HandoverBy ?? L.User(x.RequestedBy),
                        x.Status switch { "Pending" => "بانتظار القبول", "Accepted" => "مقبول", "Rejected" => "مرفوض", _ => x.Status }, L.User(x.DecidedBy), D(x.DecidedAt), Join(x.HandoverNote, x.DecisionNote) }).ToList();
                tables.Add(new("نقل العهد", ["رقم التسليم", "التاريخ", "النوع", "الصنف", "رقم القطعة", "الكمية", "من (المسلِّم)", "إلى (المستلم)", "سلّمها", "الحالة", "قبِل/رفض", "وقت القبول", "ملاحظات"], rows));
                sub = $"{period} · {rows.Count} تسليم، منها {rows.Count(r => (string?)r[9] == "بانتظار القبول")} بانتظار القبول";
                break;
            }
            case "inventory":
            {
                var bal = await db.StockBalances.Where(x => warehouseId == null || x.WarehouseId == warehouseId).ToListAsync();
                var counts = await db.StockCounts.Where(x => warehouseId == null || x.WarehouseId == warehouseId).OrderByDescending(x => x.At).ToListAsync();
                var lastLine = new Dictionary<(Guid, Guid), (CountLine Line, StockCount Count)>();
                foreach (var c in counts) foreach (var l in c.Lines) lastLine.TryAdd((c.WarehouseId, l.CatalogId), (l, c));
                tables.Add(new("أصناف الكمية", ["المستودع", "رمز الصنف", "الصنف", "موقع التخزين", "الرصيد المسجل", "آخر جرد (الفعلي)", "الفرق في الجرد", "تاريخ الجرد", "رقم الجرد", "الحد الأدنى"],
                    bal.Where(x => CatOk(x.CatalogId)).OrderBy(x => L.Wh(x.WarehouseId)).ThenBy(x => L.Item(x.CatalogId)).Select(x =>
                    {
                        var has = lastLine.TryGetValue((x.WarehouseId, x.CatalogId), out var ll);
                        return new object?[] { L.Wh(x.WarehouseId), L.Code(x.CatalogId), L.Item(x.CatalogId), x.Bin ?? (has ? ll.Line.Bin : null), x.Qty, has ? ll.Line.Actual : null,
                            has ? ll.Line.Actual - ll.Line.Recorded : null, has ? D(ll.Count.At) : null, has ? ll.Count.No : null, L.Catalog.GetValueOrDefault(x.CatalogId)?.MinQty };
                    }).ToList()));
                var parts = await db.Parts.Where(p => p.Status == PartStatus.Stock && (warehouseId == null || p.WarehouseId == warehouseId)).ToListAsync();
                tables.Add(new("القطع الفردية في المستودعات", ["المستودع", "رمز الصنف", "الصنف", "العدد", "مواقع التخزين", "أرقام القطع"],
                    parts.Where(p => CatOk(p.CatalogId)).GroupBy(p => (p.WarehouseId, p.CatalogId)).OrderBy(g => L.Wh(g.Key.WarehouseId)).Select(g => new object?[] { L.Wh(g.Key.WarehouseId), L.Code(g.Key.CatalogId), L.Item(g.Key.CatalogId),
                        g.Count(), string.Join("، ", g.Select(p => p.Bin).Where(b => b != null).Distinct()), string.Join("، ", g.Select(p => p.InternalNo)) }).ToList()));
                tables.Add(new("عمليات الجرد", ["رقم الجرد", "التاريخ", "المستودع", "أصناف بفروقات", "قطع غير موجودة", "قطع غير متوقعة", "بواسطة", "ملاحظات"],
                    counts.Select(c => new object?[] { c.No, D(c.At), L.Wh(c.WarehouseId), c.Lines.Count(l => l.Actual != l.Recorded), string.Join("، ", c.MissingParts), string.Join("، ", c.UnexpectedParts), L.User(c.ByUserId), c.Note }).ToList()));
                sub = warehouseId is { } w ? L.Wh(w) : "كل المستودعات";
                break;
            }
            case "shortages":
            {
                var stock = await db.StockBalances.GroupBy(x => x.CatalogId).Select(g => new { g.Key, q = g.Sum(x => x.Qty) }).ToDictionaryAsync(x => x.Key, x => x.q);
                var parts = await db.Parts.Where(p => p.Status == PartStatus.Stock).GroupBy(p => p.CatalogId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => (decimal)x.n);
                var rows = L.Catalog.Values.Where(c => c.IsActive && c.MinQty is not null && CatOk(c.Id)).Select(c =>
                {
                    var have = c.Tracking == PartTracking.Quantity ? stock.GetValueOrDefault(c.Id) : parts.GetValueOrDefault(c.Id);
                    return (c, have);
                }).Where(x => x.have <= x.c.MinQty!.Value).OrderBy(x => x.have - x.c.MinQty!.Value)
                    .Select(x => new object?[] { x.c.Code, x.c.Name, x.c.Category, x.c.Tracking == PartTracking.Quantity ? "كمية" : "فردي", x.c.Unit, x.have, x.c.MinQty, x.c.ReorderQty,
                        Math.Max(x.c.ReorderQty ?? x.c.MinQty!.Value, x.c.MinQty!.Value - x.have), cost ? x.c.UnitCost : null,
                        cost && x.c.UnitCost is { } u ? u * Math.Max(x.c.ReorderQty ?? x.c.MinQty!.Value, x.c.MinQty!.Value - x.have) : null }).ToList();
                tables.Add(new("النواقص", ["رمز الصنف", "الصنف", "الفئة", "التتبع", "الوحدة", "المتوفر", "الحد الأدنى", "كمية إعادة الطلب", "المقترح شراؤه", "سعر الوحدة", "التكلفة التقديرية"], rows));
                sub = $"{rows.Count} صنف عند الحد الأدنى أو أقل";
                break;
            }
            case "damaged":
            {
                var parts = await db.Parts.Where(p => p.Status == PartStatus.Damaged || p.Status == PartStatus.Lost || p.Status == PartStatus.Scrapped).ToListAsync();
                var ids = parts.Select(p => p.Id).ToList();
                var lastMove = (await db.PartMovements.Where(m => m.PartId != null && ids.Contains(m.PartId.Value) && (m.Type == "damaged" || m.Type == "lost" || m.Type == "scrap" || m.Type == "repair-return" || m.Type == "claim-result"))
                    .ToListAsync()).GroupBy(m => m.PartId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.At).First());
                var rows = parts.Where(p => CatOk(p.CatalogId)).Select(p => (p, m: lastMove.GetValueOrDefault(p.Id))).Where(x => x.m is null || (x.m.At >= F && x.m.At <= T) || x.p.Status != PartStatus.Scrapped)
                    .OrderByDescending(x => x.m?.At).Select(x => new object?[] { x.p.InternalNo, L.Item(x.p.CatalogId), x.p.Serial, CustodyText.Status[x.p.Status], D(x.m?.At), x.p.LastLocation, x.m?.FromHolderRef ?? "",
                        x.p.StatusReason, x.p.ReviewOutcome ?? "بانتظار المراجعة", x.m?.DocRef, L.User(x.p.ScrapApprovedBy), cost ? x.p.Cost : null }).ToList();
                tables.Add(new("التالف والمفقود", ["رقم القطعة", "الصنف", "الرقم التسلسلي", "الحالة", "التاريخ", "آخر موقع/حامل", "آخر حامل", "السبب", "نتيجة المراجعة", "مرجع الاعتماد", "معتمد الإتلاف", "التكلفة"], rows));
                sub = $"تالف {parts.Count(p => p.Status == PartStatus.Damaged)} · مفقود {parts.Count(p => p.Status == PartStatus.Lost)} · متلف {parts.Count(p => p.Status == PartStatus.Scrapped)}";
                break;
            }
            case "warranty":
            {
                var soon = today.AddDays(days ?? 30);
                var parts = await db.Parts.Where(p => p.WarrantyEnd != null && p.Status != PartStatus.Scrapped && p.Status != PartStatus.Lost).ToListAsync();
                var active = parts.Where(p => p.WarrantyEnd >= today && CatOk(p.CatalogId)).OrderBy(p => p.WarrantyEnd).ToList();
                tables.Add(new("القطع تحت الضمان", ["رقم القطعة", "الصنف", "الرقم التسلسلي", "المورد", "الفاتورة", "بداية الضمان", "نهاية الضمان", "الأيام المتبقية", "الحالة", "شروط الضمان", "الموقع الحالي"],
                    active.Select(p => new object?[] { p.InternalNo, L.Item(p.CatalogId), p.Serial, p.Supplier, p.InvoiceNo, p.WarrantyStart?.ToString("yyyy-MM-dd"), p.WarrantyEnd!.Value.ToString("yyyy-MM-dd"),
                        p.WarrantyEnd.Value.DayNumber - today.DayNumber, p.WarrantyEnd <= soon ? "ينتهي قريبًا" : "ساري", p.WarrantyTerms, L.Location(p) }).ToList()));
                var claims = await db.Parts.Where(p => p.ClaimRef != null).OrderByDescending(p => p.ClaimOpenedAt).ToListAsync();
                tables.Add(new("مطالبات الضمان لدى الموردين", ["رقم المطالبة", "تاريخ الرفع", "رقم القطعة", "الصنف", "الرقم التسلسلي", "المورد", "نهاية الضمان", "الحالة", "النتيجة"],
                    claims.Where(p => CatOk(p.CatalogId)).Select(p => new object?[] { p.ClaimRef, D(p.ClaimOpenedAt), p.InternalNo, L.Item(p.CatalogId), p.Serial, p.Supplier, p.WarrantyEnd?.ToString("yyyy-MM-dd"),
                        p.Status == PartStatus.ReturnedToSupplier ? "مفتوحة لدى المورد" : "مغلقة", p.ClaimResult }).ToList()));
                sub = $"تحت الضمان {active.Count} · ينتهي خلال {(days ?? 30)} يومًا {active.Count(p => p.WarrantyEnd <= soon)} · مطالبات مفتوحة {claims.Count(p => p.Status == PartStatus.ReturnedToSupplier)}";
                break;
            }
            case "life":
            {
                var moves = await db.PartMovements.Where(m => m.PartId != null && (m.Type == "install" || m.Type == "replace-in" || m.Type == "remove" || m.Type == "replace-out" || m.Type == "damaged" || m.Type == "lost"))
                    .OrderBy(m => m.At).ToListAsync();
                var parts = await db.Parts.Where(p => moves.Select(m => m.PartId).Contains(p.Id)).ToDictionaryAsync(p => p.Id);
                var periods = moves.Where(m => CatOk(m.CatalogId)).GroupBy(m => m.PartId!.Value).SelectMany(g => CustodyController.LifePeriods(g.ToList(), parts[g.Key], L).Select(x => (Part: parts[g.Key], x)))
                    .Where(x => vehicleId == null || L.Plate(vehicleId) == x.x.Plate).ToList();
                var fates = moves.Where(m => m.Type is "remove" or "replace-out").ToLookup(m => m.PartId!.Value);
                tables.Add(new("فترات الاستخدام", ["رقم القطعة", "الصنف", "الرقم التسلسلي", "الشاحنة", "الموضع", "من", "إلى", "الأيام", "كم", "ساعات التشغيل", "سبب الفك"],
                    periods.Select(x => new object?[] { x.Part.InternalNo, L.Item(x.Part.CatalogId), x.Part.Serial, x.x.Plate, x.x.Position, D(x.x.From), x.x.To is null ? "ما زالت مركّبة" : D(x.x.To), x.x.Days, x.x.Km, x.x.Hours, x.x.EndReason }).ToList()));
                tables.Add(new("ملخص لكل صنف", ["الصنف", "الفئة", "فترات منتهية", "متوسط الأيام", "متوسط كم", "متوسط الساعات", "أعطال (إصلاح/ضمان/إتلاف/تالف)", "معدل الأعطال لكل 100 ألف كم"],
                    periods.Where(x => x.x.To != null).GroupBy(x => x.Part.CatalogId).Select(g =>
                    {
                        var kms = g.Where(x => x.x.Km != null).Select(x => x.x.Km!.Value).ToList();
                        var hrs = g.Where(x => x.x.Hours != null).Select(x => x.x.Hours!.Value).ToList();
                        var failures = g.Select(x => x.Part.Id).Distinct().Sum(pid => fates[pid].Count(m => m.OldPartFate is "repair" or "claim" or "scrap")) + g.Count(x => x.x.EndReason != null && moves.Any(m => m.PartId == x.Part.Id && m.Type == "damaged"));
                        return new object?[] { L.Item(g.Key), L.Catalog.GetValueOrDefault(g.Key)?.Category, g.Count(), Math.Round(g.Average(x => (decimal)x.x.Days), 1), kms.Count > 0 ? Math.Round(kms.Average(), 0) : null,
                            hrs.Count > 0 ? Math.Round(hrs.Average(), 0) : null, failures, kms.Sum() > 0 ? Math.Round(failures * 100000m / kms.Sum(), 2) : null };
                    }).ToList()));
                sub = $"{periods.Count} فترة تركيب";
                break;
            }
            case "costs":
            {
                var wos = await db.WorkOrders.Where(w => w.OpenedAt >= F && w.OpenedAt <= T && (vehicleId == null || w.VehicleId == vehicleId)).ToListAsync();
                var woVeh = await db.WorkOrders.ToDictionaryAsync(w => w.Id, w => w.VehicleId);
                var moves = await db.PartMovements.Where(m => m.At >= F && m.At <= T && m.Cost != null && CustodyController.CostTypes.Contains(m.Type)).ToListAsync();
                Guid? Veh(PartMovement m) => m.VehicleId ?? (m.WorkOrderId is { } w && woVeh.TryGetValue(w, out var v) ? v : null);
                var ops = moves.Where(m => Veh(m) is { } v && (vehicleId == null || v == vehicleId)).ToList();
                var nos = await Nos(ops.Select(m => m.PartId));
                var trucks = ops.Select(m => Veh(m)!.Value).Concat(wos.Select(w => w.VehicleId)).Distinct().ToList();
                tables.Add(new("الإجمالي لكل شاحنة", ["اللوحة", "أوامر الصيانة", "عمليات القطع", "تكلفة القطع والمواد", "الأجور", "الإجمالي"],
                    trucks.Select(v =>
                    {
                        var p = ops.Where(m => Veh(m) == v).Sum(m => m.Cost ?? 0); var l = wos.Where(w => w.VehicleId == v).Sum(w => w.LaborCost ?? 0);
                        return new object?[] { L.Plate(v), wos.Count(w => w.VehicleId == v), ops.Count(m => Veh(m) == v), p, l, p + l };
                    }).OrderByDescending(r => (decimal)r[5]!).ToList()));
                var detail = ops.Select(m => new object?[] { D(m.At), L.Plate(Veh(m)), L.Wo(m.WorkOrderId), CustodyText.Move(m.Type), L.Item(m.CatalogId), m.PartId is { } pid ? nos.GetValueOrDefault(pid) : "", m.Qty, m.Cost, m.Vendor ?? m.Reason })
                    .Concat(wos.Where(w => w.LaborCost is > 0).Select(w => new object?[] { D(w.OpenedAt), L.Plate(w.VehicleId), w.No, "أجور: " + w.Type, w.Description, "", null, w.LaborCost, w.Workshop }))
                    .OrderBy(r => (string?)r[0]).ToList();
                tables.Add(new("تفاصيل العمليات", ["التاريخ", "اللوحة", "أمر الصيانة", "العملية", "الصنف / الوصف", "رقم القطعة", "الكمية", "التكلفة", "الورشة / المورد / السبب"], detail));
                sub = $"{period} · الإجمالي {CustodyText.N(ops.Sum(m => m.Cost ?? 0) + wos.Sum(w => w.LaborCost ?? 0))} ريال";
                break;
            }
            case "repair":
            {
                var parts = await db.Parts.Where(p => p.Status == PartStatus.Repair).OrderBy(p => p.RepairSentAt).ToListAsync();
                var ids = parts.Select(p => p.Id).ToList();
                var sends = (await db.PartMovements.Where(m => m.Type == "repair-send" && m.PartId != null && ids.Contains(m.PartId.Value)).ToListAsync())
                    .GroupBy(m => m.PartId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.At).First());
                tables.Add(new("تحت الإصلاح", ["رقم القطعة", "الصنف", "الرقم التسلسلي", "الورشة", "تاريخ الإرسال", "العودة المتوقعة", "أيام لدى الورشة", "متأخرة", "التكلفة التقديرية", "السبب", "من الشاحنة", "أمر الصيانة"],
                    parts.Where(p => CatOk(p.CatalogId)).Select(p =>
                    {
                        var s = sends.GetValueOrDefault(p.Id);
                        return new object?[] { p.InternalNo, L.Item(p.CatalogId), p.Serial, p.RepairVendor, D(p.RepairSentAt), p.RepairExpectedAt?.ToString("yyyy-MM-dd"), p.RepairSentAt is { } rs ? (int)(DateTimeOffset.UtcNow - rs).TotalDays : null,
                            p.RepairExpectedAt is { } e && e < today ? "نعم" : "", cost ? p.RepairCost : null, p.StatusReason ?? s?.Reason, L.Plate(s?.FromVehicleId), L.Wo(s?.WorkOrderId) };
                    }).ToList()));
                var back = await db.PartMovements.Where(m => m.Type == "repair-return" && m.At >= F && m.At <= T).OrderByDescending(m => m.At).ToListAsync();
                var nos = await Nos(back.Select(m => m.PartId));
                tables.Add(new("عادت من الإصلاح خلال الفترة", ["التاريخ", "رقم القطعة", "الصنف", "الورشة", "النتيجة", "التكلفة الفعلية"],
                    back.Where(m => CatOk(m.CatalogId)).Select(m => new object?[] { D(m.At), m.PartId is { } pid ? nos.GetValueOrDefault(pid) : "", L.Item(m.CatalogId), m.Vendor, m.Note, cost ? m.Cost : null }).ToList()));
                sub = $"{parts.Count} قطعة لدى الورش · متأخرة {parts.Count(p => p.RepairExpectedAt < today)}";
                break;
            }
            case "clearance":
            {
                if (holder is null)
                {
                    var parts = await db.Parts.Where(p => p.Status == PartStatus.Issued).ToListAsync();
                    var qty = await db.HolderBalances.Where(x => x.Qty > 0).ToListAsync();
                    var returns = await db.PartMovements.Where(m => (m.Type == "return" || m.Type == "stock-return") && m.FromHolderId != null).GroupBy(m => m.FromHolderId!.Value).Select(g => new { g.Key, at = g.Max(m => m.At) }).ToDictionaryAsync(x => x.Key, x => x.at);
                    var people = parts.Select(p => (p.HolderKind!, p.HolderId!.Value, p.HolderRef ?? "")).Concat(qty.Select(x => (x.HolderKind, x.HolderId, x.HolderName))).Distinct().ToList();
                    tables.Add(new("ما بقي على كل حامل", ["الحامل", "النوع", "قطع بعهدته", "أصناف كمية", "آخر إرجاع"],
                        people.Select(x => new object?[] { x.Item3, CustodyText.Holder(x.Item1), parts.Count(p => p.HolderId == x.Item2), qty.Count(q2 => q2.HolderId == x.Item2), returns.TryGetValue(x.Item2, out var r) ? D(r) : "" }).OrderBy(r => r[0]).ToList()));
                    sub = "اختر موظفًا أو سائقًا لعرض بيان إخلاء عهدته";
                    break;
                }
                var ret = await db.PartMovements.Where(m => (m.Type == "return" || m.Type == "stock-return" || m.Type == "transfer") && m.FromHolderKind == holder.Kind && m.FromHolderId == holder.Id && m.At >= F && m.At <= T).OrderBy(m => m.At).ToListAsync();
                var nos = await Nos(ret.Select(m => m.PartId));
                tables.Add(new("ما سلّمه", ["التاريخ", "العملية", "رقم القطعة", "الصنف", "الكمية", "سُلّم إلى", "ملاحظات"],
                    ret.Select(m => new object?[] { D(m.At), m.Reason == "إخلاء عهدة" ? "إخلاء عهدة" : CustodyText.Move(m.Type), m.PartId is { } pid ? nos.GetValueOrDefault(pid) : "", L.Item(m.CatalogId), m.Qty, L.To(m), m.Note }).ToList()));
                var still = await db.Parts.Where(p => p.Status == PartStatus.Issued && p.HolderKind == holder.Kind && p.HolderId == holder.Id).ToListAsync();
                var stillQ = await db.HolderBalances.Where(x => x.HolderKind == holder.Kind && x.HolderId == holder.Id && x.Qty > 0).ToListAsync();
                var pend = await db.CustodyTransfers.Where(x => x.Status == "Pending" && ((x.ToKind == holder.Kind && x.ToId == holder.Id) || (x.FromKind == holder.Kind && x.FromId == holder.Id))).ToListAsync();
                tables.Add(new("ما بقي بعهدته", ["رقم القطعة", "الصنف", "الرقم التسلسلي", "الكمية", "تاريخ الاستلام", "الحالة", "التكلفة"],
                    still.Select(p => new object?[] { p.InternalNo, L.Item(p.CatalogId), p.Serial, 1m, D(p.HolderSince), p.Condition, cost ? p.Cost : null })
                        .Concat(stillQ.Select(x => new object?[] { "", L.Item(x.CatalogId), "", x.Qty, D(x.Since), "كمية", cost && L.Catalog.GetValueOrDefault(x.CatalogId)?.UnitCost is { } u ? u * x.Qty : null })).ToList()));
                tables.Add(new("تسليمات معلّقة", ["رقم التسليم", "الصنف", "الكمية", "من", "إلى", "التاريخ"],
                    pend.Select(x => new object?[] { x.No, L.Item(x.CatalogId), x.Qty, x.FromName, x.ToName, D(x.RequestedAt) }).ToList()));
                var clear = still.Count == 0 && stillQ.Count == 0 && pend.Count == 0;
                sub = $"{holder.Name} · {(clear ? "لا شيء متبقٍ عليه: العهدة مُخلاة" : $"متبقٍ: {still.Count} قطعة و{stillQ.Count} صنف كمية")}";
                break;
            }
        }

        var result = new ReportResult(key, meta.Title, sub, tables);
        if (format != "xlsx") return Ok(new { result.Key, result.Title, result.Subtitle, tables = result.Tables.Select(x => new { x.Title, x.Columns, x.Rows }), generatedAt = DateTimeOffset.UtcNow });
        a.Require(Section.Export, PermissionLevel.Edit);
        return File(Excel(result), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"custody-{key}-{today:yyyyMMdd}.xlsx");
    }

    private async Task<Dictionary<Guid, string>> Nos(IEnumerable<Guid?> ids)
    {
        var l = ids.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        return await db.Parts.Where(p => l.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.InternalNo);
    }

    private static string D(DateTimeOffset? t) => Ksa.Text(t);
    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public static byte[] Excel(ReportResult r)
    {
        using var wb = new XLWorkbook();
        var used = new HashSet<string>();
        foreach (var t in r.Tables)
        {
            var name = new string(t.Title.Where(ch => !"[]:*?/\\".Contains(ch)).Take(28).ToArray());
            while (!used.Add(name)) name += "_";
            var ws = wb.AddWorksheet(name); ws.RightToLeft = true;
            ws.Cell(1, 1).Value = r.Title; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(2, 1).Value = r.Subtitle;
            ws.Cell(3, 1).Value = t.Title; ws.Cell(3, 1).Style.Font.Bold = true;
            for (var i = 0; i < t.Columns.Length; i++) ws.Cell(4, i + 1).Value = t.Columns[i];
            var row = 5;
            foreach (var v in t.Rows)
            {
                for (var i = 0; i < v.Length; i++)
                    ws.Cell(row, i + 1).Value = v[i] switch { null => Blank.Value, decimal d => d, int n => n, double x => x, var o => o.ToString() ?? "" };
                row++;
            }
            ws.Row(4).Style.Font.Bold = true; ws.Row(4).Style.Fill.BackgroundColor = XLColor.FromHtml("#EBECFC");
            ws.Columns().AdjustToContents(); ws.SheetView.FreezeRows(4);
        }
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        return ms.ToArray();
    }
}
