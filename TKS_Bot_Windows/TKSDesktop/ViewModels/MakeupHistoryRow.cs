using TKSDesktop.App;
using TKSDesktop.Contracts.Dtos;

namespace TKSDesktop.ViewModels;

public sealed record LedgerReasonOption(string Code, string Label);

public sealed class MakeupHistoryRow(MakeupCardRecordDto item)
{
    public long Id => item.Id;
    public string GrantedMonth => item.GrantedMonth ?? string.Empty;
    public string UsedForDate => item.UsedForDate ?? string.Empty;
    public string StatusText => item.Status is "AVAILABLE" or "USED"
        ? I18n.T("makeup.status." + item.Status) : I18n.T("points.reason.unknown");
    public string TimeText => MessageItemViewModel.FromUnixMilliseconds(item.UsedAt ?? item.CreatedAt)
        .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}
