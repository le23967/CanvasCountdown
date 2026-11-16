using System.ComponentModel;
using System.Globalization;
using CanvasCountdown.App.Infrastructure;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace CanvasCountdown.App.Views;

/// <summary>
/// Day, week, month and year. Every decision about what goes where — which
/// days fill a month, how overlapping deadlines share a column, how many chips
/// fit — is made in the core and tested there; this only draws it.
/// </summary>
public sealed partial class CalendarView : UserControl
{
    private const double HourHeight = 44;
    private const double TimeGutter = 54;
    private const double ChipHeight = 34;

    private bool isUpdatingScale;
    private string lastRenderKey = string.Empty;

    public CalendarView()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) => Render(force: true);
    }

    public MainViewModel ViewModel => AppServices.Current.ViewModel;

    private static AppCalendar Calendar => AppCalendar.Current;

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel.SidebarSelection == SidebarDestination.Calendar)
        {
            Render(force: false);
        }
        if (ViewModel.IsShowingCalendarDateEntry && !GoToDateFlyout.IsOpen && IsLoaded)
        {
            GoToDateFlyout.ShowAt(GoToDateButton);
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => Render(force: true);

    private void Render(bool force)
    {
        var viewModel = ViewModel;
        var scale = viewModel.CalendarScale;

        isUpdatingScale = true;
        ScaleBar.SelectedItem = scale switch
        {
            CalendarScale.Day => DayScale,
            CalendarScale.Week => WeekScale,
            CalendarScale.Year => YearScale,
            _ => MonthScale,
        };
        isUpdatingScale = false;

        PeriodTitle.Text = viewModel.CalendarTitle;
        PreviousButton.Content = $"Previous {scale.Title().ToLowerInvariant()}";
        NextButton.Content = $"Next {scale.Title().ToLowerInvariant()}";
        TodayButton.IsEnabled = !viewModel.IsShowingToday;

        if (viewModel.NearestAssignment is { } nearest)
        {
            var days = nearest.RemainingDays(viewModel.CurrentDate, Calendar);
            CompactNextDeadline.Text = $"Next deadline: {days} {(days == 1 ? "day" : "days")} left · {nearest.Title}";
            AutomationProperties.SetName(CompactNextDeadline, $"Next deadline, {nearest.Title}, {days} days left");
            CompactNextDeadline.Visibility = Visibility.Visible;
        }
        else
        {
            CompactNextDeadline.Visibility = Visibility.Collapsed;
        }

        // Redraw only when something on the grid could have changed; the view
        // model announces every change, and most are elsewhere.
        var key = string.Join(
            "|",
            scale,
            viewModel.CalendarAnchor.UtcTicks,
            viewModel.SelectedCalendarDay?.UtcTicks,
            viewModel.CurrentDate.UtcTicks / TimeSpan.TicksPerMinute,
            GridHost.ActualWidth,
            string.Join(",", viewModel.FilteredAssignments.Select(item => item.GetHashCode())),
            string.Join(",", viewModel.EventLabels.Select(label => label.GetHashCode())));
        if (!force && key == lastRenderKey)
        {
            return;
        }
        lastRenderKey = key;

        GridHost.Children.Clear();
        GridHost.Children.Add(scale switch
        {
            CalendarScale.Day => TimeGrid([viewModel.CalendarDay]),
            CalendarScale.Week => TimeGrid(viewModel.CalendarWeekDays),
            CalendarScale.Year => YearGrid(viewModel.CalendarYearMonths),
            _ => MonthGrid(viewModel.CalendarDays),
        });
    }

    // ------------------------------------------------------------------
    // Month

    private UIElement MonthGrid(IReadOnlyList<CalendarDay> days)
    {
        var grid = new Grid { RowSpacing = CalendarLayout.MonthColumnSpacing, ColumnSpacing = CalendarLayout.MonthColumnSpacing };
        for (var column = 0; column < 7; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var symbols = AssignmentCalendar.WeekdaySymbols(Calendar, Culture);
        for (var column = 0; column < symbols.Count; column++)
        {
            var header = new TextBlock
            {
                Text = symbols[column],
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
                Style = Resource<Style>("CaptionTextBlockStyle"),
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
            };
            Grid.SetColumn(header, column);
            grid.Children.Add(header);
        }

        var cells = CalendarMonthCell.Cells(days, ViewModel.SelectedCalendarDay, Calendar, Culture);
        var rows = (int)Math.Ceiling(cells.Count / 7.0);
        for (var row = 0; row < rows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 62 });
        }

        for (var index = 0; index < cells.Count; index++)
        {
            var element = MonthCell(cells[index]);
            Grid.SetRow(element, (index / 7) + 1);
            Grid.SetColumn(element, index % 7);
            grid.Children.Add(element);
        }
        return grid;
    }

    private FrameworkElement MonthCell(CalendarMonthCell cell)
    {
        var day = cell.Day;
        var panel = new StackPanel { Spacing = 2, Padding = new Thickness(4) };

        var number = new TextBlock
        {
            Text = day.DayNumber.ToString(Culture),
            FontWeight = day.IsToday ? FontWeights.Bold : FontWeights.Normal,
            Foreground = day.IsToday
                ? Resource<Brush>("TextOnAccentFillColorPrimaryBrush")
                : Resource<Brush>(day.IsInDisplayedMonth ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush"),
        };
        panel.Children.Add(day.IsToday
            ? new Border
            {
                Background = Resource<Brush>("AccentFillColorDefaultBrush"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 0, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = number,
            }
            : number);

        foreach (var item in cell.VisibleItems)
        {
            panel.Children.Add(Chip(item, compact: true));
        }
        if (cell.OverflowText is { } overflow)
        {
            panel.Children.Add(new TextBlock
            {
                Text = overflow,
                Style = Resource<Style>("CaptionTextBlockStyle"),
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
            });
        }

        var border = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Resource<Brush>(cell.IsSelected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"),
            Background = Resource<Brush>(day.IsInDisplayedMonth ? "CardBackgroundFillColorDefaultBrush" : "CardBackgroundFillColorSecondaryBrush"),
            IsTabStop = true,
            UseSystemFocusVisuals = true,
        };
        AutomationProperties.SetName(border, cell.AccessibilityLabel);
        border.Tapped += (_, args) =>
        {
            ViewModel.SelectCalendarDay(cell.SelectionAfterClick);
            if (cell.SelectionAfterClick is not null)
            {
                DayFlyout(cell).ShowAt(border);
            }
            args.Handled = true;
        };
        border.DoubleTapped += (_, args) =>
        {
            ViewModel.ShowCalendarDate(day.Date, CalendarScale.Day);
            args.Handled = true;
        };
        border.KeyDown += (_, args) =>
        {
            if (args.Key is VirtualKey.Enter or VirtualKey.Space)
            {
                ViewModel.SelectCalendarDay(day.Date);
                DayFlyout(cell).ShowAt(border);
                args.Handled = true;
            }
        };
        return border;
    }

    /// <summary>What a clicked day holds, with a way into the day view.</summary>
    private Flyout DayFlyout(CalendarMonthCell cell)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 240, MaxWidth = 340 };
        panel.Children.Add(new TextBlock { Text = cell.DateText, Style = Resource<Style>("BodyStrongTextBlockStyle") });
        if (cell.Day.Items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Nothing due on this day",
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
            });
        }
        foreach (var item in cell.Day.Items)
        {
            panel.Children.Add(Chip(item, compact: false));
        }
        var flyout = new Flyout { Content = panel };
        var open = new Button { Content = "Open in Day View", HorizontalAlignment = HorizontalAlignment.Right };
        open.Click += (_, _) =>
        {
            flyout.Hide();
            ViewModel.ShowCalendarDate(cell.Day.Date, CalendarScale.Day);
        };
        panel.Children.Add(open);
        return flyout;
    }

    // ------------------------------------------------------------------
    // Week and day

    private UIElement TimeGrid(IReadOnlyList<CalendarDay> days)
    {
        var columns = CalendarTimeGrid.Columns(days, Calendar, Culture);
        var hourLabels = CalendarTimeGrid.HourLabels(Calendar, Culture, ViewModel.CurrentDate);
        var compactAllDay = CalendarTimeGrid.AllDayChipsAreCompact(days.Count);

        var outer = new Grid { RowSpacing = 6 };
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Day headers and the all-day strip, aligned with the columns below.
        var header = ColumnsRow(columns.Count);
        var allDay = ColumnsRow(columns.Count);
        var showsAllDay = CalendarTimeGrid.ShowsAllDayStrip(days, Calendar);
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var title = new TextBlock
            {
                Text = column.WeekdayText,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontWeight = column.Day.IsToday ? FontWeights.Bold : FontWeights.Normal,
                Foreground = Resource<Brush>(column.Day.IsToday ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"),
            };
            var dayButton = new HyperlinkButton { Content = title, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
            ToolTipService.SetToolTip(dayButton, "Open this day");
            var date = column.Day.Date;
            dayButton.Click += (_, _) => ViewModel.ShowCalendarDate(date, CalendarScale.Day);
            Grid.SetColumn(dayButton, index + 1);
            header.Children.Add(dayButton);

            if (showsAllDay)
            {
                var strip = new StackPanel { Spacing = 2, Padding = new Thickness(2, 0, 2, 0) };
                AutomationProperties.SetName(strip, column.AllDayAccessibilityLabel);
                foreach (var item in column.AllDayItems)
                {
                    strip.Children.Add(Chip(item, compact: compactAllDay));
                }
                Grid.SetColumn(strip, index + 1);
                allDay.Children.Add(strip);
            }
        }
        if (showsAllDay)
        {
            var allDayLabel = new TextBlock
            {
                Text = "all-day",
                Style = Resource<Style>("CaptionTextBlockStyle"),
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            allDay.Children.Add(allDayLabel);
        }
        outer.Children.Add(header);
        Grid.SetRow(allDay, 1);
        outer.Children.Add(allDay);

        // The hour grid.
        var height = CalendarLayout.TimeGridHeight(HourHeight);
        var hours = new Grid { Height = height };
        hours.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimeGutter) });
        for (var index = 0; index < columns.Count; index++)
        {
            hours.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var labels = new Canvas();
        for (var hour = 0; hour < hourLabels.Count; hour++)
        {
            var label = new TextBlock
            {
                Text = hourLabels[hour],
                Style = Resource<Style>("CaptionTextBlockStyle"),
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
            };
            Canvas.SetTop(label, (hour * HourHeight) - 7);
            Canvas.SetLeft(label, 4);
            labels.Children.Add(label);
        }
        hours.Children.Add(labels);

        var availableWidth = Math.Max(0, GridHost.ActualWidth - TimeGutter);
        var columnWidth = columns.Count == 0 ? 0 : availableWidth / columns.Count;
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var canvas = new Canvas
            {
                Background = Resource<Brush>(column.Day.IsToday ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTransparentBrush"),
            };
            for (var hour = 0; hour <= CalendarTimeGrid.HoursShown; hour++)
            {
                var line = new Rectangle { Height = 1, Width = Math.Max(0, columnWidth), Fill = Resource<Brush>("DividerStrokeColorDefaultBrush") };
                Canvas.SetTop(line, hour * HourHeight);
                canvas.Children.Add(line);
            }
            foreach (var entry in column.TimedEntries)
            {
                var placement = CalendarLayout.TimedChip(entry, columnWidth, HourHeight);
                var chip = Chip(entry.Item, compact: true);
                chip.Width = placement.Width;
                chip.Height = ChipHeight;
                Canvas.SetLeft(chip, placement.X);
                Canvas.SetTop(chip, placement.Y);
                canvas.Children.Add(chip);
            }
            if (column.Day.IsToday)
            {
                var now = new Rectangle
                {
                    Height = 2,
                    Width = Math.Max(0, columnWidth),
                    Fill = Resource<Brush>("SystemFillColorCriticalBrush"),
                };
                AutomationProperties.SetName(now, "Now");
                Canvas.SetTop(now, CalendarLayout.VerticalOffset(CalendarTimeGrid.MinuteOfDay(ViewModel.CurrentDate, Calendar), HourHeight));
                canvas.Children.Add(now);
            }
            Grid.SetColumn(canvas, index + 1);
            hours.Children.Add(canvas);
        }

        var scroller = new ScrollViewer { Content = hours, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var initialHour = CalendarTimeGrid.InitialHour(days, Calendar);
        scroller.Loaded += (_, _) => scroller.ChangeView(null, initialHour * HourHeight, null, disableAnimation: true);
        Grid.SetRow(scroller, 2);
        outer.Children.Add(scroller);
        return outer;
    }

    private static Grid ColumnsRow(int count)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimeGutter) });
        for (var index = 0; index < count; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        return grid;
    }

    // ------------------------------------------------------------------
    // Year

    private UIElement YearGrid(IReadOnlyList<CalendarMonthSummary> months)
    {
        var layout = CalendarLayout.YearMonthColumns(Math.Max(0, GridHost.ActualWidth - 8));
        var grid = new Grid { ColumnSpacing = CalendarLayout.YearMonthSpacing, RowSpacing = CalendarLayout.YearMonthSpacing };
        for (var column = 0; column < layout.Count; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Width) });
        }
        var rows = (int)Math.Ceiling(months.Count / (double)Math.Max(1, layout.Count));
        for (var row = 0; row < rows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (var index = 0; index < months.Count; index++)
        {
            var element = MiniMonth(months[index], layout.Width);
            Grid.SetRow(element, index / layout.Count);
            Grid.SetColumn(element, index % layout.Count);
            grid.Children.Add(element);
        }
        return new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private FrameworkElement MiniMonth(CalendarMonthSummary month, double width)
    {
        var panel = new StackPanel { Spacing = 4 };
        var title = new HyperlinkButton { Content = month.Title, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(title, "Open this month");
        title.Click += (_, _) => ViewModel.ShowCalendarDate(month.Start, CalendarScale.Month);
        panel.Children.Add(title);

        var dayWidth = CalendarLayout.YearDayColumnWidth(width);
        var days = new Grid { ColumnSpacing = CalendarLayout.YearDaySpacing, RowSpacing = 1 };
        for (var column = 0; column < 7; column++)
        {
            days.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(dayWidth) });
        }
        var symbols = AssignmentCalendar.NarrowWeekdaySymbols(Calendar, Culture);
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var column = 0; column < symbols.Count; column++)
        {
            var header = new TextBlock
            {
                Text = symbols[column],
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
            };
            Grid.SetColumn(header, column);
            days.Children.Add(header);
        }

        var cells = CalendarYearCell.Cells(month, item => ViewModel.Label(item) is not null, Culture);
        var rows = (int)Math.Ceiling(cells.Count / 7.0);
        for (var row = 0; row < rows; row++)
        {
            days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            if (cell.IsHidden)
            {
                continue;
            }
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var number = new TextBlock
            {
                Text = cell.Day.DayNumber.ToString(Culture),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontWeight = cell.Day.IsToday ? FontWeights.Bold : FontWeights.Normal,
                Foreground = Resource<Brush>(cell.Day.IsToday ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"),
            };
            stack.Children.Add(number);
            if (cell.Dot.Tint != CalendarTint.Clear)
            {
                stack.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = TintBrush(cell.Dot.Tint, cell.Dot.LabelledItem) });
            }
            var button = new Button
            {
                Content = stack,
                Padding = new Thickness(0, 1, 0, 1),
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Resource<Brush>("SubtleFillColorTransparentBrush"),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(button, cell.AccessibilityLabel);
            var date = cell.Day.Date;
            button.Click += (_, _) => ViewModel.ShowCalendarDate(date, CalendarScale.Day);
            Grid.SetRow(button, (index / 7) + 1);
            Grid.SetColumn(button, index % 7);
            days.Children.Add(button);
        }
        panel.Children.Add(days);
        return panel;
    }

    // ------------------------------------------------------------------
    // A deadline, anywhere on the calendar

    private Button Chip(AssignmentListItem item, bool compact)
    {
        var label = ViewModel.Label(item);
        var style = CalendarChip.Style(item, label is not null);
        var tint = TintBrush(style.Tint, item);

        var title = new TextBlock
        {
            Text = item.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = compact ? 12 : 14,
            Foreground = Resource<Brush>(style.IsDimmed ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"),
            TextDecorations = style.IsStruckThrough ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new Rectangle { Width = 3, Fill = tint, RadiusX = 1.5, RadiusY = 1.5, Height = compact ? 14 : 18 });
        if (!compact)
        {
            content.Children.Add(new TextBlock
            {
                Text = CalendarChip.TimeText(item, Calendar, Culture),
                FontSize = 12,
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        content.Children.Add(title);

        var chip = new Button
        {
            Content = content,
            Padding = new Thickness(4, 1, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(TintColor(style.Tint, item, 0x26)),
        };
        var help = CalendarChip.HelpText(item, Calendar, Culture);
        ToolTipService.SetToolTip(chip, help);
        AutomationProperties.SetName(chip, help);
        chip.Click += (_, args) => ViewModel.PresentEditor(item);
        chip.ContextRequested += (_, args) =>
        {
            EventActions.Menu(ViewModel, item, XamlRoot).ShowAt(chip);
            args.Handled = true;
        };
        return chip;
    }

    private Brush TintBrush(CalendarTint tint, AssignmentListItem? item) => new SolidColorBrush(TintColor(tint, item, 0xFF));

    private Windows.UI.Color TintColor(CalendarTint tint, AssignmentListItem? item, byte alpha)
    {
        var color = tint switch
        {
            CalendarTint.Label when item is not null && ViewModel.Label(item) is { } label => Palette.ColorFromHex(label.ColorHex),
            CalendarTint.Accent => Resource<Windows.UI.Color>("SystemAccentColor"),
            CalendarTint.Secondary => Microsoft.UI.Colors.Gray,
            _ => Microsoft.UI.Colors.Transparent,
        };
        return Windows.UI.Color.FromArgb(tint == CalendarTint.Clear ? (byte)0 : alpha, color.R, color.G, color.B);
    }

    private static T Resource<T>(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is T typed ? typed : default!;

    // ------------------------------------------------------------------
    // The bar

    private void OnScaleChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (isUpdatingScale || sender.SelectedItem?.Tag is not string tag || !Enum.TryParse<CalendarScale>(tag, out var scale))
        {
            return;
        }
        ViewModel.ShowCalendarScale(scale);
    }

    private void OnPrevious(object sender, RoutedEventArgs e) => ViewModel.ShowCalendar(-1);

    private void OnNext(object sender, RoutedEventArgs e) => ViewModel.ShowCalendar(1);

    private void OnToday(object sender, RoutedEventArgs e) => ViewModel.ShowToday();

    private void OnGoToDateOpened(object? sender, object e)
    {
        ViewModel.IsShowingCalendarDateEntry = true;
        DateEntry.Text = ViewModel.CalendarDateEntry;
        UpdateDateEntryPreview();
        DateEntry.Focus(FocusState.Programmatic);
    }

    private void OnGoToDateClosed(object? sender, object e) => ViewModel.IsShowingCalendarDateEntry = false;

    private void OnDateEntryChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.CalendarDateEntry = DateEntry.Text;
        UpdateDateEntryPreview();
    }

    private void UpdateDateEntryPreview()
    {
        var preview = ViewModel.CalendarDateEntryPreview;
        DateEntryMessage.Text = preview.Message;
        GoButton.IsEnabled = preview.CanGo;
    }

    private void OnDateEntryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            Go();
            e.Handled = true;
        }
    }

    private void OnGo(object sender, RoutedEventArgs e) => Go();

    /// <summary>Stays open on text it cannot read, so a typo can be fixed rather than retyped.</summary>
    private void Go()
    {
        if (ViewModel.CommitCalendarDateEntry())
        {
            GoToDateFlyout.Hide();
        }
    }
}
