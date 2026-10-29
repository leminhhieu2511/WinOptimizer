using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using WinOptimizer.Helpers;
using WinOptimizer.Services;
using WinOptimizer.Services.DiskSpace;

namespace WinOptimizer.Pages;

/// <summary>
/// Disk Space: a 5-step wizard (disk map → analysis → amount → confirm → progress/result) that moves free space from
/// a data partition to C: with <see cref="DiskMoveRunner"/>, plus the Resume screen for an interrupted move.
/// The move runs on a normal-priority background thread (Task.Run inside the runner); while it runs the window
/// cannot be closed, the sidebar is locked and a <see cref="PowerGuard"/> keeps the PC awake.
/// </summary>
public partial class DiskSpacePage : UserControl
{
    private enum Screen { Map, Analysis, Amount, Confirm, Progress, Resume }

    private static readonly Guid EfiType = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    private static readonly Guid MsrType = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    private const long GiB = DiskMovePlanner.GiB, MiB = DiskMovePlanner.MiB;

    private static readonly Brush RedBrush = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly Brush YellowBrush = Frozen(Color.FromRgb(0xD8, 0x8A, 0x00));
    private static readonly Brush GreenBrush = Frozen(Color.FromRgb(0x10, 0x8A, 0x10));

    private sealed record Analysis(IReadOnlyList<DiskSnapshot> Disks, DiskSnapshot Disk, PartitionSnapshot Target,
        PartitionSnapshot Source, SystemFacts Facts, IReadOnlyList<CheckResult> Results, int? MediaType);

    private Screen _screen = Screen.Map;
    private IReadOnlyList<DiskSnapshot> _disks = [];
    private DiskSnapshot? _targetDisk;
    private PartitionSnapshot? _target;
    private Analysis? _analysis;
    private long _maxAmount;
    private MovePlan? _plan;
    private bool _syncingAmount, _loading;

    // Run state
    private CancellationTokenSource? _cts;
    private PowerGuard? _guard;
    private bool _resuming, _hasRecovery;
    private readonly DispatcherTimer _batteryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<(TextBlock Icon, TextBlock Text)> _stepRows = [];
    private MoveStep _currentStep;
    private readonly HashSet<string> _liveWarnings = [];
    private MoveStep _copyStep = MoveStep.Done;
    private readonly Stopwatch _copyClock = new();
    private long _copyStartBytes;

    // What the result screen compares (fresh run: from the analysis; resume: from the journal).
    private Guid _targetGuid, _sourceGuid;
    private long _targetBefore, _sourceBefore, _amount;
    private string _targetName = "C:", _sourceName = "D:";
    private char? _sourceLetter;

    public DiskSpacePage()
    {
        InitializeComponent();
        _batteryTimer.Tick += (_, _) => UpdateBatteryBanner();

        if (DiskMoveSession.TestTargetDisk is int test)
        {
            TestModeBanner.Visibility = Visibility.Visible;
            TestModeText.Text = $"Debug test mode: disk {test} is the target disk (its first NTFS partition plays C:). " +
                                $"Windows RE is not managed. Journal: {DiskMoveSession.JournalPath}";
        }

        PageActivity.Track(this,
            onActive: async () =>
            {
                if (!DiskMoveSession.IsRunning && _screen is Screen.Map or Screen.Resume) await RefreshAsync();
            },
            onInactive: () => { });
    }

    // ================================================================== loading / map

    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            LimitedBanner.Visibility = AdminHelper.IsAdmin || DiskMoveSession.IsRunning ? Visibility.Collapsed : Visibility.Visible;
            bool unfinished = DiskMoveSession.HasUnfinishedMove();
            if (unfinished && !DiskMoveSession.ResumeDismissed)
            {
                await ShowResumeAsync();
                return;
            }

