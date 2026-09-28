using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.UI.Styles;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using LiveChartsCore.SkiaSharpView.VisualElements;
using SkiaSharp;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Real-time hardware monitoring window with LiveCharts2 graphs.
/// Shows temperature, fan speed, and power/load over a rolling 5-minute window.
/// </summary>
public partial class MonitorWindow : Window
{
    private const int MaxPoints = 300; // 5 minutes at 1-second interval

    private readonly DispatcherTimer _timer;

    // Temperature series data
    private readonly ObservableCollection<ObservableValue> _cpuTempValues = [];
    private readonly ObservableCollection<ObservableValue> _gpuTempValues = [];

    // Fan speed series data
    private readonly ObservableCollection<ObservableValue> _cpuFanValues = [];
    private readonly ObservableCollection<ObservableValue> _gpuFanValues = [];
    private readonly ObservableCollection<ObservableValue> _midFanValues = [];

    // Power & load series data
    private readonly ObservableCollection<ObservableValue> _gpuLoadValues = [];
    private readonly ObservableCollection<ObservableValue> _gpuPowerValues = [];
    private readonly ObservableCollection<ObservableValue> _batteryPowerValues = [];

    // Series references for relabeling on language change
    private LineSeries<ObservableValue> _cpuTempSeries = null!;
    private LineSeries<ObservableValue> _gpuTempSeries = null!;
    private LineSeries<ObservableValue> _cpuFanSeries = null!;
    private LineSeries<ObservableValue> _gpuFanSeries = null!;
    private LineSeries<ObservableValue> _midFanSeries = null!;
    private LineSeries<ObservableValue> _gpuLoadSeries = null!;
    private LineSeries<ObservableValue> _gpuPowerSeries = null!;
    private LineSeries<ObservableValue> _batteryPowerSeries = null!;

    // G-Helper palette
    private static readonly SKColor BlueCpu = SKColor.Parse("#3AAEEF");
    private static readonly SKColor RedGpu = SKColor.Parse("#FF6B6B");
    private static readonly SKColor GreenMid = SKColor.Parse("#50C878");
    private static readonly SKColor Turquoise = SKColor.Parse("#00CED1");
    private static readonly SKColor Orange = SKColor.Parse("#FFA500");
    private static readonly SKColor Gold = SKColor.Parse("#FFD700");

    // Theme paints, taken when the charts are built; tooltips stay dark in both
    private static readonly SKTypeface SansSerif = SKTypeface.FromFamilyName("sans-serif");
    private static SolidColorPaint LegendText =>
        new(ThemeColors.IsLight ? new SKColor(48, 48, 48) : SKColors.LightGray) { SKTypeface = SansSerif };
    private static readonly SolidColorPaint LegendBg = new(SKColors.Transparent);
    private static readonly SolidColorPaint TooltipText = new(SKColors.White) { SKTypeface = SansSerif };
    private static readonly SolidColorPaint TooltipBg = new(new SKColor(50, 50, 50));
    private static SolidColorPaint AxisLabels =>
        new(ThemeColors.IsLight ? new SKColor(80, 80, 80) : new SKColor(160, 160, 160)) { SKTypeface = SansSerif };
    private static SolidColorPaint GridLines => new(ThemeColors.IsLight ? new SKColor(210, 210, 210) : new SKColor(60, 60, 60))
    {
        StrokeThickness = 1,
        PathEffect = new DashEffect([3, 3]),
    };

    public MonitorWindow()
    {
        InitializeComponent();

        SetupCharts();
        ApplyLabels();

        Labels.LanguageChanged += OnLanguageChanged;

        Helpers.SystemTelemetry.ResetCpuSample();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => PollSensors();
        _timer.Start();

        Closed += (_, _) =>
        {
            _timer.Stop();
            Labels.LanguageChanged -= OnLanguageChanged;
        };

        // Initial poll
        PollSensors();
    }

