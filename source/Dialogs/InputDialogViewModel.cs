using StreamZilla.ViewModels;

namespace StreamZilla.Dialogs;

public class InputDialogViewModel : ViewModelBase
{
    private string _input = "";
    private string _label = "";
    private string _title = "";

    public string Title
    {
        get
        {
            return _title;
        }
        set
        {
            SetProperty(ref _title, value);
        }
    }

    public string Label
    {
        get
        {
            return _label;
        }
        set
        {
            SetProperty(ref _label, value);
        }
    }

    public string Input
    {
        get
        {
            return _input;
        }
        set
        {
            SetProperty(ref _input, value);
        }
    }
}
