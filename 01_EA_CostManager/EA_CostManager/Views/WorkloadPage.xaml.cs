using System.Windows;
using System.Windows.Controls;
using EA_CostManager.ViewModels;

namespace EA_CostManager.Views
{
    /// <summary>
    /// ▼ 修正 [Sprint 9A]：工数表ページ
    ///
    /// 【DataContext】
    /// ページ専用の workload_view_model を自前で生成して保持する。
    /// MainWindow の main_view_model は Visibility バインド
    /// （RelativeSource AncestorType=Window）でのみ参照されるため干渉しない。
    /// サイドバーの大区分ツリーからは MainWindow 側がこのVMの selected_group を更新する。
    ///
    /// 【初期化のタイミング】
    /// 起動時に全ページが生成される構成のため、コンストラクタでDBアクセスすると
    /// 工数表を使わない起動でも読み込みが走ってしまう。
    /// そのため IsVisibleChanged で初回表示時にのみ案件一覧を読み込む。
    ///
    /// 【タブ展開ボタン】
    /// CostPage と同一仕様。ControlTemplate 内の tab_expand_border は
    /// テンプレート適用後でないと FindName で取得できないため、
    /// Loaded で ApplyTemplate() を呼んでからイベントをアタッチする。
    /// </summary>
    public partial class WorkloadPage : UserControl
    {
        /// <summary>このページ専用のViewModel（MainWindow から大区分の選択を反映するため公開）</summary>
        public workload_view_model vm { get; } = new();

        /// <summary>タブ一覧が全展開されているか（false＝3段まで表示）</summary>
        private bool _is_tabs_expanded = false;

        public WorkloadPage()
        {
            InitializeComponent();
            DataContext = vm;

            Loaded += (_, _) =>
            {
                // ▼ タブ展開ボタンのイベントをテンプレート適用後にアタッチ
                //   （ControlTemplate 内の要素は ApplyTemplate 前は取得できない）
                project_tab_control.ApplyTemplate();
                var expand_border = project_tab_control.Template.FindName(
                    "tab_expand_border", project_tab_control) as Border;
                if (expand_border != null)
                {
                    // 二重登録を防ぐため、いったん解除してから登録する
                    // （Loaded はページの再表示で複数回発火しうるため）
                    expand_border.MouseLeftButtonUp -= tab_expand_border_click;
                    expand_border.MouseLeftButtonUp += tab_expand_border_click;
                }
            };

            IsVisibleChanged += WorkloadPage_IsVisibleChanged;
        }

        /// <summary>初回表示時に案件一覧を読み込む（2回目以降は ensure 側で無視される）</summary>
        private async void WorkloadPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool visible && visible)
                await vm.ensure_initialized_async();
        }

        /// <summary>
        /// タブ一覧の展開／折りたたみを切り替える。
        /// 折りたたみ時は MaxHeight=90（3段まで）、展開時は制限なし。
        /// ※ CostPage.tab_expand_border_click と同一仕様。
        /// </summary>
        private void tab_expand_border_click(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            _is_tabs_expanded = !_is_tabs_expanded;

            var scroll = project_tab_control.Template.FindName(
                "tab_header_scroll", project_tab_control) as ScrollViewer;
            var icon = project_tab_control.Template.FindName(
                "tab_expand_icon", project_tab_control) as TextBlock;

            if (scroll != null)
                scroll.MaxHeight = _is_tabs_expanded ? double.PositiveInfinity : 90;
            if (icon != null)
                icon.Text = _is_tabs_expanded ? "▲" : "▼";

            e.Handled = true;
        }
    }
}