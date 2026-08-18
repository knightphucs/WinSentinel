using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Data;

public sealed class MonitorDbContext(DbContextOptions<MonitorDbContext> options)
    : DbContext(options)
{
    public DbSet<WindowsMonitorEvent> Events => Set<WindowsMonitorEvent>();

    /// <summary>Cảnh báo do tầng phát hiện sinh ra (bước 11).</summary>
    public DbSet<Alert> Alerts => Set<Alert>();

    /// <summary>
    /// Ảnh chụp cấu hình service, dùng làm mốc so sánh cho <c>ServiceConfigWatcher</c>.
    /// Lưu xuống DB chứ không giữ trong bộ nhớ để restart app không mất mốc — cùng
    /// tinh thần với cursor <c>RecordId</c> ở bước 7.
    /// </summary>
    public DbSet<ServiceConfigSnapshot> ServiceConfigSnapshots => Set<ServiceConfigSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureEvents(modelBuilder);
        ConfigureAlerts(modelBuilder);
        ConfigureServiceConfigSnapshots(modelBuilder);
    }

    private static void ConfigureEvents(ModelBuilder modelBuilder)
    {
        var e = modelBuilder.Entity<WindowsMonitorEvent>();

        e.ToTable("Events");
        e.HasKey(x => x.Id);

        // Luu enum thanh chu ('ScheduledTask') thay vi so - doc DB bang mat de hon,
        // va them gia tri moi vao enum sau nay khong lam lech y nghia du lieu cu.
        e.Property(x => x.ObjectType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        e.Property(x => x.RiskLevel)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        e.Property(x => x.Hostname).HasMaxLength(255).IsRequired();
        e.Property(x => x.Channel).HasMaxLength(128).IsRequired();
        e.Property(x => x.ProviderName).HasMaxLength(255).IsRequired();
        e.Property(x => x.ActionDescription).HasMaxLength(128).IsRequired();

        e.Property(x => x.ObjectName).HasMaxLength(512);
        e.Property(x => x.DisplayName).HasMaxLength(512);
        e.Property(x => x.ActorAccount).HasMaxLength(255);
        e.Property(x => x.ActorSid).HasMaxLength(255);
        e.Property(x => x.ImagePath).HasMaxLength(1024);
        e.Property(x => x.ServiceType).HasMaxLength(64);
        e.Property(x => x.StartType).HasMaxLength(64);
        e.Property(x => x.PreviousStartType).HasMaxLength(64);
        e.Property(x => x.ServiceAccount).HasMaxLength(255);
        e.Property(x => x.TaskActionType).HasMaxLength(64);
        e.Property(x => x.TaskComHandlerClassId).HasMaxLength(64);
        e.Property(x => x.TaskCommand).HasMaxLength(1024);
        e.Property(x => x.TaskArguments).HasMaxLength(2048);
        e.Property(x => x.TaskRunAsUser).HasMaxLength(255);
        e.Property(x => x.TaskRunLevel).HasMaxLength(64);
        e.Property(x => x.TaskInstanceId).HasMaxLength(64);
        e.Property(x => x.TaskActionResultCode).HasMaxLength(32);

        // Nhom hien thi kieu Event Viewer (buoc 8). TAT CA nullable - co y, xem ghi
        // chu trong WindowsMonitorEvent: cot string non-null bi EF sinh
        // defaultValue: "" va chuoi rong khong phai gia tri hop le de doc nguoc.
        e.Property(x => x.LevelDisplayName).HasMaxLength(32);
        e.Property(x => x.TaskCategoryName).HasMaxLength(255);
        e.Property(x => x.OpcodeName).HasMaxLength(128);
        e.Property(x => x.Keywords).HasMaxLength(255);

        // Noi dung dai - de kieu text, khong gioi han do dai.
        e.Property(x => x.RawXml).HasColumnType("text").IsRequired();
        e.Property(x => x.TaskContentXml).HasColumnType("text");

        // Description la cau van do provider render ra, dai tuy y (co cai vai KB nhu
        // event PowerShell 403 kem ca dong lenh) - khong dat gioi han do dai.
        e.Property(x => x.Description).HasColumnType("text");

        // Npgsql map DateTime co Kind=Utc sang 'timestamp with time zone'.
        // Parser da bao dam luon tra ve UTC (xem WindowsEventParser.ReadTimeCreated).
        e.Property(x => x.TimeCreated)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // Toan bo field tho cua <EventData> luu thanh jsonb - query vao trong JSON duoc.
        e.Property(x => x.Data)
            .HasColumnName("Data")
            .HasColumnType("jsonb")
            .HasConversion(EventDataJsonConverter.Converter, EventDataJsonConverter.Comparer)
            .IsRequired();

        // Dashboard luon hoi "moi nhat truoc", loc theo may va theo loai doi tuong.
        e.HasIndex(x => x.TimeCreated).IsDescending();
        e.HasIndex(x => x.Hostname);
        e.HasIndex(x => x.ObjectType);
        e.HasIndex(x => x.RiskLevel);

        // Chong ghi trung: mot event duoc xac dinh duy nhat boi
        // (may nguon, channel, so thu tu ban ghi trong channel do).
        // Loc IS NOT NULL vi RecordId co the khong doc duoc o vai truong hop.
        e.HasIndex(x => new { x.Hostname, x.Channel, x.RecordId })
            .IsUnique()
            .HasFilter("\"RecordId\" IS NOT NULL")
            .HasDatabaseName("IX_Events_Dedup");
    }

    private static void ConfigureAlerts(ModelBuilder modelBuilder)
    {
        var a = modelBuilder.Entity<Alert>();

        a.ToTable("Alerts");
        a.HasKey(x => x.Id);

        // Enum luu thanh chu, giong Events - xem ghi chu o ConfigureEvents.
        a.Property(x => x.Severity)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        a.Property(x => x.ObjectType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        a.Property(x => x.RuleId).HasMaxLength(64).IsRequired();
        a.Property(x => x.RuleName).HasMaxLength(255).IsRequired();
        a.Property(x => x.Hostname).HasMaxLength(255).IsRequired();
        a.Property(x => x.ObjectName).HasMaxLength(512);

        // Cau bang chung ghep tu duong dan + tham so - co the rat dai, de kieu text.
        a.Property(x => x.Evidence).HasColumnType("text").IsRequired();
        a.Property(x => x.Recommendation).HasColumnType("text");

        a.Property(x => x.DetectedAt).HasColumnType("timestamp with time zone").IsRequired();
        a.Property(x => x.EventTime).HasColumnType("timestamp with time zone").IsRequired();
        a.Property(x => x.AcknowledgedAt).HasColumnType("timestamp with time zone");

        // Tab Canh bao luon hoi "moi nhat truoc", loc theo muc / rule / trang thai.
        a.HasIndex(x => x.DetectedAt).IsDescending();
        a.HasIndex(x => x.Severity);
        a.HasIndex(x => x.RuleId);
        a.HasIndex(x => x.Acknowledged);

        // Chong ghi trung: mot event chi sinh dung MOT canh bao cho moi rule.
        // Nho index nay ma '--rebuild-alerts' chay lai bao nhieu lan cung khong nhan doi.
        // Loc IS NOT NULL vi canh bao tu ServiceConfigWatcher khong co event goc -
        // chung duoc chong trung bang chinh snapshot (chi sinh khi gia tri THAY DOI).
        a.HasIndex(x => new { x.SourceEventId, x.RuleId })
            .IsUnique()
            .HasFilter("\"SourceEventId\" IS NOT NULL")
            .HasDatabaseName("IX_Alerts_Dedup");
    }

    private static void ConfigureServiceConfigSnapshots(ModelBuilder modelBuilder)
    {
        var s = modelBuilder.Entity<ServiceConfigSnapshot>();

        s.ToTable("ServiceConfigSnapshots");

        // Khoa chinh la (may, ten service): mot dong cho moi service tren moi may.
        s.HasKey(x => new { x.Hostname, x.ServiceName });

        s.Property(x => x.Hostname).HasMaxLength(255);
        s.Property(x => x.ServiceName).HasMaxLength(255);
        s.Property(x => x.ImagePath).HasMaxLength(1024);
        s.Property(x => x.Account).HasMaxLength(255);
        s.Property(x => x.StartType).HasMaxLength(64);
        s.Property(x => x.CapturedAt).HasColumnType("timestamp with time zone").IsRequired();
    }
}
