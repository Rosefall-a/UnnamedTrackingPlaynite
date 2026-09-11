using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace UnnamedTrackingPlaynite;

public partial class UnnamedTrackingSettingsView : UserControl
{
    public UnnamedTrackingSettingsView()
    {
        InitializeComponent();
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

        try
        {
            var result = await settings.UploadLibraryAsync();
            UploadStatus.Text = FormatResult(result);
        }
        catch (Exception ex)
        {
            UploadStatus.Text = $"Library sync could not be started: {ex.Message}";
        }
        finally
        {
            UploadButton.IsEnabled = true;
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
