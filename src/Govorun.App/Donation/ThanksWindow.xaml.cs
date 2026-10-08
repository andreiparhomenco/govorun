using System.Windows;
using Govorun.Core.Settings;
using Govorun.Core.Support;

namespace Govorun.App.Donation;

/// <summary>
/// The donation ask. Shown at most three times in the app's life (see
/// <see cref="DonationPolicy"/>), always after a finished dictation, and it leads with
/// what the user got out of Govorun rather than with the request.
/// </summary>
public partial class ThanksWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>Raised when the user chose to donate; the host opens the page.</summary>
    public event Action? DonateRequested;

    public ThanksWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        StatsText.Text = BuildStats(settings);
    }

    private static string BuildStats(AppSettings settings)
    {
        var words = settings.WordsDictated;
        var hours = DonationPolicy.HoursSaved(words);
        var dictations = settings.DictationCount;

        // Only claim saved time once it rounds to something believable.
        return hours >= 1
            ? $"Вы надиктовали {words:N0} слов за {dictations:N0} диктовок — это примерно {hours:N0} ч, которые не пришлось печатать."
            : $"Вы надиктовали {words:N0} слов за {dictations:N0} диктовок.";
    }

    private void OnDonateClicked(object sender, RoutedEventArgs e)
    {
        DonateRequested?.Invoke();
        Close();
    }

    private void OnLaterClicked(object sender, RoutedEventArgs e) => Close();

    private void OnAlreadyDonatedClicked(object sender, RoutedEventArgs e)
    {
        _settings.DonationPromptSuppressed = true;
        _settings.Save();
        Close();
    }
}
