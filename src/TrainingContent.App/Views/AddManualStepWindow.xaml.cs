using System.Windows;

namespace TrainingContent.App.Views;

/// <summary>
/// manual Step の Title を入力する最小 Dialog（B2）。MessageBox に text input は無いため独自 Window で実装する。
///
/// <para>
/// 既定 text は空で、「新しい手順」等を勝手に保存しない。Trim 後に空なら拒否して Dialog を閉じない。
/// </para>
/// </summary>
public partial class AddManualStepWindow : Window
{
    /// <summary>確定されたタイトル（入力そのまま。trim しない）。Cancel 時は空。</summary>
    public string StepTitle { get; private set; } = string.Empty;

    public AddManualStepWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => TitleTextBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var input = TitleTextBox.Text;

        // blank（whitespace-only 含む）は reject するが、non-blank は trim せずそのまま保存する
        // （B1 の Review edit と同じ semantics。create と edit で normalization を変えない）。
        if (string.IsNullOrWhiteSpace(input))
        {
            ValidationText.Text = "タイトルを入力してください。";
            ValidationText.Visibility = Visibility.Visible;
            TitleTextBox.Focus();
            return;
        }

        StepTitle = input;
        DialogResult = true;
    }
}
