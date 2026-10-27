using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinOptimizer.Helpers;
using WinOptimizer.Services;

namespace WinOptimizer.Pages;

public partial class MonitorPage : UserControl
{
    private const int HistoryLength = 60;

    private readonly SystemInfoService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _memoryHistory = new();
    private bool _refreshing;
    private int _tick;

    // CPU and memory refresh every tick (2 s); the process list every 2nd tick; drives every 15th (30 s).
    private const int ProcessEvery = 2;
    private const int DisksEvery = 15;

    public MonitorPage()
    {
        InitializeComponent();

        ComputerText.Text = $"{Environment.MachineName}  ·  {Environment.UserName}";
        OsText.Text = SystemInfoService.GetOsName();
        CpuNameText.Text = $"{SystemInfoService.GetCpuName()}  ·  {Environment.ProcessorCount} logical processors";

        _timer.Tick += async (_, _) => await RefreshAsync();

        // Only sample while the page is on screen and the window isn't minimized.
        PageActivity.Track(this,
            onActive: async () =>
            {
                _tick = 0; // refresh everything right away
                _timer.Start();
                await RefreshAsync();
            },
            onInactive: () => _timer.Stop());
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            bool processes = _tick % ProcessEvery == 0;
            bool disks = _tick % DisksEvery == 0;
            _tick++;
            var s = await Task.Run(() => _service.Sample(includeDisks: disks, includeProcesses: processes));

            UptimeText.Text = Format.Duration(s.Uptime);

            CpuText.Text = $"{s.CpuPercent:0}%";
            AddSample(_cpuHistory, s.CpuPercent);
            DrawChart(CpuChart, CpuLine, _cpuHistory);

            MemoryText.Text = $"{s.MemoryPercent:0}%";
            MemoryDetailText.Text = $"{Format.Bytes(s.MemoryUsed)} of {Format.Bytes(s.MemoryTotal)} in use";
            AddSample(_memoryHistory, s.MemoryPercent);
            DrawChart(MemoryChart, MemoryLine, _memoryHistory);

            if (s.Battery.HasBattery)
            {
                BatteryText.Text = $"{s.Battery.Percent}%";
                BatteryDetailText.Text = s.Battery.Charging ? "Charging"
                    : s.Battery.OnAcPower ? "Plugged in"
                    : s.Battery.Remaining is { } left ? $"{Format.Duration(left)} remaining"
                    : "On battery";
                BatteryBar.Value = s.Battery.Percent;
                BatteryBar.Visibility = Visibility.Visible;
            }
            else
            {
                BatteryText.Text = "AC";
                BatteryDetailText.Text = "No battery detected";
                BatteryBar.Visibility = Visibility.Collapsed;
            }

            if (s.Disks is not null)
                DiskList.ItemsSource = s.Disks.Select(d => new
                {
                    Title = $"{d.Label} ({d.Name})",
                    Detail = $"{Format.Bytes(d.FreeBytes)} free of {Format.Bytes(d.TotalBytes)}",
                    d.UsedPercent,
                }).ToList();

            if (s.TopProcesses is not null)
                ProcessList.ItemsSource = s.TopProcesses.Select(p => new
                {
                    p.Name,
                    p.Id,
                    Cpu = $"{p.CpuPercent:0.0}%",
                    Memory = Format.Bytes(p.MemoryBytes),
                }).ToList();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static void AddSample(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > HistoryLength) history.Dequeue();
    }

    private static void DrawChart(Canvas canvas, Polyline line, IReadOnlyCollection<double> history)
    {
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var points = new PointCollection();
        double step = width / (HistoryLength - 1);
        double x = width - (history.Count - 1) * step; // newest sample on the right edge
        foreach (double value in history)
        {
            points.Add(new Point(x, height - value / 100 * (height - 2) - 1));
            x += step;
        }
        line.Points = points;
    }
}
