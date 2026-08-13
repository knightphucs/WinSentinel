using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Data;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Api;

public static class EventEndpoints
{
    private const int DefaultTake = 50;
    private const int MaxTake = 500;

    public static void MapEventEndpoints(this WebApplication app)
    {
        app.MapGet("/api/events", GetEvents);
        app.MapGet("/api/events/{id:guid}", GetEventById);
    }

    /// <summary>
    /// GET /api/events?host=&amp;type=&amp;take=50 — mới nhất trước.
    /// </summary>
    private static async Task<IResult> GetEvents(
        MonitorDbContext db,
        string? host,
        string? type,
        string? risk,
        int? take,
        CancellationToken ct)
    {
        MonitoredObjectType? objectType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<MonitoredObjectType>(type, ignoreCase: true, out var parsed))
            {
                return Results.BadRequest(new
                {
                    error = $"Gia tri 'type' khong hop le: '{type}'.",
                    validValues = Enum.GetNames<MonitoredObjectType>()
                });
            }

            objectType = parsed;
        }

        RiskLevel? riskLevel = null;
        if (!string.IsNullOrWhiteSpace(risk))
        {
            if (!Enum.TryParse<RiskLevel>(risk, ignoreCase: true, out var parsedRisk))
            {
                return Results.BadRequest(new
                {
                    error = $"Gia tri 'risk' khong hop le: '{risk}'.",
                    validValues = Enum.GetNames<RiskLevel>()
                });
            }

            riskLevel = parsedRisk;
        }

        var limit = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var query = db.Events.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(host))
        {
            query = query.Where(e => e.Hostname == host);
        }

        if (objectType is not null)
        {
            query = query.Where(e => e.ObjectType == objectType);
        }

        if (riskLevel is not null)
        {
            query = query.Where(e => e.RiskLevel == riskLevel);
        }

        // Select truoc khi ToList de RawXml khong bi keo ve tu DB.
        // Dung chung Projection voi payload SignalR de hai ben khong lech nhau.
        var items = await query
            .OrderByDescending(e => e.TimeCreated)
            .ThenByDescending(e => e.RecordId)
            .Take(limit)
            .Select(EventSummaryDto.Projection)
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    private static async Task<IResult> GetEventById(
        MonitorDbContext db,
        Guid id,
        CancellationToken ct)
    {
        var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
        {
            return Results.NotFound(new { error = $"Khong tim thay event {id}." });
        }

        return Results.Ok(new EventDetailDto(
            e.Id, e.EventId, e.Hostname, e.TimeCreated, e.ObjectType, e.ObjectName,
            e.DisplayName, e.ActorAccount, e.ActorSid, e.ActionDescription, e.RiskLevel,
            e.Channel, e.ProviderName, e.RecordId, e.ImagePath, e.ServiceType, e.StartType,
            e.PreviousStartType, e.ServiceAccount, e.TaskActionType, e.TaskComHandlerClassId,
            e.TaskCommand, e.TaskArguments, e.TaskRunAsUser, e.TaskRunLevel,
            e.IsRecognized, e.Data, e.RawXml));
    }
}
