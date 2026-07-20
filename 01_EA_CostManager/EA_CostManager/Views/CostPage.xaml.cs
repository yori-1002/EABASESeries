using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Views;
using EA_CostManager.ViewModels;
using EA_CostManager.Services; // ▼▼▼ [Sprint 5G] 追加：EditHistoryService用 ▼▼▼

namespace EA_CostManager.Views
{
    public partial class CostPage : UserControl
    {
        private cost_view_model? _vm;
        // ▼▼▼ 追加：タブヘッダー展開状態（false=3段制限 / true=全展開）▼▼▼
        private bool _is_tabs_expanded = false;
        // ▼▼▼ 追加：ドラッグ&ドロップ用フィールド ▼▼▼
        private project_cost_view_model? _drag_source_vm;    // ドラッグ元のVM
        private System.Windows.Point _drag_start_pos;        // ドラッグ開始座標
        private const double DRAG_THRESHOLD = 6.0;           // ドラッグ判定の移動距離（px）

        // ▼▼▼ ★v0.9.7追加：複数選択タブ管理用フィールド ▼▼▼
        // Ctrl+クリック / Shift+クリックで複数選択された現場タブを保持するリスト
        // 通常クリック時はクリア。一括属性変更・一括アーカイブで使用。
        // List<>でなく専用のフィールドにしている理由：選択順序を維持＋is_multi_selectedフラグの同期管理が必要なため
        private readonly System.Collections.Generic.List<project_cost_view_model> _multi_selected_tabs = new();

        public CostPage()
        {
            InitializeComponent();

            // ▼▼▼ 修正：main_view_model の共有インスタンスを使用 ▼▼▼
            // MainWindow.xaml のサイドバーグループ選択と同期するため
            // new cost_view_model() ではなく main_view_model.cost_vm を参照する
            Loaded += async (_, _) =>
            {
                var main_vm = Window.GetWindow(this)?.DataContext as EA_CostManager.ViewModels.main_view_model;
                if (main_vm != null)
                {
                    _vm = main_vm.cost_vm;
                    DataContext = _vm;
                    await _vm.initialize_async();
                }

                // ▼▼▼ 追加：タブ展開ボタンのイベントをテンプレート適用後にアタッチ ▼▼▼
                project_tab_control.ApplyTemplate();
                var expand_border = project_tab_control.Template.FindName(
                    "tab_expand_border", project_tab_control) as System.Windows.Controls.Border;
                if (expand_border != null)
                    expand_border.MouseLeftButtonUp += tab_expand_border_click;

                // ▼▼▼ 追加：WrapPanel にドラッグ&ドロップイベントをアタッチ ▼▼▼
                // Template.FindName で HeaderPanel（WrapPanel）を取得してイベントを登録する
                // AllowDrop=true を設定しないと Drop イベントが発火しない
                var header_panel = project_tab_control.Template.FindName(
                    "HeaderPanel", project_tab_control) as System.Windows.Controls.WrapPanel;
                if (header_panel != null)
                {
                    header_panel.AllowDrop = true;
                    header_panel.PreviewMouseLeftButtonDown += tab_header_mouse_down;
                    header_panel.MouseMove += tab_header_mouse_move;
                    header_panel.Drop += tab_header_drop;
                }
            };

            IsVisibleChanged += async (_, e) =>
            {
                if ((bool)e.NewValue && _vm != null)
                {
                    await _vm.initialize_async();
                    // ▼▼▼ [現場タブ追跡] ページ表示時に現在のタブをDBに記録してTooltipを更新 ▼▼▼
                    var main_vm = Window.GetWindow(this)?.DataContext
                        as EA_CostManager.ViewModels.main_view_model;
                    if (main_vm != null)
                    {
                        string tab_name = _vm.selected_tab?.tab_name ?? "";
                        await main_vm.update_current_tab_async(tab_name);
                        await main_vm.refresh_online_users_async();
                    }
                }
            };
            // ▼ [Sprint 5G] ショートカットはMainWindow.OnKeyDownで一括処理するためここでは登録しない
        }

        // ▼▼▼ タブスクロール制御 ▼▼▼
        // TabControlのテンプレート内に x:Name="tab_header_scroll" (ScrollViewer) を定義済み
        // HorizontalScrollBarVisibility を Disabled にすることでタブが動かなくなる
        // 詳細設定でONにするとEnabledに切り替わる

        // ▼▼▼ タブ表示はCostPage.xamlのWrapPanel固定（設定不要） ▼▼▼

        // ▼▼▼ 追加：月度ヘッダー行の折り畳みボタンハンドラー ▼▼▼
        // 月度ヘッダー行の▼/▶ボタンをクリックするとその月度のデータ行を表示/非表示切り替える
        private void btn_month_toggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            if (btn.Tag is not EA_CostManager.Models.cost_row_item item) return;
            if (!item.is_header) return;

            // ▼▼▼ 修正：折りたたみ後にスクロール位置が先頭に戻る問題の対策 ▼▼▼
            // ICollectionView.Refresh() で DataGrid が全行を再構築しスクロールがリセットされるため
            // toggle 前に ScrollViewer の VerticalOffset を保存し、描画完了後（Background優先度）に復元する
            var dg = find_visual_ancestor<System.Windows.Controls.DataGrid>(btn);
            var sv = dg != null ? find_visual_child<System.Windows.Controls.ScrollViewer>(dg) : null;
            double saved_offset = sv?.VerticalOffset ?? 0;

            var vm = find_ancestor<EA_CostManager.ViewModels.filter_tab_view_model>(btn);
            if (vm != null)
                vm.toggle_month_group(item.fiscal_month);

            // 描画完了後に選択解除 + スクロール位置を復元（Background = Render より低優先度 = 描画後に実行）
            if (dg != null)
            {
                dg.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new System.Action(() =>
                    {
                        dg.UnselectAll();
                        sv?.ScrollToVerticalOffset(saved_offset);
                    }));
            }

