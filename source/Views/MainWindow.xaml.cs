using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StreamZilla.Dialogs;
using StreamZilla.Services;
using StreamZilla.ViewModels;

namespace StreamZilla.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IDialogService _dialogs;
    private Point _dragStartPoint;
    private bool _dragging;

    public MainWindow()
    {
        InitializeComponent();

        _dialogs = new DialogService(this);
        _viewModel = new MainViewModel(_dialogs);

        DataContext = _viewModel;
        PasswordBox.Password = _viewModel.Password;
        PasswordBox.PasswordChanged += (_, _) => _viewModel.Password = PasswordBox.Password;

        Loaded += (_, _) => Dispatcher.BeginInvoke(
            () =>
            {
                if (_viewModel.LastLocalFolder is EntryItemViewModel last)
                {
                    ScrollIntoView(LocalTree, last);
                }
            },
            DispatcherPriority.Loaded);

        _viewModel.ScrollToEntryRequested += item => ScrollIntoView(RemoteTree, item);
    }

    public string LogPath => LogService.FilePath;

    private void ScrollIntoView(ItemsControl tree, EntryItemViewModel item)
    {
        if (tree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
        {
            container.BringIntoView();
            return;
        }

        foreach (EntryItemViewModel root in tree.Items)
        {
            if (TryFindContainer(tree.ItemContainerGenerator, root, item, out TreeViewItem? found) &&
                found != null)
            {
                found.BringIntoView();
                return;
            }
        }
    }

    private static bool TryFindContainer(
        ItemContainerGenerator generator,
        EntryItemViewModel current,
        EntryItemViewModel target,
        out TreeViewItem? found)
    {
        found = null;

        if (current == target)
        {
            found = generator.ContainerFromItem(current) as TreeViewItem;
            return found != null;
        }

        if (generator.ContainerFromItem(current) is TreeViewItem container)
        {
            foreach (EntryItemViewModel child in current.Children)
            {
                if (TryFindContainer(container.ItemContainerGenerator, child, target, out found))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static EntryItemViewModel? GetItemUnderMouse(object? source)
    {
        DependencyObject? current = source as DependencyObject;

        while (current != null)
        {
            if (current is TreeViewItem container &&
                container.DataContext is EntryItemViewModel item)
            {
                return item;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void Entry_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item &&
            item.DataContext is EntryItemViewModel entry)
        {
            _viewModel.OnEntryExpanded(entry);
        }
    }

    private void Entry_Collapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item &&
            item.DataContext is EntryItemViewModel entry)
        {
            _viewModel.OnEntryCollapsed(entry);
        }
    }

    private void Tree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        _viewModel.OnSelectionChanged(e.NewValue as EntryItemViewModel);
    }

    private void Tree_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);

        if (e.OriginalSource is not TextBlock ||
            GetItemUnderMouse(e.OriginalSource) is not EntryItemViewModel item)
        {
            return;
        }

        _viewModel.OnMouseDown(item, Keyboard.Modifiers);
    }

    private void LocalTree_MouseMove(object sender, MouseEventArgs e)
    {
        StartLocalDrag(sender, e);
    }

    private void RemoteTree_MouseMove(object sender, MouseEventArgs e)
    {
        StartRemoteDrag(sender, e);
    }

    private void StartLocalDrag(object sender, MouseEventArgs e)
    {
        if (_dragging || e.LeftButton != MouseButtonState.Pressed ||
            GetItemUnderMouse(e.OriginalSource) is not EntryItemViewModel item)
        {
            return;
        }

        Point position = e.GetPosition(null);
        Vector diff = _dragStartPoint - position;

        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        List<EntryItemViewModel> items = _viewModel.GetHighlightedLocalItems();

        if (items.Count == 0)
        {
            items.Add(item);
        }

        StartDrag(LocalTree, items, "StreamZillaLocalItems");
    }

    private void StartRemoteDrag(object sender, MouseEventArgs e)
    {
        if (_dragging || e.LeftButton != MouseButtonState.Pressed ||
            GetItemUnderMouse(e.OriginalSource) is not EntryItemViewModel item)
        {
            return;
        }

        Point position = e.GetPosition(null);
        Vector diff = _dragStartPoint - position;

        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        List<EntryItemViewModel> items = _viewModel.GetHighlightedRemoteItems();

        if (items.Count == 0)
        {
            items.Add(item);
        }

        StartDrag(RemoteTree, items, "StreamZillaRemoteItems");
    }

    private void StartDrag(
        UIElement source,
        List<EntryItemViewModel> items,
        string format)
    {
        _dragging = true;

        try
        {
            DataObject data = new();
            data.SetData(format, items);
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
        }
    }

    private void LocalTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("StreamZillaRemoteItems")
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void LocalTree_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("StreamZillaRemoteItems"))
        {
            return;
        }

        EntryItemViewModel? target = GetItemUnderMouse(e.OriginalSource) ??
            _viewModel.LastLocalFolder;

        List<EntryItemViewModel> items =
            (List<EntryItemViewModel>)e.Data.GetData("StreamZillaRemoteItems");

        _viewModel.HandleRemoteDrop(items, target);
    }

    private void RemoteTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("StreamZillaLocalItems")
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void RemoteTree_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("StreamZillaLocalItems"))
        {
            return;
        }

        EntryItemViewModel? target = GetItemUnderMouse(e.OriginalSource);
        List<EntryItemViewModel> items =
            (List<EntryItemViewModel>)e.Data.GetData("StreamZillaLocalItems");

        _viewModel.HandleLocalDrop(items, target);
    }

    private void LocalTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        List<EntryItemViewModel> items = _viewModel.GetHighlightedLocalItems();

        if (items.Count == 0 && LocalTree.SelectedItem is EntryItemViewModel selected)
        {
            items.Add(selected);
        }

        if (items.Count > 0)
        {
            _viewModel.DeleteEntries(items, isRemote: false);
            e.Handled = true;
        }
    }

    private void RemoteTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        List<EntryItemViewModel> items = _viewModel.GetHighlightedRemoteItems();

        if (items.Count == 0 && RemoteTree.SelectedItem is EntryItemViewModel selected)
        {
            items.Add(selected);
        }

        if (items.Count > 0)
        {
            _viewModel.DeleteEntries(items, isRemote: true);
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.SaveStateOnClose();
        base.OnClosed(e);
    }
}
