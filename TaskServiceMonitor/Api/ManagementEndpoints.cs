using System.ComponentModel;
using TaskServiceMonitor.Management;

namespace TaskServiceMonitor.Api;

public sealed record CreateTaskRequest(string Name, string Command, string? Arguments, string? StartBoundary);
public sealed record CreateServiceRequest(string Name, string BinaryPath, string? StartType, string? DisplayName);
public sealed record ChangeStartTypeRequest(string StartType);

public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system/status", (SafeNameGuard guard) => Results.Ok(new
        {
            isElevated = ElevationInfo.IsElevated(),
            currentUser = ElevationInfo.CurrentUserName(),
            writablePrefix = guard.WritablePrefix
        }));

        // ------------------------------------------------------------ Tasks
        app.MapGet("/api/tasks", (TaskManager tasks) => Run(() => Results.Ok(tasks.List())));

        app.MapGet("/api/tasks/xml", (TaskManager tasks, string path) =>
            Run(() => Results.Text(tasks.GetXml(path), "application/xml")));

        // Ten chua co -> tao moi (4698). Ten da co -> ghi de (4702).
        app.MapPost("/api/tasks", (TaskManager tasks, CreateTaskRequest req) => Run(() =>
        {
            RequireElevation();
            tasks.CreateOrUpdate(
                req.Name,
                req.Command,
                req.Arguments,
                // Mac dinh hen gio xa trong tuong lai: muc dich la SINH RA LOG,
                // khong phai de task that su chay.
                req.StartBoundary ?? DateTime.Now.AddYears(1).ToString("yyyy-MM-ddTHH:mm:ss"));

            return Results.Ok(new
            {
                message = $"Da ghi task '{req.Name}'. Tao moi sinh event 4698, ghi de sinh 4702."
            });
        }));

        app.MapDelete("/api/tasks", (TaskManager tasks, string name) => Run(() =>
        {
            RequireElevation();
            tasks.Delete(name);
            return Results.Ok(new { message = $"Da xoa task '{name}' (event 4699)." });
        }));

        app.MapPost("/api/tasks/{name}/enable", (TaskManager tasks, string name) => Run(() =>
        {
            RequireElevation();
            tasks.SetEnabled(name, true);
            return Results.Ok(new { message = $"Da bat task '{name}' (event 4700)." });
        }));

        app.MapPost("/api/tasks/{name}/disable", (TaskManager tasks, string name) => Run(() =>
        {
            RequireElevation();
            tasks.SetEnabled(name, false);
            return Results.Ok(new { message = $"Da tat task '{name}' (event 4701)." });
        }));

        app.MapPost("/api/tasks/{name}/run", (TaskManager tasks, string name) => Run(() =>
        {
            RequireElevation();
            tasks.RunNow(name);
            return Results.Ok(new { message = $"Da chay task '{name}'." });
        }));

        // ------------------------------------------------------------ Services
        app.MapGet("/api/services", (ServiceManager services) => Run(() => Results.Ok(services.List())));

        app.MapPost("/api/services", (ServiceManager services, CreateServiceRequest req) => Run(() =>
        {
            RequireElevation();
            services.Create(req.Name, req.BinaryPath, req.StartType ?? "demand start", req.DisplayName);
            return Results.Ok(new { message = $"Da tao service '{req.Name}'. Xem event 7045 o tab Dashboard." });
        }));

        app.MapDelete("/api/services", (ServiceManager services, string name) => Run(() =>
        {
            RequireElevation();
            services.Delete(name);
            return Results.Ok(new { message = $"Da xoa service '{name}'." });
        }));

        app.MapPost("/api/services/{name}/start", (ServiceManager services, string name) => Run(() =>
        {
            RequireElevation();
            services.Start(name);
            return Results.Ok(new { message = $"Da start service '{name}'." });
        }));

        app.MapPost("/api/services/{name}/stop", (ServiceManager services, string name) => Run(() =>
        {
            RequireElevation();
            services.Stop(name);
            return Results.Ok(new { message = $"Da stop service '{name}'." });
        }));

        app.MapPost("/api/services/{name}/starttype",
            (ServiceManager services, string name, ChangeStartTypeRequest req) => Run(() =>
        {
            RequireElevation();
            services.ChangeStartType(name, req.StartType);
            return Results.Ok(new { message = $"Da doi start type cua '{name}'. Xem event 7040 o tab Dashboard." });
        }));
    }

    private sealed class NotElevatedException() : Exception(
        "Thao tac nay can quyen Administrator. Dong app, mo PowerShell bang " +
        "'Run as administrator' roi chay lai: dotnet run --project TaskServiceMonitor");

    private static void RequireElevation()
    {
        if (!ElevationInfo.IsElevated())
        {
            throw new NotElevatedException();
        }
    }

    /// <summary>
    /// Đổi exception thành mã HTTP có nghĩa. Quan trọng nhất:
    /// vi phạm rào an toàn → 403 chứ không phải 500.
    /// </summary>
    private static IResult Run(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (UnsafeTargetException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (NotElevatedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Win32Exception ex)
        {
            return Results.Json(
                new { error = $"{ex.Message} (ma loi Windows: {ex.NativeErrorCode})" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
