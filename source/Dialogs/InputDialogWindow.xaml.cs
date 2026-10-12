using System.Windows;

namespace StreamZilla.Dialogs;

public partial class InputDialogWindow : Window
{
    private readonly InputDialogViewModel _viewModel;

    public InputDialogWindow(string title, string label, string defaultValue)
    {
        InitializeComponent();

        _viewModel = new InputDialogViewModel
        {
            Title = title,
            Label = label,
            Input = defaultValue
        };

        DataContext = _viewModel;
        Title = title;

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string? Result { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = _viewModel.Input?.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
        Close();
    }
}
