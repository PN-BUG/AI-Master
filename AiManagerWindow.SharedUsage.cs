using System.Text.Json;
using System.Windows;
using AIMaster.Models;
using AIMaster.Services;

namespace AIMaster;

public partial class AiManagerWindow
{
    private bool _sharedUsageEditing;

    private string SelectedSharedUsageResetMode() => SharedUsageAccountResetRadio.IsChecked == true
        ? SharedUsageResetModes.AccountReset
        : SharedUsageWeeklyRadio.IsChecked == true ? SharedUsageResetModes.Weekly : SharedUsageResetModes.Never;

    private void LoadSharedUsageResetMode()
    {
        var mode = SharedUsageResetModes.Normalize(_service.Settings.SharedUsageResetMode);
        SharedUsageWeeklyRadio.IsChecked = mode == SharedUsageResetModes.Weekly;
        SharedUsageAccountResetRadio.IsChecked = mode == SharedUsageResetModes.AccountReset;
        SharedUsageNeverRadio.IsChecked = mode == SharedUsageResetModes.Never;
        SharedUsageRestoreButton.IsEnabled = _service.CanRestoreSharedUsage;
    }

    private void SetSharedUsageBusy(bool busy)
    {
        _sharedUsageEditing = busy;
        SharedUsageSaveButton.IsEnabled = !busy;
        SharedUsageClearButton.IsEnabled = !busy;
        SharedUsageRestoreButton.IsEnabled = !busy && _service.CanRestoreSharedUsage;
        SharedUsageWeeklyRadio.IsEnabled = !busy;
        SharedUsageAccountResetRadio.IsEnabled = !busy;
        SharedUsageNeverRadio.IsEnabled = !busy;
    }

    private async void ClearSharedUsage_Click(object sender, RoutedEventArgs e) =>
        await ChangeSharedUsageCounterAsync(SharedUsageCounterAction.Clear);

    private async void RestoreSharedUsage_Click(object sender, RoutedEventArgs e) =>
        await ChangeSharedUsageCounterAsync(SharedUsageCounterAction.Restore);

    private async Task ChangeSharedUsageCounterAsync(SharedUsageCounterAction action)
    {
        if (_sharedUsageEditing) return;
        SetSharedUsageBusy(true);
        try
        {
            var settings = _service.ReloadSettings();
            // Apply only the count policy here; connection settings still use Save & sync.
            settings.SharedUsageResetMode = SelectedSharedUsageResetMode();
            _service.SaveSettings(settings);
            var result = await _service.SyncSharedUsageAsync(_lifetime.Token, action);
            RenderSharedUsage(result);
            var message = action == SharedUsageCounterAction.Clear
                ? L("当前设备共享计数已清零", "Shared counts cleared for this device")
                : L("当前设备上次清零已撤销，包含之后新增用量", "Last clear undone for this device, including subsequent usage");
            if (!result.Enabled)
                SharedUsageStatusText.Text = message + L("；启用共享后同步", "; will sync when sharing is enabled");
            else if (result.Error is not null)
                SharedUsageStatusText.Text = message + L("；同步失败，下次同步会重试：", "; sync failed, will retry on the next sync: ") + result.Error;
            SetFooterStatus(message, warning: result.Error is not null);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SharedUsageStatusText.Text = L("本机共享计数操作失败：", "Unable to change local shared counts: ") + ex.Message;
            SetFooterStatus(SharedUsageStatusText.Text, warning: true);
        }
        finally { SetSharedUsageBusy(false); }
    }
}
