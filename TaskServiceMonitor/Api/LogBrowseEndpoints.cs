using System.Diagnostics.Eventing.Reader;
using TaskServiceMonitor.Monitoring;

namespace TaskServiceMonitor.Api;

/// <summary>Thân request của <c>POST /api/logs/export</c>.</summary>
public sealed record ExportLogRequest(string? Channel, int? EventId, string? FileName);

/// <summary>
/// Thân request của <c>POST /api/logs/export-events</c> — "Save Selected Events".
/// <paramref name="Channel"/> và <paramref name="SavedFile"/> loại trừ lẫn nhau.
/// </summary>
public sealed record ExportEventsRequest(
    string? Channel, string? SavedFile, long[]? RecordIds, string? Format);

/// <summary>
/// "Duyệt log bất kỳ" (<see cref="AdHocLogReader"/>) + "Saved Events"
/// (<see cref="SavedLogStore"/>). Không đụng tới <c>EventQueue</c>/DB/SignalR.
/// </summary>
public static class LogBrowseEndpoints
{
    private const int DefaultCount = 50;
    private const int MaxCount = 200;

    public static void MapLogBrowseEndpoints(this WebApplication app)
    {
        app.MapGet("/api/logs/channels", GetChannels);
        app.MapGet("/api/logs/browse", Browse);

        app.MapPost("/api/logs/export", ExportLog);
        app.MapPost("/api/logs/export-events", ExportEvents);
        app.MapGet("/api/logs/saved", ListSaved);
        app.MapGet("/api/logs/saved/{fileName}/download", DownloadSaved);
        app.MapDelete("/api/logs/saved/{fileName}", DeleteSaved);
    }