            ShowScreen(Screen.Map);
            PendingMoveBanner.Visibility = unfinished ? Visibility.Visible : Visibility.Collapsed;
            MapStatusText.Text = "Reading the disk layout…";
            AnalyzeButton.IsEnabled = false;
            try
            {
                _disks = await Task.Run(() => new DiskLayoutService().GetDisks());
            }
            catch (Exception ex)
            {
                MapStatusText.Text = "Could not read the disk layout: " + ex.Message;
                return;
            }
            (_targetDisk, _target) = FindTarget(_disks);
            BuildDiskMap(DiskMapHost, _disks);
            FillSources();
            MapStatusText.Text = _target is null
                ? DiskMoveSession.TestTargetDisk is int n
                    ? $"Disk {n} has no NTFS partition with a drive letter to use as the target."
                    : "The Windows partition was not found."
                : "Each bar is one disk; the darker part of a partition is the space in use.";
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The disk and partition that receive the space: the Windows partition, or (Debug test mode) the first
    /// NTFS partition with a letter on the test disk, treated as the Windows partition.</summary>
    private static (DiskSnapshot?, PartitionSnapshot?) FindTarget(IReadOnlyList<DiskSnapshot> disks)
    {
        if (DiskMoveSession.TestTargetDisk is int n)
        {
            var d = disks.FirstOrDefault(x => x.Number == n);
            if (d is null || d.IsBoot || d.IsSystem) return (null, null);
            var t = d.Partitions.FirstOrDefault(p => p.DriveLetter is not null &&
                                                     string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase));
            return t is null ? (d, null) : (d, t with { IsWindowsPartition = true });
        }
        foreach (var d in disks)
            if (d.Partitions.FirstOrDefault(p => p.IsWindowsPartition) is { } w)
                return (d, w);
        return (null, null);
    }

    private void FillSources()
    {
        SourceCombo.Items.Clear();
        TargetText.Text = _target is null ? "–" : $"{PartName(_target)} ({Format.Bytes(_target.Size)}{(DiskMoveSession.TestTargetDisk is null ? ", Windows" : ", test target")})";
        if (_target is null || _targetDisk is null)
        {
            AnalyzeButton.IsEnabled = false;
            return;
        }

        IEnumerable<(DiskSnapshot Disk, PartitionSnapshot Part)> candidates = DiskMoveSession.TestTargetDisk is null
            ? _disks.SelectMany(d => d.Partitions.Select(p => (d, p))).Where(x => x.p.DriveLetter is not null && !x.p.IsWindowsPartition)
            : _targetDisk.Partitions.Where(p => p.DriveLetter is not null && p.Offset > _target.Offset).Select(p => (_targetDisk, p));

        ComboBoxItem? preferred = null;
        foreach (var (disk, p) in candidates)
        {
            string used = p.UsedBytes is long u ? $", {Format.Bytes(u)} used" : "";
            var item = new ComboBoxItem
            {
                Content = $"{PartName(p)}  ·  {Format.Bytes(p.Size)} ({p.FileSystem ?? "no file system"}{used})  ·  disk {disk.Number}",
                Tag = p.Guid,
            };
            SourceCombo.Items.Add(item);
            bool sameDiskAfter = disk.Number == _targetDisk.Number && p.Offset > _target.Offset;
            if (preferred is null && sameDiskAfter) preferred = item;
            if (sameDiskAfter && p.DriveLetter == 'D') preferred = item;
        }
        SourceCombo.SelectedItem = preferred ?? (SourceCombo.Items.Count > 0 ? SourceCombo.Items[0] : null);
        AnalyzeButton.IsEnabled = SourceCombo.SelectedItem is not null;
        if (SourceCombo.Items.Count == 0) MapStatusText.Text = "There is no data partition with a drive letter to take space from.";
    }

    private PartitionSnapshot? SelectedSource() =>
        SourceCombo.SelectedItem is ComboBoxItem { Tag: Guid g }
            ? _disks.SelectMany(d => d.Partitions).FirstOrDefault(p => p.Guid == g)
            : null;

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AnalyzeButton.IsEnabled = SelectedSource() is not null && _target is not null;
        BuildDiskMap(DiskMapHost, _disks);   // highlight the chosen source
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private static string PartName(PartitionSnapshot p) =>
        p.DriveLetter is char c ? $"{c}:"
        : p.TypeGuid == EfiType ? "EFI"
        : p.TypeGuid == MsrType ? "MSR"
        : p.IsRecovery || p.TypeGuid == PartitionTypes.Recovery ? "Recovery"
        : $"Partition {p.Number}";

    // ------------------------------------------------------------------ proportional disk bars

    private enum Kind { System, Windows, Source, Recovery, Data, Free }

    private sealed record Segment(string Label, long Size, double Used, Kind Kind, string Tip, bool Highlight = false);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color KindColor(Kind k) => k switch
    {
        Kind.Windows => Color.FromRgb(0x2F, 0x7B, 0xD8),
        Kind.Source => Color.FromRgb(0xD0, 0x84, 0x10),
        Kind.Recovery => Color.FromRgb(0x8A, 0x6F, 0xC8),
        Kind.Data => Color.FromRgb(0x3A, 0x9A, 0x8A),
        Kind.Free => Color.FromRgb(0x80, 0x80, 0x80),
        _ => Color.FromRgb(0x70, 0x70, 0x70),
    };

    private Kind KindOf(PartitionSnapshot p, Guid? sourceGuid)
    {
        if (p.Guid == sourceGuid) return Kind.Source;
        if (p.IsWindowsPartition || p.Guid == _target?.Guid) return Kind.Windows;
        if (p.IsRecovery || p.TypeGuid == PartitionTypes.Recovery) return Kind.Recovery;
        if (p.TypeGuid == EfiType || p.TypeGuid == MsrType) return Kind.System;
        return Kind.Data;
    }

    private List<Segment> Segments(DiskSnapshot disk, IEnumerable<PartitionSnapshot> parts, Guid? sourceGuid, Guid? highlight = null)
    {
        var list = new List<Segment>();
        long pos = 0;
        foreach (var p in parts.OrderBy(p => p.Offset))
        {
            if (p.Offset - pos >= 64 * MiB)
                list.Add(new Segment("Free", p.Offset - pos, 0, Kind.Free, $"Unallocated, {Format.Bytes(p.Offset - pos)}"));
            double used = p.UsedBytes is long u && p.Size > 0 ? Math.Clamp((double)u / p.Size, 0, 1) : 0;
            string fs = p.FileSystem is null ? "" : $", {p.FileSystem}";
            string usedText = p.UsedBytes is long ub ? $", {Format.Bytes(ub)} used" : "";
            list.Add(new Segment(PartName(p), p.Size, used, KindOf(p, sourceGuid),
                $"{PartName(p)} (partition {p.Number}): {Format.Bytes(p.Size)}{fs}{usedText}", p.Guid == highlight));
            pos = Math.Max(pos, p.Offset + p.Size);
        }
        if (disk.Size - pos >= 64 * MiB)
            list.Add(new Segment("Free", disk.Size - pos, 0, Kind.Free, $"Unallocated, {Format.Bytes(disk.Size - pos)}"));
        return list;
    }

    private static FrameworkElement BuildBar(IReadOnlyList<Segment> segments)
    {
        var grid = new Grid { Height = 58 };
        long total = Math.Max(1, segments.Sum(s => s.Size));
        double min = total * 0.045;   // tiny partitions (EFI, MSR) stay readable
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(s.Size, min), GridUnitType.Star) });
            var c = KindColor(s.Kind);
            var fill = new Grid();
            fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(s.Used, 0.0001), GridUnitType.Star) });
            fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - s.Used, 0.0001), GridUnitType.Star) });
            fill.Children.Add(new Rectangle { Fill = Frozen(Color.FromArgb(0xC0, c.R, c.G, c.B)) });
            var label = new StackPanel { Margin = new Thickness(6, 4, 4, 4), VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = s.Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = Format.Bytes(s.Size), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            var inner = new Grid();
            inner.Children.Add(fill);
            inner.Children.Add(label);
            Grid.SetColumnSpan(label, 2);
            var cell = new Border
            {
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(s.Highlight ? 2.5 : 1),
                BorderBrush = s.Highlight ? GreenBrush : Frozen(Color.FromArgb(0xFF, c.R, c.G, c.B)),
                Background = Frozen(s.Kind == Kind.Free ? Color.FromArgb(0x10, c.R, c.G, c.B) : Color.FromArgb(0x38, c.R, c.G, c.B)),
                ClipToBounds = true,
                Child = inner,
                ToolTip = s.Tip,
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private void BuildDiskMap(Panel host, IReadOnlyList<DiskSnapshot> disks)
    {
        host.Children.Clear();
        Guid? source = SelectedSource()?.Guid;
        foreach (var d in disks)
        {
            var card = new Border { Style = (Style)FindResource("Card") };
            var stack = new StackPanel();
            string role = d.Number == _targetDisk?.Number ? DiskMoveSession.TestTargetDisk is null ? "  ·  Windows disk" : "  ·  test target" : "";
            stack.Children.Add(new TextBlock
            {
                Text = $"Disk {d.Number}  ·  {d.FriendlyName}  ·  {Format.Bytes(d.Size)}  ·  {(d.IsDynamic ? "Dynamic" : d.PartitionStyle)}{role}",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
            });
            stack.Children.Add(BuildBar(Segments(d, d.Partitions, source)));
            card.Child = stack;
            host.Children.Add(card);
        }
    }

    // ================================================================== ② analysis

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        var sourceGuid = SelectedSource()?.Guid ?? _analysis?.Source.Guid;
        if (sourceGuid is not Guid sg) return;

        ShowScreen(Screen.Analysis);
        CheckListHost.Children.Clear();
        AnalysisTitle.Text = "Analysis";
        AnalysisSummary.Text = "Analyzing… This includes a read-only disk check (chkdsk) of the source, which can take a few minutes.";
        CheckListHost.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 4, 0, 8) });
        SetAnalysisButtons(false);

        try
        {
            _analysis = await Task.Run(() =>
            {
                var disks = new DiskLayoutService().GetDisks();
                var (disk, target) = FindTarget(disks);
                if (disk is null || target is null) throw new InvalidOperationException("The target partition was not found.");
                var source = disks.SelectMany(d => d.Partitions).FirstOrDefault(p => p.Guid == sg)
                             ?? throw new InvalidOperationException("The source partition is gone. Refresh the disk map.");
                var facts = SystemFactsProbe.Collect(disk, source,
                    new SystemFactsOptions(RunChkdsk: true, JournalPath: DiskMoveSession.JournalPath));
                var results = ForTestTarget(DiskMovePrechecks.Run(disk, target, source, facts), facts, source);
                return new Analysis(disks, disk, target, source, facts, results, DiskMoveEstimate.ReadMediaType(disk.Number));
            });
        }
        catch (Exception ex)
        {
            CheckListHost.Children.Clear();
            AnalysisSummary.Text = "The analysis failed: " + ex.Message;
            AnalysisBackButton.IsEnabled = CheckAgainButton.IsEnabled = true;
            return;
        }

        _disks = _analysis.Disks;
        _targetDisk = _analysis.Disk;
        _target = _analysis.Target;
        _maxAmount = _analysis.Facts.SourceMinSize is long min ? DiskMovePlanner.MaxAmount(_analysis.Source, min) : 0;
        ShowAnalysis();
    }

    private void SetAnalysisButtons(bool enabled) =>
        AnalysisBackButton.IsEnabled = CheckAgainButton.IsEnabled = AnalysisNextButton.IsEnabled = enabled;

    private void ShowAnalysis()
    {
        var a = _analysis!;
        CheckListHost.Children.Clear();
        AnalysisTitle.Text = $"Analysis: {PartName(a.Source)} → {PartName(a.Target)}";
        int blockers = a.Results.Count(r => r.Severity == CheckSeverity.Blocker);
        int fixes = a.Results.Count(r => r.Severity == CheckSeverity.FixFirst);
        string max = _maxAmount >= DiskMovePlanner.MinAmount ? $" Up to {Format.Bytes(_maxAmount)} can be moved." : "";
        AnalysisSummary.Text = blockers > 0
            ? $"{blockers} problem(s) in red stop this move. Fix them and click Check again."
            : fixes > 0
                ? $"Fix the {fixes} item(s) in yellow before starting; the move itself can only start once they are solved.{max}"
                : $"Everything is ready.{max} Read the notes in green, then continue.";

        foreach (var r in a.Results.OrderBy(r => r.Severity))
            CheckListHost.Children.Add(CheckRow(r, a));

        SetAnalysisButtons(true);
        AnalysisNextButton.IsEnabled = blockers == 0 && _maxAmount >= DiskMovePlanner.MinAmount;
    }

    private FrameworkElement CheckRow(CheckResult r, Analysis a)
    {
        var (brush, label) = r.Severity switch
        {
            CheckSeverity.Blocker => (RedBrush, "Blocked"),
            CheckSeverity.FixFirst => (YellowBrush, "Fix first"),
            _ => (GreenBrush, "Note"),
        };
        var row = new DockPanel();
        var dot = new Ellipse { Width = 12, Height = 12, Fill = brush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 12, 0) };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(actions, Dock.Right);
        Button Action(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += click;
            actions.Children.Add(b);
            return b;
        }
        switch (r.Code)
        {
            case CheckCode.NotAdmin:
                Action("Restart as administrator", RestartAsAdmin_Click);
                break;
            case CheckCode.SourceInUse:
                var closable = a.Facts.SourceLockHolders.Where(h => h.CanClose).ToList();
                var close = Action("Close for me", async (_, _) => await CloseHoldersAsync(closable));
                close.IsEnabled = closable.Count > 0;
                Action("Check again", Analyze_Click);
                break;
            case CheckCode.PagefileOnSource:
                Action("Open settings", (_, _) => Launch("SystemPropertiesPerformance.exe"));
                Action("Check again", Analyze_Click);
                break;
            case CheckCode.OnBattery or CheckCode.RebootPending:
                Action("Check again", Analyze_Click);
                break;
            case CheckCode.JournalExists:
                Action("Go to Resume", async (_, _) => await ShowResumeAsync());
                break;
        }
        row.Children.Add(actions);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Foreground = brush });
        text.Children.Add(new TextBlock { Text = r.Message, TextWrapping = TextWrapping.Wrap });
        if (r.Code == CheckCode.SourceInUse && a.Facts.SourceLockHolders.Any(h => !h.CanClose))
            text.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("SecondaryText"),
                Text = "Services and drivers cannot be closed for you: " +
                       string.Join(", ", a.Facts.SourceLockHolders.Where(h => !h.CanClose).Select(h => h.Name).Distinct()) +
                       ". Stop them in Services (services.msc) or close the program that uses the drive, then check again.",
            });
        row.Children.Add(text);
        return new Border { Style = (Style)FindResource("Card"), Child = row, BorderBrush = brush };
    }

    /// <summary>"Close for me". Reached from an async void click handler, so nothing may escape from here.</summary>
    private async Task CloseHoldersAsync(IReadOnlyList<LockHolder> holders)
    {
        try
        {
            bool ok = await Task.Run(() => RestartManagerHelper.CloseApps(holders));
            if (!ok)
                MessageBox.Show(Window.GetWindow(this), "Some programs could not be closed. Close them yourself, then click Check again.",
                    "Close for me", MessageBoxButton.OK, MessageBoxImage.Information);
            await AnalyzeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Closing the programs failed: " + ex.Message +
                "\n\nClose them yourself, then click Check again.", "Close for me", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BackToMap_Click(object sender, RoutedEventArgs e) => ShowScreen(Screen.Map);

    private void AnalysisNext_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis is null) return;
        var a = _analysis;
        _syncingAmount = true;   // changing the range coerces the value; that must not count as the user's choice
        AmountSlider.Minimum = DiskMovePlanner.MinAmount / (double)GiB;
        AmountSlider.Maximum = _maxAmount / (double)GiB;
        _syncingAmount = false;
        AmountMinText.Text = $"Minimum {Format.Bytes(DiskMovePlanner.MinAmount)}";
        AmountMaxText.Text = $"Maximum {Format.Bytes(_maxAmount)} (keeps {Format.Bytes(Math.Max(GiB, (a.Source.UsedBytes ?? a.Source.Size) / 20))} spare on {PartName(a.Source)})";
        // A modest default (half the maximum, at most 10 GB); the user picks the real amount.
        double initial = Math.Min(_maxAmount / (double)GiB, Math.Max(1, Math.Min(10, Math.Floor(_maxAmount / (double)GiB / 2))));
        SetAmount(_amount >= DiskMovePlanner.MinAmount && _amount <= _maxAmount ? _amount / (double)GiB : initial);
        ShowScreen(Screen.Amount);
    }

    // ================================================================== ③ amount

    /// <summary>GB value → bytes: whole MiB, clamped to [min, max].</summary>
    private long ToAmount(double gb) =>
        Math.Clamp((long)Math.Round(gb * 1024) * MiB, DiskMovePlanner.MinAmount, Math.Max(DiskMovePlanner.MinAmount, _maxAmount));

    private void SetAmount(double gb)
    {
        _syncingAmount = true;
        try
        {
            long amount = ToAmount(gb);
            AmountSlider.Value = amount / (double)GiB;
            AmountBox.Text = (amount / (double)GiB).ToString("0.##", CultureInfo.CurrentCulture);
            UpdateAmountPreview(amount);
        }
        finally
        {
            _syncingAmount = false;
        }
    }

    private void AmountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingAmount || _analysis is null) return;
        SetAmount(Math.Round(e.NewValue * 10) / 10);   // 0.1 GB steps while dragging
    }

    private void AmountBox_LostFocus(object sender, RoutedEventArgs e) => CommitAmountText();

    private void AmountBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitAmountText();
    }

    private void CommitAmountText()
    {
        if (_analysis is null) return;
        if (double.TryParse(AmountBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double gb) ||
            double.TryParse(AmountBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out gb))
            SetAmount(gb);
        else
            SetAmount(AmountSlider.Value);
    }

    private void UpdateAmountPreview(long amount)
    {
        var a = _analysis!;
        _amount = amount;
        try
        {
            _plan = DiskMovePlanner.Plan(a.Disk, a.Target, a.Source, amount, a.Facts.SourceMinSize ?? a.Source.Size);
        }
        catch (ArgumentException ex)
        {
            _plan = null;
            AmountResultText.Text = ex.Message;
            AmountNextButton.IsEnabled = false;
            return;
        }
        AmountNextButton.IsEnabled = true;

        var recovery = _plan.HasRecovery ? _plan.PartitionsToShift[1] : null;
        var after = a.Disk.Partitions.Select(p =>
            p.Guid == a.Target.Guid ? p with { Size = p.Size + amount }
            : p.Guid == a.Source.Guid ? p with { Offset = p.Offset + amount, Size = p.Size - amount }
            : p.Guid == recovery?.Guid ? p with { Offset = p.Offset + amount }
            : p).ToList();
        BeforeBarHost.Content = BuildBar(Segments(a.Disk, a.Disk.Partitions, a.Source.Guid));
        AfterBarHost.Content = BuildBar(Segments(a.Disk, after, a.Source.Guid, highlight: a.Target.Guid));

        AmountResultText.Text = $"{PartName(a.Target)} {Format.Bytes(a.Target.Size)} → {Format.Bytes(a.Target.Size + amount)}     " +
                                $"{PartName(a.Source)} {Format.Bytes(a.Source.Size)} → {Format.Bytes(a.Source.Size - amount)}";
        string parts = recovery is null ? PartName(a.Source) : $"{PartName(a.Source)} and the Recovery partition";
        DataToCopyText.Text = $"Data to copy: {Format.Bytes(_plan.BytesToCopy)} (the whole of {parts} is shifted on the disk).";
        var time = DiskMoveEstimate.Duration(_plan.BytesToCopy, a.MediaType);
        EstimateText.Text = $"Estimated time: {DiskMoveEstimate.FormatMinutes(time)} on this {DiskMoveEstimate.MediaName(a.MediaType)} " +
                            "(every block is written and read back to check it).";
    }

    private void AmountBack_Click(object sender, RoutedEventArgs e) => ShowScreen(Screen.Analysis);

    private void AmountNext_Click(object sender, RoutedEventArgs e)
    {
        CommitAmountText();
        if (_plan is null || _analysis is null) return;
        ShowConfirm();
    }

    // ================================================================== ④ confirm

    private void ShowConfirm()
    {
        var a = _analysis!;
        var plan = _plan!;
        PlanSummaryHost.Children.Clear();
        void Line(string text, bool bold = false) => PlanSummaryHost.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        });
        Line($"Move {Format.Bytes(plan.Amount)} from {PartName(a.Source)} to {PartName(a.Target)}.", bold: true);
        Line($"{PartName(a.Target)} grows from {Format.Bytes(a.Target.Size)} to {Format.Bytes(a.Target.Size + plan.Amount)}.");
        Line($"{PartName(a.Source)} shrinks from {Format.Bytes(a.Source.Size)} to {Format.Bytes(a.Source.Size - plan.Amount)}. It keeps its letter, its files and its identity.");
        if (plan.HasRecovery)
            Line(DiskMoveSession.ManageWinRe
                ? "The Recovery partition moves along. Windows Recovery is turned off during its move and on again afterwards."
                : "The Recovery partition moves along (test mode: Windows Recovery is not touched).");
        Line($"Data to copy: {Format.Bytes(plan.BytesToCopy)}. Estimated time: {DiskMoveEstimate.FormatMinutes(DiskMoveEstimate.Duration(plan.BytesToCopy, a.MediaType))}.");
        Line($"{PartName(a.Source)} is unavailable while it moves. Do not use the computer for heavy work meanwhile.");

        ConfirmWarningsHost.Children.Clear();
        foreach (var w in a.Results.Where(r => r.Severity == CheckSeverity.Warning))
            ConfirmWarningsHost.Children.Add(CheckRow(w, a));

        var fixFirst = a.Results.Where(r => r.Severity == CheckSeverity.FixFirst).ToList();
        FixFirstNote.Visibility = fixFirst.Count > 0 || !AdminHelper.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        FixFirstText.Text = !AdminHelper.IsAdmin
            ? "Moving disk space needs administrator rights. Restart WinOptimizer as administrator to start."
            : "Fix these first (see Analysis), then click Check again: " + string.Join(" ", fixFirst.Select(r => r.Message));

        BackupCheckText.Text = $"I have backed up important data on {PartName(a.Source)}";
        BackupCheck.IsChecked = false;
        UpdateStartButton();
        ShowScreen(Screen.Confirm);
    }

    private void BackupCheck_Changed(object sender, RoutedEventArgs e) => UpdateStartButton();

    private void UpdateStartButton() =>
        StartButton.IsEnabled = BackupCheck.IsChecked == true && AdminHelper.IsAdmin && _plan is not null && _analysis is not null &&
                                !_analysis.Results.Any(r => r.Severity is CheckSeverity.Blocker or CheckSeverity.FixFirst);

    private void ConfirmBack_Click(object sender, RoutedEventArgs e) => ShowScreen(Screen.Amount);

    // ================================================================== ⑤ run

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (DiskMoveSession.IsRunning || !StartButton.IsEnabled || _analysis is null || _plan is null) return;
        var a = _analysis;
        var plan = _plan;