    private void OnLanguageChanged()
    {
        Dispatcher.UIThread.Post(ApplyLabels);
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("monitor_title");
        headerMonitor.Text = Labels.Get("monitor_title");

        // Chart titles
        SetChartTitle(chartTemperature, Labels.Get("monitor_temperature"));
        SetChartTitle(chartFanSpeed, Labels.Get("monitor_fan_speed"));
        SetChartTitle(chartPower, Labels.Get("monitor_power_load"));

        // Series legend names
        _cpuTempSeries.Name = "CPU";
        _gpuTempSeries.Name = "GPU";
        _cpuFanSeries.Name = Labels.Get("cpu_fan");
        _gpuFanSeries.Name = Labels.Get("gpu_fan");
        _midFanSeries.Name = Labels.Get("mid_fan");
        _gpuLoadSeries.Name = Labels.Get("monitor_gpu_load");
        _gpuPowerSeries.Name = Labels.Get("monitor_gpu_power");
        _batteryPowerSeries.Name = Labels.Get("monitor_battery_power");
    }

    private static void SetChartTitle(LiveChartsCore.SkiaSharpView.Avalonia.CartesianChart chart, string title)
    {
#pragma warning disable CS0618 // LabelVisual is deprecated but DrawnLabelVisual has different API
        chart.Title = new LabelVisual
        {
            Text = title,
            TextSize = 13,
            Paint = new SolidColorPaint(ThemeColors.IsLight ? SKColors.Black : new SKColor(240, 240, 240))
            {
                SKTypeface = SansSerif,
            },
            Padding = new Padding(0, 0, 0, 4),
        };
#pragma warning restore CS0618
    }

    private void SetupCharts()
    {
        // --- Temperature Chart ---
        _cpuTempSeries = CreateLineSeries(_cpuTempValues, "CPU", BlueCpu);
        _gpuTempSeries = CreateLineSeries(_gpuTempValues, "GPU", RedGpu);
        chartTemperature.Series = new ISeries[] { _cpuTempSeries, _gpuTempSeries };
        chartTemperature.YAxes = [CreateYAxis(0, 105)];
        chartTemperature.XAxes = [CreateTimeAxis()];
        ApplyChartTheme(chartTemperature);

        // --- Fan Speed Chart ---
        _cpuFanSeries = CreateLineSeries(_cpuFanValues, "CPU Fan", BlueCpu);
        _gpuFanSeries = CreateLineSeries(_gpuFanValues, "GPU Fan", RedGpu);
        _midFanSeries = CreateLineSeries(_midFanValues, "Mid Fan", GreenMid);
        chartFanSpeed.Series = new ISeries[] { _cpuFanSeries, _gpuFanSeries, _midFanSeries };
        chartFanSpeed.YAxes = [CreateYAxis(0, null)];
        chartFanSpeed.XAxes = [CreateTimeAxis()];
        ApplyChartTheme(chartFanSpeed);

        // --- Power & Load Chart ---
        _gpuLoadSeries = CreateLineSeries(_gpuLoadValues, "GPU Load %", Turquoise);
        _gpuPowerSeries = CreateLineSeries(_gpuPowerValues, "GPU Power W", Orange);
        _batteryPowerSeries = CreateLineSeries(_batteryPowerValues, "Battery W", Gold);
        chartPower.Series = new ISeries[] { _gpuLoadSeries, _gpuPowerSeries, _batteryPowerSeries };
        chartPower.YAxes = [CreateYAxis(0, null)];
        chartPower.XAxes = [CreateTimeAxis()];
        ApplyChartTheme(chartPower);
    }

    private static void ApplyChartTheme(LiveChartsCore.SkiaSharpView.Avalonia.CartesianChart chart)
    {
        chart.LegendPosition = LegendPosition.Top;
        chart.LegendTextPaint = LegendText;
        chart.LegendBackgroundPaint = LegendBg;
        chart.LegendTextSize = 12;

        chart.TooltipPosition = TooltipPosition.Top;
        chart.TooltipTextPaint = TooltipText;
        chart.TooltipBackgroundPaint = TooltipBg;
        chart.TooltipTextSize = 12;

        chart.DrawMargin = new Margin(42, 4, 8, 8);
        chart.ZoomMode = LiveChartsCore.Measure.ZoomAndPanMode.None;

        chart.AnimationsSpeed = TimeSpan.FromMilliseconds(200);
        chart.EasingFunction = LiveChartsCore.EasingFunctions.QuadraticOut;
    }

    private static LineSeries<ObservableValue> CreateLineSeries(
        ObservableCollection<ObservableValue> values, string name, SKColor color)
    {
        return new LineSeries<ObservableValue>
        {
            Values = values,
            Name = name,
            Stroke = new SolidColorPaint(color, 2.5f),
            Fill = new SolidColorPaint(color.WithAlpha(30)),
            GeometrySize = 0,
            GeometryStroke = null,
            LineSmoothness = 0.4,
            AnimationsSpeed = TimeSpan.FromMilliseconds(200),
            IsHoverable = true,
        };
    }

