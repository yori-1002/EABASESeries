using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.ViewModels;

namespace EA_CostManager.Views
{
    public partial class ImportPage : UserControl
    {
        private import_view_model? _vm;

        public ImportPage()
        {
            InitializeComponent();
            var vm = new import_view_model();
            _vm = vm;
            DataContext = vm;

            // 取込完了後に新規現場登録ダイアログを表示
            vm.import_completed += async () => await check_new_category_codes_async();

            // 取込エラー時にメッセージボックスを表示
            vm.import_failed += (error_message) =>
            {
                System.Windows.MessageBox.Show(
                    "不正データです。\n\n" + error_message,
                    "取込エラー",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            };
        }

        private async Task check_new_category_codes_async()
        {
            string batch_id = _vm?.last_import_batch_id ?? "";
            if (string.IsNullOrEmpty(batch_id)) return;

            try
            {
                // DBクエリはバックグラウンドスレッドでも実行可能
                List<string> new_codes;
                using (var conn = database_manager.create_connection())
                {
                    new_codes = (await conn.QueryAsync<string>(@"
                        SELECT DISTINCT dr.category_code
                        FROM daily_reports dr
                        WHERE dr.import_batch = @batch
                          AND dr.category_code IS NOT NULL
                          AND dr.category_code != ''
                          AND dr.category_code NOT IN ('有給', '有休')
                          AND NOT EXISTS (
                              SELECT 1 FROM projects p
                              WHERE p.category_code = dr.category_code
                          )
                        ORDER BY dr.category_code",
                        new { batch = batch_id })).ToList();
                }

                if (new_codes.Count == 0) return;

                // ▼▼▼ 修正：Dispatcher.InvokeAsync でUIスレッドに切り替えてからダイアログを表示 ▼▼▼
                // execute_import 内の CostAggregationService が ConfigureAwait(false) を使っている場合
                // import_completed.Invoke() 時点でバックグラウンドスレッドになっている可能性がある
                // WPFのダイアログ生成はUIスレッド（STAスレッド）でないと動作しないため
                // Dispatcher.InvokeAsync（非同期版）でUIスレッドに切り替える
                // ※ Dispatcher.Invoke（同期版）ではなくInvokeAsync を使うことでデッドロックを防ぐ
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var code in new_codes)
                    {
                        var dlg = new ProjectInfoDialog(code)
                        {
                            Owner = Window.GetWindow(this)
                        };
                        dlg.ShowDialog();
                    }
                });
            }
            catch (Exception ex)
            {
                // デバッグ用：エラーをMessageBoxで表示
                string err_detail = ex.GetType().Name + ": " + ex.Message;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show(
                        "現場登録ダイアログの表示中にエラーが発生しました。\n\n" + err_detail,
                        "デバッグ：現場登録エラー",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                });
            }
        }
    }
}