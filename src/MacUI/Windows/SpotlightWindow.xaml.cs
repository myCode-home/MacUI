using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MacUI.Core;

namespace MacUI.Windows;

public class SearchEntry
{
    public string Name { get; set; } = "";
    public string Sub { get; set; } = "";
    public ImageSource? Icon { get; set; }
    public Action Launch { get; set; } = () => { };
}

public partial class SpotlightWindow : Window
{
    private readonly List<SearchEntry> _entries = new();
    private bool _shown;

    public SpotlightWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.EnableAcrylic(h, AppTheme.GlassTint(AppTheme.IsDark));
            Native.RoundCorners(h, round: true);
        };
        SearchBox.Focus();
    }

    /// <summary>全局热键触发:切换显示/隐藏。</summary>
    public void Toggle()
    {
        if (_shown) HidePanel();
        else ShowPanel();
    }

    public void ShowPanel()
    {
        var wa = SystemParameters.WorkArea;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = wa.Height * 0.22;

        SearchBox.Text = "";
        UpdateResults("");
        _shown = true;
        Opacity = 0;
        Show();
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(120));
        BeginAnimation(OpacityProperty, anim);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    public void HidePanel()
    {
        _shown = false;
        Hide();
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (_shown) HidePanel();
    }

    // ---------------- 搜索 ----------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateResults(SearchBox.Text);

    private void UpdateResults(string query)
    {
        _entries.Clear();
        var results = AppCatalog.FuzzySearch(query, 8);
        foreach (var (app, _) in results)
        {
            AppCatalog.Prepare(app);
            string sub = app.TargetPath.Length > 0 ? app.TargetPath : app.ShortcutPath;
            var entry = new SearchEntry
            {
                Name = app.Name,
                Sub = sub,
                Icon = app.Icon,
                Launch = () => App.LaunchOrActivate(app),
            };
            _entries.Add(entry);
        }

        string q = query.Trim();
        if (q.Length > 0)
        {
            string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(q);
            _entries.Add(new SearchEntry
            {
                Name = $"在网页中搜索 \"{q}\"",
                Sub = url,
                Launch = () => App.ShellOpen(url),
            });
        }

        Results.ItemsSource = null;
        Results.ItemsSource = _entries;
        if (_entries.Count > 0) Results.SelectedIndex = 0;
    }

    // ---------------- 键盘 ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HidePanel();
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                LaunchSelected();
                e.Handled = true;
                break;
        }
    }

    private void Results_KeyDown(object sender, KeyEventArgs e) { /* 由 Window PreviewKeyDown 统一处理 */ }

    private void Results_Click(object sender, MouseButtonEventArgs e) => LaunchSelected();

    private void MoveSelection(int delta)
    {
        if (_entries.Count == 0) return;
        int idx = Math.Clamp(Results.SelectedIndex + delta, 0, _entries.Count - 1);
        Results.SelectedIndex = idx;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void LaunchSelected()
    {
        if (Results.SelectedIndex is >= 0 && Results.SelectedIndex < _entries.Count)
        {
            var entry = _entries[Results.SelectedIndex];
            HidePanel();
            entry.Launch();
        }
    }
}
