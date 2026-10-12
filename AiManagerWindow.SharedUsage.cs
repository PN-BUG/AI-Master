using System.Text.Json;
using System.Windows;
using AIMaster.Models;
using AIMaster.Services;

namespace AIMaster;

public partial class AiManagerWindow
{
    private bool _sharedUsageEditing;
    private bool _sharedUsageControlsLoaded;

    private string SelectedSharedUsageResetMode() => SharedUsageAccountResetRadio.IsChecked == true
        ? SharedUsageResetModes.AccountReset
        : SharedUsageWeeklyRadio.IsChecked == true ? SharedUsageResetModes.Weekly : SharedUsageResetModes.Never;

    private void LoadSharedUsageResetMode()
    {
        _sharedUsageControlsLoaded = false;
        var mode = SharedUsageResetModes.Normalize(_service.Settings.SharedUsageResetMode);
        SharedUsageWeeklyRadio.IsChecked = mode == SharedUsageResetModes.Weekly;
        SharedUsageAccountResetRadio.IsChecked = mode == SharedUsageResetModes.AccountReset;
        SharedUsageNeverRadio.IsChecked = mode == SharedUsageResetModes.Never;
        SharedUsageRestoreButton.IsEnabled = _service.CanRestoreSharedUsage;
        _sharedUsageControlsLoaded = true;
    }

    private async void SharedUsageResetMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_sharedUsageControlsLoaded || _sharedUsageEditing) return;
        await ChangeSharedUsageCounterAsync(SharedUsageCounterAction.None);
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
            SharedUsageStatusText.Text = action == SharedUsageCounterAction.None
                ? L("正在按新方式刷新用量…", "Refreshing usage with the selected policy…")
                : L("正在更新当前设备计数…", "Updating this device's counts…");
            var settings = _service.ReloadSettings();
            // Apply only the count policy here; connection settings still use Save & sync.
            settings.SharedUsageResetMode = SelectedSharedUsageResetMode();
            _service.SaveSettings(settings);
            var result = await _service.SyncSharedUsageAsync(_lifetime.Token, action);
            RenderSharedUsage(result);
            var message = action switch
            {
                SharedUsageCounterAction.Clear => L("当前设备共享计数已清零", "Shared counts cleared for this device"),
                SharedUsageCounterAction.Restore => L("当前设备上次清零已撤销，包含之后新增用量", "Last clear undone for this device, including subsequent usage"),
                _ => result.Error is null
                    ? L("清零方式已保存，用量已刷新", "Reset policy saved; usage refreshed")
                    : L("清零方式已保存", "Reset policy saved")
            };
            if (!result.Enabled)
                SharedUsageStatusText.Text = message + L("；启用共享后同步", "; will sync when sharing is enabled");
            else if (result.Error is not null)
                SharedUsageStatusText.Text = message + L("；同步失败，下次同步会重试：", "; sync failed, will retry on the next sync: ") + result.Error;
            SetFooterStatus(message, warning: result.Error is not null);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _service.ReloadSettings();
            LoadSharedUsageResetMode();
            SharedUsageStatusText.Text = L("本机共享计数操作失败：", "Unable to change local shared counts: ") + ex.Message;
            SetFooterStatus(SharedUsageStatusText.Text, warning: true);
        }
        finally { SetSharedUsageBusy(false); }
    }
}
