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
        PasswordBox.PasswordChanged += (_, _) => { _viewModel.Password = PasswordBox.Password; };

        Loaded += MainWindow_Loaded;

        _viewModel.ScrollToRemoteItemRequested += ScrollRemoteItemIntoView;
    }

    public string LogPath
    {
        get
        {
            return LogService.FilePath;
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LocalFileViewModel? lastLocal = _viewModel.LastLocalFolder;

            if (lastLocal != null)
            {
                ScrollLocalItemIntoView(lastLocal);
            }
        }, DispatcherPriority.Loaded);
    }

    private void ScrollLocalItemIntoView(LocalFileViewModel item)
    {
        if (LocalTree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
        {
            ScrollItemToCenter(container, LocalTree);
            return;
        }

        foreach (object root in LocalTree.Items)
        {
            if (root is LocalFileViewModel rootItem &&
                TryFindLocalContainerRecursive(LocalTree.ItemContainerGenerator, rootItem, item, out TreeViewItem? found))
            {
                if (found != null)
                {
                    ScrollItemToCenter(found, LocalTree);
                }

                return;
            }
        }
    }

    public void ScrollRemoteItemIntoView(RemoteFileViewModel item)
    {
        if (RemoteTree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
        {
            ScrollItemToCenter(container, RemoteTree);
            return;
        }

        foreach (object root in RemoteTree.Items)
        {
            if (root is RemoteFileViewModel rootItem &&
                TryFindRemoteContainerRecursive(RemoteTree.ItemContainerGenerator, rootItem, item, out TreeViewItem? found))
            {
                if (found != null)
                {
                    ScrollItemToCenter(found, RemoteTree);
                }

                return;
            }
        }
    }

    private static void ScrollItemToCenter(TreeViewItem container, ItemsControl tree)
    {
        container.BringIntoView();

        tree.Dispatcher.BeginInvoke(() =>
        {
            ScrollViewer? scroll = FindScrollViewer(tree);

            if (scroll == null)
            {
                return;
            }

            GeneralTransform transform = container.TransformToVisual(scroll);
            Rect bounds = transform.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

            double targetOffset = scroll.VerticalOffset + bounds.Top - (scroll.ViewportHeight / 2.0) + (bounds.Height / 2.0);
            scroll.ScrollToVerticalOffset(Math.Max(0, targetOffset));
        }, DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);

        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);

            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            ScrollViewer? result = FindScrollViewer(child);

            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static bool TryFindLocalContainerRecursive(
        ItemContainerGenerator generator,
        LocalFileViewModel current,
        LocalFileViewModel target,
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
            foreach (object child in current.Children)
            {
                if (child is LocalFileViewModel childItem &&
                    TryFindLocalContainerRecursive(container.ItemContainerGenerator, childItem, target, out found))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindRemoteContainerRecursive(
        ItemContainerGenerator generator,
        RemoteFileViewModel current,
        RemoteFileViewModel target,
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
            foreach (object child in current.Children)
            {
                if (child is RemoteFileViewModel childItem &&
                    TryFindRemoteContainerRecursive(container.ItemContainerGenerator, childItem, target, out found))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private RemoteFileViewModel? GetRemoteItemUnderMouse(object? source)
    {
        DependencyObject? current = source as DependencyObject;

        while (current != null)
        {
            if (current is TreeViewItem treeViewItem && treeViewItem.DataContext is RemoteFileViewModel item)
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private LocalFileViewModel? GetLocalItemUnderMouse(object? source)
    {
        DependencyObject? current = source as DependencyObject;

        while (current != null)
        {
            if (current is TreeViewItem treeViewItem && treeViewItem.DataContext is LocalFileViewModel item)
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void RemoteItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem treeViewItem && treeViewItem.DataContext is RemoteFileViewModel item)
        {
            _viewModel.OnRemoteItemExpanded(item);
        }
    }

    private void RemoteItem_Collapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem treeViewItem && treeViewItem.DataContext is RemoteFileViewModel item)
        {
            _viewModel.OnRemoteItemCollapsed(item);
        }
    }

    private void RemoteTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _viewModel.OnRemoteSelectionChanged(e.NewValue as RemoteFileViewModel);
    }

    private void RemoteTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        List<RemoteFileViewModel> items = _viewModel.GetHighlightedRemoteItems();

        if (items.Count == 0 && RemoteTree.SelectedItem is RemoteFileViewModel selected)
        {
            items.Add(selected);
        }

        if (items.Count > 0)
        {
            _viewModel.DeleteRemoteItems(items);
            e.Handled = true;
        }
    }

    private void RemoteTree_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);

        if (e.OriginalSource is not TextBlock)
        {
            return;
        }

        RemoteFileViewModel? item = GetRemoteItemUnderMouse(e.OriginalSource);

        if (item == null)
        {
            return;
        }

        _viewModel.OnRemoteMouseDown(item, Keyboard.Modifiers);
    }

    private void RemoteTree_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        RemoteFileViewModel? underMouse = GetRemoteItemUnderMouse(e.OriginalSource);

        if (underMouse == null)
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

        List<RemoteFileViewModel> selectedItems = _viewModel.GetHighlightedRemoteItems();

        if (selectedItems.Count == 0)
        {
            selectedItems.Add(underMouse);
        }

        _dragging = true;

        try
        {
            DataObject data = new DataObject();
            data.SetData("StreamZillaRemoteItems", selectedItems);
            DragDrop.DoDragDrop(RemoteTree, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
        }
    }

    private void LocalTree_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("StreamZillaRemoteItems"))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void LocalTree_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("StreamZillaRemoteItems"))
        {
            return;
        }

        LocalFileViewModel? target = GetLocalItemUnderMouse(e.OriginalSource);

        if (target == null)
        {
            target = _viewModel.LastLocalFolder;
        }

        List<RemoteFileViewModel> items = (List<RemoteFileViewModel>)e.Data.GetData("StreamZillaRemoteItems");

        _viewModel.HandleRemoteDrop(items, target);
    }

    private void LocalTree_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);

        if (e.OriginalSource is not TextBlock)
        {
            return;
        }

        LocalFileViewModel? item = GetLocalItemUnderMouse(e.OriginalSource);

        if (item == null)
        {
            return;
        }

        _viewModel.OnLocalMouseDown(item, Keyboard.Modifiers);
    }

    private void LocalTree_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        LocalFileViewModel? underMouse = GetLocalItemUnderMouse(e.OriginalSource);

        if (underMouse == null)
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

        List<LocalFileViewModel> selectedItems = _viewModel.GetHighlightedLocalItems();

        if (selectedItems.Count == 0)
        {
            selectedItems.Add(underMouse);
        }

        _dragging = true;

        try
        {
            DataObject data = new DataObject();
            data.SetData("StreamZillaLocalItems", selectedItems);
            DragDrop.DoDragDrop(LocalTree, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
        }
    }

    private void RemoteTree_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("StreamZillaLocalItems"))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void RemoteTree_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("StreamZillaLocalItems"))
        {
            return;
        }

        RemoteFileViewModel? target = GetRemoteItemUnderMouse(e.OriginalSource);

        if (target == null)
        {
            target = null;
        }

        List<LocalFileViewModel> items = (List<LocalFileViewModel>)e.Data.GetData("StreamZillaLocalItems");

        _viewModel.HandleLocalDrop(items, target);
    }

    private void LocalItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem treeViewItem && treeViewItem.DataContext is LocalFileViewModel item)
        {
            _viewModel.OnLocalItemExpanded(item);
        }
    }

    private void LocalItem_Collapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem treeViewItem && treeViewItem.DataContext is LocalFileViewModel item)
        {
            _viewModel.OnLocalItemCollapsed(item);
        }
    }

    private void LocalTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _viewModel.OnLocalSelectionChanged(e.NewValue as LocalFileViewModel);
    }

    private void LocalTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        List<LocalFileViewModel> items = _viewModel.GetHighlightedLocalItems();

        if (items.Count == 0 && LocalTree.SelectedItem is LocalFileViewModel selected)
        {
            items.Add(selected);
        }

        if (items.Count > 0)
        {
            _viewModel.DeleteLocalItems(items);
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.SaveStateOnClose();
        base.OnClosed(e);
    }
}
