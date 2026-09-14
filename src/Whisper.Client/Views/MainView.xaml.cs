using System.Collections.Specialized;
using System.Windows.Controls;
using Whisper.Client.ViewModels;

namespace Whisper.Client.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel previous)
        {
            previous.Messages.CollectionChanged -= OnMessagesChanged;
        }

        if (e.NewValue is MainViewModel current)
        {
            current.Messages.CollectionChanged += OnMessagesChanged;
        }
    }

    /// <summary>
    /// Follows the conversation only when a new message is appended. Prepending an older
    /// page must leave the scroll position alone or reading history becomes impossible.
    /// </summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsLoaded
            || e.Action != NotifyCollectionChangedAction.Add
            || sender is not System.Collections.ICollection collection
            || e.NewStartingIndex != collection.Count - 1)
        {
            return;
        }

        MessageScroller.ScrollToEnd();
    }
}
