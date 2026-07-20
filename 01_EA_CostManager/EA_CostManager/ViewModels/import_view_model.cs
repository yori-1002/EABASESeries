using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Models;
using EA_CostManager.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace EA_CostManager.ViewModels
{
    public class import_view_model : base_view_model
    {
        private readonly IExcelImportService _import_service;

        public import_view_model()
        {
            _import_service = new ExcelImportService();
            select_file_command = new RelayCommand(execute_select_file);
            preview_command = new AsyncRelayCommand(execute_preview, () => can_preview);
            import_command = new AsyncRelayCommand(execute_import, () => can_import);
        }

        // ── コマンド ──────────────────────────────
        public ICommand select_file_command { get; }
        public IAsyncRelayCommand preview_command { get; }
        public IAsyncRelayCommand import_command { get; }

        // ▼▼▼ 追加：取込完了・失敗通知イベント ▼▼▼
        public event Func<Task>? import_completed;
        public event Action<string>? import_failed;

        // ▼▼▼ 追加：直前の取込バッチID（ImportPage側で未登録コードの絞り込みに使用）▼▼▼
        // check_new_category_codes_async がこの値を使って今回取込んだコードのみを対象にする
        // → 過去に未登録のまま放置したコードが毎回ダイアログに出てくる問題を防ぐ
        public string last_import_batch_id { get; private set; } = "";

        // ── ファイルパス（複数対応） ─────────────────
        // ▼▼▼ 変更：単一パス → リスト形式に変更 ▼▼▼
        private List<string> _file_paths = new();
        public List<string> file_paths
        {
            get => _file_paths;
            set
            {
                _file_paths = value;
                OnPropertyChanged(nameof(file_paths));
                OnPropertyChanged(nameof(file_name_display));
                OnPropertyChanged(nameof(can_preview));
                preview_command.NotifyCanExecuteChanged();
                import_command.NotifyCanExecuteChanged();
                preview_rows = new ObservableCollection<DailyReportRow>();
                result_message = string.Empty;
                progress_message = string.Empty;
            }
        }

        // ファイル名表示（複数の場合は件数も表示）
        public string file_name_display =>
            _file_paths.Count == 0 ? "ファイルが選択されていません" :
            _file_paths.Count == 1 ? Path.GetFileName(_file_paths[0]) :
            $"{_file_paths.Count}件のファイルを選択中：{string.Join("、", _file_paths.Select(Path.GetFileName))}";

        // ── プレビューデータ ───────────────────────
        private ObservableCollection<DailyReportRow> _preview_rows = new();
        public ObservableCollection<DailyReportRow> preview_rows
        {
            get => _preview_rows;
            private set
            {
                if (SetProperty(ref _preview_rows, value))
                {
                    OnPropertyChanged(nameof(has_preview));
                    OnPropertyChanged(nameof(preview_count));
                    OnPropertyChanged(nameof(preview_date_range));
                    import_command.NotifyCanExecuteChanged();
                }
            }
        }

        // プレビューサマリー
        public bool has_preview => preview_rows.Count > 0;
        public int preview_count => preview_rows.Count;
        public string preview_date_range
        {
            get
            {
                if (!has_preview) return string.Empty;
                var from = preview_rows.Min(r => r.work_date);
                var to = preview_rows.Max(r => r.work_date);
                return $"{from:yyyy/MM/dd} ～ {to:yyyy/MM/dd}";
            }
        }

        // ── 進捗・結果メッセージ ─────────────────────
        private string _progress_message = string.Empty;
        public string progress_message
        {
            get => _progress_message;
            set => SetProperty(ref _progress_message, value);
        }

        private string _result_message = string.Empty;
        public string result_message
        {
            get => _result_message;
            set => SetProperty(ref _result_message, value);
        }

        private bool _is_result_success;
        public bool is_result_success
        {
            get => _is_result_success;
            set => SetProperty(ref _is_result_success, value);
        }

        // ── ビジー状態 ────────────────────────────
        private bool _is_busy;
        public new bool is_busy
        {
            get => _is_busy;
            set
            {
                if (SetProperty(ref _is_busy, value))
                {
                    preview_command.NotifyCanExecuteChanged();
                    import_command.NotifyCanExecuteChanged();
                }
            }
        }

        // ── コマンド実行条件 ──────────────────────
        public bool can_preview => _file_paths.Count > 0 && !is_busy;
        public bool can_import => preview_rows.Count > 0 && !is_busy;

        // ── ファイル選択（複数対応） ─────────────────
        // ▼▼▼ 変更：Multiselect=true で複数ファイル選択可能に ▼▼▼
        private void execute_select_file()
        {
            var dlg = new OpenFileDialog
            {
                Title = "日報Excelファイルを選択してください（複数選択可）",
                Filter = "Excelファイル (*.xlsx;*.xlsm)|*.xlsx;*.xlsm",
                Multiselect = true,
            };
            if (dlg.ShowDialog() == true)
                file_paths = dlg.FileNames.ToList();
        }

        // ── プレビュー（全ファイル結合） ──────────────
        // ▼▼▼ 変更：複数ファイルを順に読み込んで結合プレビュー ▼▼▼
        private async Task execute_preview()
        {
            is_busy = true;
            progress_message = "Excelファイルを読み込んでいます...";
            result_message = string.Empty;

            try
            {
                var all_rows = new List<DailyReportRow>();
                foreach (var path in _file_paths)
                {
                    progress_message = $"読込中：{Path.GetFileName(path)}";
                    var rows = await _import_service.PreviewAsync(path);
                    all_rows.AddRange(rows);
                }

                preview_rows = new ObservableCollection<DailyReportRow>(all_rows);

                progress_message = all_rows.Count > 0
                    ? $"プレビュー完了：{all_rows.Count}件（{_file_paths.Count}ファイル）"
                    : "データが見つかりませんでした。";
            }
            catch (Exception ex)
            {
                progress_message = $"読み込みエラー：{ex.Message}";
            }
            finally
            {
                is_busy = false;
            }
        }

        // ── 取込実行（複数ファイル・期間重複チェック付き） ──
        // ▼▼▼ 変更：複数ファイルの期間重複を検出してエラー停止 ▼▼▼
        // ▼ v0.9.7 追加：取込完了後にバリデーションエラー一覧 / 新規登録社員一覧を MessageBox 表示
        private async Task execute_import()
        {
            // ▼ 追加 [Sprint 8 / Phase 0]：読取専用モードでは取込（＝大量の書込）を止める
            if (ReadOnlyGuard.block_if_read_only()) return;

            is_busy = true;
            result_message = string.Empty;

            var progress = new Progress<string>(msg => progress_message = msg);

            // ▼ v0.9.7 追加：複数ファイルにまたがるバリデーションエラー / 新規社員を集約するリスト
            //   各ファイルの ImportResult から取得して蓄積し、最後にまとめて MessageBox 表示する
            //   ・複数ファイルでも MessageBox を1回（または2回：エラー警告/新規社員通知）で済ませて
            //     ユーザーの確認操作を最小化する
            //   ・ファイルごとに MessageBox を出すと操作が煩雑になるため
            var all_validation_errors = new List<EA_CostManager.Models.ImportValidationError>();
            var all_newly_registered = new List<string>();

            try
            {
                // ─ Step1：全ファイルのプレビューデータから会計期間を判定 ─
                // 会計期間：21日以降は翌月期（例：6/21〜7/20 → 7月期）
                var file_periods = new Dictionary<string, (int year, int month)>();
                foreach (var path in _file_paths)
                {
                    var rows = await _import_service.PreviewAsync(path);
                    if (rows.Count == 0) continue;

                    // 代表日付（最小日付）から期間を判定
                    var rep_date = rows.Min(r => r.work_date);
                    var period = rep_date.Day >= 21
                        ? (rep_date.AddMonths(1).Year, rep_date.AddMonths(1).Month)
                        : (rep_date.Year, rep_date.Month);
                    file_periods[path] = period;
                }

                // ─ Step2：同一会計期間が複数ファイルにあればエラー ─
                var duplicate_periods = file_periods
                    .GroupBy(kv => kv.Value)
                    .Where(g => g.Count() > 1)
                    .ToList();

                if (duplicate_periods.Any())
                {
                    // エラーメッセージ：どの期間のどのファイルが重複しているか表示
                    var lines = new System.Text.StringBuilder();
                    lines.AppendLine("以下のファイルに期間の重複があります。取込を中止しました。\n");

                    foreach (var grp in duplicate_periods)
                    {
                        lines.AppendLine($"■ 重複期間：{grp.Key.year}年{grp.Key.month}月期");
                        foreach (var kv in grp)
                        {
                            var rows = await _import_service.PreviewAsync(kv.Key);
                            var from = rows.Min(r => r.work_date);
                            var to = rows.Max(r => r.work_date);
                            lines.AppendLine(
                                $"  ・{Path.GetFileName(kv.Key)}" +
                                $"（{from:yyyy/MM/dd} 〜 {to:yyyy/MM/dd}・{rows.Count}行）");
                        }
                        lines.AppendLine();
                    }
                    lines.AppendLine("1期間につき1ファイルのみ選択してください。");

                    string error_msg = lines.ToString();
                    is_result_success = false;
                    result_message = "❌ 期間重複エラー：取込を中止しました";
                    import_failed?.Invoke(error_msg);
                    return;
                }

                // ─ Step3：ファイルを順番に取込 ─
                // ▼▼▼ 修正：UserSessionから実際のユーザー名を取得（旧:ハードコード"システム管理者"）▼▼▼
                string user = EA_CostManager.UserSession.user_name;
                int user_id = EA_CostManager.UserSession.user_id;
                int total_inserted = 0;
                var success_labels = new List<string>();

                // バッチIDを生成（同一取込操作を識別するためのユニークキー）
                string batch_id = $"{DateTime.Now:yyyyMMdd_HHmmss}_{System.Guid.NewGuid().ToString()[..8]}";

                foreach (var path in _file_paths)
                {
                    progress_message = $"取込中：{Path.GetFileName(path)}";
                    var result = await _import_service.ImportAsync(path, user, progress);

                    if (!result.is_success)
                    {
                        is_result_success = false;
                        result_message = $"❌ 取込失敗：{Path.GetFileName(path)} / {result.error_message}";
                        import_failed?.Invoke(
                            $"【{Path.GetFileName(path)}】\n{result.error_message}");
                        return;
                    }

                    total_inserted += result.inserted_rows;
                    success_labels.Add(result.fiscal_month_label);

                    // ▼ v0.9.7 追加：このファイルのバリデーションエラーと新規登録社員を集約
                    //   ・複数ファイル取込の場合、最終的に1回の MessageBox で全ファイル分を表示する
                    //   ・newly_registered は重複する可能性あり（同じ社員が複数ファイルに含まれる場合）
                    //     → 最終表示時に Distinct() で重複除去する
                    if (result.validation_errors.Count > 0)
                        all_validation_errors.AddRange(result.validation_errors);
                    if (result.newly_registered_employees.Count > 0)
                        all_newly_registered.AddRange(result.newly_registered_employees);

                    // ▼▼▼ 追加：ExcelImportServiceのbatch_idを記録（daily_reportsのimport_batchと一致させる）▼▼▼
                    // result.batch_id は ExcelImportService が daily_reports に書き込んだ import_batch と同じ値
                    // 複数ファイルの場合は最後のファイルのbatch_idが last_import_batch_id にセットされる
                    last_import_batch_id = result.batch_id;

                    // ▼▼▼ 追加：ファイルごとに操作ログを書き込む ▼▼▼
                    try
                    {
                        using var conn_log = EA_CostManager.Data.database_manager.create_connection();
                        await conn_log.ExecuteAsync(@"
                            INSERT INTO operation_logs
                                (log_datetime, pc_user_id, operator_name, operation_type,
                                 import_batch, record_count, file_path, detail)
                            VALUES
                                (datetime('now','localtime'), @uid, @name, '日報読込',
                                 @batch, @count, @path, @detail)",
                            new
                            {
                                uid = user_id,
                                name = user,
                                batch = batch_id,
                                count = result.inserted_rows,
                                path = path,
                                detail = $"{Path.GetFileName(path)}（{result.fiscal_month_label}・{result.inserted_rows}件）"
                            });
                    }
                    catch { /* ログ失敗は取込結果に影響しない */ }

                    // 原価集計を自動実行
                    progress_message = $"集計中：{Path.GetFileName(path)}";
                    var preview = await _import_service.PreviewAsync(path);
                    await run_auto_aggregation_for(preview, user);
                }

                is_result_success = true;
                result_message = _file_paths.Count == 1
                    ? $"✅ 取込・集計完了：{total_inserted}件登録（{success_labels[0]}）"
                    : $"✅ {_file_paths.Count}ファイル取込完了：合計{total_inserted}件登録";

                // ▼ v0.9.7 追加：バリデーションエラーがあれば MessageBox で警告表示
                //   ・取込全体は成功扱い（is_result_success = true）のまま、警告として通知
                //   ・該当行は ExcelImportService 側で既にスキップ済み（DB に入っていない）
                //   ・WPFのMessageBoxはUIスレッドからしか呼べないため Dispatcher.InvokeAsync を使用
                //     （ImportPage.xaml.cs の check_new_category_codes_async と同じパターン）
                if (all_validation_errors.Count > 0)
                    await show_validation_errors_async(all_validation_errors);

                // ▼ v0.9.7 追加：新規登録された社員があれば MessageBox で通知
                //   ・既存挙動（UpsertEmployeesAsync の自動INSERT）は変更なし
                //   ・「誰が新規登録されたか」を可視化することで、後から人員マスタで
                //     職種・単価を設定する作業の漏れを防ぐ
                //   ・複数ファイル取込時は重複除去（Distinct）して五十音順に表示
                var distinct_new_emps = all_newly_registered
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct()
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
                if (distinct_new_emps.Count > 0)
                    await show_newly_registered_employees_async(distinct_new_emps);

                // 新規現場登録ダイアログ呼び出し
                // last_import_batch_id は foreach 内で result.batch_id をセット済み
                if (import_completed != null)
                    await import_completed.Invoke();
            }
            catch (Exception ex)
            {
                is_result_success = false;
                result_message = $"❌ 予期しないエラー：{ex.Message}";
            }
            finally
            {
                is_busy = false;
            }
        }

        // ▼▼▼ 変更：1ファイル分のpreviewデータを受け取って集計する形に変更 ▼▼▼
        private async Task run_auto_aggregation_for(
            List<DailyReportRow> rows, string operator_name)
        {
            try
            {
                var service = new CostAggregationService();

                var category_codes = rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.category_code))
                    .Select(r => r.category_code)
                    .Distinct()
                    .ToList();

                if (category_codes.Count == 0) return;

                string min_date = rows.Min(r => r.work_date).ToString("yyyy-MM-dd");
                string max_date = rows.Max(r => r.work_date).ToString("yyyy-MM-dd");

                foreach (var cat in category_codes)
                {
                    progress_message = $"集計中：{cat}";
                    await service.aggregate_async(cat, min_date, max_date, operator_name);
                }

                progress_message = $"集計完了（{category_codes.Count}現場）";
            }
            catch (Exception ex)
            {
                // 集計エラーは取込結果に影響しない（警告として表示）
                progress_message = $"⚠️ 集計で一部エラー：{ex.Message}";
            }
        }

        // ============================================================
        // ▼ v0.9.7 追加：MessageBox 表示用ヘルパー
        //
        // 取込完了後にバリデーションエラー一覧と新規登録社員一覧を表示する。
        // ・WPF の MessageBox は UI スレッド（STA）からしか呼べないため、
        //   Application.Current.Dispatcher.InvokeAsync で UI スレッドに切替えて呼ぶ
        //   （他ファイルの ImportPage.xaml.cs の check_new_category_codes_async と
        //   同じパターン。await 中にバックグラウンドスレッドにいる可能性に対応）
        // ・敬語表現を使用してユーザーへの伝達を丁寧にする
        // ============================================================

        /// <summary>
        /// バリデーションエラー一覧を MessageBox で警告表示する
        /// 該当行は既に取込対象から除外されている（DBには入っていない）
        /// 件数が多い場合は最初の50件 + 「他N件」と省略表示する
        /// （MessageBox が画面外に伸びて切れるのを防ぐため）
        /// </summary>
        private async Task show_validation_errors_async(
            List<EA_CostManager.Models.ImportValidationError> errors)
        {
            // 表示件数の上限（これを超えた分は省略）
            const int MAX_DISPLAY = 50;

            // 各エラー行の表示テキストを構築
            // ImportValidationError.display_line に整形済みフォーマットを持たせている
            int total = errors.Count;
            var lines = new System.Text.StringBuilder();
            lines.AppendLine($"以下の {total} 件のデータに不備があったため、該当行は取込対象から除外されました。");
            lines.AppendLine("Excelファイルを修正して再取込してください。");
            lines.AppendLine();

            int show_count = Math.Min(total, MAX_DISPLAY);
            for (int i = 0; i < show_count; i++)
                lines.AppendLine(errors[i].display_line);

            if (total > MAX_DISPLAY)
            {
                lines.AppendLine();
                lines.AppendLine($"…他 {total - MAX_DISPLAY} 件のエラーがあります。");
                lines.AppendLine("（MessageBoxの表示上限により省略しています）");
            }

            string body = lines.ToString();

            // UI スレッドに切替えて MessageBox を表示
            // Dispatcher.Invoke ではなく InvokeAsync を使うことでデッドロックを防ぐ
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show(
                    body,
                    "取込時のバリデーション警告",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            });
        }

        /// <summary>
        /// 新規登録された社員一覧を MessageBox で通知表示する
        /// 既存挙動（UpsertEmployeesAsync の自動INSERT）はそのままで、
        /// 「誰が新規登録されたか」をユーザーに伝えることが目的
        /// 後から人員マスタで職種・単価を設定する作業の漏れを防ぐ
        /// </summary>
        private async Task show_newly_registered_employees_async(
            List<string> employee_names)
        {
            var lines = new System.Text.StringBuilder();
            lines.AppendLine($"今回の取込で {employee_names.Count} 名の社員が新規登録されました。");
            lines.AppendLine("お手数ですが、設定画面の「人員マスタ」から");
            lines.AppendLine("職種・単価のご確認・設定をお願いいたします。");
            lines.AppendLine();
            lines.AppendLine("【新規登録された社員】");
            foreach (var name in employee_names)
                lines.AppendLine($"・{name}");

            string body = lines.ToString();

            // UI スレッドに切替えて MessageBox を表示
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show(
                    body,
                    "新規登録社員のお知らせ",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            });
        }
    }
}