            e.Handled = true;
        }

        // ▼▼▼ 追加：年ヘッダー行の折り畳みボタンハンドラー ▼▼▼
        // 年ヘッダー行の▼/▶ボタンをクリックするとその年の全月度・データ行を表示/非表示切り替える
        private void btn_year_toggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            if (btn.Tag is not EA_CostManager.Models.cost_row_item item) return;
            if (!item.is_year_header) return;

            // ▼▼▼ 修正：折りたたみ後にスクロール位置が先頭に戻る問題の対策 ▼▼▼
            var dg = find_visual_ancestor<System.Windows.Controls.DataGrid>(btn);
            var sv = dg != null ? find_visual_child<System.Windows.Controls.ScrollViewer>(dg) : null;
            double saved_offset = sv?.VerticalOffset ?? 0;

            var vm = find_ancestor<EA_CostManager.ViewModels.filter_tab_view_model>(btn);
            if (vm != null)
                vm.toggle_year_group(item.year_key);

            if (dg != null)
            {
                dg.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new System.Action(() =>
                    {
                        dg.UnselectAll();
                        sv?.ScrollToVerticalOffset(saved_offset);
                    }));
            }

            e.Handled = true;
        }

        // ▼▼▼ 追加：現場タブ ドラッグ開始検出（PreviewMouseLeftButtonDown） ▼▼▼
        // WrapPanel 全体でマウスダウンを受け取り、クリックされた TabItem の VM を記録する
        // PreviewMouseLeftButtonDown はトンネリングで早期に届くため確実に捕捉できる
        private void tab_header_mouse_down(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _drag_start_pos = e.GetPosition(null);
            _drag_source_vm = null;

            // クリック位置から TabItem を探してDataContextを取得
            var element = e.OriginalSource as System.Windows.DependencyObject;
            var tab_item = find_visual_ancestor<System.Windows.Controls.TabItem>(element);
            if (tab_item?.DataContext is project_cost_view_model pvm)
                _drag_source_vm = pvm;
        }

        // ▼▼▼ 追加：現場タブ ドラッグ判定（MouseMove） ▼▼▼
        // DRAG_THRESHOLD を超えたらドラッグ開始。閾値未満は通常クリックとして処理される
        private void tab_header_mouse_move(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed
                || _drag_source_vm == null) return;

            var current = e.GetPosition(null);
            var diff = _drag_start_pos - current;

            if (Math.Abs(diff.X) > DRAG_THRESHOLD || Math.Abs(diff.Y) > DRAG_THRESHOLD)
            {
                // ドラッグ開始（DoDragDrop は完了まで処理をブロックする同期呼び出し）
                var source = _drag_source_vm;
                _drag_source_vm = null; // 再入り防止のため先にリセット
                DragDrop.DoDragDrop(
                    sender as System.Windows.DependencyObject ?? project_tab_control,
                    source,
                    DragDropEffects.Move);
            }
        }

        // ▼▼▼ 追加：現場タブ ドロップ処理（Drop） ▼▼▼
        // ドロップ先の TabItem を特定して同大区分内なら並び替えを実行
        private async void tab_header_drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(project_cost_view_model))) return;

            var source = e.Data.GetData(typeof(project_cost_view_model)) as project_cost_view_model;
            if (source == null) return;

            // ドロップ先の TabItem を取得
            var element = e.OriginalSource as System.Windows.DependencyObject;
            var tab_item = find_visual_ancestor<System.Windows.Controls.TabItem>(element);
            var target = tab_item?.DataContext as project_cost_view_model;

            if (target == null || target == source)
            {
                e.Handled = true;
                return;
            }

            // ▼▼▼ 修正：大区分を跨ぐ並び替えを許可（グループチェック削除）▼▼▼
            // 並び替え実行 & DB保存
            if (_vm != null)
                await _vm.reorder_tab_async(source, target);

            e.Handled = true;
        }

        // ▼▼▼ 追加：全月度折りたたみ/展開トグルボタンハンドラー ▼▼▼
        private void btn_toggle_all_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            var project_vm = btn.Tag as project_cost_view_model;
            if (project_vm == null) { e.Handled = true; return; }

            // ▼▼▼ 修正：全折り/全展開時もスクロール位置を復元 ▼▼▼
            var dg = find_visual_ancestor<System.Windows.Controls.DataGrid>(btn);
            var sv = dg != null ? find_visual_child<System.Windows.Controls.ScrollViewer>(dg) : null;
            double saved_offset = sv?.VerticalOffset ?? 0;

            project_vm.selected_display_tab?.toggle_all_months();

            if (dg != null)
            {
                dg.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new System.Action(() => sv?.ScrollToVerticalOffset(saved_offset)));
            }

            e.Handled = true;
        }

        // ▼▼▼ 追加：🔄 更新ボタンハンドラー（他ユーザーの変更を手動反映）▼▼▼
        // 現場の全データ（集計レコード＋絞り込みタブ）を再ロードする
        // 他のPCで日報取込・単価変更があった場合に手動で最新化するために使用
        private async void btn_refresh_costpage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            var project_vm = btn.Tag as project_cost_view_model;
            if (project_vm == null) return;

            // is_loading は load_records_async / load_filter_tabs_async 内で管理
            await project_vm.load_records_async();
            await project_vm.load_filter_tabs_async();

            e.Handled = true;
        }

        // ▼▼▼ 追加：全年度折りたたみ/展開トグルボタンハンドラー ▼▼▼
        // 全折りたたみ状態 → 全展開、それ以外 → 全折りたたみ
        private void btn_toggle_all_years_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            var project_vm = btn.Tag as project_cost_view_model;
            if (project_vm == null) { e.Handled = true; return; }

            // ▼▼▼ 修正：全折り/全展開時もスクロール位置を復元 ▼▼▼
            var dg = find_visual_ancestor<System.Windows.Controls.DataGrid>(btn);
            var sv = dg != null ? find_visual_child<System.Windows.Controls.ScrollViewer>(dg) : null;
            double saved_offset = sv?.VerticalOffset ?? 0;

            project_vm.selected_display_tab?.toggle_all_years();

            if (dg != null)
            {
                dg.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new System.Action(() => sv?.ScrollToVerticalOffset(saved_offset)));
            }

            e.Handled = true;
        }

        // ▼▼▼ 追加：タブヘッダー展開/折りたたみボタンハンドラー ▼▼▼
        // ControlTemplate 内の tab_expand_border の MouseLeftButtonUp から呼ばれる
        // MaxHeight を切り替えて3段制限 ↔ 全展開を切り替える
        private void tab_expand_border_click(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            _is_tabs_expanded = !_is_tabs_expanded;

            var scroll = project_tab_control.Template.FindName(
                "tab_header_scroll", project_tab_control) as ScrollViewer;
            var icon = project_tab_control.Template.FindName(
                "tab_expand_icon", project_tab_control) as System.Windows.Controls.TextBlock;

            if (scroll != null)
                scroll.MaxHeight = _is_tabs_expanded ? double.PositiveInfinity : 90;
            if (icon != null)
                icon.Text = _is_tabs_expanded ? "▲" : "▼";

            e.Handled = true;
        }

        // ▼▼▼ 追加：DataGrid RowStyle の EventSetter から呼ばれるハンドラー ▼▼▼
        // DataGrid の PreviewMouseDown（行選択処理）実行後に DataGridRow が受け取る
        // IsSelected=false を即座にセットして青背景を維持する
        private void header_row_PreviewMouseDown(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row &&
                row.Item is EA_CostManager.Models.cost_row_item item &&
                item.is_header)
            {
                row.IsSelected = false; // 選択直後に即解除して青背景を保持
            }
        }

        // ビジュアルツリーで要素型を直接検索するヘルパー（DataContextではなく要素型で検索）
        private static T? find_visual_ancestor<T>(DependencyObject element) where T : DependencyObject
        {
            var current = VisualTreeHelper.GetParent(element);
            while (current != null)
            {
                if (current is T found) return found;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ▼▼▼ 追加：ビジュアルツリーを下方向に再帰検索するヘルパー ▼▼▼
        // DataGrid 内の ScrollViewer を取得するために使用
        // ICollectionView.Refresh() 後のスクロール位置復元に必要
        private static T? find_visual_child<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                var result = find_visual_child<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        // ---- 絞り込みタブ追加ボタン ----
        private async void btn_add_filter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var project_vm = btn.DataContext as project_cost_view_model;
            if (project_vm == null)
                project_vm = btn.Tag as project_cost_view_model;
            if (project_vm == null) return;

            await open_add_filter_dialog_async(project_vm);
        }

        /// <summary>
        /// [Sprint 5G] MainWindowのCtrl+NショートカットからもFilterTabDialogを開けるように公開
        /// </summary>
        public async Task trigger_add_filter(project_cost_view_model project_vm)
            => await open_add_filter_dialog_async(project_vm);

        /// <summary>
        /// ★v0.9.7追加：MainWindowのF2ショートカットから現場編集ダイアログを開けるように公開
        /// </summary>
        public async void trigger_project_edit(project_cost_view_model project_vm)
        {
            var snap = await get_current_snapshot_async(project_vm.project_id);
            var dlg = new ProjectEditDialog(
                project_vm.project_id,
                project_vm.category_code,
                project_vm.site_name,
                project_vm.detail ?? "")
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && snap != null)
            {
                await fill_new_values_and_push_async(snap);
                if (_vm != null) await _vm.initialize_async();
            }
        }

        private async Task open_add_filter_dialog_async(project_cost_view_model project_vm)
        {
            var dlg = new FilterTabDialog(
                project_vm.project_id,
                project_vm.available_months,
                project_vm.selected_display_tab?.agg_mode ?? "daily")   // ▼追加：現在タブのモードを初期値として引き継ぐ
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() == true && dlg.created_tab != null)
            {
                int inherit_custom = (project_vm.selected_display_tab?.use_custom_rates == true) ? 1 : 0;
                if (inherit_custom == 1)
                {
                    dlg.created_tab.use_custom_rates = 1;
                    try
                    {
                        using var conn = EA_CostManager.Data.database_manager.create_connection();
                        await conn.ExecuteAsync(
                            "UPDATE cost_filter_tabs SET use_custom_rates = 1 WHERE id = @id",
                            new { id = dlg.created_tab.id });
                        await copy_rates_to_tab_async(project_vm.project_id, dlg.created_tab.id);
                    }
                    catch { }
                }

                // ▼▼▼ add_filter_tab_async が内部でデータロードを行うため is_busy 不要 ▼▼▼
                await project_vm.add_filter_tab_async(dlg.created_tab);
            }
        }

        // ▼▼▼ 修正①：現場タブ切替ハンドラー ▼▼▼
        // タブが動く問題の原因だった Dispatcher.BeginInvoke を削除
        // find_inner_tab_control / find_child_tab_control も不要になるため削除
        // 内側絞り込みタブのリセットはバインド（selected_display_tab）で制御する
        private async void project_tab_selection_changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TabControl tc) return;
            if (e.Source != sender) return;

            var project_vm = tc.SelectedItem as project_cost_view_model;
            if (project_vm == null) return;

            project_vm.selected_display_tab = project_vm.all_records_tab;

            // ▼▼▼ [現場タブ追跡] update → refresh の順で確実に実行 ▼▼▼
            var main_vm = Window.GetWindow(this)?.DataContext
                as EA_CostManager.ViewModels.main_view_model;
            if (main_vm != null)
            {
                await main_vm.update_current_tab_async(project_vm.tab_name);
                await main_vm.refresh_online_users_async();
            }

            e.Handled = true;
        }

        // ▼▼▼ 追加：絞り込みタブ選択変更ハンドラー（Sprint 3.7） ▼▼▼
        // TwoWayバインドはアプリ起動時・再ロード時に書き戻しが保証されないため
        // SelectionChanged イベントで selected_display_tab を確実に更新する
        private void filter_tab_selection_changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // 内側のTabControl（絞り込みタブ）のみ処理する
            // DataContext が project_cost_view_model でない場合（外側の現場タブ等）は無視
            if (sender is not System.Windows.Controls.TabControl tc) return;
            if (tc.DataContext is not project_cost_view_model project_vm) return;

            project_vm.selected_display_tab = tc.SelectedItem as filter_tab_view_model;

            // 外側の TabControl へのイベントバブリングを抑止する
            e.Handled = true;
        }

        // ▼▼▼ 追加：絞り込みタブ右クリックメニューのクリックハンドラー ▼▼▼
        // XAML の ContextMenu MenuItem の Click="filter_tab_menu_Click" から呼ばれる
        private async void filter_tab_menu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menu_item) return;
            string tag = menu_item.Tag?.ToString() ?? "";

            // DataContext は filter_tab_view_model
            // ContextMenu の PlacementTarget → TextBlock → DataContext
            var ctx_menu = menu_item.Parent as ContextMenu;
            var text_block = ctx_menu?.PlacementTarget as FrameworkElement;
            var ft_vm = text_block?.DataContext as filter_tab_view_model;

            // ▼▼▼ 修正：show_archived は全件タブからも呼べるため
            //           ガードより前に処理して早期リターン ▼▼▼
            if (tag == "show_archived")
            {
                var pvm = find_ancestor<project_cost_view_model>(text_block);
                if (pvm != null)
                    await show_archived_tabs_async(pvm);
                return;
            }

            // 全件タブ（filter_tab_id=0）は操作しない
            if (ft_vm == null || ft_vm.filter_tab_id == 0) return;

            // 親の project_cost_view_model を取得
            var project_vm = find_ancestor<project_cost_view_model>(text_block);
            if (project_vm == null) return;

            // ▼ 追加 [Sprint 8 / Phase 0]：以下の switch は全て書込（改名・削除・アーカイブ等）。
            //   閲覧のみの show_archived は上で早期returnしているためガード対象外。
            if (ReadOnlyGuard.block_if_read_only()) return;

            switch (tag)
            {
                case "rename":
                    // ▼▼▼ 追加：タブ名変更（Sprint 3.7） ▼▼▼
                    // 現在のタブ名を初期値としてダイアログを表示し、DB更新後にリロードする
                    var rename_dlg = new TabNameInputDialog(ft_vm.tab_name)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    if (rename_dlg.ShowDialog() != true) break;

                    using (var conn_r = database_manager.create_connection())
                    {
                        await conn_r.ExecuteAsync(
                            "UPDATE cost_filter_tabs SET tab_name = @name WHERE id = @id",
                            new { name = rename_dlg.input_name, id = ft_vm.filter_tab_id });
                    }
                    // フィルタータブ一覧をリロードして変更を反映
                    await project_vm.load_filter_tabs_async();
                    break;

                case "archive":
                    // アーカイブ：DBでis_archived=1に更新して画面から非表示
                    var arc_result = MessageBox.Show(
                        $"「{ft_vm.tab_name}」をアーカイブします。\n後から「アーカイブ済みタブを表示」で復元できます。",
                        "アーカイブ確認",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question);
                    if (arc_result == MessageBoxResult.OK)
                        project_vm.archive_filter_tab_command.Execute(ft_vm);
                    break;

                case "delete":
                    // 削除：完全に消去（元に戻せない）
                    var del_result = MessageBox.Show(
                        $"「{ft_vm.tab_name}」を完全に削除します。\nこの操作は元に戻せません。",
                        "削除確認",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning);
                    if (del_result == MessageBoxResult.OK)
                        project_vm.delete_filter_tab_command.Execute(ft_vm);
                    break;
            }
        }

        // ▼▼▼ 追加：アーカイブ済みタブ表示・復元処理 ▼▼▼
        // ArchivedTabsDialog でチェックボックス選択 → 選択したタブのみ復元
        private async Task show_archived_tabs_async(project_cost_view_model project_vm)
        {
            var archived = await project_vm.get_archived_tabs_async();

            if (archived.Count == 0)
            {
                MessageBox.Show(
                    "アーカイブ済みのタブはありません。",
                    "アーカイブ済みタブ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // チェックボックス選択ダイアログを表示
            var dlg = new ArchivedTabsDialog(archived)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true || dlg.selected_tabs.Count == 0) return;

            // 選択されたタブのみ復元
            foreach (var tab in dlg.selected_tabs)
                await project_vm.restore_filter_tab_async(tab);

            MessageBox.Show(
                $"{dlg.selected_tabs.Count}件のタブを復元しました。",
                "復元完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ▼▼▼ 追加：現場タブ右クリックメニューのクリックハンドラー ▼▼▼
        private async void project_tab_menu_Click(object sender, RoutedEventArgs e)
        {
            // ▼ 追加 [Sprint 8 / Phase 0]：このメニューは全分岐が書込（属性変更・アーカイブ・一括）
            if (ReadOnlyGuard.block_if_read_only()) return;
            if (sender is not MenuItem menu_item) return;
            string tag = menu_item.Tag?.ToString() ?? "";

            // ▼▼▼ ★v0.9.7追加：複数選択時の一括操作を最初に処理 ▼▼▼
            // bulk_change_attribute / bulk_archive は project_vm 不要のため早期に分岐
            if (tag == "bulk_change_attribute")
            {
                await bulk_change_attribute_async();
                return;
            }
            if (tag == "bulk_archive")
            {
                await bulk_archive_async();
                return;
            }

            // ContextMenu → StackPanel → DataContext = project_cost_view_model
            var ctx_menu = menu_item.Parent as MenuItem; // サブメニューの場合
            // TagがattrXXXの場合は属性変更サブメニュー項目
            // それ以外はトップレベルメニュー項目
            var placement = (menu_item.Parent is ContextMenu cm)
                ? cm.PlacementTarget as FrameworkElement
                : ((menu_item.Parent as MenuItem)?.Parent is ContextMenu cm2)
                    ? cm2.PlacementTarget as FrameworkElement
                    : null;

            var project_vm = placement?.DataContext as project_cost_view_model;
            if (project_vm == null)
            {
                // VisualTree経由でも探す
                project_vm = find_ancestor<project_cost_view_model>(placement);
            }
            if (project_vm == null) return;

            // 属性変更（Tag = "attr_契約中" 等）
            if (tag.StartsWith("attr_"))
            {
                string new_attr = tag[5..];
                string new_color = project_cost_view_model.get_tab_color(new_attr);

                var confirm = MessageBox.Show(
                    $"「{project_vm.tab_name}」の属性を「{new_attr}」に変更します。",
                    "属性変更確認",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.OK) return;

                // ▼▼▼ [Sprint 5G] 変更前のスナップショットを取得 ▼▼▼
                var snap_attr = await get_current_snapshot_async(project_vm.project_id);

                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(@"
                    UPDATE projects SET attribute = @attr, tab_color = @color
                    WHERE id = @id",
                    new { attr = new_attr, color = new_color, id = project_vm.project_id });

                // ▼▼▼ [Sprint 5G] 変更後の値をスナップショットに記録してUndoスタックへ ▼▼▼
                if (snap_attr != null)
                    await fill_new_values_and_push_async(snap_attr);

                // ▼▼▼ initialize_async が内部で is_busy を管理してローディングを表示する ▼▼▼
                if (_vm != null) await _vm.initialize_async();

                // ★v0.9.7追加：属性変更後にCtrl+Zが効くよう、検索ボックスからフォーカスを外す
                unfocus_search_box();
                return;
            }

            switch (tag)
            {
                case "edit_project":
                    // ▼▼▼ [Sprint 5G] 変更前のスナップショットを取得 ▼▼▼
                    var snap_edit = await get_current_snapshot_async(project_vm.project_id);

                    var edit_dlg = new ProjectEditDialog(
                        project_vm.project_id,
                        project_vm.category_code,
                        project_vm.site_name,
                        project_vm.detail ?? "")
                    {
                        Owner = Window.GetWindow(this)
                    };
                    // ▼▼▼ [Sprint 5G] 保存後に変更後の値を記録してUndoスタックへ ▼▼▼
                    if (edit_dlg.ShowDialog() == true && _vm != null)
                    {
                        if (snap_edit != null)
                            await fill_new_values_and_push_async(snap_edit);
                        await _vm.initialize_async();
                        // ★v0.9.7追加：現場名/詳細編集後にCtrl+Zが効くよう、検索ボックスからフォーカスを外す
                        unfocus_search_box();
                    }
                    break;

                case "archive_project":
                    var arc = MessageBox.Show(
                        $"「{project_vm.tab_name}」をアーカイブします。\n設定画面から復元できます。",
                        "アーカイブ確認",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question);
                    if (arc != MessageBoxResult.OK) return;

                    using (var conn2 = database_manager.create_connection())
                    {
                        await conn2.ExecuteAsync(
                            "UPDATE projects SET is_active = 0 WHERE id = @id",
                            new { id = project_vm.project_id });

                        // ▼▼▼ 追加：操作ログ書き込み ▼▼▼
                        await conn2.ExecuteAsync(@"
                            INSERT INTO operation_logs
                                (log_datetime, pc_user_id, operator_name, operation_type, target_table, target_id, detail)
                            VALUES
                                (datetime('now','localtime'), @uid, @name, '現場アーカイブ', 'projects', @id, @detail)",
                            new
                            {
                                uid = EA_CostManager.UserSession.user_id,
                                name = EA_CostManager.UserSession.user_name,
                                id = project_vm.project_id,
                                detail = $"{project_vm.tab_name} をアーカイブ"
                            });
                    }
                    if (_vm != null) await _vm.initialize_async();
                    break;
            }
        }

        // ▼▼▼ 修正：検索バーの「クリア」ボタンハンドラー（Sprint 4）▼▼▼
        // 現場情報バー（project_cost_view_model DataContext）とタブ内（filter_tab_view_model DataContext）の
        // 両方の位置から呼ばれる。DataContextに応じて適切なVMを取得する。
        private void btn_clear_search_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;

            // ① まず filter_tab_view_model を直接探す（タブ内のボタンの場合）
            var ft_vm = find_ancestor<EA_CostManager.ViewModels.filter_tab_view_model>(btn);
            if (ft_vm != null) { ft_vm.clear_all_filters(); return; }

            // ② 見つからない場合は project_cost_view_model → selected_display_tab で取得
            //    （現場情報バーの✕ボタンの場合）
            var p_vm = find_ancestor<EA_CostManager.ViewModels.project_cost_view_model>(btn);
            p_vm?.selected_display_tab?.clear_all_filters();
        }

        // ▼▼▼ 追加：現場タブ検索ハンドラー ▼▼▼

        private void tab_search_box_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not System.Windows.Controls.ComboBox cb) return;
            if (_vm == null) return;

            // IMEが処理したキー（日本語変換中のEnter等）は完全無視
            if (e.Key == System.Windows.Input.Key.ImeProcessed) return;

            // Escapeで検索キャンセル
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                _vm.tab_search_text = "";
                cb.IsDropDownOpen = false;
                return;
            }

            // 候補があればドロップダウンを開く（テキスト更新はバインドが自動でやる）
            if (_vm.search_suggestions.Count > 0)
                cb.IsDropDownOpen = true;
            else
                cb.IsDropDownOpen = false;
        }

        // ドロップダウンが開いたとき候補がなければすぐ閉じる
        private void tab_search_box_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is not System.Windows.Controls.ComboBox cb) return;
            if (_vm?.search_suggestions.Count == 0)
                cb.IsDropDownOpen = false;
        }

        // ▼▼▼ ドロップダウンから候補をクリック選択したときのハンドラー ▼▼▼
        // SelectedItemバインドをやめてこちらで処理する
        // →バインドだと選択時にTextBoxが青くなって次のキー入力でテキストが消える問題があった
        private void tab_search_box_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ComboBox cb) return;
            if (_vm == null) return;

            // ドロップダウンからの選択（クリック）のみ処理
            if (!cb.IsDropDownOpen) return;
            if (cb.SelectedItem is not EA_CostManager.ViewModels.project_cost_view_model target) return;

            // 選択解除してからジャンプ（TextBoxが青くならないようにSelectedItemをnullに）
            cb.SelectedItem = null;
            cb.IsDropDownOpen = false;

            // テキストをクリアしてジャンプ
            _vm.tab_search_text = "";
            _vm.selected_search_item = target;

            e.Handled = true;
        }

        private static T? find_ancestor<T>(DependencyObject element) where T : class
        {
            var current = element;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.DataContext is T found)
                    return found;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ▼▼▼ 追加：現在タブ再集計ボタンのハンドラー ▼▼▼
        // 現在の現場の全期間データを再集計してタブを更新する
        private async void btn_reaggregate_current_Click(object sender, RoutedEventArgs e)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (sender is not System.Windows.Controls.Button btn) return;
            var project_vm = btn.Tag as project_cost_view_model;
            if (project_vm == null) return;

            var service = new EA_CostManager.Services.CostAggregationService();
            const string ALL_START = "2000-01-01";
            const string ALL_END = "2099-12-31";

            project_vm.is_loading = true;
            try
            {
                var (cnt, err) = await service.aggregate_async(
                    project_vm.category_code, ALL_START, ALL_END);

                if (!string.IsNullOrEmpty(err))
                {
                    MessageBox.Show($"再集計エラー：{err}", "エラー",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                await project_vm.load_records_async();
                await project_vm.load_filter_tabs_async();
            }
            finally
            {
                project_vm.is_loading = false;
            }
        }

        // ▼▼▼ 追加：人員単価変更ボタンのハンドラー ▼▼▼
        private async void btn_change_rates_Click(object sender, RoutedEventArgs e)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (sender is not System.Windows.Controls.Button btn) return;
            var project_vm = btn.Tag as project_cost_view_model;
            if (project_vm == null) return;

            bool load_existing = project_vm.selected_display_tab?.use_custom_rates == true;

            var dlg = new ProjectRatesDialog(project_vm.project_id, project_vm.tab_name, load_existing)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true) return;

            // ▼▼▼ ここは initialize_async を呼ばないので is_busy を直接管理できる ▼▼▼
            if (_vm != null) { _vm.is_busy = true; _vm.status_message = "単価を適用中..."; }
            try
            {
                await project_vm.check_custom_rates_async();

                // ---- 4択アクション処理 ----
                var service = new EA_CostManager.Services.CostAggregationService();
                const string ALL_START = "2000-01-01";
                const string ALL_END = "2099-12-31";

                // ▼▼▼ 追加：単価変更の操作ログを記録 ▼▼▼
                try
                {
                    using var conn_log = EA_CostManager.Data.database_manager.create_connection();
                    await conn_log.ExecuteAsync(@"
                        INSERT INTO operation_logs
                            (log_datetime, pc_user_id, operator_name, operation_type, target_table, target_id, detail)
                        VALUES
                            (datetime('now','localtime'), @uid, @name, '単価変更', 'project_rates', @id, @detail)",
                        new
                        {
                            uid = EA_CostManager.UserSession.user_id,
                            name = EA_CostManager.UserSession.user_name,
                            id = project_vm.project_id,
                            detail = $"{project_vm.tab_name} の単価を変更"
                        });
                }
                catch { /* ログ書き込み失敗は無視して処理継続 */ }

                switch (dlg.selected_action)
                {
                    case RatesDialogAction.ReAggregate:
                        // すべてのタブにproject_ratesを適用して再集計
                        await service.aggregate_async(
                            project_vm.category_code, ALL_START, ALL_END,
                            project_id: project_vm.project_id);
                        using (var conn_ra = EA_CostManager.Data.database_manager.create_connection())
                        {
                            await conn_ra.ExecuteAsync(
                                "UPDATE cost_filter_tabs SET use_custom_rates = 0 WHERE project_id = @id",
                                new { id = project_vm.project_id });
                        }
                        // ▼▼▼ 追加：全タブ反映済みフラグをtrueにして全件タブにもバナーを表示 ▼▼▼
                        project_vm.set_rates_applied_to_all(true);
                        await project_vm.load_records_async();
                        await project_vm.load_filter_tabs_async();
                        break;

                    case RatesDialogAction.ReAggregateCurrentTab:
                        var saved_tab_id = project_vm.selected_display_tab?.filter_tab_id ?? 0;

                        if (project_vm.selected_display_tab?.use_custom_rates == true)
                        {
                            // use_custom_rates=1 タブ → filter_tab_ratesを最新のproject_ratesで更新
                            if (saved_tab_id != 0)
                                await copy_rates_to_tab_async(project_vm.project_id, saved_tab_id);
                            await project_vm.load_filter_tabs_async(preserve_tab_id: saved_tab_id);
                        }
                        else
                        {
                            // 全件タブ or 通常絞り込みタブ → cost_recordsにproject_ratesを反映して再集計
                            await service.aggregate_async(
                                project_vm.category_code, ALL_START, ALL_END,
                                project_id: project_vm.project_id);
                            // ▼▼▼ 追加：全件タブに反映した場合はバナーを表示 ▼▼▼
                            project_vm.set_rates_applied_to_all(true);
                            await project_vm.load_records_async();
                            await project_vm.load_filter_tabs_async(preserve_tab_id: saved_tab_id);
                        }
                        break;

                    case RatesDialogAction.CreateNewTab:
                        // ▼▼▼ 追加：タブ名入力ダイアログ（Sprint 3.7）▼▼▼
                        // 「単価変更版」をデフォルト値として表示し、ユーザーが任意の名前に変更できる
                        var name_dlg = new TabNameInputDialog("単価変更版")
                        {
                            Owner = Window.GetWindow(this)
                        };
                        // キャンセル時はタブ作成しない
                        if (name_dlg.ShowDialog() != true) break;

                        // 「単価変更版」絞り込みタブを新規作成してproject_ratesで集計
                        var new_tab_model = new EA_CostManager.Models.cost_filter_tab
                        {
                            project_id = project_vm.project_id,
                            tab_name = name_dlg.input_name,  // ← ダイアログで入力した名前を使用
                            filter_month = "",
                            filter_content = "",
                            filter_match = "partial",
                            filter_names = "",
                            is_single_mode = 0,
                            use_custom_rates = 1,  // ← 現場別単価を使用するフラグ
                        };
                        using (var conn = EA_CostManager.Data.database_manager.create_connection())
                        {
                            // ▼▼▼ 修正：INSERT前にuse_custom_rates列の存在を保証（Sprint 3.7） ▼▼▼
                            try
                            {
                                await conn.ExecuteAsync(
                                    "ALTER TABLE cost_filter_tabs ADD COLUMN use_custom_rates INTEGER DEFAULT 0");
                            }
                            catch { /* 列が既に存在する場合は無視 */ }

                            var new_id = await conn.QuerySingleAsync<int>(@"
                            INSERT INTO cost_filter_tabs
                                (project_id, tab_name, filter_month, filter_content,
                                 filter_match, filter_names, is_single_mode, use_custom_rates)
                            VALUES
                                (@project_id, @tab_name, @filter_month, @filter_content,
                                 @filter_match, @filter_names, @is_single_mode, @use_custom_rates);
                            SELECT last_insert_rowid();", new_tab_model);
                            new_tab_model.id = new_id;

                            // ▼▼▼ 追加：project_ratesをfilter_tab_ratesにスナップショットとして保存 ▼▼▼
                            await copy_rates_to_tab_async(project_vm.project_id, new_id);
                        }
                        await project_vm.add_filter_tab_async(new_tab_model);
                        break;

                    case RatesDialogAction.ResetAll:
                        // project_rates を削除して全タブをデフォルト単価で再集計
                        using (var conn2 = EA_CostManager.Data.database_manager.create_connection())
                        {
                            // ▼▼▼ 修正：project_rates だけでなく filter_tab_rates も削除 ▼▼▼
                            await conn2.ExecuteAsync(
                                "DELETE FROM project_rates WHERE project_id = @id",
                                new { id = project_vm.project_id });

                            // このプロジェクトの全絞り込みタブの filter_tab_rates を削除
                            await conn2.ExecuteAsync(@"
                            DELETE FROM filter_tab_rates
                            WHERE filter_tab_id IN (
                                SELECT id FROM cost_filter_tabs WHERE project_id = @id
                            )",
                                new { id = project_vm.project_id });

                            // 全絞り込みタブの use_custom_rates を 0 にリセット
                            await conn2.ExecuteAsync(
                                "UPDATE cost_filter_tabs SET use_custom_rates = 0 WHERE project_id = @id",
                                new { id = project_vm.project_id });
                        }
                        await project_vm.check_custom_rates_async();
                        await service.aggregate_async(
                            project_vm.category_code, ALL_START, ALL_END);
                        project_vm.set_rates_applied_to_all(false);
                        await project_vm.load_records_async();
                        await project_vm.load_filter_tabs_async();
                        break;

                    case RatesDialogAction.ResetCurrentTab:
                        // 現在のタブのみデフォルト単価で再集計（project_ratesは保持）
                        if (project_vm.selected_display_tab?.use_custom_rates == true
                            && project_vm.selected_display_tab.filter_tab_id != 0)
                        {
                            using var conn_r = EA_CostManager.Data.database_manager.create_connection();
                            await conn_r.ExecuteAsync(
                                "UPDATE cost_filter_tabs SET use_custom_rates = 0 WHERE id = @id",
                                new { id = project_vm.selected_display_tab.filter_tab_id });
                        }
                        await service.aggregate_async(
                            project_vm.category_code, ALL_START, ALL_END);
                        await project_vm.load_records_async();
                        await project_vm.load_filter_tabs_async();
                        break;

                        // RatesDialogAction.None（後で反映）は何もしない
                }
            }
            finally
            {
                if (_vm != null) _vm.is_busy = false;
            }
        }

        // ▼▼▼ 追加：project_rates → filter_tab_rates へスナップショットをコピー ▼▼▼
        // use_custom_rates=1 のタブ作成時・「このタブに反映」時に呼ぶ
        private static async Task copy_rates_to_tab_async(
            int project_id, int filter_tab_id)
        {
            using var conn = EA_CostManager.Data.database_manager.create_connection();
            using var tx = conn.BeginTransaction();
            try
            {
                // 既存のスナップショットを削除して最新の project_rates で上書き
                await conn.ExecuteAsync(
                    "DELETE FROM filter_tab_rates WHERE filter_tab_id = @tid",
                    new { tid = filter_tab_id }, tx);

                await conn.ExecuteAsync(@"
                    INSERT INTO filter_tab_rates (filter_tab_id, rate_type, employee_name, daily_rate)
                    SELECT @tid, rate_type, employee_name, daily_rate
                    FROM project_rates
                    WHERE project_id = @pid",
                    new { tid = filter_tab_id, pid = project_id }, tx);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        // ▼▼▼ 追加：絞り込み条件表示ボタン（ⓘ）のハンドラー ▼▼▼
        // use_custom_rates=1 のタブ → 適用中の単価一覧を表示
        // 通常の絞り込みタブ    → フィルター条件＋マッチ件数を表示
        private async void btn_filter_info_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            var ft = btn.Tag as filter_tab_view_model;
            if (ft == null || ft.filter_tab_id == 0) return;
            e.Handled = true;

            // ---- 単価変更版タブ：適用中の単価一覧を表示 ----
            if (ft.use_custom_rates)
            {
                var lines = new System.Text.StringBuilder();
                lines.AppendLine($"【{ft.tab_name}　の適用単価】");
                lines.AppendLine();

                try
                {
                    using var conn = EA_CostManager.Data.database_manager.create_connection();
                    var rates = (await conn.QueryAsync<dynamic>(
                        "SELECT rate_type, employee_name, daily_rate FROM project_rates WHERE project_id = @pid ORDER BY rate_type, employee_name",
                        new { pid = ft.project_id })).ToList();

                    if (rates.Count == 0)
                    {
                        lines.AppendLine("（単価設定が見つかりません）");
                    }
                    else
                    {
                        // 技師・助手（職種単価）
                        foreach (var r in rates.Where(r => (string)r.rate_type != "個人"))
                            lines.AppendLine($"{(string)r.rate_type}単価：{(decimal)r.daily_rate:#,##0} 円/日");

                        // 個人別単価
                        var individual = rates.Where(r => (string)r.rate_type == "個人").ToList();
                        if (individual.Count > 0)
                        {
                            lines.AppendLine();
                            lines.AppendLine("【個人別単価】");
                            foreach (var r in individual)
                                lines.AppendLine($"  {(string)(r.employee_name ?? "（不明）")}：{(decimal)r.daily_rate:#,##0} 円/日");
                        }
                    }
                }
                catch (Exception ex)
                {
                    lines.AppendLine($"単価情報の取得に失敗しました：{ex.Message}");
                }

                MessageBox.Show(lines.ToString().TrimEnd(), "適用単価", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // ---- 通常の絞り込みタブ：フィルター条件＋マッチ件数を表示 ----
            {
                var lines = new System.Text.StringBuilder();
                lines.AppendLine($"【{ft.tab_name}　の絞り込み条件】");
                lines.AppendLine();

                bool has_condition = false;

                if (!string.IsNullOrWhiteSpace(ft.filter_month))
                {
                    lines.AppendLine($"対象月度：{ft.filter_month}");
                    has_condition = true;
                }

                if (!string.IsNullOrWhiteSpace(ft.filter_date_from)
                    && !string.IsNullOrWhiteSpace(ft.filter_date_to))
                {
                    lines.AppendLine($"期間指定：{ft.filter_date_from} ～ {ft.filter_date_to}");
                    has_condition = true;
                }

                if (!string.IsNullOrWhiteSpace(ft.filter_content))
                {
                    string match_label = ft.filter_match == "exact" ? "完全一致" : "部分一致";
                    lines.AppendLine($"作業内容：{ft.filter_content}　（{match_label}）");
                    has_condition = true;
                }

                if (!string.IsNullOrWhiteSpace(ft.filter_names))
                {
                    lines.AppendLine($"氏名フィルター：{ft.filter_names}");
                    has_condition = true;
                }

                if (ft.is_single_mode)
                {
                    lines.AppendLine("個人別集計モード：ON");
                    has_condition = true;
                }

                if (!has_condition)
                    lines.AppendLine("（絞り込み条件なし・全件表示）");

                MessageBox.Show(lines.ToString().TrimEnd(), "絞り込み条件", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ▼▼▼ (X/2段)：集計モード「変更 ▾」ボタン → メニューを開く際に対象VMを確保 ▼▼▼
        //   2段メニューの末端MenuItemは ContextMenu を直接の親に持たないため、
        //   ボタン押下時に現場VMを退避しておき、メニュー実行時はそれを使う（ツリー探索不要・確実）。
        private project_cost_view_model? _mode_target_vm;
        private void btn_apply_mode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.ContextMenu != null)
            {
                _mode_target_vm = b.DataContext as project_cost_view_model;
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.IsOpen = true;
            }
        }

        // ▼▼▼ (X)：集計モード変更メニュー（Tagで「このタブ/複製」×「日単位/業務単位」を判別） ▼▼▼
        //   コンボ(pending_agg_mode)は廃止。メニューで選んだモードをそのまま実行する。
        private async void mi_mode_action_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem mi) return;
            if (_mode_target_vm is not project_cost_view_model vm) return;   // ボタン押下時に確保した現場VM

            string tag = mi.Tag?.ToString() ?? "";          // this_daily / dup_daily / this_task / dup_task
            bool is_dup = tag.StartsWith("dup");
            string mode = tag.EndsWith("task") ? "task" : "daily";

            if (is_dup)
                await vm.duplicate_with_mode_async(vm.selected_display_tab, mode);
            else
                await vm.apply_mode_to_current_async(vm.selected_display_tab, mode);
        }

        // ▼▼▼ 追加(B)：再集計ボタン（この現場の全タブを集計し直す） ▼▼▼
        private async void btn_reaggregate_Click(object sender, RoutedEventArgs e)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (sender is not System.Windows.Controls.Button btn) return;
            if (btn.Tag is not project_cost_view_model project_vm) return;
            await project_vm.reaggregate_all_async();
        }

        // ▼▼▼ 追加：Excel書き出しボタンのハンドラー ▼▼▼
        private void btn_excel_export_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            var project_vm = btn.Tag as project_cost_view_model;
            if (project_vm == null) return;

            var dlg = new ExcelExportDialog(project_vm)
            {
                Owner = Window.GetWindow(this)
            };
            dlg.ShowDialog();
        }

        // ▼▼▼ 追加：印刷ボタンのハンドラー ▼▼▼
        private void btn_print_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            var project_vm = btn.Tag as project_cost_view_model;
            if (project_vm == null) return;

            var dlg = new PrintOptionsDialog(project_vm)
            {
                Owner = Window.GetWindow(this)
            };
            dlg.ShowDialog();
        }

        // ▼▼▼ [Sprint 5G] キーボードショートカット処理 ▼▼▼
        private async void on_key_down(object sender, KeyEventArgs e)
        {
            bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

            // Ctrl+Z：Undo
            if (ctrl && e.Key == Key.Z)
            {
                e.Handled = true;
                var project_id = await EditHistoryService.undo_async();
                if (project_id >= 0 && _vm != null)
                    await _vm.initialize_async();
                else if (project_id < 0 && EditHistoryService.can_undo == false)
                    System.Windows.MessageBox.Show("これ以上元に戻せません。",
                        "Undo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Ctrl+Y：Redo
            if (ctrl && e.Key == Key.Y)
            {
                e.Handled = true;
                var project_id = await EditHistoryService.redo_async();
                if (project_id >= 0 && _vm != null)
                    await _vm.initialize_async();
                else if (project_id < 0 && EditHistoryService.can_redo == false)
                    System.Windows.MessageBox.Show("これ以上やり直せません。",
                        "Redo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Ctrl+R：🔄更新
            if (ctrl && e.Key == Key.R)
            {
                e.Handled = true;
                if (_vm?.selected_tab != null)
                {
                    await _vm.selected_tab.load_records_async();
                    await _vm.selected_tab.load_filter_tabs_async();
                }
                return;
            }

            // Ctrl+F：タブ検索フォーカス
            if (ctrl && e.Key == Key.F)
            {
                e.Handled = true;
                tab_search_box.Focus();
                return;
            }

            // Ctrl+P：印刷ダイアログ
            if (ctrl && e.Key == Key.P)
            {
                e.Handled = true;
                if (_vm?.selected_tab != null)
                {
                    var dlg = new PrintOptionsDialog(_vm.selected_tab)
                    { Owner = Window.GetWindow(this) };
                    dlg.ShowDialog();
                }
                return;
            }

            // Ctrl+E：Excel出力ダイアログ
            if (ctrl && e.Key == Key.E)
            {
                e.Handled = true;
                if (_vm?.selected_tab != null)
                {
                    var dlg = new ExcelExportDialog(_vm.selected_tab)
                    { Owner = Window.GetWindow(this) };
                    dlg.ShowDialog();
                }
                return;
            }

            // F2：現場編集ダイアログ
            if (e.Key == Key.F2 && !ctrl)
            {
                e.Handled = true;
                var pvm = _vm?.selected_tab;
                if (pvm != null)
                {
                    var snap = await get_current_snapshot_async(pvm.project_id);
                    var dlg = new ProjectEditDialog(
                        pvm.project_id,
                        pvm.category_code,
                        pvm.site_name,
                        pvm.detail ?? "")
                    { Owner = Window.GetWindow(this) };
                    if (dlg.ShowDialog() == true && snap != null)
                    {
                        await fill_new_values_and_push_async(snap);
                        if (_vm != null) await _vm.initialize_async();
                    }
                }
                return;
            }

            // Ctrl+N：絞り込みタブ追加
            if (ctrl && e.Key == Key.N)
            {
                e.Handled = true;
                if (_vm?.selected_tab != null)
                {
                    var fake_btn = new System.Windows.Controls.Button
                    { Tag = _vm.selected_tab };
                    var args = new RoutedEventArgs(
                        System.Windows.Controls.Button.ClickEvent, fake_btn);
                    btn_add_filter_Click(fake_btn, args);
                }
                return;
            }

            // ▼▼▼ [Sprint 5G] 追加ショートカット ▼▼▼

            // Ctrl+U：人員単価変更
            if (ctrl && e.Key == Key.U)
            {
                e.Handled = true;
                if (_vm?.selected_tab != null)
                {
                    var fake_btn = new System.Windows.Controls.Button
                    { Tag = _vm.selected_tab };
                    var args = new RoutedEventArgs(
                        System.Windows.Controls.Button.ClickEvent, fake_btn);
                    btn_change_rates_Click(fake_btn, args);
                }
                return;
            }

            // Ctrl+M：月度を折りたたむ/展開（トグル）
            if (ctrl && e.Key == Key.M
                && !Keyboard.IsKeyDown(Key.LeftShift)
                && !Keyboard.IsKeyDown(Key.RightShift))
            {
                e.Handled = true;
                _vm?.selected_tab?.selected_display_tab?.toggle_all_months();
                return;
            }

            // Ctrl+Shift+M：年度を折りたたむ/展開（トグル）
            if (ctrl && e.Key == Key.M
                && (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
            {
                e.Handled = true;
                _vm?.selected_tab?.selected_display_tab?.toggle_all_years();
                return;
            }
        }

        // ---- Undo/Redo ヘルパー ----

        /// <summary>指定project_idの現在の値をDBからスナップショットとして取得する</summary>
        private static async Task<project_edit_snapshot?> get_current_snapshot_async(int project_id)
        {
            try
            {
                using var conn = database_manager.create_connection();
                var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT category_code, site_name, detail, attribute, tab_color FROM projects WHERE id = @id",
                    new { id = project_id });
                if (row == null) return null;
                return new project_edit_snapshot
                {
                    project_id = project_id,
                    old_category_code = (string)(row.category_code ?? ""),
                    old_site_name = (string)(row.site_name ?? ""),
                    old_detail = (string)(row.detail ?? ""),
                    old_attribute = (string)(row.attribute ?? ""),
                    old_tab_color = (string)(row.tab_color ?? ""),
                };
            }
            catch { return null; }
        }

        /// <summary>スナップショットの new_* をDBから取得してUndoスタックに積む</summary>
        private static async Task fill_new_values_and_push_async(project_edit_snapshot snap)
        {
            try
            {
                using var conn = database_manager.create_connection();
                var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT category_code, site_name, detail, attribute, tab_color FROM projects WHERE id = @id",
                    new { id = snap.project_id });
                if (row == null) return;
                snap.new_category_code = (string)(row.category_code ?? "");
                snap.new_site_name = (string)(row.site_name ?? "");
                snap.new_detail = (string)(row.detail ?? "");
                snap.new_attribute = (string)(row.attribute ?? "");
                snap.new_tab_color = (string)(row.tab_color ?? "");

                // 変更がなければ記録しない
                if (snap.old_category_code == snap.new_category_code
                    && snap.old_site_name == snap.new_site_name
                    && snap.old_detail == snap.new_detail
                    && snap.old_attribute == snap.new_attribute)
                    return;

                EditHistoryService.push(snap);
            }
            catch { }
        }

        // ---- 新規区分コード検出・登録ダイアログ ----
        private async System.Threading.Tasks.Task check_new_category_codes_async()
        {
            try
            {
                using var conn = database_manager.create_connection();

                var new_codes = (await conn.QueryAsync<string>(@"
                    SELECT DISTINCT dr.category_code
                    FROM daily_reports dr
                    WHERE dr.category_code IS NOT NULL
                      AND dr.category_code != ''
                      AND dr.category_code NOT IN ('有給', '有休')
                      AND NOT EXISTS (
                          SELECT 1 FROM projects p
                          WHERE p.category_code = dr.category_code
                      )
                    ORDER BY dr.category_code")).ToList();

                if (new_codes.Count == 0) return;

                bool any_registered = false;
                foreach (var code in new_codes)
                {
                    var dlg = new ProjectInfoDialog(code)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    dlg.ShowDialog();
                    if (dlg.was_registered)
                        any_registered = true;
                }

                if (any_registered && _vm != null)
                    await _vm.initialize_async();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"新規区分コード検出エラー: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // ★v0.9.7追加：現場タブの複数選択ロジック
        // 操作方法：
        //   ・Ctrl+クリック → 1件ずつ追加/解除（選択中のタブも自動で複数選択リストに含める）
        //   ・Shift+クリック → 範囲選択（選択中タブとクリック位置の間を全て選択）
        //   ・通常クリック → 複数選択をクリアして単一選択に戻る
        // 仕様：
        //   1) Ctrl+クリックで現在選択中のタブも自動で _multi_selected_tabs に含める（iPhone風）
        //   2) 通常クリックで _multi_selected_tabs をクリアして通常選択に戻る
        //   3) 共通の属性を選んで適用するBulkAttributeDialogを使用
        //   4) 1件あたりの確認ダイアログは出さず、件数を含めた1回の確認のみ
        //   5) F2/Ctrl+N等のショートカットは複数選択中でも先頭タブだけが対象
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// TabItem のマウスダウンイベント
        /// Ctrl+クリック / Shift+クリックを判定して複数選択を構築する
        /// 通常クリックの場合は複数選択をクリアしてWPFのデフォルト動作（タブ切替）に任せる
        /// </summary>
        private void tab_item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // ▼ クリックされたTabItemとそのDataContext（タブのVM）を取得
            if (sender is not TabItem tab_item) return;
            if (tab_item.DataContext is not project_cost_view_model clicked_vm) return;
            if (_vm == null) return;

            // ▼ 修飾キー判定：Ctrl押下中 / Shift押下中
            bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            // ▼ 通常クリック（修飾キーなし）：複数選択をクリアしてデフォルト動作（タブ切替）に任せる
            if (!ctrl && !shift)
            {
                clear_multi_selection();
                return; // e.Handled=falseのままなのでWPFが通常のタブ切替を実行する
            }

            // ▼ Ctrl+クリック：選択状態をトグル（追加/解除）
            //   現在選択中のタブも自動で複数選択リストに含める（iPhone風の挙動）
            //   ※TabControlは1つしか選択できないため、is_multi_selectedフラグで視覚的に複数選択を表現する
            if (ctrl)
            {
                e.Handled = true; // タブ切替は発生させない（複数選択中はSelectedItemを変えない）

                // 現在選択中のタブをまず複数選択に含める（まだ含まれていない場合）
                var current_selected = _vm.selected_tab;
                if (current_selected != null && !_multi_selected_tabs.Contains(current_selected))
                {
                    _multi_selected_tabs.Add(current_selected);
                    current_selected.is_multi_selected = true;
                }

                // クリックされたタブをトグル
                if (_multi_selected_tabs.Contains(clicked_vm))
                {
                    // 既に選択されていれば解除
                    _multi_selected_tabs.Remove(clicked_vm);
                    clicked_vm.is_multi_selected = false;
                }
                else
                {
                    // 未選択なら追加
                    _multi_selected_tabs.Add(clicked_vm);
                    clicked_vm.is_multi_selected = true;
                }
                return;
            }

            // ▼ Shift+クリック：範囲選択（現在選択中のタブからクリック位置まで全て選択）
            if (shift)
            {
                e.Handled = true;

                // 現在表示中のタブ一覧（filtered_tabs）を取得
                // _vm.filtered_tabs はフィルタ済みのObservableCollection
                var all_tabs = _vm.filtered_tabs?.ToList()
                    ?? new System.Collections.Generic.List<project_cost_view_model>();
                if (all_tabs.Count == 0) return;

                // 範囲の起点：現在の selected_tab、なければ既存の複数選択リストの先頭
                var anchor = _vm.selected_tab
                    ?? (_multi_selected_tabs.Count > 0 ? _multi_selected_tabs[0] : null)
                    ?? clicked_vm;

                int idx_anchor = all_tabs.IndexOf(anchor);
                int idx_clicked = all_tabs.IndexOf(clicked_vm);
                if (idx_anchor < 0 || idx_clicked < 0) return;

                int from = System.Math.Min(idx_anchor, idx_clicked);
                int to = System.Math.Max(idx_anchor, idx_clicked);

                // 既存の複数選択を一旦クリアして新しい範囲を設定
                clear_multi_selection();
                for (int i = from; i <= to; i++)
                {
                    _multi_selected_tabs.Add(all_tabs[i]);
                    all_tabs[i].is_multi_selected = true;
                }
                return;
            }
        }

        /// <summary>
        /// 複数選択をすべてクリアする（is_multi_selectedフラグもfalseに戻す）
        /// 通常クリック時・大区分切替時・初期化時等から呼び出される
        /// </summary>
        private void clear_multi_selection()
        {
            foreach (var tab in _multi_selected_tabs)
                tab.is_multi_selected = false;
            _multi_selected_tabs.Clear();
        }

        /// <summary>
        /// ★v0.9.7追加：属性変更や現場名変更後の検索ボックスからフォーカスを外す
        /// 問題：属性変更後に initialize_async でタブが再構築されると、
        ///       なぜかタブ検索ボックス（ComboBox内のTextBox）にフォーカスが残ってしまい、
        ///       Ctrl+Z等のショートカットがTextBoxの「テキスト編集の取り消し」として消費されていた。
        /// 対処：処理完了後にウィンドウ自体にフォーカスを移してショートカットを正しく動作させる。
        /// 影響範囲：属性変更・現場名/詳細編集・一括属性変更・一括アーカイブ後に呼び出す。
        ///
        /// ★修正版：WPFは「論理フォーカス（FocusManager）」と「キーボードフォーカス（Keyboard.Focus）」の
        /// 2つを管理しており、ClearFocus() だけでは TextBox のキー入力消費が止まらないため、
        /// 両方を確実にウィンドウへ移す。Dispatcher.BeginInvoke で UI更新後に実行する。
        /// </summary>
        private void unfocus_search_box()
        {
            try
            {
                // initialize_async 後のUI更新が完了してから実行するため Dispatcher 経由で呼ぶ
                // タイミングが早すぎるとフォーカスが取り戻されてしまうため Background 優先度で遅延実行
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() =>
                    {
                        try
                        {
                            var window = Window.GetWindow(this);
                            if (window == null) return;

                            // ① 論理フォーカスをウィンドウに移す（FocusManager）
                            //    XAML側のIsKeyboardFocusWithin等を正しく更新するため必要
                            System.Windows.Input.FocusManager.SetFocusedElement(window, window);

                            // ② キーボードフォーカスを明示的にウィンドウに移す
                            //    これでTextBoxへのキー入力が止まり、ウィンドウのOnKeyDownにキーが届く
                            System.Windows.Input.Keyboard.Focus(window);

                            // ③ ウィンドウ自体をフォーカス可能にして強制的にフォーカス
                            //    WindowはデフォルトでFocusable=trueだが念のため明示
                            window.Focusable = true;
                            window.Focus();
                        }
                        catch
                        {
                            // フォーカス操作の失敗は致命的ではないため握りつぶす
                        }
                    }));
            }
            catch
            {
                // Dispatcher呼び出し失敗も致命的ではないため握りつぶす
            }
        }

        /// <summary>
        /// 右クリックメニューが開かれたときに、複数選択時のメニュー項目を動的に表示・非表示にする
        /// _multi_selected_tabs.Count > 1 のときだけ「選択中のN件」メニューを表示する
        /// </summary>
        private void tab_context_menu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu) return;

            // 一括操作メニューを名前で取得（XAMLで x:Name 設定済み）
            var bulk_separator = menu.Items.OfType<Separator>()
                .FirstOrDefault(s => s.Name == "bulk_separator");
            var bulk_attr = menu.Items.OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == "bulk_attribute_menu");
            var bulk_arc = menu.Items.OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == "bulk_archive_menu");

            // 複数選択中（2件以上）の場合のみ表示
            int count = _multi_selected_tabs.Count;
            bool show = count > 1;
            var vis = show ? Visibility.Visible : Visibility.Collapsed;

            if (bulk_separator != null) bulk_separator.Visibility = vis;
            if (bulk_attr != null)
            {
                bulk_attr.Visibility = vis;
                bulk_attr.Header = $"選択中の {count} 件の属性を一括変更";
            }
            if (bulk_arc != null)
            {
                bulk_arc.Visibility = vis;
                bulk_arc.Header = $"選択中の {count} 件をアーカイブ";
            }
        }

        /// <summary>
        /// 一括属性変更：BulkAttributeDialogで属性を選択 → 選択中の全タブに適用
        /// 各タブの変更前スナップショットをUndo履歴に積むため、1件ずつDB更新する
        /// </summary>
        public async Task bulk_change_attribute_async()
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (_multi_selected_tabs.Count < 2) return; // 2件未満なら通常処理のはず
            if (_vm == null) return;

            // ▼ ダイアログを開いて属性を選択させる
            var dlg = new BulkAttributeDialog(
                new System.Collections.Generic.List<project_cost_view_model>(_multi_selected_tabs))
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true) return;
            if (string.IsNullOrEmpty(dlg.selected_attribute)) return;

            string new_attr = dlg.selected_attribute;
            // 属性カラーを判定（既存のロジックに合わせる：context_menu の attr_xxx と同じ）
            string new_color = get_color_from_attribute(new_attr);

            // ▼ 各タブのスナップショットを取得 → DB更新 → Undo履歴に積む
            // ★v0.9.7修正：処理中はローディング表示で画面が固まったように見えるのを防ぐ
            if (_vm != null) { _vm.is_busy = true; _vm.status_message = $"{_multi_selected_tabs.Count} 件の属性を変更中..."; }
            int success_count = 0;
            try
            {
                using var conn = database_manager.create_connection();
                foreach (var tab in _multi_selected_tabs.ToList())
                {
                    var snap = await get_current_snapshot_async(tab.project_id);
                    if (snap == null) continue;

                    await conn.ExecuteAsync(@"
                        UPDATE projects SET attribute = @attr, tab_color = @color
                        WHERE id = @id",
                        new { attr = new_attr, color = new_color, id = tab.project_id });

                    await fill_new_values_and_push_async(snap);
                    success_count++;
                }
            }
            finally
            {
                // ★ローディング解除（initialize_asyncが内部でis_busyを管理するためここでfalseにする）
                if (_vm != null) { _vm.is_busy = false; _vm.status_message = ""; }
            }

            // ▼ 成功通知＋画面更新
            MessageBox.Show($"{success_count} 件の属性を「{new_attr}」に変更しました。",
                "一括変更完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            clear_multi_selection();
            await _vm.initialize_async();

            // ★v0.9.7追加：一括属性変更後にCtrl+Zが効くよう、検索ボックスからフォーカスを外す
            unfocus_search_box();
        }

        /// <summary>
        /// 一括アーカイブ：選択中の全タブをアーカイブ（is_active=0）に変更
        /// 操作ログも各件ごとに記録する
        /// </summary>
        public async Task bulk_archive_async()
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (_multi_selected_tabs.Count < 2) return;
            if (_vm == null) return;

            // ▼ 確認ダイアログ：件数と先頭3件のタブ名を表示
            var preview = string.Join("\n",
                _multi_selected_tabs.Take(3).Select(t => $"・{t.tab_name}"));
            int remaining = _multi_selected_tabs.Count - 3;
            string msg = $"選択中の {_multi_selected_tabs.Count} 件をアーカイブします。\n\n{preview}";
            if (remaining > 0) msg += $"\n他 {remaining} 件";
            msg += "\n\n設定画面から復元できます。";

            var ans = MessageBox.Show(msg,
                "一括アーカイブ確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (ans != MessageBoxResult.OK) return;

            // ▼ 1件ずつDB更新＋操作ログ書き込み
            // ★v0.9.7修正：処理中のローディング表示でUIフリーズを回避
            if (_vm != null) { _vm.is_busy = true; _vm.status_message = $"{_multi_selected_tabs.Count} 件をアーカイブ中..."; }
            int success_count = 0;
            try
            {
                using var conn = database_manager.create_connection();
                foreach (var tab in _multi_selected_tabs.ToList())
                {
                    await conn.ExecuteAsync(
                        "UPDATE projects SET is_active = 0 WHERE id = @id",
                        new { id = tab.project_id });

                    await conn.ExecuteAsync(@"
                        INSERT INTO operation_logs
                            (log_datetime, pc_user_id, operator_name, operation_type, target_table, target_id, detail)
                        VALUES
                            (datetime('now','localtime'), @uid, @name, '現場アーカイブ', 'projects', @id, @detail)",
                        new
                        {
                            uid = EA_CostManager.UserSession.user_id,
                            name = EA_CostManager.UserSession.user_name,
                            id = tab.project_id,
                            detail = $"{tab.tab_name} をアーカイブ（一括）"
                        });
                    success_count++;
                }
            }
            finally
            {
                if (_vm != null) { _vm.is_busy = false; _vm.status_message = ""; }
            }

            MessageBox.Show($"{success_count} 件をアーカイブしました。",
                "一括アーカイブ完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            clear_multi_selection();
            await _vm.initialize_async();

            // ★v0.9.7追加：一括アーカイブ後にCtrl+Zが効くよう、検索ボックスからフォーカスを外す
            unfocus_search_box();
        }

        /// <summary>
        /// 属性名からタブカラー（HEX文字列）を取得する
        /// 既存の attribute_colors 定義に合わせた値を返す（CostPage.xaml.cs の attr_xxx 処理と統一）
        /// </summary>
        private string get_color_from_attribute(string attr) => attr switch
        {
            "契約前" => "#9E9E9E",       // グレー
            "契約済" => "#4CAF50",       // グリーン
            "自社案件" => "#2196F3",     // ブルー
            "第一土木" => "#FF9800",     // オレンジ
            "セミナー" => "#9C27B0",     // パープル
            "追加" => "#F44336",         // レッド
            "その他" => "#607D8B",       // ブルーグレー
            _ => "#9E9E9E",
        };
    }
}