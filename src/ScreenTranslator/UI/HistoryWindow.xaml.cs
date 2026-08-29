using System.Windows;
using ScreenTranslator.History;
using ScreenTranslator.Infrastructure;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace ScreenTranslator.UI;

/// <summary>
/// The full list of remembered translations. A plain, activatable window — unlike the
/// result popup, this one is opened deliberately from the tray, so there is no reason for
/// it to avoid taking focus. That also means the ordinary text-copying gestures (Ctrl+C,
/// right-click) work here for free; the buttons exist alongside them, not instead.
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

    /// <summary>
    /// The text box the user last put the caret in. Remembered because clicking "复制选中"
    /// moves focus to the button, so by the time the click handler runs the box is no
    /// longer focused — but its selection is still there.
    /// </summary>
    private TextBox? _lastFocused;

    public HistoryWindow()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var rows = HistoryStore.All().Select(r => new Row(r)).ToList();
        RecordList.ItemsSource = rows;
        _lastFocused = null;

        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = rows.Count == 0
            ? $"记录保存在 {Paths.DataDir}"
            : $"共 {rows.Count} 条，最多保留 {HistoryStore.MaxEntries} 条　·　保存在 {Paths.DataDir}";
    }

    private void RecordText_GotKeyboardFocus(object sender, RoutedEventArgs e)
        => _lastFocused = sender as TextBox;

    // ------------------------------------------------------------------ copying

    private void CopyTranslation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Row row) return;

        // A record with no translation is one that failed; the recognized text is all
        // there is, and copying nothing at all would be the unhelpful answer.
        var text = string.IsNullOrWhiteSpace(row.Record.Translation)
            ? row.Record.Original
            : row.Record.Translation;
        Copy(text, "译文");
    }

    private void CopyOriginal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Row row) return;
        Copy(row.Record.Original, "原文");
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = _lastFocused?.SelectedText ?? "";
        if (selected.Length == 0)
        {
            StatusText.Text = "先用鼠标拖选一段文字，再点「复制选中」。";
            return;
        }

        Copy(selected, "选中的文字");
    }

    private void Copy(string text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = $"这条记录没有{what}可以复制。";
            return;
        }

        StatusText.Text = ClipboardHelper.TrySetText(text)
            ? $"已复制{what}（{text.Length} 字）　{DateTime.Now:HH:mm:ss}"
            : "复制失败，剪贴板被别的程序占着，过一秒再试。";
    }

    // ----------------------------------------------------------------- clearing

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
