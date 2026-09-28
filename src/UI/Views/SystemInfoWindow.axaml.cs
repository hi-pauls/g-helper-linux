using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;
using GHelper.Linux.UI.Styles;
using static GHelper.Linux.Platform.Linux.SystemInfoCollector;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// System information window - shows comprehensive hardware and software details
/// gathered from sysfs, procfs, and lspci. All data readable without root.
/// </summary>
public partial class SystemInfoWindow : Window
{
    private List<InfoSection>? _cachedSections;

    public SystemInfoWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += OnLanguageChanged;
        ApplyLabels();

        Loaded += (_, _) => CollectAndDisplay();
    }

    private void OnLanguageChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyLabels();
            CollectAndDisplay();
        });
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("sysinfo_title");
        labelCopyButton.Text = Labels.Get("sysinfo_copy");
    }

    /// <summary>Collect data on background thread, then build UI.</summary>
    private async void CollectAndDisplay()
    {
        contentPanel.Children.Clear();

        // Loading indicator
        var loading = new TextBlock
        {
            Text = Labels.Get("loading"),
            Foreground = ThemeColors.Brush("GH.ForeMid"),
            Margin = new Thickness(0, 20),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        contentPanel.Children.Add(loading);

        var sections = await Task.Run(SystemInfoCollector.CollectAll);
        _cachedSections = sections;
        BuildContent(sections);
    }

    /// <summary>Build UI panels from collected data.</summary>
    private void BuildContent(List<InfoSection> sections)
    {
        contentPanel.Children.Clear();

        foreach (var section in sections)
        {
            var border = new Border();
            border.Classes.Add("panel");

            var stack = new StackPanel();

            // Section header
            var headerBorder = new Border();
            headerBorder.Classes.Add("panel-header");
            headerBorder.Margin = new Thickness(0, 0, 0, 8);

            var headerText = new TextBlock { Text = Labels.Get(section.HeaderKey) };
            headerText.Classes.Add("header");
            headerBorder.Child = headerText;
            stack.Children.Add(headerBorder);

            // Entries
            foreach (var entry in section.Entries)
            {
                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("140,*"),
                    Margin = new Thickness(0, 2)
                };

                var label = new TextBlock
                {
                    Text = Labels.Get(entry.LabelKey),
                    VerticalAlignment = VerticalAlignment.Top
                };
                label.Classes.Add("label-dim");
                Grid.SetColumn(label, 0);

                var value = new SelectableTextBlock
                {
                    Text = entry.Value,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top
                };
                value.Classes.Add("value");
                Grid.SetColumn(value, 1);

                grid.Children.Add(label);
                grid.Children.Add(value);
                stack.Children.Add(grid);
            }

            border.Child = stack;
            contentPanel.Children.Add(border);
        }
    }

    private async void ButtonCopy_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_cachedSections == null)
            return;

        try
        {
            buttonCopy.IsEnabled = false;
            labelCopyButton.Text = Labels.Get("collecting");

            string report = await Task.Run(() => SystemInfoCollector.ToText(_cachedSections));

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(report);
                labelCopyButton.Text = Labels.Get("copied");
            }
            else
            {
                labelCopyButton.Text = Labels.Get("clipboard_unavailable");
            }
        }
        catch
        {
            labelCopyButton.Text = Labels.Get("failed");
        }

        // Reset button after 2 seconds
        _ = Task.Delay(2000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                labelCopyButton.Text = Labels.Get("sysinfo_copy");
                buttonCopy.IsEnabled = true;
            }));
    }
}
