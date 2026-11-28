// SPDX-License-Identifier: AGPL-3.0-or-later
using TVControlForWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TVControlForWin.Views;

public sealed partial class SchedulePage : Page
{
    public MainViewModel VM => ((App)Application.Current).ViewModel!;

    public SchedulePage()
    {
        // Which TVs can be dimmed can change while the app runs; size the TV pickers to now.
        VM.RefreshTvLists();
        InitializeComponent();
        // The page isn't observable, so EmptyVisibility binds OneTime and is
        // re-evaluated here whenever the schedule list grows or shrinks. Only while the page is
        // shown: the list lives as long as the app, a new page is made on every visit, and a
        // handler left on the list kept every page ever visited alive and updating.
        Loaded += (_, _) => VM.Schedules.CollectionChanged += OnSchedulesChanged;
        Unloaded += (_, _) => VM.Schedules.CollectionChanged -= OnSchedulesChanged;
    }

    private void OnSchedulesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Bindings.Update();

    public Visibility EmptyVisibility =>
        VM.Schedules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduleViewModel s })
            VM.RemoveScheduleCommand.Execute(s);
    }

    // ----- TV picker -----
    //
    // The flyout holds an "All TVs" box, a divider, and a panel that gets one box per TV each time
    // it opens, so it lists exactly the TVs that can be dimmed at that moment. The schedule being
    // edited is kept on the flyout's root panel for the click handlers.

    private void OnTvFlyoutOpening(object sender, object e)
    {
        if (sender is not Flyout { Content: StackPanel root } flyout) return;
        if (flyout.Target is not FrameworkElement { Tag: ScheduleViewModel schedule }) return;

        root.Tag = schedule;
        var tvPanel = (StackPanel)root.Children[2];
        tvPanel.Children.Clear();
        foreach (var tv in schedule.PickerTvs)
        {
            var box = new CheckBox { Content = tv.Name, Tag = tv.Id };
            box.Click += OnTvClick;
            tvPanel.Children.Add(box);
        }
        SyncTvPicker(root, schedule);
    }

    private void OnAllTvsClick(object sender, RoutedEventArgs e) => ToggleTarget(sender, null);

    private void OnTvClick(object sender, RoutedEventArgs e) =>
        ToggleTarget(sender, (sender as FrameworkElement)?.Tag as string);

    private static void ToggleTarget(object sender, string? tvId)
    {
        // Walk up from the clicked box to the root panel, which carries the schedule.
        var element = sender as FrameworkElement;
        while (element is not null && element.Tag is not ScheduleViewModel)
            element = element.Parent as FrameworkElement;
        if (element is not StackPanel { Tag: ScheduleViewModel schedule } root) return;

        schedule.ToggleTarget(tvId);
        SyncTvPicker(root, schedule);
    }

    // The boxes always show the schedule's state rather than their own click: picking a TV
    // unticks "All TVs", unticking the last TV ticks it again, and so on.
    private static void SyncTvPicker(StackPanel root, ScheduleViewModel schedule)
    {
        ((CheckBox)root.Children[0]).IsChecked = schedule.TargetsAll;
        foreach (var box in ((StackPanel)root.Children[2]).Children.OfType<CheckBox>())
            box.IsChecked = box.Tag is string id && schedule.TargetsTv(id);
    }
}
