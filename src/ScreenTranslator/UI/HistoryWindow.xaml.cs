using System.Windows;
using ScreenTranslator.History;
using ScreenTranslator.Infrastructure;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace ScreenTranslator.UI;

/// <summary>
/// The full list of remembered translations. A plain, activatable window — unlike the
/// result popup, this one is opened deliberately from the tray, so there is no reason for
/// it to avoid taking focus.
/// </summary>
public partial class HistoryWindow : Window
{
    /// <summary>Wraps a record with the one derived string the template needs.</summary>
    public sealed record Row(TranslationRecord Record)
    {
        public string Header
        {
            get
            {
                var language = string.IsNullOrWhiteSpace(Record.SourceLanguage)
                    ? ""
                    : $"　·　{Record.SourceLanguage} → 中文";
                return $"{Record.Time:MM-dd HH:mm}{language}";
            }
        }
    }

    public HistoryWindow()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var rows = HistoryStore.All().Select(r => new Row(r)).ToList();
        RecordList.ItemsSource = rows;

        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = rows.Count == 0
            ? $"记录保存在 {Paths.DataDir}"
            : $"共 {rows.Count} 条，最多保留 {HistoryStore.MaxEntries} 条　·　保存在 {Paths.DataDir}";
    }

    private void CopyRecord_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Row row) return;

        var text = string.IsNullOrWhiteSpace(row.Record.Translation)
            ? row.Record.Original
            : row.Record.Translation;

        try
        {
            Clipboard.SetText(text);
            StatusText.Text = $"已复制　{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Log.Warn($"复制历史记录失败：{ex.Message}");
            StatusText.Text = "复制失败，剪贴板被别的程序占着，过一秒再试。";
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        // Irreversible and one click away, so it asks first.
        var answer = MessageBox.Show(this,
            "确定要删掉全部翻译记录吗？删了就找不回来了。",
            "清空翻译历史", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        HistoryStore.Clear();
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