#if DEBUG
        if (TestDiskRefusal(a.Disk.Guid) is string refused)
        {
            MessageBox.Show(Window.GetWindow(this), refused, "Test target refused", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
#endif
        _targetGuid = a.Target.Guid;
        _sourceGuid = a.Source.Guid;
        _targetBefore = a.Target.Size;
        _sourceBefore = a.Source.Size;
        _targetName = PartName(a.Target);
        _sourceName = PartName(a.Source);
        _sourceLetter = a.Source.DriveLetter;
        _hasRecovery = plan.HasRecovery;

        var progress = new Progress<RunProgress>(OnProgress);
        IReadOnlyList<CheckResult> Recheck()
        {
            // Spec section 6: the checks run again right before step 2 (on the runner's thread).
            ((IProgress<RunProgress>)progress).Report(new RunProgress(MoveStep.LockSource, 0, 0, "Checking again before locking"));
#if DEBUG
            if (DiskMoveSession.TestTargetDisk is not null) Thread.Sleep(TimeSpan.FromSeconds(4));   // room to test Cancel at step 2
#endif
            var facts = SystemFactsProbe.Collect(a.Disk, a.Source, new SystemFactsOptions(RunChkdsk: false, JournalPath: null));
            return ForTestTarget(DiskMovePrechecks.Run(a.Disk, a.Target, a.Source, facts), facts, a.Source);
        }

        try
        {
            BeginRun(resuming: false);   // inside the try: EndRun (finally) always clears the running flag
            var runner = new DiskMoveRunner(new WindowsDiskMoveBackend(), DiskMoveSession.JournalPath, DiskMoveSession.ManageWinRe);
            var result = await runner.RunAsync(a.Disk, a.Target, a.Source, plan, Recheck, progress, _guard!.ShouldPause, _cts!.Token);
            await ShowResultAsync(result);
        }
        catch (OperationCanceledException)
        {
            ShowStopped("Cancelled", $"Nothing was moved. {_sourceName} has its original size and letter again.", ok: true);
        }
        catch (DiskMoveAbortedException ex)
        {
            ShowStopped("Stopped before any data was moved",
                "The checks right before locking found a problem, so the change was undone: " +
                string.Join(" ", ex.Results.Where(r => r.Severity != CheckSeverity.Warning).Select(r => r.Message)), ok: true);
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
        finally
        {
            EndRun();
        }
    }

    private void BeginRun(bool resuming)
    {
        _resuming = resuming;
        _cts = new CancellationTokenSource();
        _liveWarnings.Clear();
        LiveWarningsHost.Children.Clear();
        _copyStep = MoveStep.Done;
        DiskMoveSession.SetRunning(true);
        (Window.GetWindow(this) as MainWindow)?.SetNavigationLocked(true);
        var hwnd = Window.GetWindow(this) is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        _guard = new PowerGuard(hwnd);
        UpdateBatteryBanner();
        _batteryTimer.Start();
        LimitedBanner.Visibility = Visibility.Collapsed;

        BuildStepList();
        ProgressTitle.Text = resuming ? "Finishing the interrupted move" : "Moving disk space";
        CurrentStepText.Text = "Starting…";
        SpeedText.Text = "";
        MoveProgress.IsIndeterminate = true;
        CopyCard.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
        CancelRow.Visibility = resuming ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.IsEnabled = true;
        CancelHintText.Text = "You can cancel until the data starts moving (step 3). Everything is put back as it was.";
        ShowScreen(Screen.Progress);
    }

    private void EndRun()
    {
        _batteryTimer.Stop();
        BatteryBanner.Visibility = Visibility.Collapsed;
        _guard?.Dispose();
        _guard = null;
        _cts?.Dispose();
        _cts = null;
        DiskMoveSession.SetRunning(false);
        (Window.GetWindow(this) as MainWindow)?.SetNavigationLocked(false);
        CancelRow.Visibility = Visibility.Collapsed;
        CopyCard.Visibility = Visibility.Collapsed;
        LimitedBanner.Visibility = AdminHelper.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateBatteryBanner() =>
        BatteryBanner.Visibility = _guard?.OnBattery == true ? Visibility.Visible : Visibility.Collapsed;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null || _currentStep >= MoveStep.MoveSource) return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        CancelHintText.Text = "Cancelling… putting everything back as it was.";
    }

    private static readonly (MoveStep Step, string Title)[] StepTitles =
    [
        (MoveStep.Checks, "Check the layout and back up the partition table"),
        (MoveStep.ShrinkSource, "Shrink {S}"),
        (MoveStep.LockSource, "Lock {S} and hide it from Windows"),
        (MoveStep.MoveSource, "Move the data of {S}"),
        (MoveStep.CommitSource, "Update the partition table and give {S} its letter back"),
        (MoveStep.MoveRecovery, "Move the Recovery partition"),
        (MoveStep.ExtendTarget, "Extend {T}"),
        (MoveStep.Verify, "Verify the result"),
    ];

    private void BuildStepList()
    {
        StepListHost.Children.Clear();
        _stepRows.Clear();
        foreach (var (step, title) in StepTitles)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            var icon = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), Width = 22, VerticalAlignment = VerticalAlignment.Center };
            string text = $"{(int)step}. " + title.Replace("{S}", _sourceName).Replace("{T}", _targetName);
            if (step == MoveStep.MoveRecovery && !_hasRecovery) text = $"{(int)step}. No Recovery partition to move";
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(icon);
            row.Children.Add(label);
            StepListHost.Children.Add(row);
            _stepRows.Add((icon, label));
        }
        SetCurrentStep(MoveStep.Checks);
    }

    private void SetCurrentStep(MoveStep current)
    {
        _currentStep = current;
        for (int i = 0; i < _stepRows.Count; i++)
        {
            var (icon, label) = _stepRows[i];
            bool skipped = i == (int)MoveStep.MoveRecovery && !_hasRecovery;
            if (i < (int)current || current == MoveStep.Done)
            {
                icon.Text = skipped ? "" : "";   // dash / check
                icon.Foreground = skipped ? (Brush)FindResource("TextFillColorSecondaryBrush") : GreenBrush;
                label.FontWeight = FontWeights.Normal;
            }
            else if (i == (int)current)
            {
                icon.Text = "";   // play
                icon.Foreground = (Brush)FindResource("AccentTextFillColorPrimaryBrush");
                label.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                icon.Text = "";   // ring
                icon.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
                label.FontWeight = FontWeights.Normal;
            }
        }
    }

    private void OnProgress(RunProgress p)
    {
        if (p.Message.StartsWith("Warning: ", StringComparison.Ordinal))
        {
            string w = p.Message["Warning: ".Length..];
            if (_liveWarnings.Add(w))
                LiveWarningsHost.Children.Add(new Border
                {
                    Style = (Style)FindResource("Card"), BorderBrush = YellowBrush, BorderThickness = new Thickness(2),
                    Child = new TextBlock { Text = w, TextWrapping = TextWrapping.Wrap },
                });
            return;
        }
        if (p.Step == MoveStep.Done)
        {
            SetCurrentStep(MoveStep.Done);
            return;
        }

        SetCurrentStep(p.Step);
        bool copying = p.Step is MoveStep.MoveSource or MoveStep.MoveRecovery && p.BytesTotal > 0;
        if (p.Message == "Paused")
        {
            CurrentStepText.Text = $"Step {(int)p.Step}: paused — the battery is below 20%. Plug in your charger and the move continues by itself.";
            SpeedText.Text = "";
            _copyStep = MoveStep.Done;   // restart the speed measurement after the pause
        }
        else
        {
            CurrentStepText.Text = $"Step {(int)p.Step}: {p.Message}";
        }

        if (copying)
        {
            MoveProgress.IsIndeterminate = false;
            MoveProgress.Value = 1000.0 * p.BytesDone / p.BytesTotal;
            if (_copyStep != p.Step || p.BytesDone < _copyStartBytes)
            {
                _copyStep = p.Step;
                _copyStartBytes = p.BytesDone;
                _copyClock.Restart();
            }
            double seconds = _copyClock.Elapsed.TotalSeconds;
            if (seconds >= 1 && p.BytesDone > _copyStartBytes)
            {
                double speed = (p.BytesDone - _copyStartBytes) / seconds;
                var left = TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / speed);
                string remaining = left.TotalMinutes < 1 ? "less than a minute left" : $"about {Math.Ceiling(left.TotalMinutes):0} min left";
                SpeedText.Text = $"{Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)}  ·  {Format.Bytes(speed)}/s  ·  {remaining}";
            }
            else
            {
                SpeedText.Text = $"{Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)}";
            }
        }
        else if (p.Message != "Paused")
        {
            MoveProgress.IsIndeterminate = true;
            SpeedText.Text = "";
        }

        if (!_resuming && p.Step >= MoveStep.MoveSource && p.Step != MoveStep.Done)
        {
            // The point of no return has passed. A Cancel clicked after the runner's last check is too late: say so.
            bool lateCancel = _cts?.IsCancellationRequested == true;
            CancelRow.Visibility = lateCancel ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = false;
            if (lateCancel)
                CancelHintText.Text = "The cancel came too late: the data had already started moving, so the move will be finished.";
        }
    }

    // ------------------------------------------------------------------ result

    private async Task ShowResultAsync(DiskMoveResult result)
    {
        SetCurrentStep(MoveStep.Done);
        ResultPanel.Visibility = Visibility.Visible;
        ResultResumeButton.Visibility = Visibility.Collapsed;
        ResultPathsText.Visibility = Visibility.Collapsed;
        ResultCard.BorderBrush = GreenBrush;

        string? letterChange = result.FinalSourceLetter is char fl && _sourceLetter is char ol && fl != ol
            ? $"Your data drive is now {fl}: (it was {ol}:)." : null;
        switch (result.Outcome)
        {
            case DiskMoveOutcome.Completed:
                ResultTitle.Text = $"Done: {_targetName} has {Format.Bytes(_amount)} more space";
                ResultText.Text = letterChange ?? $"{_sourceName} kept its letter, files and identity.";
                ResultVerifyText.Text = result.Warnings.Count > 0
                    // Review I2: step 7 problems are warnings; the data itself was verified block by block while copying.
                    ? "The data was copied and verified block by block. Please read the notes below."
                    : "Verified: the partitions have the planned sizes and positions, a read-only chkdsk found no errors on " +
                      $"{(result.FinalSourceLetter is char l ? $"{l}:" : _sourceName)}" +
                      (DiskMoveSession.ManageWinRe && _hasRecovery ? ", and Windows Recovery is in its original state." : ".");
                break;
            case DiskMoveOutcome.Undone:
                ResultTitle.Text = "The interrupted attempt was undone";
                ResultText.Text = $"No data had been moved yet. {_sourceName} has its original size again. You can start a new move from the disk map.";
                ResultVerifyText.Text = "";
                break;
            default:
                ResultTitle.Text = "Nothing to finish";
                ResultText.Text = "No unfinished move was found.";
                ResultVerifyText.Text = "";
                break;
        }

        var warnings = result.Warnings.Concat(letterChange is null ? [] : [letterChange]).Distinct().ToList();
        ShowResultWarnings(warnings);
        LiveWarningsHost.Children.Clear();   // shown in the result card instead

        ResultSizesText.Text = "Reading the new layout…";
        try
        {
            var disks = await Task.Run(() => new DiskLayoutService().GetDisks());
            var parts = disks.SelectMany(d => d.Partitions).ToList();
            var t = parts.FirstOrDefault(p => p.Guid == _targetGuid);
            var s = parts.FirstOrDefault(p => p.Guid == _sourceGuid);
            string tName = t is null ? _targetName : PartName(t), sName = s is null ? _sourceName : PartName(s);
            ResultSizesText.Text =
                $"{tName,-10} before {Format.Bytes(_targetBefore),10}   after {(t is null ? "?" : Format.Bytes(t.Size)),10}\n" +
                $"{sName,-10} before {Format.Bytes(_sourceBefore),10}   after {(s is null ? "?" : Format.Bytes(s.Size)),10}";
        }
        catch (Exception ex)
        {
            ResultSizesText.Text = "Could not read the new layout: " + ex.Message;
        }
    }

    private void ShowResultWarnings(IReadOnlyList<string> warnings)
    {
        ResultWarningsHost.Children.Clear();
        foreach (var w in warnings)
            ResultWarningsHost.Children.Add(new TextBlock { Text = "• " + w, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 2, 0, 2) });
        ResultWarningsCard.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStopped(string title, string text, bool ok)
    {
        ResultPanel.Visibility = Visibility.Visible;
        ResultCard.BorderBrush = ok ? YellowBrush : RedBrush;
        ResultTitle.Text = title;
        ResultText.Text = text;
        ResultSizesText.Text = "";
        ResultVerifyText.Text = "";
        ResultPathsText.Visibility = Visibility.Collapsed;
        ResultResumeButton.Visibility = Visibility.Collapsed;
        ShowResultWarnings(_liveWarnings.ToList());
        LiveWarningsHost.Children.Clear();
    }

    private void ShowFailure(Exception ex)
    {
        string message = ex is AggregateException agg
            ? agg.Message.Split(" (")[0] + " " + string.Join(" ", agg.InnerExceptions.Select(i => i.Message))
            : ex.Message;
        bool unfinished = DiskMoveSession.HasUnfinishedMove();
        string hint;
        if (!unfinished) hint = "No unfinished move was left behind.";
        else if (ex is DiskMoveUnsafeFilesException) hint = "Nothing was resumed; the Resume screen refuses too until the folder's permissions are fixed.";
        else if (ex is DiskMoveAfterCommitException) hint = "The journal is kept, so the move can be finished later (Go to Resume).";
        else hint = ResumeHint(ReadResumeInfo());
        ShowStopped("The move stopped", message + "\n\n" + hint, ok: false);

        var paths = new List<string> { "Journal: " + DiskMoveSession.JournalPath };
        try
        {
            foreach (var pattern in new[] { "layout-*.json", "gpt-*.bin" })
                if (Directory.EnumerateFiles(DiskMoveSession.DataDir, pattern).OrderDescending().FirstOrDefault() is { } f)
                    paths.Add("Partition table backup: " + f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            paths.Add("Partition table backups: " + DiskMoveSession.DataDir);
        }
        ResultPathsText.Text = string.Join("\n", paths);
        ResultPathsText.Visibility = Visibility.Visible;
        ResultResumeButton.Visibility = unfinished ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ResultResume_Click(object sender, RoutedEventArgs e) => await ShowResumeAsync();

    private async void ResultDone_Click(object sender, RoutedEventArgs e)
    {
        _analysis = null;
        _plan = null;
        _amount = 0;
        await RefreshAsync();
    }

    private void OpenDiskManagement_Click(object sender, RoutedEventArgs e) => Launch("diskmgmt.msc");

    private void Launch(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Could not open " + file, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ================================================================== resume

    /// <param name="JournalExists">journal.bin exists (it may still be unreadable: <paramref name="Journal"/> null).</param>
    /// <param name="Unsafe">The data folder guard refused (review I1): nothing was read and Continue is disabled.</param>
    private sealed record ResumeInfo(bool JournalExists, JournalInfo? Journal, RestoreState? State, PendingMove? Pending,
        string? Error, string? Unsafe = null)
    {
        public DiskMoveRunner.ResumeDirection Direction => DiskMoveRunner.PredictResume(JournalExists, Journal, Pending);
    }

    private async Task ShowResumeAsync()
    {
        DiskMoveSession.ResumeDismissed = false;   // shown on purpose (Go to Resume, or a new start)
        ShowScreen(Screen.Resume);
        bool admin = AdminHelper.IsAdmin;
        ContinueButton.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        ResumeRestartButton.Visibility = admin || DiskMoveSession.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        ResumeLaterButton.Visibility = DiskMoveSession.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        ResumeDetailText.Text = "Journal folder: " + DiskMoveSession.DataDir;

        _ = LoadResumeMapAsync();
        if (!admin)
        {
            // The journal is readable only by administrators: do not try (nor guess which way the resume goes).
            ResumeText.Text = "The last disk space move was interrupted. Your data is safe — restart WinOptimizer as administrator " +
                              "to finish it (or to undo it, if no data had been moved yet).";
            return;
        }

        ResumeText.Text = "Reading the journal…";
        ContinueButton.IsEnabled = false;
        var info = await Task.Run(ReadResumeInfo);
        _resumeInfo = info;
        if (info.Unsafe is not null)
        {
            ResumeText.Text = info.Unsafe;
            return;   // Continue stays disabled
        }
        ContinueButton.IsEnabled = true;
        string src = (info.State?.SourceLetter ?? info.Pending?.SourceLetter ?? '\0') is var c && c != '\0' ? $"{c}:" : "the source";
        if (info.Journal is { } j && info.Direction == DiskMoveRunner.ResumeDirection.Finish)
        {
            int step = Math.Max((int)MoveStep.LockSource, (int)j.Step);
            int index = Math.Clamp(j.MoveIndex, 0, j.ChunkCount.Length - 1);
            ResumeText.Text = (j.Step >= MoveStep.MoveRecovery && j.Step <= MoveStep.Verify
                                  ? $"The last disk space move stopped at step {step}. {src}'s data is already moved and safe — "
                                  : $"The last disk space move was interrupted at step {Math.Min(step, (int)MoveStep.Verify)} " +
                                    $"(block {j.ChunksDone} of {j.ChunkCount[index]}). Your data is safe — ") +
                              "click Continue to finish the move, or Try again later.";
        }
        else
        {
            ResumeText.Text = ResumeHint(info);
        }
        if (info.Error is not null) ResumeDetailText.Text += "\nNote: " + info.Error;
    }

    /// <summary>What Continue will do, in words (review minor 2: never "finish" when the resume undoes).</summary>
    private static string ResumeHint(ResumeInfo info)
    {
        if (info.Unsafe is not null) return info.Unsafe;
        char letter = info.State?.SourceLetter ?? info.Pending?.SourceLetter ?? '\0';
        string src = letter == '\0' ? "the source" : $"{letter}:";
        return info.Direction switch
        {
            DiskMoveRunner.ResumeDirection.Finish =>
                "Your data is safe. Continue on the Resume screen finishes the move from where it stopped (Go to Resume).",
            DiskMoveRunner.ResumeDirection.Undo =>
                $"No data had been moved yet, so your data is safe. Continue on the Resume screen undoes the attempt and gives {src} its space back (Go to Resume).",
            DiskMoveRunner.ResumeDirection.Unknown =>
                "The journal could not be read. Continue on the Resume screen checks the disk and stops without changing anything " +
                "unless the state is clear (Go to Resume).",
            _ => "No unfinished move was left behind.",
        };
    }

    private ResumeInfo? _resumeInfo;

    /// <summary>Reads the journal and marker for the Resume screen, after the data folder guard (review I1) when the
    /// folder is the real one: nothing is read from an unsafe folder.</summary>
    private static ResumeInfo ReadResumeInfo()
    {
        string pendingPath = Path.Combine(DiskMoveSession.DataDir, DiskMoveRunner.PendingFileName);
        bool journalExists = File.Exists(DiskMoveSession.JournalPath);
        if (DiskMoveSession.SecureDataFolder && (journalExists || File.Exists(pendingPath)))
        {
            try
            {
                DataFolderSecurity.PrepareTrusted(DiskMoveSession.DataDir, [DiskMoveSession.JournalPath, pendingPath]);
            }
            catch (DiskMoveUnsafeFilesException ex)
            {
                return new ResumeInfo(journalExists, null, null, null, null, ex.Message);
            }
        }

        JournalInfo? journal = null;
        RestoreState? state = null;
        PendingMove? pending = null;
        string? error = null;
        try
        {
            if (journalExists) journal = new WindowsDiskMoveBackend().ReadJournal(DiskMoveSession.JournalPath, out state);
        }
        catch (Exception ex) when (ex is DiskMoveException or IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            error = "the journal could not be read (" + ex.Message + ").";
        }
        try
        {
            if (File.Exists(pendingPath)) pending = PendingMove.FromJsonBytes(File.ReadAllBytes(pendingPath));
        }
        catch (Exception ex) when (ex is DiskMoveException or IOException or UnauthorizedAccessException)
        {
            error ??= "the pending marker could not be read (" + ex.Message + ").";
        }
        return new ResumeInfo(journalExists, journal, state, pending, error);
    }

    /// <summary>Review I2: leave the Resume screen for this session. Nothing changes on disk; new moves stay blocked.</summary>
    private async void ResumeLater_Click(object sender, RoutedEventArgs e)
    {
        if (DiskMoveSession.IsRunning) return;
        DiskMoveSession.ResumeDismissed = true;
        _resumeInfo = null;
        await RefreshAsync();
    }

    private async Task LoadResumeMapAsync()
    {
        try
        {
            var disks = await Task.Run(() => new DiskLayoutService().GetDisks());
            _disks = disks;
            (_targetDisk, _target) = FindTarget(disks);
            BuildDiskMap(ResumeMapHost, disks);
        }
        catch (Exception ex)
        {
            ResumeMapHost.Children.Clear();
            ResumeMapHost.Children.Add(new TextBlock { Text = "Could not read the disk layout: " + ex.Message, TextWrapping = TextWrapping.Wrap });
        }
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (DiskMoveSession.IsRunning || !AdminHelper.IsAdmin) return;
        var info = _resumeInfo ?? await Task.Run(ReadResumeInfo);
        if (info.Unsafe is not null)
        {
            _resumeInfo = null;
            ResumeText.Text = info.Unsafe;
            return;
        }
#if DEBUG
        Guid? diskGuid = info.State?.DiskGuid ?? info.Pending?.DiskGuid;
        if (DiskMoveSession.TestTargetDisk is not null &&
            (diskGuid is null ? "The test disk of the journal is unknown." : TestDiskRefusal(diskGuid.Value)) is string refused)
        {
            MessageBox.Show(Window.GetWindow(this), refused, "Test target refused", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
#endif
        var parts = _disks.SelectMany(d => d.Partitions).ToList();
        var state = info.State;
        _sourceGuid = state?.SourceGuid ?? info.Pending?.SourceGuid ?? Guid.Empty;
        _targetGuid = state?.TargetGuid ?? info.Pending?.TargetGuid ?? Guid.Empty;
        char letter = state?.SourceLetter ?? info.Pending?.SourceLetter ?? '\0';
        _sourceLetter = letter == '\0' ? null : letter;
        _sourceName = _sourceLetter is char sl ? $"{sl}:" : "the source";
        _targetName = parts.FirstOrDefault(p => p.Guid == _targetGuid) is { } tp ? PartName(tp) : "C:";
        _sourceBefore = state?.SourceOriginalSize ?? info.Pending?.SourceOriginalSize ?? 0;
        _targetBefore = state?.TargetOriginalSize ?? parts.FirstOrDefault(p => p.Guid == _targetGuid)?.Size ?? 0;
        _amount = state?.Amount ?? info.Pending?.Amount ?? 0;
        _hasRecovery = state?.RecoveryGuid is not null;

        try
        {
            BeginRun(resuming: true);   // inside the try: EndRun (finally) always clears the running flag
            var runner = new DiskMoveRunner(new WindowsDiskMoveBackend(), DiskMoveSession.JournalPath, DiskMoveSession.ManageWinRe);
            var result = await runner.ResumeAsync(new Progress<RunProgress>(OnProgress), _guard!.ShouldPause);
            _resumeInfo = null;
            await ShowResultAsync(result);
        }
        catch (Exception ex)
        {
            _resumeInfo = null;
            ShowFailure(ex);
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Debug test mode only: two machine facts that say nothing about a VHDX test target become notes, so the VHDX
    /// flow can run on a development PC: a pending Windows restart, extra boot menu entries, and a BitLocker state that is merely unknown for
    /// the VHDX volume. Release builds (and the normal page) use the checks unchanged.
    /// </summary>
    private static IReadOnlyList<CheckResult> ForTestTarget(IReadOnlyList<CheckResult> results, SystemFacts facts, PartitionSnapshot source)
    {
#if DEBUG
        if (DiskMoveSession.TestTargetDisk is null) return results;
        bool unknownBitLocker = source.DriveLetter is char c && facts.BitLockerStatus.TryGetValue(char.ToUpperInvariant(c), out var st) && st == "Unknown";
        return results.Select(r => r.Code switch
        {
            CheckCode.RebootPending or CheckCode.MultipleOs => r with { Severity = CheckSeverity.Warning, Message = "(Test mode, ignored for the VHDX) " + r.Message },
            CheckCode.SourceBitLocker when unknownBitLocker =>
                r with { Severity = CheckSeverity.Warning, Message = "(Test mode, ignored for the VHDX) The BitLocker status of the test volume is unknown." },
            _ => r,
        }).ToList();
#else
        return results;
#endif
    }

#if DEBUG
    /// <summary>Debug test mode: the disk must still be the test target and neither boot nor system. Null = OK.</summary>
    private static string? TestDiskRefusal(Guid diskGuid)
    {
        if (DiskMoveSession.TestTargetDisk is not int n) return null;
        var d = new DiskLayoutService().GetDisks().FirstOrDefault(x => x.Guid == diskGuid);
        if (d is null) return "The test disk was not found.";
        if (d.Number != n) return $"The disk is number {d.Number}, not the test target {n}.";
        if (d.IsBoot || d.IsSystem) return $"Disk {d.Number} is a boot or system disk.";
        if (!d.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) return $"Disk {d.Number} is not a virtual disk.";
        return null;
    }
#endif

    // ================================================================== shared

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (DiskMoveSession.IsRunning) return;
        if (AdminHelper.RestartAsAdmin()) App.ExitApp();
    }

    private void ShowScreen(Screen screen)
    {
        _screen = screen;
        MapPanel.Visibility = screen == Screen.Map ? Visibility.Visible : Visibility.Collapsed;
        AnalysisPanel.Visibility = screen == Screen.Analysis ? Visibility.Visible : Visibility.Collapsed;
        AmountPanel.Visibility = screen == Screen.Amount ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPanel.Visibility = screen == Screen.Confirm ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = screen == Screen.Progress ? Visibility.Visible : Visibility.Collapsed;
        ResumePanel.Visibility = screen == Screen.Resume ? Visibility.Visible : Visibility.Collapsed;
        StepBar.Visibility = screen == Screen.Resume ? Visibility.Collapsed : Visibility.Visible;

        int index = screen switch
        {
            Screen.Map => 0, Screen.Analysis => 1, Screen.Amount => 2, Screen.Confirm => 3, _ => 4,
        };
        var labels = new[] { Step1Label, Step2Label, Step3Label, Step4Label, Step5Label };
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i].FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
            if (i == index) labels[i].Foreground = (Brush)FindResource("AccentTextFillColorPrimaryBrush");
            else labels[i].ClearValue(TextBlock.ForegroundProperty);
        }
        ContentScroll.ScrollToTop();
    }
}
