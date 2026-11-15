using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    /// <summary>Covers the OpenCode Go subscription allowance: the usage response parser, the plan
    /// key round-trip, the DeepSeek refresh that attaches the allowance, and the two windows that
    /// switch from the pay-as-you-go balance to the plan quota when the key is configured.</summary>
    private static void VerifyOpenCodeGoSupport()
    {
        LocalizationService.SetLanguage(LocalizationService.English);
        VerifyGoQuotaParsing();
        VerifyGoQuotaClientResults();
        VerifyGoQuotaSettingsRoundTrip();
        VerifyGoQuotaRefresh();
        VerifyGoQuotaFloatingRender();
        VerifyGoQuotaDashboardRender();
    }

    private static void VerifyGoQuotaParsing()
    {
        using var document = JsonDocument.Parse("""
            {"usage":{
              "rolling":{"status":"ok","percent":4,"resetsAt":"2026-11-13T17:57:39.000Z"},
              "weekly":{"status":"ok","percent":41,"resetsAt":"2026-11-16T00:00:00.000Z"},
              "monthly":{"status":"ok","percent":20,"resetsAt":"2026-12-13T06:30:34.000Z"}}}
            """);
        var quota = OpenCodeGoClient.ParseUsage(document.RootElement);
        Check(quota.HasAnyData && quota.Rolling is { UsedPercent: 4, RemainingPercent: 96 } &&
              quota.Weekly is { RemainingPercent: 59 } && quota.Monthly is { RemainingPercent: 80 } &&
              quota.Tightest?.Id == "weekly" &&
              quota.UsedSummary == "5h 4% · Week 41% · Month 20%" &&
              quota.RemainingSummary == "5h 96% · Week 59% · Month 80%",
            "OpenCode Go usage windows are read into remaining percentages and summaries");
        Check(quota.Rolling!.ResetsAt ==
              DateTimeOffset.Parse("2026-11-13T17:57:39Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) &&
              quota.Rolling.WindowDurationMinutes == 300 && quota.Weekly!.WindowDurationMinutes == 10080 &&
              quota.Monthly!.WindowDurationMinutes == 43200,
            "OpenCode Go windows keep their reset time and documented durations");

        using var partial = JsonDocument.Parse("""
            {"usage":{"weekly":{"status":"ok","percent":"12.5","resetsAt":1763000000}}}
            """);
        var partialQuota = OpenCodeGoClient.ParseUsage(partial.RootElement);
        Check(partialQuota.Rolling is null && partialQuota.Monthly is null &&
              partialQuota.Weekly is { UsedPercent: 12.5, ResetsAt: not null },
            "a partial OpenCode Go response keeps only the windows it reported and accepts string percents");

        using var empty = JsonDocument.Parse("{}");
        Check(!OpenCodeGoClient.ParseUsage(empty.RootElement).HasAnyData,
            "an empty OpenCode Go response has no allowance data");
    }

    private static void VerifyGoQuotaClientResults()
    {
        var usageJson = """
            {"usage":{"rolling":{"status":"ok","percent":10,"resetsAt":"2026-11-13T17:57:39.000Z"},
            "weekly":{"status":"ok","percent":30},"monthly":{"status":"ok","percent":50}}}
            """;
        using (var http = new HttpClient(new StubHandler(_ => _httpResponse(usageJson))))
        using (var client = new OpenCodeGoClient(http, () => DateTimeOffset.Now))
        {
            var success = client.ReadAsync("oc_sk_test").GetAwaiter().GetResult();
            Check(success.HasAnyData && success.Error is null && success.Rolling?.RemainingPercent == 90,
                "the OpenCode Go client keeps a successful allowance read free of errors");
        }

        using (var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))))
        using (var client = new OpenCodeGoClient(http))
        {
            var expired = client.ReadAsync("oc_sk_expired").GetAwaiter().GetResult();
            Check(expired is { HasAnyData: false, Error: { } message } &&
                  message.Contains("invalid or expired", StringComparison.Ordinal),
                "an expired OpenCode Go key reports a credential error instead of a quota");
        }

        using (var client = new OpenCodeGoClient())
        {
            var missing = client.ReadAsync(" ").GetAwaiter().GetResult();
            Check(missing is { HasAnyData: false, Error: { } message } &&
                  message.Contains("No OpenCode Go API key", StringComparison.Ordinal),
                "a missing OpenCode Go key explains that the plan allowance cannot be read");
        }
    }

    private static void VerifyGoQuotaSettingsRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-opencode-go-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        try
        {
            var service = new AiManagerService(settingsPath);
            try
            {
                var settings = service.ReloadSettings();
                settings.DeepSeekApiKey = "sk-deepseek-test";
                settings.OpenCodeGoApiKey = "oc_sk_go_test";
                service.SaveSettings(settings);
                var json = File.ReadAllText(settingsPath);
                Check(!json.Contains("oc_sk_go_test", StringComparison.Ordinal) &&
                      json.Contains("openCodeGoApiKeyProtected", StringComparison.Ordinal),
                    "the OpenCode Go key is stored DPAPI-protected, never as plain text");
                var reloaded = service.ReloadSettings();
                Check(reloaded.OpenCodeGoApiKey == "oc_sk_go_test" && reloaded.DeepSeekApiKey == "sk-deepseek-test",
                    "the OpenCode Go key round-trips through the protected settings file");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void VerifyGoQuotaRefresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-opencode-go-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        try
        {
            const string balanceJson = """
                {"is_available":true,"balance_infos":[
                  {"currency":"CNY","total_balance":"0.00","granted_balance":"0.00","topped_up_balance":"0.00"}]}
                """;
            const string goJson = """
                {"usage":{"rolling":{"status":"ok","percent":4,"resetsAt":"2026-11-13T17:57:39.000Z"},
                "weekly":{"status":"ok","percent":41},"monthly":{"status":"ok","percent":20}}}
                """;
            using var balanceHttp = new HttpClient(new StubHandler(_ => _httpResponse(balanceJson)));
            using var goHttp = new HttpClient(new StubHandler(_ => _httpResponse(goJson)));
            using var balanceClient = new DeepSeekClient(balanceHttp);
            using var goClient = new OpenCodeGoClient(goHttp);
            var service = new AiManagerService(settingsPath, deepSeekClient: balanceClient,
                openCodeGoClient: goClient);
            try
            {
                var settings = service.ReloadSettings();
                settings.Provider = AiProviderModes.DeepSeek;
                settings.DeepSeekApiKey = "sk-test";
                settings.OpenCodeGoApiKey = "oc_sk_test";
                service.SaveSettings(settings);

                var snapshot = service.RefreshAsync().GetAwaiter().GetResult();
                Check(snapshot.DeepSeek?.GoQuota is { HasAnyData: true } go &&
                      go.Rolling?.UsedPercent == 4 && go.Weekly?.UsedPercent == 41 && snapshot.DeepSeek.Balance is { Total: 0 } &&
                      service.LastGoQuotaError is null,
                    "a DeepSeek refresh attaches the OpenCode Go allowance next to the zero balance");

                settings = service.ReloadSettings();
                settings.OpenCodeGoApiKey = string.Empty;
                service.SaveSettings(settings);
                var withoutPlan = service.RefreshAsync().GetAwaiter().GetResult();
                Check(withoutPlan.DeepSeek?.GoQuota is null && withoutPlan.DeepSeek?.Balance is not null,
                    "removing the OpenCode Go key switches the account view back to the balance");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void VerifyGoQuotaFloatingRender()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-opencode-go-floating-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(settingsPath, "{\"provider\":\"deepseek\"}");
            var service = new AiManagerService(settingsPath);
            var floating = new AiFloatingWindow(service, new ResetSignalAttention(Path.Combine(root, "attention.json")));
            try
            {
                var render = typeof(AiFloatingWindow).GetMethod("RenderSnapshot",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var now = DateTimeOffset.Now;
                var quota = new AiGoQuotaSnapshot
                {
                    Rolling = new AiGoQuotaWindow { Id = "rolling", UsedPercent = 4, ResetsAt = now.AddHours(2) },
                    Weekly = new AiGoQuotaWindow { Id = "weekly", UsedPercent = 41, ResetsAt = now.AddDays(3) },
                    Monthly = new AiGoQuotaWindow { Id = "monthly", UsedPercent = 20, ResetsAt = now.AddDays(20) },
                    CapturedAt = now
                };
                render.Invoke(floating, [new AiFloatingSnapshot
                {
                    Provider = AiProviderModes.DeepSeek,
                    GoQuota = quota, GoQuotaCapturedAt = now,
                    DeepSeekBalanceText = "¥0.00", DeepSeekBalanceCapturedAt = now
                }]);
                Check(((FrameworkElement)floating.FindName("GoQuotaRow")).Visibility == Visibility.Visible &&
                      ((FrameworkElement)floating.FindName("DeepSeekQuotaRow")).Visibility == Visibility.Collapsed &&
                      ((TextBlock)floating.FindName("GoRollingText")).Text == "96" &&
                      ((TextBlock)floating.FindName("GoWeeklyText")).Text == "59" &&
                      ((TextBlock)floating.FindName("GoMonthlyText")).Text == "80",
                    "the floating monitor shows the Go plan windows instead of the balance row");

                var countdown = AiFloatingWindow.FormatQuotaCountdown(new AiFloatingSnapshot
                {
                    Provider = AiProviderModes.DeepSeek, GoQuota = quota
                }, now);
                Check(countdown.StartsWith("Go week 59%", StringComparison.Ordinal),
                    "the floating header countdown follows the tightest Go window");
                var alert = AiFloatingWindow.FormatQuotaAlert(new AiFloatingSnapshot
                {
                    Provider = AiProviderModes.DeepSeek, GoQuota = quota
                }, 40, 0);
                Check(alert is { Text: "Go week 59% left" } && alert.Value.ColorKey == "WarningText",
                    "the floating alert reports a Go window below the warning threshold");

                render.Invoke(floating, [new AiFloatingSnapshot
                {
                    Provider = AiProviderModes.DeepSeek,
                    DeepSeekBalanceText = "¥12.34", DeepSeekBalanceCapturedAt = now
                }]);
                Check(((FrameworkElement)floating.FindName("GoQuotaRow")).Visibility == Visibility.Collapsed &&
                      ((FrameworkElement)floating.FindName("DeepSeekQuotaRow")).Visibility == Visibility.Visible &&
                      ((TextBlock)floating.FindName("DeepSeekBalanceText")).Text == "¥12.34",
                    "without a Go plan the floating monitor keeps showing the DeepSeek balance");
            }
            finally
            {
                floating.Close();
                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void VerifyGoQuotaDashboardRender()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-opencode-go-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var service = new AiManagerService(settingsPath);
            var settings = service.ReloadSettings();
            settings.Provider = AiProviderModes.DeepSeek;
            settings.OpenCodeGoApiKey = "oc_sk_dashboard";
            service.SaveSettings(settings);
            var attention = new ResetSignalAttention(Path.Combine(root, "attention.json"));
            var main = new AiManagerWindow(service, attention);
            try
            {
                var now = DateTimeOffset.Now;
                var snapshot = new AiManagerSnapshot
                {
                    Provider = AiProviderModes.DeepSeek,
                    DeepSeek = new AiDeepSeekSnapshot
                    {
                        Balance = new AiDeepSeekBalance { Currency = "CNY", Total = 0, IsAvailable = false },
                        GoQuota = new AiGoQuotaSnapshot
                        {
                            Rolling = new AiGoQuotaWindow { Id = "rolling", UsedPercent = 4, ResetsAt = now.AddHours(2) },
                            Weekly = new AiGoQuotaWindow { Id = "weekly", UsedPercent = 41 },
                            Monthly = new AiGoQuotaWindow { Id = "monthly", UsedPercent = 20 },
                            CapturedAt = now
                        },
                        CapturedAt = now
                    },
                    CapturedAt = now
                };
                typeof(AiManagerWindow).GetMethod("RenderSnapshot",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [snapshot]);
                Check(((TextBlock)main.FindName("RemainingCaptionText")).Text == "PLAN LEFT" &&
                      ((TextBlock)main.FindName("RemainingText")).Text == "59" &&
                      ((TextBlock)main.FindName("QuotaStateText")).Text == "Plan allowance available" &&
                      ((TextBlock)main.FindName("MainResetText")).Text.Contains("DeepSeek balance",
                          StringComparison.Ordinal),
                    "the dashboard account card leads with the Go plan allowance and keeps the balance as context");

                typeof(AiManagerWindow).GetMethod("LoadDeepSeekControls",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
                Check(main.FindName("OpenCodeGoApiKeyBox") is PasswordBox { Password: "oc_sk_dashboard" } &&
                      main.FindName("DeepSeekSettingsStatusText") is TextBlock status &&
                      status.Text.Contains("OpenCode Go", StringComparison.Ordinal),
                    "the DeepSeek settings card loads and describes the OpenCode Go key");
            }
            finally { main.Close(); }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
