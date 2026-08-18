using System.Globalization;

namespace TaskServiceMonitor.Api;

/// <summary>
/// Đọc cặp query param <c>from</c>/<c>to</c> thành mốc thời gian UTC, dùng chung cho
/// <see cref="EventEndpoints"/> và <see cref="AlertEndpoints"/> để hai chỗ không lệch
/// cách hiểu chuỗi ngày giờ.
/// </summary>
/// <remarks>
/// BẪY MÚI GIỜ — lý do phải có <see cref="DateTimeStyles.AdjustToUniversal"/> kèm
/// <see cref="DateTimeStyles.AssumeUniversal"/>:
/// mọi cột thời gian trong DB đều lưu UTC (quy ước dự án, xem CLAUDE.md), nhưng
/// <c>DateTime.TryParse</c> mặc định trả <c>DateTimeKind.Unspecified</c> theo giờ
/// MÁY CHẠY APP. Không ép về UTC thì bộ lọc "1 giờ qua" trên máy giờ Việt Nam (UTC+7)
/// sẽ lệch đúng 7 tiếng và trả về rỗng — im lặng, không lỗi.
///
/// Frontend luôn gửi chuỗi ISO có hậu tố <c>Z</c> (<c>toISOString()</c>), nên nhánh
/// <c>AssumeUniversal</c> chỉ là lưới an toàn cho người gõ tay <c>curl</c>.
/// </remarks>
internal static class TimeRangeFilter
{
    private const DateTimeStyles Styles =
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;

    /// <summary>
    /// <c>null</c> đầu ra = không lọc. Trả <c>false</c> khi chuỗi có nhưng không parse
    /// được — gọi bên ngoài phải trả 400 chứ không được âm thầm bỏ qua bộ lọc.
    /// </summary>
    public static bool TryParse(string? value, out DateTime? utc)
    {
        utc = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, Styles, out var parsed))
        {
            return false;
        }

        utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    public static IResult Invalid(string parameterName, string value) =>
        Results.BadRequest(new
        {
            error = $"Gia tri '{parameterName}' khong phai moc thoi gian hop le: '{value}'.",
            expected = "Chuoi ISO-8601, vi du 2026-08-18T09:30:00Z"
        });
}
