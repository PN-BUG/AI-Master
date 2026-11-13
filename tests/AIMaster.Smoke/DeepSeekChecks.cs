using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyDeepSeekSupport()
    {
        VerifyDeepSeekParsing();
        VerifyDeepCodeReader();
        VerifyDeepSeekDesktopSupport();
        VerifyDeepSeekSettings();
        VerifyDeepSeekRefresh();
        VerifyProviderAwareFloatingSnapshot();
    }

    private static void VerifyDeepSeekParsing()
    {
        using (var document = JsonDocument.Parse("""
            {"is_available":true,"balance_infos":[
              {"currency":"USD","total_balance":"0.00","granted_balance":"0.00","topped_up_balance":"0.00"},
              {"currency":"CNY","total_balance":"88.50","granted_balance":"8.50","topped_up_balance":"80.00"}]}
            """))
        {
            var balance = DeepSeekClient.ParseApiBalance(document.RootElement);
            Check(balance.Currency == "CNY" && Math.Abs(balance.Total - 88.5) < 0.001 &&
                  Math.Abs(balance.Granted - 8.5) < 0.001 && Math.Abs(balance.ToppedUp - 80) < 0.001 &&
                  balance.IsAvailable && balance.TotalText == "¥88.50" && balance.PaidText == "¥80.00",
                "DeepSeek API balance prefers a funded currency and formats the paid/granted split");
        }

        using (var document = JsonDocument.Parse("""
            {"code":0,"data":{"biz_code":0,"biz_data":{
              "normal_wallets":[{"currency":"USD","balance":12.5}],
              "bonus_wallets":[{"currency":"USD","balance":"2.5"}]}}}
            """))
        {
            var balance = DeepSeekClient.ParsePlatformBalance(document.RootElement);
            Check(balance.Currency == "USD" && Math.Abs(balance.Total - 15) < 0.001 &&
                  Math.Abs(balance.ToppedUp - 12.5) < 0.001 && Math.Abs(balance.Granted - 2.5) < 0.001 &&
                  balance.IsAvailable && balance.TotalText == "$15.00",
                "DeepSeek Platform summary adds paid and granted wallets per currency");
        }

        using (var document = JsonDocument.Parse("""{"code":40002,"message":"expired"}"""))
        {
            var rejected = false;
            try { DeepSeekClient.ParsePlatformBalance(document.RootElement); }
            catch (DeepSeekRequestException) { rejected = true; }
            Check(rejected, "expired DeepSeek Platform sessions are reported as credential errors");
        }

        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8));
        using var amount = JsonDocument.Parse("""
            {"code":0,"data":{"biz_code":0,"biz_data":{"usage":[
              {"date":"2026-11-05","api_key_id":"key-1","total_tokens":1200},
              {"date":"2026-11-04","api_key_id":"key-1","total_tokens":800},
              {"date":"2026-10-01","api_key_id":"key-1","total_tokens":9999}]}}}
            """);
        using var cost = JsonDocument.Parse("""
            {"code":0,"data":{"biz_code":0,"biz_data":{"usage":[
              {"date":"2026-11-05","api_key_id":"key-1","cost":0.12},
              {"date":"2026-11-04","api_key_id":"key-1","cost":0.08}],
              "model_costs":[{"model":"deepseek-v4-pro","cost":0.15,"total_tokens":1500}]}}}
            """);
        var usage = DeepSeekClient.ParseUsage(amount.RootElement, cost.RootElement, now,
            new DateOnly(2026, 10, 7), new DateOnly(2026, 11, 5), monthlyFallback: false);
        Check(usage.TodayTokens == 1200 && Math.Abs(usage.TodayCost - 0.12) < 0.0001 &&
              usage.PeriodTokens == 2000 && Math.Abs(usage.PeriodCost - 0.20) < 0.0001 &&
              usage.ApiKeyCount == 1 && usage.TopModel == "deepseek-v4-pro" &&
              usage.Daily.Count == 2 && usage.Daily[^1].Date == new DateOnly(2026, 11, 5),
            "DeepSeek usage aggregates per-key daily buckets and keeps model spend");

        using var undatedAmount = JsonDocument.Parse("""{"data":{"biz_data":{"total_tokens":5000}}}""");
        using var undatedCost = JsonDocument.Parse("""{"data":{"biz_data":{"cost":1.25}}}""");
        var monthly = DeepSeekClient.ParseUsage(undatedAmount.RootElement, undatedCost.RootElement, now,
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), monthlyFallback: true);
        Check(monthly.PeriodTokens == 5000 && Math.Abs(monthly.PeriodCost - 1.25) < 0.0001 &&
              monthly.Daily.Count == 0 && monthly.IsMonthlyFallback && monthly.TodayTokens == 0,
            "monthly DeepSeek totals without dated buckets still produce a usage summary");
    }

    private static void VerifyDeepCodeReader()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-deepcode-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8));
            var alpha = Path.Combine(root, "alpha-project");
            var beta = Path.Combine(root, "beta-project");
            Directory.CreateDirectory(alpha);
            Directory.CreateDirectory(beta);

            File.WriteAllText(Path.Combine(root, "settings.json"),
                """{"env":{"MODEL":"deepseek-v4-flash"},"thinkingEnabled":true,"reasoningEffort":"max"}""");
            File.WriteAllText(Path.Combine(alpha, "sessions-index.json"), JsonSerializer.Serialize(new
            {
                version = 1,
                originalPath = @"F:\Work\Alpha",
                entries = new object[]
                {
                    new { id = "s1", summary = "Fix login bug", status = "processing",
                        usage = new { total_tokens = 1200, prompt_tokens = 900, completion_tokens = 300 },
                        usagePerModel = new Dictionary<string, object> { ["deepseek-v4-pro"] = new { total_tokens = 1200 } },
                        updateTime = now.AddHours(-1).ToString("O") },
                    new { id = "s2", summary = "Add dark mode\nsecond line", status = "ask_permission",
                        usage = new { total_tokens = 300 },
                        usagePerModel = new Dictionary<string, object> { ["deepseek-v4-flash"] = new { total_tokens = 300 } },
                        updateTime = now.ToString("O") }
                }
            }));
            File.WriteAllText(Path.Combine(beta, "sessions-index.json"), JsonSerializer.Serialize(new
            {
                version = 1,
                originalPath = @"F:\Work\Beta",
                entries = new object[]
                {
                    new { id = "s3", summary = "Refactor auth", status = "completed",
                        usage = new { total_tokens = 500 }, updateTime = now.AddDays(-2).ToString("O") },
                    new { id = "s4", summary = "Old failure", status = "failed",
                        usage = new { total_tokens = 100 }, updateTime = now.AddDays(-10).ToString("O") }
                }
            }));
            Directory.CreateDirectory(Path.Combine(beta, "broken-project"));
            File.WriteAllText(Path.Combine(beta, "broken-project", "sessions-index.json"), "{not json");

            var reader = new DeepCodeTaskReader(root, Path.Combine(root, "settings.json"),
                () => now, TimeSpan.Zero);
            var snapshot = reader.Read(20, new DateOnly(2026, 11, 2));

            var tasks = snapshot.Tasks;
            Check(tasks.Count == 4 && tasks[0].Id == "s2" && tasks[0].Status == "waitingOnApproval" &&
                  tasks[0].Title.Contains("Add dark mode", StringComparison.Ordinal) &&
                  tasks[1].Id == "s1" && tasks[1].Status == "inProgress" && tasks[1].ModelName == "deepseek-v4-pro" &&
                  tasks[0].ProjectPath == @"F:\Work\Alpha" && tasks[0].ReasoningEffort == "max",
                "Deep Code sessions map statuses, models, reasoning effort, and project roots");
            var completed = tasks.First(item => item.Id == "s3");
            Check(completed.ModelName == "deepseek-v4-flash" && completed.ProjectPath == @"F:\Work\Beta",
                "Deep Code falls back to the CLI settings model when a session has no per-model usage");

            var usage = snapshot.LocalUsage;
            Check(usage.TotalTokens == 2000 && usage.TodayTokens == 1500 &&
                  usage.SessionCount == 3 && usage.Projects.Count == 2 &&
                  usage.Projects[0].ProjectName == "Alpha" && usage.Projects[0].Tokens == 1500 &&
                  usage.MostUsedModel == "deepseek-v4-pro" &&
                  usage.DailyUsage.Sum(item => item.Tokens) == 2000,
                "Deep Code local usage is attributed by session update day and project");
            Check(DeepCodeTaskReader.MapStatus("permission_denied") == "failed" &&
                  DeepCodeTaskReader.MapStatus("waiting_for_user") == "waitingOnUserInput" &&
                  DeepCodeTaskReader.MapStatus("pending") == "pending" &&
                  DeepCodeTaskReader.MapStatus("unknown") == "notLoaded",
                "Deep Code statuses map to the shared task vocabulary");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void VerifyDeepSeekSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-deepseek-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        try
        {
            var service = new AiManagerService(settingsPath: path);
            try
            {
                Check(service.ReloadSettings().Provider == AiProviderModes.Codex,
                    "existing settings keep Codex as the default data source");
                var settings = service.ReloadSettings();
                settings.Provider = "deepseek";
                settings.DeepSeekApiKey = "sk-test-secret";
                settings.DeepSeekPlatformToken = "platform-secret";
                settings.DeepSeekBalanceWarning = 5;
                service.SaveSettings(settings);

                var json = File.ReadAllText(path);
                Check(!json.Contains("sk-test-secret", StringComparison.Ordinal) &&
                      !json.Contains("platform-secret", StringComparison.Ordinal) &&
                      json.Contains("deepseekApiKeyProtected", StringComparison.Ordinal),
                    "DeepSeek secrets are persisted through protected fields only");

                var restored = service.ReloadSettings();
                Check(restored.Provider == AiProviderModes.DeepSeek &&
                      restored.DeepSeekApiKey == "sk-test-secret" &&
                      restored.DeepSeekPlatformToken == "platform-secret" &&
                      Math.Abs(restored.DeepSeekBalanceWarning - 5) < 0.001,
                    "DeepSeek provider, credentials, and warning line round-trip through settings");

                restored.Provider = "DeepSeek";
                service.SaveSettings(restored);
                Check(service.ReloadSettings().Provider == AiProviderModes.DeepSeek,
                    "provider identifiers are normalized case-insensitively");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void VerifyDeepSeekRefresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-deepseek-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.Now;
            var projects = Path.Combine(root, "projects", "demo-project");
            Directory.CreateDirectory(projects);
            File.WriteAllText(Path.Combine(projects, "sessions-index.json"), JsonSerializer.Serialize(new
            {
                version = 1,
                originalPath = @"F:\Work\Demo",
                entries = new[]
                {
                    new { id = "d1", summary = "Wire provider", status = "completed",
                        usage = new { total_tokens = 450 }, updateTime = now.ToString("O") }
                }
            }));

            var handler = new StubHandler(_ => _httpResponse("""
                {"is_available":true,"balance_infos":[
                  {"currency":"CNY","total_balance":"12.34","granted_balance":"2.34","topped_up_balance":"10.00"}]}
                """));
            using var http = new HttpClient(handler);
            using var client = new DeepSeekClient(http);
            var reader = new DeepCodeTaskReader(Path.Combine(root, "projects"),
                Path.Combine(root, "settings.json"), () => now, TimeSpan.Zero);
            var service = new AiManagerService(Path.Combine(root, "settings.json"),
                deepSeekClient: client, deepCodeTaskReader: reader);
            try
            {
                var settings = service.ReloadSettings();
                settings.Provider = AiProviderModes.DeepSeek;
                settings.DeepSeekApiKey = "sk-test";
                service.SaveSettings(settings);

                var snapshot = service.RefreshAsync().GetAwaiter().GetResult();
                Check(snapshot.Provider == AiProviderModes.DeepSeek && snapshot.IsDeepSeek &&
                      snapshot.Limits.Count == 0 && snapshot.DeepSeek?.Balance is { } balance &&
                      Math.Abs(balance.Total - 12.34) < 0.001 &&
                      snapshot.DeepSeek.Usage is null && snapshot.DeepSeek.UsageError is not null &&
                      snapshot.Threads.Count == 1 && snapshot.Threads[0].Id == "d1" &&
                      snapshot.LocalUsage.TodayTokens == 450 && snapshot.DailyUsage.Count == 0,
                    "DeepSeek refresh combines the API balance with Deep Code tasks and local usage");
                Check(service.LastDeepSeekError is null && service.LastQuotaError is null,
                    "a successful DeepSeek balance read clears the provider error state");
                Check(service.PauseActiveThreadsAsync().GetAwaiter().GetResult() == 0,
                    "DeepSeek does not attempt a Codex thread interrupt");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void VerifyDeepSeekUi()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-deepseek-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new AiManagerService(Path.Combine(root, "settings.json"));
            var settings = service.ReloadSettings();
            settings.Provider = AiProviderModes.DeepSeek;
            service.SaveSettings(settings);
            var main = new AiManagerWindow(service);
            try
            {
                Check(main.FindName("DeepSeekProviderButton") is RadioButton { IsChecked: true } &&
                      main.FindName("CodexProviderButton") is RadioButton { IsChecked: false },
                    "the dashboard starts on the DeepSeek data source selected in settings");

                typeof(AiManagerWindow).GetMethod("LoadDeepSeekControls", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, null);
                Check(main.FindName("DeepSeekApiKeyBox") is PasswordBox &&
                      main.FindName("DeepSeekPlatformTokenBox") is PasswordBox &&
                      main.FindName("DeepSeekBalanceWarningBox") is TextBox warningBox && warningBox.Text == "0" &&
                      main.FindName("DeepSeekSettingsStatusText") is TextBlock status &&
                      status.Text.Contains("No API key", StringComparison.Ordinal),
                    "DeepSeek settings controls load their saved values and empty-key state");

                var snapshot = new AiManagerSnapshot
                {
                    Provider = AiProviderModes.DeepSeek,
                    DeepSeek = new AiDeepSeekSnapshot
                    {
                        Balance = new AiDeepSeekBalance
                        {
                            Currency = "CNY", Total = 12.34, Granted = 2.34, ToppedUp = 10, IsAvailable = true
                        },
                        Usage = new AiDeepSeekUsage
                        {
                            Currency = "CNY", TodayTokens = 100, TodayCost = 0.12,
                            PeriodTokens = 1000, PeriodCost = 1.2, TopModel = "deepseek-v4-pro",
                            Daily = [new AiDeepSeekUsageDay { Date = new DateOnly(2026, 11, 5), Tokens = 100, Cost = 0.12 }]
                        }
                    },
                    Threads = [new AiThreadSummary { Id = "d1", Title = "Deep task", Status = "inProgress",
                        ProjectPath = @"F:\Work\Demo" }],
                    LocalUsage = new AiLocalUsageSummary { TotalTokens = 1000, TodayTokens = 100 }
                };
                typeof(AiManagerWindow).GetMethod("RenderSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, [snapshot]);

                Check(((TextBlock)main.FindName("RemainingText")).Text == "¥12.34" &&
                      ((TextBlock)main.FindName("RemainingCaptionText")).Text == "AVAILABLE BALANCE" &&
                      ((TextBlock)main.FindName("RemainingUnitText")).Visibility == Visibility.Collapsed &&
                      ((Border)main.FindName("GuardCard")).Visibility == Visibility.Collapsed &&
                      ((Border)main.FindName("ForecastCard")).Visibility == Visibility.Collapsed &&
                      ((Border)main.FindName("ResetSignalsCard")).Visibility == Visibility.Collapsed &&
                      ((Border)main.FindName("DeepSeekCard")).Visibility == Visibility.Visible,
                    "DeepSeek mode shows the balance and hides Codex-only guard, forecast, and reset cards");
                Check(((ItemsControl)main.FindName("ThreadList")).ItemsSource is IEnumerable<AiThreadSummary> tasks &&
                      tasks.Count() == 1 && tasks.First().Id == "d1" &&
                      ((TextBlock)main.FindName("DailyUsageTokensText")).Text == "100" &&
                      ((TextBlock)main.FindName("DailyUsageShareText")).Text.Contains("Today", StringComparison.Ordinal),
                    "DeepSeek mode renders Deep Code tasks and platform usage in the shared cards");

                var codex = new AiManagerSnapshot
                {
                    Provider = AiProviderModes.Codex,
                    Limits = [new AiLimitWindow { LimitId = "codex", Name = "Codex", UsedPercent = 40,
                        WindowDurationMinutes = 10080, IsPrimary = true }]
                };
                typeof(AiManagerWindow).GetMethod("RenderSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, [codex]);
                var weeklyCard = ((ContentControl)main.FindName("WeeklyQuotaTile")).Content;
                Check(weeklyCard.GetType().GetProperty("RemainingValue")!.GetValue(weeklyCard) as string == "60" &&
                      ((Grid)main.FindName("QuotaSummaryGrid")).Visibility == Visibility.Visible &&
                      ((Grid)main.FindName("DeepSeekQuotaSummary")).Visibility == Visibility.Collapsed &&
                      ((TextBlock)main.FindName("RemainingCaptionText")).Text == "QUOTA LEFT" &&
                      ((TextBlock)main.FindName("RemainingUnitText")).Visibility == Visibility.Visible &&
                      ((Border)main.FindName("GuardCard")).Visibility == Visibility.Visible,
                    "switching back to Codex restores quota windows and the task guard");
            }
            finally { main.Close(); }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void CaptureDeepSeekPreview()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-deepseek-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var service = new AiManagerService(Path.Combine(root, "settings.json"));
        var settings = service.ReloadSettings();
        settings.Provider = AiProviderModes.DeepSeek;
        service.SaveSettings(settings);
        var main = new AiManagerWindow(service);
        var now = DateTimeOffset.Now;
        var snapshot = new AiManagerSnapshot
        {
            Provider = AiProviderModes.DeepSeek,
            CapturedAt = now,
            DeepSeek = new AiDeepSeekSnapshot
            {
                Balance = new AiDeepSeekBalance
                { Currency = "CNY", Total = 126.84, Granted = 16.84, ToppedUp = 110, IsAvailable = true },
                Usage = new AiDeepSeekUsage
                {
                    Currency = "CNY", TodayTokens = 182_400, TodayCost = 0.86,
                    PeriodTokens = 4_268_000, PeriodCost = 19.42, RequestCount = 168,
                    ApiKeyCount = 2, TopModel = "deepseek-v4-pro",
                    Daily = Enumerable.Range(0, 30)
                        .Select(index => new AiDeepSeekUsageDay
                        {
                            Date = DateOnly.FromDateTime(now.LocalDateTime).AddDays(index - 29),
                            Tokens = 90_000 + index * 7_500, Cost = 0.4 + index * 0.03
                        }).ToList()
                },
                CapturedAt = now
            },
            DailyUsage = Enumerable.Range(0, 7).Select(index => new AiDailyUsage
                { Date = DateOnly.FromDateTime(now.LocalDateTime).AddDays(index - 6), Tokens = 300_000 + index * 50_000 }).ToList(),
            Threads =
            [
                new AiThreadSummary { Id = "d1", Title = "接入 DeepSeek 数据源", Status = "inProgress",
                    ModelName = "deepseek-v4-pro", ReasoningEffort = "max", ProjectPath = @"F:\Work\Demo", UpdatedAt = now },
                new AiThreadSummary { Id = "d2", Title = "完善余额卡片布局", Status = "waitingOnApproval",
                    ModelName = "deepseek-v4-flash", ReasoningEffort = "high", ProjectPath = @"F:\Work\Demo", UpdatedAt = now.AddMinutes(-12) },
                new AiThreadSummary { Id = "d3", Title = "检查 Deep Code 会话解析", Status = "completed",
                    ModelName = "deepseek-v4-flash", UpdatedAt = now.AddHours(-3) }
            ],
            LocalUsage = new AiLocalUsageSummary
            {
                PeriodStart = DateOnly.FromDateTime(now.LocalDateTime).AddDays(-3),
                PeriodEnd = DateOnly.FromDateTime(now.LocalDateTime).AddDays(3),
                TotalTokens = 1_260_000, TodayTokens = 412_000, SessionCount = 3,
                MostUsedModel = "deepseek-v4-pro",
                DailyUsage = Enumerable.Range(0, 7).Select(index => new AiDailyUsage
                    { Date = DateOnly.FromDateTime(now.LocalDateTime).AddDays(index - 6), Tokens = 120_000 + index * 20_000 }).ToList(),
                TodayHourlyUsage = Enumerable.Range(0, 24).Select(hour => new AiHourlyUsage { Hour = hour }).ToList(),
                Projects =
                [
                    new AiProjectUsage { ProjectName = "Demo", ProjectPath = @"F:\Work\Demo", Tokens = 860_000,
                        SharePercent = 68.3, SessionCount = 2, MostUsedModel = "deepseek-v4-pro" },
                    new AiProjectUsage { ProjectName = "Tools", ProjectPath = @"F:\Work\Tools", Tokens = 400_000,
                        SharePercent = 31.7, SessionCount = 1, MostUsedModel = "deepseek-v4-flash" }
                ]
            }
        };
        typeof(AiManagerWindow).GetMethod("RenderSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, [snapshot]);
        var content = main.Content as FrameworkElement ?? main;
        content.Measure(new Size(1180, 860));
        content.Arrange(new Rect(0, 0, 1180, 860));
        content.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1180, 860, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var output = Path.Combine(root, "deepseek-dashboard.png");
        using (var stream = File.Create(output)) encoder.Save(stream);
        Console.WriteLine(output);
        main.Close();
    }

    private static void VerifyProviderAwareFloatingSnapshot()
    {
        var floating = new AiFloatingSnapshot
        {
            Provider = AiProviderModes.DeepSeek,
            QuotaText = "¥12.34",
            QuotaLabel = "Balance",
            QuotaDetail = "Paid ¥10.00 · Granted ¥2.34"
        };
        var applied = AiTaskSnapshotFeed.ApplyToFloating(floating,
            new[] { new AiThreadSummary { Id = "x", Title = "Task", Status = "inProgress" } });
        Check(applied.IsDeepSeek && applied.QuotaText == "¥12.34" && applied.QuotaLabel == "Balance" &&
              applied.QuotaDetail == "Paid ¥10.00 · Granted ¥2.34" && applied.Tasks.Count == 1,
            "shared task updates preserve the DeepSeek balance shown by the floating monitor");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage _httpResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
