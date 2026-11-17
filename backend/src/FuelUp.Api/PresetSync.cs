using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api;

/// <summary>
/// When a release adds a new section (e.g. «تقارير التعبئة»), accounts created earlier get that section at their role's preset level,
/// once. Each company remembers which sections were already handed out (setting "preset_sections"), so a level an administrator
/// later removes is never put back. Custom-role accounts are left alone.
/// </summary>
public static class PresetSync
{
    private const string Key = "preset_sections";

    public static async Task RunAsync(FuelUpDb db, TenantContext tenant, ILogger log)
    {
        var all = Enum.GetValues<Section>().Select(s => s.ToString()).ToList();
        foreach (var company in await db.Companies.IgnoreQueryFilters().Select(c => c.Id).ToListAsync())
        {
            tenant.Set(company);
            var mark = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key);
            var known = mark?.Value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet()
                        ?? ["Ops", "Moves", "Approve", "Assign", "Meters", "Tracking", "Custody", "VehicleFile", "Costs", "Master", "Users", "Driver", "Export", "Attendance"];
            var fresh = Enum.GetValues<Section>().Where(s => !known.Contains(s.ToString())).ToList();
            if (fresh.Count > 0)
            {
                var users = await db.Users.Include(u => u.Permissions).Where(u => u.Role != Role.Custom).ToListAsync();
                var added = 0;
                foreach (var u in users)
                    foreach (var s in fresh)
                        if (Permissions.Presets.TryGetValue(u.Role, out var p) && p.TryGetValue(s, out var level) && u.Permissions.All(x => x.Section != s))
                        {
                            u.Permissions.Add(new UserPermission { UserId = u.Id, Section = s, Level = level });
                            added++;
                        }
                if (added > 0) log.LogWarning("Granted new sections {Sections} to {Count} existing permission(s) by role preset", string.Join(", ", fresh), added);
            }
            if (mark is null) db.Settings.Add(new Setting { Key = Key, Value = string.Join(',', all) });
            else mark.Value = string.Join(',', all);
            await db.SaveChangesAsync();
        }
        tenant.Clear();
    }
}
