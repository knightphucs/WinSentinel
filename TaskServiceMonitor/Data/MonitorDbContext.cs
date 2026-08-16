using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Data;

public sealed class MonitorDbContext(DbContextOptions<MonitorDbContext> options)
    : DbContext(options)
{
    public DbSet<WindowsMonitorEvent> Events => Set<WindowsMonitorEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
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
}
