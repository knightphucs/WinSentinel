using TaskServiceMonitor.Detection;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Chấm mức rủi ro cho event theo rule cố định (rule-based, không học máy).
/// Gọi ngay sau khi parse và TRƯỚC khi lưu DB, để mức rủi ro được lưu cùng event.
///
/// TỪ BƯỚC 11, lớp này KHÔNG còn giữ rule riêng nữa. Nó uỷ quyền hoàn toàn cho
/// <c>Detection.RuleCatalog</c> và lấy mức CAO NHẤT trong các rule khớp.
///
/// Vì sao đổi: trước đây rule nằm trong hai mảng private ở đây (<c>\Temp\</c>,
/// <c>\AppData\</c>, <c>-enc</c>...), còn danh sách cảnh báo lại cần một bộ rule
/// đầy đủ hơn. Để hai bộ song song thì sớm muộn cũng lệch — dashboard tô màu một
/// đằng, tab Cảnh báo nói một nẻo. Đây là cùng lý do <c>--backfill</c> gọi lại
/// chính lớp này thay vì viết lại rule bằng SQL.
///
/// Giữ nguyên lớp (không xoá, không đổi tên) vì <c>EventWatcherService</c>,
/// <c>AdHocLogReader</c> và <c>BackfillTool</c> đều đang inject nó.
/// </summary>
public sealed class RiskScorer
{
    public RiskLevel Score(WindowsMonitorEvent evt) => RuleCatalog.HighestSeverity(evt);
}
