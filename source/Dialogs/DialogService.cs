using System.Windows;

namespace StreamZilla.Dialogs;

public class DialogService : IDialogService
{
    private readonly Window? _owner;

    public DialogService(Window? owner = null)
    {
        _owner = owner;
    }

    public string? PromptText(string title, string label, string defaultValue)
    {
        InputDialogWindow dialog = new InputDialogWindow(title, label, defaultValue)
        {
            Owner = _owner
        };

        dialog.ShowDialog();
        return dialog.Result;
    }

    public bool Confirm(string title, string message)
    {
        return MessageBox.Show(_owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    public bool ConfirmHostKey(string host, int port, string fingerprint)
    {
        string message =
            $"The authenticity of host '{host}:{port}' can't be established.\n\n" +
            $"SSH host key fingerprint (SHA-256):\n{fingerprint}\n\n" +
            "Trust this host and remember its key?";

        return MessageBox.Show(_owner, message, "Unknown SSH host key", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public void ShowError(string title, string message)
    {
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void ShowInformation(string title, string message)
    {
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
