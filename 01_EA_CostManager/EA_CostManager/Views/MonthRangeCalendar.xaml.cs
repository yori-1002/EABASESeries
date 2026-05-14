using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace EA_CostManager.Views
{
    /// <summary>
    /// 2ヶ月横並びの日付範囲選択カレンダーコントロール
    /// 年選択プルダウン付き。1回目クリック=開始日、2回目クリック=終了日。
    /// </summary>
    public partial class MonthRangeCalendar : UserControl
    {
        private DateTime _left_month;
        private DateTime _right_month;
        private DateTime? _date_from;
        private DateTime? _date_to;
        private bool _selecting_from = true;
        private bool _updating_year = false;

        private static readonly SolidColorBrush C_START_END = new(Color.FromRgb(0x2E, 0x75, 0xB6));
        private static readonly SolidColorBrush C_IN_RANGE = new(Color.FromRgb(0xBE, 0xD8, 0xF0));
        private static readonly SolidColorBrush C_CLEAR = Brushes.Transparent;
        private static readonly SolidColorBrush C_WHITE = Brushes.White;
        private static readonly SolidColorBrush C_DARK = new(Color.FromRgb(0x1A, 0x1A, 0x2E));
        private static readonly SolidColorBrush C_TODAY = new(Color.FromRgb(0xE6, 0x7E, 0x22));
        private static readonly SolidColorBrush C_SUN = new(Color.FromRgb(0xC0, 0x39, 0x2B));
        private static readonly SolidColorBrush C_SAT = new(Color.FromRgb(0x2E, 0x75, 0xB6));
        private static readonly SolidColorBrush C_GRAY = new(Color.FromRgb(0x88, 0x88, 0x88));

        public DateTime? SelectedDateFrom => _date_from;
        public DateTime? SelectedDateTo => _date_to;
        public event EventHandler? RangeSelected;

        public MonthRangeCalendar()
        {
            InitializeComponent();
            var today = DateTime.Today;
            _left_month = new DateTime(today.Year, today.Month, 1);
            _right_month = _left_month.AddMonths(1);
            init_year_combobox();
            render_both();
        }

        // ---- 年プルダウン初期化 ----
        private void init_year_combobox()
        {
            _updating_year = true;
            cmb_year.Items.Clear();
            int cur = DateTime.Today.Year;
            for (int y = cur - 5; y <= cur + 1; y++)
                cmb_year.Items.Add(y);
            cmb_year.SelectedItem = cur;
            _updating_year = false;
        }

        private void cmb_year_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating_year || cmb_year.SelectedItem is not int yr) return;
            _left_month = new DateTime(yr, 1, 1);
            _right_month = new DateTime(yr, 2, 1);
            render_both();
        }

        // ---- 外部セット ----
        public void set_range(DateTime? from, DateTime? to)
        {
            _date_from = from; _date_to = to;
            _selecting_from = (from == null);
            if (from.HasValue)
            {
                _left_month = new DateTime(from.Value.Year, from.Value.Month, 1);
                _right_month = _left_month.AddMonths(1);
                _updating_year = true;
                if (cmb_year.Items.Contains(from.Value.Year))
                    cmb_year.SelectedItem = from.Value.Year;
                _updating_year = false;
            }
            update_range_display(); render_both();
        }

        // ---- ナビゲーション ----
        private void btn_prev_left_Click(object sender, RoutedEventArgs e)
        { _left_month = _left_month.AddMonths(-1); if (_left_month >= _right_month) _right_month = _left_month.AddMonths(1); sync_year(); render_both(); }
        private void btn_next_left_Click(object sender, RoutedEventArgs e)
        { _left_month = _left_month.AddMonths(1); if (_left_month >= _right_month) _right_month = _left_month.AddMonths(1); sync_year(); render_both(); }
        private void btn_prev_right_Click(object sender, RoutedEventArgs e)
        { _right_month = _right_month.AddMonths(-1); if (_right_month <= _left_month) _left_month = _right_month.AddMonths(-1); sync_year(); render_both(); }
        private void btn_next_right_Click(object sender, RoutedEventArgs e)
        { _right_month = _right_month.AddMonths(1); sync_year(); render_both(); }
        private void btn_clear_Click(object sender, RoutedEventArgs e)
        { _date_from = null; _date_to = null; _selecting_from = true; update_range_display(); render_both(); }

        private void sync_year()
        {
            _updating_year = true;
            if (cmb_year.Items.Contains(_left_month.Year)) cmb_year.SelectedItem = _left_month.Year;
            _updating_year = false;
        }

        // ---- レンダリング ----
        private void render_both()
        {
            txt_left_header.Text = $"{_left_month.Year}年{_left_month.Month}月";
            txt_right_header.Text = $"{_right_month.Year}年{_right_month.Month}月";
            render_calendar(grid_left_header, grid_left, _left_month);
            render_calendar(grid_right_header, grid_right, _right_month);
        }

        private void render_calendar(UniformGrid hdr, UniformGrid days, DateTime month)
        {
            hdr.Children.Clear();
            string[] names = { "日", "月", "火", "水", "木", "金", "土" };
            for (int i = 0; i < 7; i++)
                hdr.Children.Add(new TextBlock
                {
                    Text = names[i],
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = i == 0 ? C_SUN : i == 6 ? C_SAT : C_DARK,
                    Margin = new Thickness(1, 0, 1, 3),
                });

            days.Children.Clear();
            int start_dow = (int)new DateTime(month.Year, month.Month, 1).DayOfWeek;
            int day_count = DateTime.DaysInMonth(month.Year, month.Month);
            for (int i = 0; i < start_dow; i++) days.Children.Add(new TextBlock());
            for (int d = 1; d <= day_count; d++)
                days.Children.Add(make_day_btn(new DateTime(month.Year, month.Month, d)));
        }

        private Button make_day_btn(DateTime date)
        {
            bool is_start = _date_from.HasValue && date == _date_from.Value;
            bool is_end = _date_to.HasValue && date == _date_to.Value;
            bool in_range = _date_from.HasValue && _date_to.HasValue && date > _date_from.Value && date < _date_to.Value;
            bool is_today = date == DateTime.Today;
            bool is_sun = date.DayOfWeek == DayOfWeek.Sunday;
            bool is_sat = date.DayOfWeek == DayOfWeek.Saturday;

            Brush bg, fg;
            if (is_start || is_end) { bg = C_START_END; fg = C_WHITE; }
            else if (in_range) { bg = C_IN_RANGE; fg = C_DARK; }
            else { bg = C_CLEAR; fg = is_sun ? C_SUN : is_sat ? C_SAT : C_DARK; }

            var btn = new Button
            {
                Content = date.Day.ToString(),
                Width = 34,
                Height = 28,
                Margin = new Thickness(1),
                FontSize = 12,
                FontWeight = is_today ? FontWeights.Bold : FontWeights.Normal,
                Background = bg,
                Foreground = fg,
                BorderThickness = (is_today && !is_start && !is_end) ? new Thickness(1.5) : new Thickness(0),
                BorderBrush = C_TODAY,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = date,
                Template = build_template(),
            };
            btn.Click += day_btn_Click;
            return btn;
        }

        private static ControlTemplate build_template()
        {
            var tpl = new ControlTemplate(typeof(Button));
            var f = new FrameworkElementFactory(typeof(Border));
            var rel = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent);
            f.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = rel });
            f.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = rel });
            f.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = rel });
            f.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            f.AppendChild(cp); tpl.VisualTree = f; return tpl;
        }

        private void day_btn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DateTime clicked) return;
            if (_selecting_from) { _date_from = clicked; _date_to = null; _selecting_from = false; }
            else
            {
                if (clicked < _date_from) { _date_to = _date_from; _date_from = clicked; }
                else { _date_to = clicked; }
                _selecting_from = true;
                RangeSelected?.Invoke(this, EventArgs.Empty);
            }
            update_range_display(); render_both();
        }

        private void update_range_display()
        {
            if (_date_from == null) { txt_range_display.Text = "クリックして開始日を選択してください"; txt_range_display.Foreground = C_GRAY; }
            else if (_date_to == null) { txt_range_display.Text = $"{_date_from:yyyy/MM/dd} ～ 終了日をクリックしてください"; txt_range_display.Foreground = C_TODAY; }
            else { txt_range_display.Text = $"{_date_from:yyyy/MM/dd} ～ {_date_to:yyyy/MM/dd}"; txt_range_display.Foreground = C_START_END; }
        }
    }
}