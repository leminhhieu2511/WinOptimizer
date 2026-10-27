using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WinOptimizer.Helpers;
using WinOptimizer.Services;

namespace WinOptimizer.Pages;

public partial class CleanerPage : UserControl
{
    private readonly List<JunkCategory> _categories = JunkCleanerService.CreateCategories();

    public CleanerPage()
    {
        InitializeComponent();
        foreach (var category in _categories)
        {
            if (!category.CanSelect) category.IsSelected = false;
            category.PropertyChanged += Category_PropertyChanged;
        }
        CategoryList.ItemsSource = _categories;
    }

    private void Category_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JunkCategory.IsSelected)) UpdateTotal();
    }

    private void UpdateTotal()
    {
        var scanned = _categories.Where(c => c.IsSelected && c.SizeBytes >= 0).ToList();
        if (scanned.Count > 0)
            TotalText.Text = $"{Format.Bytes(scanned.Sum(c => c.SizeBytes))} selected to clean";
    }

    private void SetBusy(bool busy, string status)
    {
        ScanButton.IsEnabled = CleanButton.IsEnabled = !busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        SetBusy(true, "Scanning…");
        foreach (var category in _categories.Where(c => c.CanSelect))
        {
            StatusText.Text = $"Scanning {category.Name.ToLowerInvariant()}…";
            var (bytes, count) = await LowPriority.Run(() => JunkCleanerService.Scan(category));
            category.FileCount = count;
            category.SizeBytes = bytes;
        }
        long total = _categories.Where(c => c.SizeBytes > 0).Sum(c => c.SizeBytes);
        JunkCleanerService.LastScanBytes = total;
        SetBusy(false, $"Found {Format.Bytes(total)} of junk in total.");
        UpdateTotal();
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _categories.Where(c => c.IsSelected && c.CanSelect).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select at least one category to clean.";
            return;
        }

        string list = string.Join("\n", selected.Select(c => "  • " + c.Name));
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"Permanently delete files in these categories?\n\n{list}\n\nThis can't be undone.",
            "Clean junk files", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        SetBusy(true, "Cleaning…");
        long freed = 0;
        foreach (var category in selected)
        {
            StatusText.Text = $"Cleaning {category.Name.ToLowerInvariant()}…";
            freed += await LowPriority.Run(() => JunkCleanerService.Clean(category));
        }

        await ScanAsync();
        StatusText.Text = $"Freed {Format.Bytes(freed)}. Anything left was in use or protected.";
        TotalText.Text = $"Freed {Format.Bytes(freed)}";
    }
}
