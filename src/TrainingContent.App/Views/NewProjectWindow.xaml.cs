using System.Windows;
using TrainingContent.App.Services;

namespace TrainingContent.App.Views;

/// <summary>
/// 新規 Project の入力 Dialog。WPF 標準のみで実装する。
///
/// <para>
/// 入力の正規化・必須チェックは <see cref="NewProjectInput.FromRaw"/> に集約されており、
/// この Dialog は生入力を渡して結果を受け取るだけ。Home からでも Contents からでも
/// 同じ規則が適用される。
/// </para>
/// </summary>
public partial class NewProjectWindow : Window
{
    /// <summary>確定された正規化済み入力。Cancel 時は null。</summary>
    public NewProjectInput? Input { get; private set; }

    public NewProjectWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => TitleTextBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Input = NewProjectInput.FromRaw(
                TitleTextBox.Text,
                ObjectiveTextBox.Text,
                TargetAudienceTextBox.Text,
                PrerequisitesTextBox.Text);
        }
        catch (ArgumentException)
        {
            // 必須 Title が Trim 後に空。Dialog は閉じない。
            ValidationText.Text = "教材名を入力してください。";
            ValidationText.Visibility = Visibility.Visible;
            TitleTextBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
