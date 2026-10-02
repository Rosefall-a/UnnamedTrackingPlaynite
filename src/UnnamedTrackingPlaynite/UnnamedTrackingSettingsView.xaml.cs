using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace UnnamedTrackingPlaynite;

public partial class UnnamedTrackingSettingsView : UserControl
{
    public UnnamedTrackingSettingsView()
    {
        InitializeComponent();
    }

    private void ApiKeyBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is UnnamedTrackingSettings settings) ApiKeyBox.Password = settings.AuthValue;
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ApiKeyBox.IsLoaded && DataContext is UnnamedTrackingSettings settings) settings.AuthValue = ApiKeyBox.Password;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is UnnamedTrackingSettings settings))
        {
            ConnectionStatus.Text = "Unable to access plugin settings.";
            return;
        }

        TestButton.IsEnabled = false;
        ConnectionStatus.Text = "Testing...";
        try
        {
            await settings.TestConnectionAsync();
            ConnectionStatus.Text = "Connection successful.";
        }
        catch (Exception ex)
        {
            ConnectionStatus.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is UnnamedTrackingSettings settings))
        {
            UploadStatus.Text = "Unable to access plugin settings.";
            return;
        }

        UploadButton.IsEnabled = false;
        UploadStatus.Text = "Syncing Playnite library...";
        CancelButton.IsEnabled = true;
        PreviewButton.IsEnabled = false;

        try
        {
            var result = await settings.UploadLibraryAsync((completed, total, game) => Dispatcher.BeginInvoke(new Action(() => UploadStatus.Text = $"Syncing {completed}/{total}: {game}")));
            UploadStatus.Text = FormatResult(result);
        }
        catch (OperationCanceledException)
        {
            UploadStatus.Text = "Library sync cancelled.";
        }
        catch (Exception ex)
        {
            UploadStatus.Text = $"Library sync could not be started: {ex.Message}";
        }
        finally
        {
            UploadButton.IsEnabled = true;
            PreviewButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is UnnamedTrackingSettings settings))
        {
            UploadStatus.Text = "Unable to access plugin settings.";
            return;
        }

        UploadButton.IsEnabled = false;
        PreviewButton.IsEnabled = false;
        UploadStatus.Text = "Building synchronization preview...";
        CancelButton.IsEnabled = true;
        try
        {
            var result = await settings.PreviewLibraryAsync();
            var builder = new StringBuilder();
            builder.AppendLine("Synchronization preview");
            builder.AppendLine($"Total library entries: {result.TotalGames}");
            builder.AppendLine($"Would create: {result.WouldCreate}");
            builder.AppendLine($"Would update: {result.WouldUpdate}");
            builder.AppendLine($"Ignored: {result.Ignored}");
            if (result.Creates.Count > 0) builder.AppendLine("Creates: " + string.Join(", ", result.Creates));
            if (result.Updates.Count > 0) builder.AppendLine("Updates: " + string.Join(", ", result.Updates));
            if (result.IgnoredGames.Count > 0) builder.AppendLine("Ignored games: " + string.Join(", ", result.IgnoredGames));
            UploadStatus.Text = builder.ToString();
        }
        catch (OperationCanceledException) { UploadStatus.Text = "Preview cancelled."; }
        catch (Exception ex)
        {
            UploadStatus.Text = $"Preview failed: {ex.Message}";
        }
        finally
        {
            UploadButton.IsEnabled = true;
            PreviewButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is UnnamedTrackingSettings settings)
        {
            settings.CancelSync();
            UploadStatus.Text = "Cancelling library sync...";
        }
    }

    private static string FormatResult(UnnamedTrackingUploadResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Library sync complete");
        builder.AppendLine();
        builder.AppendLine($"Total: {result.TotalGames}");
        builder.AppendLine($"Succeeded: {result.SucceededGames}");
        builder.AppendLine($"Failed: {result.FailedGames}");
        builder.AppendLine($"Artwork warnings: {result.WarningCount}");
        foreach (var warning in result.Warnings) builder.AppendLine($"  {warning.GameName}: {warning.Operation} — {warning.ResponseBody}");

        if (result.FailedGames == 0)
        {
            return builder.ToString();
        }

        builder.AppendLine();
        builder.AppendLine("Failed:");

        foreach (var failure in result.Failures)
        {
            builder.AppendLine($"  {failure.GameName}");
            if (failure.GameId.HasValue)
            {
                builder.AppendLine($"    Playnite ID: {failure.GameId.Value}");
            }

            var status = failure.StatusCode > 0 ? $"HTTP {failure.StatusCode}" : "No HTTP status";
            builder.AppendLine($"    {failure.Operation} failed ({status}):");
            var responseBody = string.IsNullOrWhiteSpace(failure.ResponseBody)
                ? "<empty server response>"
                : failure.ResponseBody.Trim();
            builder.AppendLine($"      {responseBody}");
        }

        return builder.ToString();
    }
}
