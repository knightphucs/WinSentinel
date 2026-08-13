using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Api;
using TaskServiceMonitor.Configuration;
using TaskServiceMonitor.Data;
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

// Ve doi enum sang chuoi: xem ghi chu o ConfigureHttpJsonOptions ben tren.
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<WindowsEventParser>();
builder.Services.AddSingleton<RiskScorer>();
builder.Services.AddSingleton<RawXmlSampleWriter>();
builder.Services.AddSingleton<EventQueue>();
builder.Services.AddSingleton<EventNotifier>();

// Rao an toan cho thao tac ghi task/service. Doc thi khong gioi han.
builder.Services.AddSingleton(new SafeNameGuard(
    builder.Configuration["Management:WritablePrefix"] ?? "WinSentinel"));
builder.Services.AddSingleton<ServiceManager>();
builder.Services.AddSingleton<TaskManager>();

// Producer: doc event tu Windows -> parse -> day vao hang doi.
builder.Services.AddHostedService<EventWatcherService>();
// Consumer: doc hang doi -> ghi DB.
builder.Services.AddHostedService<EventPersistenceService>();

var app = builder.Build();

// Cham lai RiskLevel cho event da luu truoc khi co RiskScorer, roi thoat.
//   dotnet run --project TaskServiceMonitor -- --rescore
if (args.Contains("--rescore"))
{
    using var scope = app.Services.CreateScope();
    return await RiskRescoreTool.RunAsync(
        scope.ServiceProvider.GetRequiredService<MonitorDbContext>(),
        scope.ServiceProvider.GetRequiredService<RiskScorer>());
}

// Dashboard tinh o wwwroot/. UseDefaultFiles phai dung TRUOC UseStaticFiles
// thi "/" moi tra ve index.html.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapEventEndpoints();
app.MapManagementEndpoints();
app.MapHub<MonitorHub>(MonitorHub.Route);

app.Run();
return 0;
