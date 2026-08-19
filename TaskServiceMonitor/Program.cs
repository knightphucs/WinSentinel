using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Api;
using TaskServiceMonitor.Configuration;
using TaskServiceMonitor.Data;
using TaskServiceMonitor.Detection;
using TaskServiceMonitor.Management;
using TaskServiceMonitor.Monitoring;
using TaskServiceMonitor.Realtime;

// Che do kiem tra parser: nap lai cac file XML that trong samples/ va in ket qua parse.
// Chay duoc ma KHONG can DB - dung de xac nhan parser truoc khi dung den PostgreSQL.
//   dotnet run --project TaskServiceMonitor -- --parse-samples
if (args.Contains("--parse-samples"))
{
    return SampleReplay.Run(args);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<EventLogOptions>(
    builder.Configuration.GetSection(EventLogOptions.SectionName));

builder.Services.Configure<AlertingOptions>(
    builder.Configuration.GetSection(AlertingOptions.SectionName));

// Tra enum ra JSON dang chu ("Service") thay vi so (2). Dashboard doc de hon va
// khong bi vo khi thu tu gia tri trong enum thay doi.
//
// BAY: Minimal API va SignalR dung HAI bo serializer HOAN TOAN TACH BIET.
// Chi dang ky o mot ben thi ben kia van gui so -> dashboard hien "2" luc realtime
// roi doi thanh "Service" sau khi F5. Phai dang ky ca hai (xem AddSignalR ben duoi).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var connectionString = builder.Configuration.GetConnectionString("MonitorDb")
    ?? throw new InvalidOperationException(
        "Thieu connection string 'MonitorDb'. Khai bao trong appsettings.json muc " +
        "ConnectionStrings:MonitorDb, hoac dat bang: " +
        "dotnet user-secrets set \"ConnectionStrings:MonitorDb\" \"...\"");

builder.Services.AddDbContext<MonitorDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddScoped<EventStorageService>();
builder.Services.AddScoped<AlertStorageService>();

// Tang phat hien (buoc 11). Scoped vi CorrelationRules can DbContext de tra lich su
// event - xem docs/hanh-vi-mapping.md muc 4.
var alertingOptions = builder.Configuration
    .GetSection(AlertingOptions.SectionName).Get<AlertingOptions>() ?? new AlertingOptions();

builder.Services.AddSingleton(alertingOptions);
builder.Services.AddScoped<CorrelationRules>();
builder.Services.AddScoped<AlertEvaluator>();

// Blacklist: SINGLETON vi no giu snapshot trong bo nho - BlacklistMatcher chay tren
// MOI event nen khong the hoi DB tung lan. Tu cham DB qua IServiceScopeFactory.
builder.Services.AddSingleton<BlacklistRegistry>();

// Ve doi enum sang chuoi: xem ghi chu o ConfigureHttpJsonOptions ben tren.
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<WindowsEventParser>();
builder.Services.AddSingleton<RiskScorer>();
builder.Services.AddSingleton<RawXmlSampleWriter>();
builder.Services.AddSingleton<EventQueue>();
builder.Services.AddSingleton<EventNotifier>();
builder.Services.AddSingleton<ChannelStatusRegistry>();

// Doc log "theo yeu cau" cho tinh nang duyet log bat ky - tach khoi pipeline
// curated o tren (EventQueue/EventPersistenceService/EventNotifier).
builder.Services.AddSingleton<AdHocLogReader>();

// Saved Events (.evtx): xuat / liet ke / xoa. Chua luon rao chan path traversal -
// bat buoc vi ten file den thang tu URL ma app chay quyen Administrator.
builder.Services.AddSingleton<SavedLogStore>();

// Doc (khong ghi) Task/Service hien co tren may - phuc vu tab Tasks/Services CHI
// XEM. Mentor xac nhan app chi can MONITORING, khong can tu thao tac Task/Service,
// nen lop rao SafeNameGuard/InputPolicy (chi ton tai de bao ve thao tac ghi) da bi
// go bo cung cac method Create/Delete/Start/Stop/... o hai class ben duoi.
builder.Services.AddSingleton<ServiceManager>();
builder.Services.AddSingleton<TaskManager>();

// Producer: doc event tu Windows -> parse -> day vao hang doi.
builder.Services.AddHostedService<EventWatcherService>();
// Consumer: doc hang doi -> ghi DB.
builder.Services.AddHostedService<EventPersistenceService>();

// Poll cau hinh service de bat viec doi binPath / doi tai khoan - hai hanh vi ma SCM
// KHONG phat event nao (7040 chi bao doi start type).
builder.Services.AddHostedService<ServiceConfigWatcher>();

var app = builder.Build();

// Tinh lai cac cot dan xuat tu RawXml cho event da luu (RiskLevel + nhom Level/
// TaskCategory/Keywords them o buoc 8), roi thoat. '--rescore' giu lai lam alias
// vi ten cu da nam trong CLAUDE.md va trong thoi quen go lenh.
//   dotnet run --project TaskServiceMonitor -- --backfill
if (args.Contains("--backfill") || args.Contains("--rescore"))
{
    using var scope = app.Services.CreateScope();
    return await BackfillTool.RunAsync(
        scope.ServiceProvider.GetRequiredService<MonitorDbContext>(),
        scope.ServiceProvider.GetRequiredService<WindowsEventParser>(),
        scope.ServiceProvider.GetRequiredService<RiskScorer>());
}

// Cham lai toan bo rule tren event DA LUU de dung lai bang canh bao, roi thoat.
// Cung la cach do ti le duong tinh gia: in so canh bao theo tung rule.
//   dotnet run --project TaskServiceMonitor -- --rebuild-alerts
if (args.Contains("--rebuild-alerts"))
{
    using var scope = app.Services.CreateScope();

    // Nap blacklist truoc: khong nap thi rule BLACKLIST_HIT khong bao gio khop luc
    // dung lai bang canh bao, va con so do duong tinh gia se thieu mat mot rule.
    await app.Services.GetRequiredService<BlacklistRegistry>().ReloadAsync();

    return await AlertRebuildTool.RunAsync(
        scope.ServiceProvider.GetRequiredService<MonitorDbContext>(),
        scope.ServiceProvider.GetRequiredService<AlertEvaluator>());
}

// Dashboard tinh o wwwroot/. UseDefaultFiles phai dung TRUOC UseStaticFiles
// thi "/" moi tra ve index.html.
app.UseDefaultFiles();

// "no-cache" = van luu nhung bat buoc hoi lai server (ETag -> 304 neu khong doi).
// Can vi index.html va app.js doi cung luc o buoc 8: trinh duyet lay HTML moi (12
// cot) nhung dung app.js cu (ve 8 o) -> du lieu lech han mot cot, rat kho doan ra.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "no-cache"
});

app.MapEventEndpoints();
app.MapAlertEndpoints();
app.MapBlacklistEndpoints();
app.MapManagementEndpoints();
app.MapLogBrowseEndpoints();
app.MapHub<MonitorHub>(MonitorHub.Route);

// Nap blacklist vao bo nho TRUOC khi nhan event dau tien - BlacklistMatcher doc
// snapshot nay tren moi event. Khong await duoc o day (Program la top-level statement
// dong bo toi diem nay) nen chay dong bo mot lan, chi vai chuc dong.
await app.Services.GetRequiredService<BlacklistRegistry>().ReloadAsync();

app.Run();
return 0;
