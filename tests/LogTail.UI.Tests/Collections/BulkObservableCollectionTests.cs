using System.Collections.Specialized;
using FluentAssertions;
using LogTail.UI.Collections;

namespace LogTail.UI.Tests.Collections;

/// <summary>
/// The log view binds to this collection, so the notification shape matters:
/// a single range Add keeps the ScrollViewer offset untouched, while a Reset
/// makes it re-anchor and snap the view back.
/// </summary>
public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void AddRange_raises_one_add_notification_for_the_whole_batch()
    {
        var sut = new BulkObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        sut.CollectionChanged += (_, args) => notifications.Add(args);

        sut.AddRange(new[] { 1, 2, 3 });

        notifications.Should().ContainSingle();
        notifications[0].Action.Should().Be(NotifyCollectionChangedAction.Add);
        notifications[0].NewStartingIndex.Should().Be(0);
        notifications[0].NewItems!.Cast<int>().Should().Equal(1, 2, 3);
        sut.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void AddRange_appends_after_existing_items()
    {
        var sut = new BulkObservableCollection<int> { 1, 2 };
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        sut.CollectionChanged += (_, args) => notifications.Add(args);

        sut.AddRange(new[] { 3, 4 });

        notifications.Should().ContainSingle();
        notifications[0].Action.Should().Be(NotifyCollectionChangedAction.Add);
        notifications[0].NewStartingIndex.Should().Be(2);
        notifications[0].NewItems!.Cast<int>().Should().Equal(3, 4);
        sut.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void AddRange_accepts_a_plain_enumerable()
    {
        var sut = new BulkObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        sut.CollectionChanged += (_, args) => notifications.Add(args);

        sut.AddRange(Enumerable.Range(1, 3).Select(value => value * 10));

        notifications.Should().ContainSingle();
        notifications[0].NewItems!.Cast<int>().Should().Equal(10, 20, 30);
        sut.Should().Equal(10, 20, 30);
    }

    [Fact]
    public void AddRange_with_no_items_does_not_notify()
    {
        var sut = new BulkObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        sut.CollectionChanged += (_, args) => notifications.Add(args);

        sut.AddRange(Array.Empty<int>());

        notifications.Should().BeEmpty();
        sut.Should().BeEmpty();
    }

    [Fact]
    public void AddRange_never_raises_reset()
    {
        var sut = new BulkObservableCollection<int>();
        var actions = new List<NotifyCollectionChangedAction>();
        sut.CollectionChanged += (_, args) => actions.Add(args.Action);

        sut.AddRange(new[] { 1, 2, 3 });

        actions.Should().NotContain(NotifyCollectionChangedAction.Reset);
    }
}
