namespace StreamZilla.Dialogs;

public interface IDialogService
{
    string? PromptText(string title, string label, string defaultValue);

    bool Confirm(string title, string message);

    bool ConfirmHostKey(string host, int port, string fingerprint);

    void ShowError(string title, string message);

    void ShowInformation(string title, string message);
}