    private static IResult GetChannels(AdHocLogReader reader, bool? refresh)
    {
        try
        {
            return Results.Ok(reader.ListChannels(refresh ?? false));
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Đọc từ channel đang chạy (<c>?channel=</c>) HOẶC file đã lưu
    /// (<c>?savedFile=</c>) — loại trừ lẫn nhau, nhận cả hai thì báo lỗi rõ ràng
    /// chứ không đoán bừa.
    /// </summary>
    private static async Task<IResult> Browse(
        AdHocLogReader reader, SavedLogStore store,
        string? channel, string? savedFile, int? eventId, int? count, CancellationToken ct)
    {
        var hasChannel = !string.IsNullOrWhiteSpace(channel);
        var hasFile = !string.IsNullOrWhiteSpace(savedFile);

        if (hasChannel == hasFile)
        {
            return Results.BadRequest(new
            {
                error = hasChannel
                    ? "Chi duoc dung MOT trong hai tham so 'channel' hoac 'savedFile'."
                    : "Thieu tham so 'channel' (doc channel dang chay) hoac 'savedFile' (doc file .evtx da luu)."
            });
        }

        var limit = Math.Clamp(count ?? DefaultCount, 1, MaxCount);

        string source;
        PathType pathType;

        if (hasFile)
        {
            try
            {
                source = store.Resolve(savedFile);
                pathType = PathType.FilePath;
            }
            catch (UnsafeSavedLogNameException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (!File.Exists(source))
            {
                return Results.NotFound(new { error = $"Khong tim thay file da luu '{savedFile}'." });
            }
        }
        else
        {
            source = channel!;
            pathType = PathType.LogName;
        }

        try
        {
            var events = await reader.ReadAsync(source, eventId, limit, ct, pathType);
            return Results.Ok(events.Select(LogBrowseEventDto.From));
        }
        catch (EventLogNotFoundException ex)
        {
            return Results.NotFound(new
            {
                error = $"Khong tim thay '{savedFile ?? channel}'. Kiem tra ten dung bang " +
                        $"'GET /api/logs/channels'. (Chi tiet: {ex.Message})"
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(
                new { error = $"Khong du quyen doc '{savedFile ?? channel}' — can chay app bang quyen " +
                               $"Administrator. (Chi tiet: {ex.Message})" },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (EventLogException ex)
        {
            return Results.Json(
                new { error = $"Khong doc duoc '{savedFile ?? channel}'. (Chi tiet: {ex.Message})" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    // ---------------------------------------------------------------- Saved Events

    /// <summary>
    /// File ghi ra TRÊN MÁY CHẠY APP, không phải máy người dùng — muốn lấy về thì
    /// gọi tiếp endpoint download.
    /// </summary>
    private static async Task<IResult> ExportLog(
        SavedLogStore store, ExportLogRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Channel))
        {
            return Results.BadRequest(new { error = "Thieu tham so 'channel'." });
        }

        try
        {
            // Dong bo va co the ghi ca chuc MB ra dia -> day sang thread pool.
            var result = await Task.Run(
                () => store.Export(request.Channel, request.EventId, request.FileName), ct);

            return Results.Created(
                $"/api/logs/saved/{Uri.EscapeDataString(result.File.FileName)}/download",
                new
                {
                    result.File.FileName,
                    result.File.SizeBytes,
                    result.File.CreatedUtc,
                    result.MessagesEmbedded
                });
        }
        catch (UnsafeSavedLogNameException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (EventLogNotFoundException ex)
        {
            return Results.NotFound(new { error = $"Khong tim thay channel '{request.Channel}'. (Chi tiet: {ex.Message})" });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(
                new { error = $"Khong du quyen xuat channel '{request.Channel}' — can quyen Administrator. " +
                               $"(Chi tiet: {ex.Message})" },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (EventLogException ex)
        {
            return Results.Json(
                new { error = $"Khong xuat duoc channel '{request.Channel}'. (Chi tiet: {ex.Message})" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (IOException ex)
        {
            return Results.Json(
                new { error = $"Loi ghi file: {ex.Message}" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// "Save Selected Events". Hai đường ra khác hẳn nhau:
    /// - <c>evtx</c>: nhờ Windows xuất (giữ được định dạng gốc, mở lại bằng Event
    ///   Viewer được) → ghi vào thư mục <c>saved-logs</c> rồi trả metadata.
    /// - <c>xml</c>/<c>csv</c>: app tự dựng text → trả THẲNG về trình duyệt, không
    ///   để lại file trên server (đây là thứ để nộp/đọc, không phải để mở lại).
    /// </summary>
    private static async Task<IResult> ExportEvents(
        SavedLogStore store, AdHocLogReader reader, ExportEventsRequest request, CancellationToken ct)
    {
        var hasChannel = !string.IsNullOrWhiteSpace(request.Channel);
        var hasFile = !string.IsNullOrWhiteSpace(request.SavedFile);

        if (hasChannel == hasFile)
        {
            return Results.BadRequest(new
            {
                error = "Chi duoc dung MOT trong hai tham so 'channel' hoac 'savedFile'."
            });
        }

        var recordIds = request.RecordIds ?? [];
        var format = (request.Format ?? "evtx").ToLowerInvariant();

        string xpath;
        try
        {
            xpath = SavedLogStore.BuildRecordIdXPath(recordIds);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        try
        {
            if (format == "evtx")
            {
                if (hasFile)
                {
                    return Results.BadRequest(new
                    {
                        error = "Xuat .evtx tu file da luu chua ho tro - chon dinh dang XML hoac CSV."
                    });
                }

                var result = await Task.Run(
                    () => store.ExportSelected(request.Channel!, recordIds, null), ct);

                return Results.Ok(new
                {
                    result.File.FileName,
                    result.File.SizeBytes,
                    result.File.CreatedUtc,
                    result.MessagesEmbedded,
                    count = recordIds.Length
                });
            }

            var (source, pathType) = hasFile
                ? (store.Resolve(request.SavedFile), PathType.FilePath)
                : (request.Channel!, PathType.LogName);

            var events = await reader.ReadByXPathAsync(
                source, xpath, SavedLogStore.MaxSelectedRecords, ct, pathType);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            return format switch
            {
                "xml" => Results.File(
                    System.Text.Encoding.UTF8.GetBytes(BuildXmlExport(events)),
                    "application/xml", $"events-{stamp}.xml"),
                "csv" => Results.File(
                    // BOM de Excel doc dung tieng Viet co dau.
                    System.Text.Encoding.UTF8.GetPreamble()
                        .Concat(System.Text.Encoding.UTF8.GetBytes(BuildCsvExport(events))).ToArray(),
                    "text/csv", $"events-{stamp}.csv"),
                _ => Results.BadRequest(new { error = $"Dinh dang '{format}' khong ho tro (evtx/xml/csv)." })
            };
        }
        catch (UnsafeSavedLogNameException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (EventLogException ex)
        {
            return Results.Json(
                new { error = $"Khong xuat duoc event da chon. (Chi tiet: {ex.Message})" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string BuildXmlExport(IReadOnlyList<Models.WindowsMonitorEvent> events)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<Events>");

        foreach (var e in events)
        {
            sb.AppendLine(e.RawXml);
        }

        sb.AppendLine("</Events>");
        return sb.ToString();
    }

    private static string BuildCsvExport(IReadOnlyList<Models.WindowsMonitorEvent> events)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("RecordId,TimeCreated,Level,EventId,Source,TaskCategory,Computer,User,Description");

        foreach (var e in events)
        {
            sb.AppendLine(string.Join(",",
                Csv(e.RecordId?.ToString()),
                Csv(e.TimeCreated.ToString("o")),
                Csv(e.LevelDisplayName),
                Csv(e.EventId.ToString()),
                Csv(e.ProviderName),
                Csv(e.TaskCategoryName),
                Csv(e.Hostname),
                Csv(e.ActorAccount),
                Csv(e.Description ?? e.ActionDescription)));
        }

        return sb.ToString();
    }

    /// <summary>Bọc field CSV: nhân đôi dấu nháy kép rồi bọc ngoài — Description có cả xuống dòng lẫn dấu phẩy.</summary>
    private static string Csv(string? value) =>
        value is null ? "" : $"\"{value.Replace("\"", "\"\"")}\"";

    private static IResult ListSaved(SavedLogStore store)
    {
        try
        {
            // Kem duong dan thu muc: file ghi tren MAY CHAY APP nen nguoi dung khong
            // co cach nao doan ra no nam o dau neu UI khong noi.
            return Results.Ok(new { directory = store.RootDirectory, files = store.List() });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult DownloadSaved(SavedLogStore store, string fileName)
    {
        string path;
        try
        {
            path = store.Resolve(fileName); // Rao path traversal o tang server.
        }
        catch (UnsafeSavedLogNameException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (!File.Exists(path))
        {
            return Results.NotFound(new { error = $"Khong tim thay file '{fileName}'." });
        }

        return Results.File(path, "application/octet-stream", Path.GetFileName(path));
    }

    private static IResult DeleteSaved(SavedLogStore store, string fileName)
    {
        try
        {
            return store.Delete(fileName)
                ? Results.Ok(new { deleted = fileName })
                : Results.NotFound(new { error = $"Khong tim thay file '{fileName}'." });
        }
        catch (UnsafeSavedLogNameException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (IOException ex)
        {
            return Results.Json(
                new { error = $"Khong xoa duoc file: {ex.Message}" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
