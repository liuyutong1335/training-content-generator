using System.Windows;

namespace TrainingContent.App.Views;

/// <summary>
/// Project の表示名を入力する最小 Dialog。MessageBox に text input は無いため独自 Window で実装する。
/// 第三者 Dialog library は使わない。
/// </summary>
public partial class RenameProjectWindow : Window
{
    /// <summary>確定された新しい名前。Cancel 時は元のタイトルのまま。</summary>
    public string ProjectTitle { get; private set; }

    public RenameProjectWindow(string currentTitle)
    {
        InitializeComponent();

        ProjectTitle = currentTitle;
        TitleTextBox.Text = currentTitle;

        Loaded += (_, _) =>
        {
            TitleTextBox.SelectAll();
            TitleTextBox.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 前後 whitespace は Trim し、Trim 後に空なら拒否する（Dialog は閉じない）。
        var trimmed = TitleTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            ValidationText.Text = "名前を入力してください。";
            ValidationText.Visibility = Visibility.Visible;
            TitleTextBox.Focus();
            return;
        }

        ProjectTitle = trimmed;
        DialogResult = true;
    }
}