    private static Axis CreateYAxis(double? min, double? max)
    {
        return new Axis
        {
            MinLimit = min,
            MaxLimit = max,
            LabelsPaint = AxisLabels,
            SeparatorsPaint = GridLines,
            MinStep = 1,
            Labeler = v => $"{v:0}",
        };
    }

    private static Axis CreateTimeAxis()
    {
        return new Axis
        {
            LabelsPaint = null,
            SeparatorsPaint = null,
            ShowSeparatorLines = false,
        };
    }

    private void PollSensors()
    {
        try
        {
            var wmi = App.Wmi;
            if (wmi == null)
                return;

            // Temperatures
            int cpuTemp = wmi.DeviceGet(0x00120094);
            int gpuTemp = wmi.DeviceGet(0x00120097);
            AddValue(_cpuTempValues, cpuTemp > 0 ? cpuTemp : null);
            AddValue(_gpuTempValues, gpuTemp > 0 ? gpuTemp : null);

            // Fan RPMs
            int cpuFan = wmi.GetFanRpm(0);
            int gpuFan = wmi.GetFanRpm(1);
            int midFan = wmi.GetFanRpm(2);
            AddValue(_cpuFanValues, cpuFan > 0 ? cpuFan : 0);
            AddValue(_gpuFanValues, gpuFan > 0 ? gpuFan : (gpuFan <= -2 ? -(gpuFan + 2) : 0));
            AddValue(_midFanValues, midFan > 0 ? midFan : null);

            // GPU load & power (only when dGPU is active)
            int? gpuLoad = null;
            int? gpuPower = null;
            bool isEcoMode = wmi.GetGpuEco();
            if (!isEcoMode && App.GpuControl?.IsAvailable() == true)
            {
                try
                {
                    gpuLoad = App.GpuControl.GetGpuUse();
                    gpuPower = App.GpuControl.GetCurrentPower();
                }
                catch { }
            }
            AddValue(_gpuLoadValues, gpuLoad.HasValue && gpuLoad.Value >= 0 ? gpuLoad.Value : 0);
            AddValue(_gpuPowerValues, gpuPower.HasValue && gpuPower.Value >= 0 ? gpuPower.Value : 0);

            // Battery power (show 0 when idle so line stays visible)
            double batteryW = 0;
            try
            {
                int drainMw = App.Power?.GetBatteryDrainRate() ?? 0;
                batteryW = Math.Round(Math.Abs(drainMw) / 1000.0);
            }
            catch { }
            AddValue(_batteryPowerValues, batteryW);

            // procfs counters - no firmware equivalent, so read here.
            int cpuUsage = Helpers.SystemTelemetry.GetCpuUsagePercent();
            int cpuMhz = Helpers.SystemTelemetry.GetCpuFrequencyMhz();
            int ramUsage = Helpers.SystemTelemetry.GetRamUsagePercent();

            // Update header with live summary
            string stats = $"CPU: {(cpuTemp > 0 ? Helpers.TempHelper.FormatTemp(cpuTemp) : "--")}  " +
                           $"GPU: {(gpuTemp > 0 ? Helpers.TempHelper.FormatTemp(gpuTemp) : "--")}  " +
                           $"Fans: {Fan.FanSensorControl.FormatFan(0, cpuFan)}/{Fan.FanSensorControl.FormatFan(1, gpuFan)}";
            if (cpuUsage >= 0)
                stats += $"  Usage: {cpuUsage}%";
            if (cpuMhz > 0)
                stats += $"  Clock: {cpuMhz} MHz";
            if (ramUsage >= 0)
                stats += $"  RAM: {ramUsage}%";
            if (gpuLoad.HasValue && gpuLoad.Value >= 0)
                stats += $"  Load: {gpuLoad.Value}%";
            if (batteryW > 0)
                stats += $"  Battery: {batteryW:0}W";
            labelLiveStats.Text = stats;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("MonitorWindow poll error", ex);
        }
    }

    private static void AddValue(ObservableCollection<ObservableValue> series, double? value)
    {
        series.Add(new ObservableValue(value));
        if (series.Count > MaxPoints)
            series.RemoveAt(0);
    }
}
