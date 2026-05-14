// ============================================================
// Services/ExcelImportService.cs  ★完全修正版（Sprint 2 + v0.9.7修正）
//
// 【v0.9.7 追加（データバリデーション強化）】
// ─ ▼ 追加：validate_data_rows メソッド
//     ・区分列に日付形式（"/" or "-" 含む）が入った行を検出してスキップ
//     ・時間が 24h 以上 / マイナス値の行を検出してスキップ
//     ・距離がマイナス値の行を検出してスキップ
//     ・エラー行は ImportResult.validation_errors に蓄積（VM側でMessageBox表示）
// ─ ▼ 追加：DailyReportRow に row_number / sheet_name をセット
//     ・ReadExcel / ReadLegacyExcel の各行生成時に行番号・シート名を設定
//     ・MessageBox エラー表示時に「どの行か」を特定するため
// ─ ▼ 修正：UpsertEmployeesAsync のシグネチャ変更（void → Task<List<string>>）
//     ・新規 INSERT した社員名のリストを返す
//     ・ImportResult.newly_registered_employees に蓄積（VM側でMessageBox通知）
//     ・既存挙動（自動INSERT）は変更なし。「誰が新規登録されたか」を可視化するのみ
//
// 【v0.9.7 修正内容（日報読込の人欠落問題対応）】
// ─ ReadLegacyExcel: E列空欄の作業行スキップ問題を修正
//     変更前: E列(区分コード)が空 → 行スキップ → 人が丸ごと消える
//     変更後: E列が空でもH列(時間)やG列(作業内容)があれば「その他」として取込
// ─ ReadLegacyExcel: 3タイプの列レイアウト自動判定を追加
//     Type A(中間): I列="割合" → ソフト・機材=J列(10)、車両=L列(12)
//     Type B/C(旧/現行): I列="ソフト・機材" → ソフト・機材=I列(9)、車両=K列(11)
// ─ ReadLegacyExcel: グループ区切り行の明示的スキップを追加
//     「株式会社 第一土木」「ベトナム駐在所」「リアクト技研」「T-ROBO」等
// ─ 参照一覧読取: break→continueに変更（250621形式でcode_mapが空になる問題修正）
// ─ project_name取得: code_map→F列→区分コードの優先順で取得するよう改善
//
// 【Sprint 1 との差分修正点】
// ─ employees:    name → employee_name / employee_type → job_type / unit_price → daily_rate
// ─ projects:     project_code → category_code / project_name → site_name
// ─ daily_reports:work_date → report_date / employee_id(FK) → employee_name(TEXT直接保存)
//                 project_id(FK)削除 / employee_type → job_type
//                 work_hours → hours / work_detail → detail
//                 check_in → clock_in / check_out → clock_out
//                 location_start → start_location / location_end → end_location
//                 import_batch カラム追加
// ─ daily_transport: vehicle_name → vehicle / depart_from → departure
//                    arrive_at → arrival / distance_km → distance
//                    road_type → travel_method
// ─ daily_equipment: equipment_fee → daily_rate
// ─ ログ先: import_logs → operation_logs（Sprint 1 既存テーブル）
// ─ namespace: EA_CostManager.Database → EA_CostManager.Data に合わせる
// ─ Excel読込ライブラリ: EPPlus → ClosedXML に変更
// ============================================================
using ClosedXML.Excel;
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace EA_CostManager.Services;

public interface IExcelImportService
{
    Task<List<DailyReportRow>> PreviewAsync(string file_path);
    Task<ImportResult> ImportAsync(
        string file_path,
        string imported_by,
        IProgress<string>? progress = null);
}

public class ExcelImportService : IExcelImportService
{
    // 日報入力シート名（固定）
    private const string TARGET_SHEET = "日報入力";

    // データ開始行（Row5〜）
    private const int DATA_START_ROW = 5;

    // 列番号定数（1-based）
    private const int COL_DATE = 1;   // A: 日付
    private const int COL_NAME = 2;   // B: 氏名
    private const int COL_CHECK_IN = 3;   // C: 出勤
    private const int COL_CHECK_OUT = 4;   // D: 退勤
    private const int COL_OVERTIME = 5;   // E: 残業
    private const int COL_LOC_START = 6;   // F: 開始場所
    private const int COL_LOC_END = 7;   // G: 終了場所
    private const int COL_JOB_TYPE = 8;   // H: 職種
    private const int COL_CATEGORY = 9;   // I: 区分コード
    private const int COL_PROJ_NAME = 10;  // J: 業務名
    private const int COL_DETAIL = 11;  // K: 詳細
    private const int COL_HOURS = 12;  // L: 時間(h)
    private const int COL_EQUIP_NAME = 13;  // M: ソフト・機材
    private const int COL_EQUIP_RATE = 14;  // N: 料金(損料)
    private const int COL_VEHICLE = 15;  // O: 車両
    private const int COL_DEPART = 16;  // P: 発
    private const int COL_ARRIVE = 17;  // Q: 着
    private const int COL_DISTANCE = 18;  // R: 距離
    private const int COL_TRAVEL = 19;  // S: 移動方法
    private const int COL_REMARKS = 20;  // T: 備考

    // ClosedXML はライセンス設定不要のため空コンストラクタ
    public ExcelImportService() { }

    // ▼▼▼ 追加：未登録車両検出時のコールバック ▼▼▼
    // ImportPage 側で設定する。true=追加する / false=スキップ
    public Func<IReadOnlyList<string>, Task<bool>>? on_unknown_vehicles_found { get; set; }

    // ============================================================
    // プレビュー（DB保存なし）
    // ============================================================
    public async Task<List<DailyReportRow>> PreviewAsync(string file_path)
    {
        return await Task.Run(() => ReadExcel(file_path));
    }

    // ============================================================
    // 本取込（洗い替え + DB保存）
    // ============================================================
    public async Task<ImportResult> ImportAsync(
        string file_path,
        string imported_by,
        IProgress<string>? progress = null)
    {
        var result = new ImportResult
        {
            file_name = Path.GetFileName(file_path),
            imported_at = DateTime.Now,
        };

        try
        {
            // 1. Excel読込
            progress?.Report("Excelファイルを読み込んでいます...");
            var rows = await Task.Run(() => ReadExcel(file_path));

            if (rows.Count == 0)
            {
                result.is_success = false;
                result.error_message = "「日報入力」シートに読み込めるデータが見つかりませんでした。";
                return result;
            }

            // 2. 期間判定
            result.date_from = rows.Min(r => r.work_date).Date;
            result.date_to = rows.Max(r => r.work_date).Date;
            result.total_rows = rows.Count;

            // ▼▼▼ 追加：日報期間バリデーション（21日始まり〜翌月20日締め） ▼▼▼
            var validation_error = validate_fiscal_period(rows);
            if (validation_error != null)
            {
                result.is_success = false;
                result.error_message = validation_error;
                progress?.Report($"❌ 不正データ：{validation_error}");
                return result;
            }

            // ▼ v0.9.7 追加：データ行バリデーション（型エラー・範囲外チェック）
            //   ・区分列に日付形式（"/" or "-" 含む）の行を検出
            //   ・時間が 24h 以上 / マイナス値の行を検出
            //   ・距離がマイナス値の行を検出
            //   検出された行は rows から除外（スキップ動作）し、エラー情報を result に蓄積
            //   呼び出し元（import_view_model）が MessageBox で一覧表示する
            //   注意：バリデーションエラーは「警告」扱い。取込全体は中止せず継続する
            var data_errors = validate_data_rows(rows, result.file_name);
            if (data_errors.Count > 0)
            {
                // エラー行を取込対象から除外
                // 行番号 + シート名の組み合わせで一意に特定（旧形式は複数シートあり）
                var error_keys = new HashSet<string>(
                    data_errors.Select(e => $"{e.sheet_name}#{e.row_number}"));
                int before_count = rows.Count;
                rows.RemoveAll(r => error_keys.Contains($"{r.sheet_name}#{r.row_number}"));
                int removed = before_count - rows.Count;

                // エラーリストを ImportResult に蓄積（VM側で MessageBox 表示）
                result.validation_errors.AddRange(data_errors);
                progress?.Report($"⚠️ バリデーション：{data_errors.Count}件のエラー検出 / {removed}件スキップ");
            }

            // バリデーション後に有効データが0件になった場合は取込中止
            if (rows.Count == 0)
            {
                result.is_success = false;
                result.error_message =
                    "バリデーション後に有効なデータが残っていません。Excelファイルを確認してください。";
                progress?.Report($"❌ 取込中止：有効データなし");
                return result;
            }

            // 取込バッチID（ファイル名+日時）
            string batch_id = $"{Path.GetFileNameWithoutExtension(file_path)}_{DateTime.Now:yyyyMMdd_HHmmss}";

            progress?.Report(
                $"取込期間検出: {result.date_from:yyyy/MM/dd} ～ {result.date_to:yyyy/MM/dd}" +
                $" → {result.fiscal_month_label}");

            // ▼▼▼ 追加：2.5 未登録車両チェック（トランザクション開始前に実行） ▼▼▼
            // トランザクション内で別接続を開くとSQLite Error 5 (locked) が発生するため
            // 必ずトランザクション開始前に独立して実行する
            progress?.Report("車両マスタを確認しています...");
            await CheckAndRegisterVehiclesAsync(rows);

            // 3. DB操作
            using var conn = database_manager.create_connection();

            // ★修正: BeginTransactionAsync は DbTransaction を返すため SqliteTransaction にキャスト
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

            try
            {
                // 3-1. 洗い替え
                progress?.Report("既存データを削除しています（洗い替え）...");
                await DeleteRangeAsync(conn, tx, result.date_from, result.date_to);

                // 3-2. 社員マスタ upsert
                progress?.Report("社員マスタを更新しています...");
                // ▼ v0.9.7 修正：戻り値で新規 INSERT した社員名リストを受け取る
                //   既存社員のみなら空リスト。リストの中身は ImportResult に蓄積し、
                //   後で VM 側で「以下の社員が新規登録されました」と MessageBox 表示する
                var newly_registered_emps = await UpsertEmployeesAsync(conn, tx, rows);
                if (newly_registered_emps.Count > 0)
                    result.newly_registered_employees.AddRange(newly_registered_emps);

                // ▼▼▼ 追加：旧形式日報はjob_typeが空のためemployeesテーブルから補完 ▼▼▼
                // 旧形式では職種列がないため、取込後にemployeesテーブルのjob_typeで補完する
                // ★v0.9.7修正：トランザクション tx を渡してトランザクション内の最新データを参照する
                //   修正前：txなしでSELECTしていたため、UpsertEmployeesAsyncの結果が反映されず職種が取得できなかった
                //   修正後：txを渡すことでトランザクション内の最新データから職種を取得できる
                if (rows.Any(r => string.IsNullOrWhiteSpace(r.job_type)))
                {
                    progress?.Report("旧形式日報：職種情報をマスタから補完しています...");

                    // ★ tx を渡してトランザクション内のデータを参照する
                    // is_active=1 の行のみ取得（無効化されたユーザーは補完対象外）
                    // 同名キー対策として LastOrDefault で後勝ちに統一
                    var emp_rows = await conn.QueryAsync<(string name, string job)>(@"
                        SELECT employee_name, job_type
                        FROM employees
                        WHERE COALESCE(is_active, 1) = 1
                          AND employee_name IS NOT NULL
                          AND employee_name != ''
                          AND job_type IS NOT NULL
                          AND job_type != ''", transaction: tx);

                    // 同名キーが複数ある場合は最後の値で上書き（重複登録への保険）
                    var emp_dict = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (name, job) in emp_rows)
                        emp_dict[name] = job;

                    int complemented = 0;
                    foreach (var row in rows)
                    {
                        if (string.IsNullOrWhiteSpace(row.job_type) &&
                            emp_dict.TryGetValue(row.employee_name, out string? job) &&
                            !string.IsNullOrWhiteSpace(job))
                        {
                            row.job_type = job;
                            complemented++;
                        }
                        // マスタにも見つからない場合は「不明」として取込む
                        if (string.IsNullOrWhiteSpace(row.job_type))
                            row.job_type = "不明";
                    }
                    System.Diagnostics.Debug.WriteLine($"[ExcelImport] 職種補完: {complemented}件 / 辞書サイズ: {emp_dict.Count}件");
                }

                // 3-3. 現場マスタ：自動upsertは行わずProjectInfoDialogに任せる
                // ※ ImportPage.xaml.csのcheck_new_category_codes_asyncが未登録コードを検出してダイアログを表示する

                // 3-4. 日報データ insert
                progress?.Report("日報データを登録しています...");
                int inserted = 0;
                int skipped = 0;

                foreach (var row in rows)
                {
                    int report_id = await InsertDailyReportAsync(conn, tx, row, batch_id);

                    if (row.has_transport)
                        await InsertTransportAsync(conn, tx, row, report_id);

                    if (row.has_equipment)
                        await InsertEquipmentAsync(conn, tx, row, report_id);

                    inserted++;
                }

                result.inserted_rows = inserted;
                result.skipped_rows = skipped;

                // 3-5. 操作ログ記録（Sprint 1 の operation_logs を使用）
                progress?.Report("操作ログを記録しています...");
                await InsertOperationLogAsync(conn, tx, result, imported_by);

                await tx.CommitAsync();
                // ▼▼▼ 追加：ImportResultにbatch_idを設定（ImportPageの現場登録ダイアログで使用）▼▼▼
                result.batch_id = batch_id;
                result.is_success = true;

                progress?.Report(
                    $"✅ 取込完了: {inserted}件登録" +
                    (skipped > 0 ? $" / {skipped}件スキップ" : ""));
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                throw new InvalidOperationException(
                    $"DB保存中にエラーが発生しました: {ex.Message}", ex);
            }
        }
        catch (Exception ex)
        {
            result.is_success = false;
            result.error_message = ex.Message;
            progress?.Report($"❌ エラー: {ex.Message}");
        }

        return result;
    }

    // ============================================================
    // Excel読込（ClosedXML版）
    // 「日報入力」シートがあれば現行形式、なければ旧形式（1日1シート）として処理
    // ============================================================
    private List<DailyReportRow> ReadExcel(string file_path)
    {
        var rows = new List<DailyReportRow>();

        using var stream = new FileStream(file_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(stream);

        // ▼▼▼ 追加：旧形式判定（「日報入力」シートがなければ旧形式） ▼▼▼
        if (!wb.TryGetWorksheet(TARGET_SHEET, out _))
            return ReadLegacyExcel(wb, file_path);

        // 以下は現行形式の処理
        if (!wb.TryGetWorksheet(TARGET_SHEET, out var sheet))
            throw new InvalidOperationException($"「{TARGET_SHEET}」シートが見つかりません。");

        int max_row = sheet.LastRowUsed()?.RowNumber() ?? 0;
        if (max_row < DATA_START_ROW) return rows;

        for (int r = DATA_START_ROW; r <= max_row; r++)
        {
            var cell_date = GetCellObject(sheet.Cell(r, COL_DATE));
            if (cell_date == null) continue;
            if (!TryParseDate(cell_date, out DateTime work_date)) continue;

            string emp_name = GetStr(GetCellObject(sheet.Cell(r, COL_NAME)));
            if (string.IsNullOrWhiteSpace(emp_name)) continue;

            rows.Add(new DailyReportRow
            {
                // ▼ v0.9.7 追加：エラー時の行特定用
                row_number = r,
                sheet_name = TARGET_SHEET,

                work_date = work_date,
                employee_name = emp_name,
                clock_in = GetTimeStr(GetCellObject(sheet.Cell(r, COL_CHECK_IN))),
                clock_out = GetTimeStr(GetCellObject(sheet.Cell(r, COL_CHECK_OUT))),
                overtime = GetTimeStr(GetCellObject(sheet.Cell(r, COL_OVERTIME))),
                start_location = GetStr(GetCellObject(sheet.Cell(r, COL_LOC_START))),
                end_location = GetStr(GetCellObject(sheet.Cell(r, COL_LOC_END))),
                job_type = GetStr(GetCellObject(sheet.Cell(r, COL_JOB_TYPE))),
                category_code = GetStr(GetCellObject(sheet.Cell(r, COL_CATEGORY))),
                project_name = GetStr(GetCellObject(sheet.Cell(r, COL_PROJ_NAME))),
                detail = GetStr(GetCellObject(sheet.Cell(r, COL_DETAIL))),
                hours = GetDec(GetCellObject(sheet.Cell(r, COL_HOURS))),
                equipment_name = GetStr(GetCellObject(sheet.Cell(r, COL_EQUIP_NAME))),
                equipment_rate = GetDec(GetCellObject(sheet.Cell(r, COL_EQUIP_RATE))),
                vehicle = GetStr(GetCellObject(sheet.Cell(r, COL_VEHICLE))),
                departure = GetStr(GetCellObject(sheet.Cell(r, COL_DEPART))),
                arrival = GetStr(GetCellObject(sheet.Cell(r, COL_ARRIVE))),
                distance = GetNullDec(GetCellObject(sheet.Cell(r, COL_DISTANCE))),
                travel_method = GetStr(GetCellObject(sheet.Cell(r, COL_TRAVEL))),
                remarks = GetStr(GetCellObject(sheet.Cell(r, COL_REMARKS))),
            });
        }

        return rows;
    }

    // ============================================================
    // 旧形式日報読込（1日1シート形式）
    // シート名が日付の数字（例：「23」「1」）になっており、
    // 各シートのA2セルに作業日付が記載されている形式。
    // 「参照一覧」シートはオプション扱い（あれば業務名マスタとして使用）。
    // ▼▼▼ 修正：参照一覧オプション化・A2セルから日付直接取得 ▼▼▼
    // ============================================================
    private List<DailyReportRow> ReadLegacyExcel(XLWorkbook wb, string file_path)
    {
        var rows = new List<DailyReportRow>();

        // ▼▼▼ ★v0.9.7追加：DBの legacy_name_map から旧形式氏名→現行表記の変換辞書を取得 ▼▼▼
        // 設定画面の「氏名変換」マスタで登録された変換ルールを反映する
        // これによりベトナム人社員（ドック・トゥン等）の追加に対しUIで対応可能になる
        // 旧来のハードコード版（normalize_legacy_name の vietnam_map）はフォールバックとして残す
        var db_name_map = load_legacy_name_map();

        // ── 参照一覧から区分コード→業務名マスタを取得（任意） ──
        // 参照一覧がないファイルでも動作するようオプション扱いにする。
        // あれば code_map を構築してF列の業務名を補完、なければ区分コードをそのまま使う。
        var code_map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wb.TryGetWorksheet("参照一覧", out var ref_sheet))
        {
            // 実際のExcelレイアウト：
            //   Row1 = 年月ヘッダー（B1=年・D1=前半月・F1=後半年・H1=後半月）
            //   Row2 = 列ヘッダー（番号/業務名(略)/業務名）
            //   Row3〜 = データ（L列:番号/コード、N列:業務名）
            // ▼▼▼ 修正：breakをcontinueに変更 ▼▼▼
            // 250621形式ではRow3-4がヘッダー/空行のためbreakすると
            // Row5以降のデータが読めなくなる問題を修正
            for (int r = 3; r <= 100; r++)
            {
                var code_val = ref_sheet.Cell(r, 12).Value; // L列：番号/コード
                var name_val = ref_sheet.Cell(r, 14).Value; // N列：業務名
                if (code_val.IsBlank) continue; // ★修正：break→continue
                string code_str = code_val.ToString()?.Trim() ?? "";
                string name_str = name_val.ToString()?.Trim() ?? "";
                // ヘッダー文字列（"番号"等）はスキップ
                if (!string.IsNullOrWhiteSpace(code_str) && code_str != "番号")
                    code_map[code_str] = name_str;
            }
        }

        // ── 日付シートを順に処理 ──
        // 有効なシート名：数字のみ（「参照一覧」「休日出勤表」等は除外）
        var day_sheets = wb.Worksheets
            .Where(ws => int.TryParse(ws.Name, out _))
            .OrderBy(ws => int.Parse(ws.Name))
            .ToList();

        if (day_sheets.Count == 0)
            throw new InvalidOperationException("旧形式日報：日付シート（数字名のシート）が見つかりません。");

        foreach (var sheet in day_sheets)
        {
            // ▼ A2セルから作業日付を直接取得（参照一覧の年月に依存しない）
            // 旧形式では各シートのA2に「令和7年7月16日」等の形式で日付が入っている。
            // ClosedXML は日付セルを DateTime として返すため TryParseDate で変換する。
            if (!TryParseDate(GetCellObject(sheet.Cell(2, 1)), out DateTime report_date))
                continue; // A2から日付が取れないシートはスキップ

            int max_row = sheet.LastRowUsed()?.RowNumber() ?? 0;
            if (max_row < 7) continue; // ヘッダーがRow6なのでRow7以降がデータ

            // ★v0.9.7追加：ヘッダー行（Row6）のI列（col 9）で列レイアウトを判定
            // 日報Excelには3タイプの列構成が存在し、シートごとに異なる場合がある。
            // ─ Type A（中間タイプ）：I列="割合" → ソフト・機材=J列(10)、車両=L列(12)
            // ─ Type B/C（旧/現行） ：I列="ソフト・機材" → ソフト・機材=I列(9)、車両=K列(11)
            // ヘッダー行のI列の値で自動判定することで、同一ファイル内のタイプ混在にも対応する。
            string header_col_i = sheet.Cell(6, 9).Value.ToString()?.Trim() ?? "";
            bool is_type_a = header_col_i.Contains("割合");
            int col_equip = is_type_a ? 10 : 9;   // ソフト・機材の列番号
            int col_vehicle = is_type_a ? 12 : 11;  // 車両の列番号

            // 現在処理中の人物名（A列に氏名があれば更新）
            string current_name = "";
            string current_clock_in = "";
            string current_clock_out = "";

            for (int r = 7; r <= max_row; r++)
            {
                // A列に氏名があれば現在担当者を更新
                string name_cell = GetStr(sheet.Cell(r, 1).Value);

                // ★v0.9.7追加：グループ区切り行の検出
                // 日報には「株式会社 第一土木」「ベトナム駐在所」「リアクト技研」「T-ROBO」等の
                // 所属グループ区切り行がある。これらが氏名として取り込まれないよう明示的にスキップする。
                // A列に値があり、かつ区切りキーワードを含む行は丸ごとスキップ。
                if (!string.IsNullOrWhiteSpace(name_cell))
                {
                    bool is_separator = name_cell.Contains("株式会社")
                                     || name_cell.Contains("駐在所")
                                     || name_cell.Contains("リアクト")
                                     || name_cell.Contains("T-ROBO")
                                     || name_cell.Contains("アース・アナライザー");
                    if (is_separator) continue; // 区切り行はスキップ（current_nameを更新しない）
                }

                var cat_val = sheet.Cell(r, 5).Value;  // E列：番号/区分コード
                var hrs_val = sheet.Cell(r, 8).Value;  // H列：時間

                bool has_category = !cat_val.IsBlank && !string.IsNullOrWhiteSpace(cat_val.ToString());
                bool has_hours = !hrs_val.IsBlank;

                if (!string.IsNullOrWhiteSpace(name_cell))
                {
                    // B列が本社/駐在所等の場所名なら氏名行ではなく出退勤場所行
                    string b_cell = sheet.Cell(r, 2).Value.ToString()?.Trim() ?? "";
                    bool is_location_row = b_cell == "本社" || b_cell == "駐在所" || b_cell == "現場"
                                        || b_cell == "リモート";

                    if (!is_location_row)
                    {
                        // ▼▼▼ 追加：旧形式の氏名を現行employees表記に正規化 ▼▼▼
                        // 旧形式：フルネーム（例：「仲　良智」「ｸﾞｴﾝ ﾃｨｴﾝ ﾌｯｸ」「ﾌﾟｲ ｱｲﾝ ﾄﾞｯｸ」）
                        // 現行  ：短縮名（例：「仲」「フック」「ドック」）
                        // → employeesテーブルと一致させてjob_type補完を可能にする
                        // ★v0.9.7修正：第2引数にDBの変換辞書（db_name_map）を渡す
                        current_name = normalize_legacy_name(name_cell, db_name_map);
                        // B列・C列はTimeSpan型で格納されている場合がある
                        var b_val = sheet.Cell(r, 2).Value;
                        var c_val = sheet.Cell(r, 3).Value;
                        current_clock_in = b_val.IsTimeSpan ? b_val.GetTimeSpan().ToString(@"hh\:mm")
                                          : b_val.IsDateTime ? b_val.GetDateTime().ToString("HH:mm") : "";
                        current_clock_out = c_val.IsTimeSpan ? c_val.GetTimeSpan().ToString(@"hh\:mm")
                                          : c_val.IsDateTime ? c_val.GetDateTime().ToString("HH:mm") : "";
                    }
                }

                // ★v0.9.7修正：E列空欄でもH列（時間）に値があれば取り込む
                // 【変更前】if (!has_category || !has_hours) continue;
                //   → E列空欄の作業行が全スキップされ、区分コードなしの社員が丸ごと消える問題
                // 【変更後】H列（時間）がなければスキップ。E列空欄はG列（作業内容）も確認する
                // E列が空でも、G列（作業内容）に記載がある行は作業データとして取り込む。
                string detail_for_check = sheet.Cell(r, 7).Value.ToString()?.Trim() ?? "";
                bool has_detail = !string.IsNullOrWhiteSpace(detail_for_check);

                // 時間も作業内容もない行はスキップ（場所行・空行等）
                if (!has_hours && !has_detail) continue;
                if (string.IsNullOrWhiteSpace(current_name)) continue;

                // ★v0.9.7修正：E列が空の場合は「その他」を使用
                // 社内作業（研修・事務・会議等）は区分コードが入力されないケースがある。
                // 旧形式ではこれが頻発するため、空欄の場合は「その他」として取り込む。
                string category;
                if (has_category)
                {
                    category = cat_val.ToString()?.Trim() ?? "";
                }
                else
                {
                    // E列空欄 → F列に業務名があればproject_nameに活用、区分は「その他」
                    category = "その他";
                }
                if (string.IsNullOrWhiteSpace(category)) category = "その他";

                // ▼▼▼ 追加：番号（1〜4）を現行区分コードに変換 ▼▼▼
                // 旧形式では数値番号で管理されていたコードを現行の文字コードに統一する
                category = category switch
                {
                    "1" => "事務",
                    "2" => "営業",
                    "3" => "研修",
                    "4" => "その他",
                    _ => category  // EA** 等はそのまま
                };

                // ▼▼▼ 追加：区分コード正規化（大文字化 + 先頭ゼロ除去） ▼▼▼
                // 例: ea94 → EA94 / RD024 → RD24 / Ea094 → EA94
                category = normalize_category_code(category);

                // ★v0.9.7修正：project_name の取得ロジック改善
                // 1. code_map にマッピングがあればそれを使用
                // 2. なければ F列（業務名）の値を使用
                // 3. F列も空なら区分コードをそのまま使用
                string f_col_name = sheet.Cell(r, 6).Value.ToString()?.Trim() ?? "";
                string proj_name;
                if (code_map.TryGetValue(category, out string? mapped))
                    proj_name = mapped;
                else if (!string.IsNullOrWhiteSpace(f_col_name))
                    proj_name = f_col_name;
                else
                    proj_name = category;

                // 時間取得（H列=8列目）
                // IsNumber で取得を試み、失敗した場合は文字列パースにフォールバック
                // （セルの型がテキストになっている場合でも正しく読めるよう対策）
                decimal hours = 0m;
                if (has_hours)
                {
                    if (hrs_val.IsNumber)
                        hours = (decimal)hrs_val.GetNumber();
                    else if (decimal.TryParse(hrs_val.ToString()?.Trim(), out decimal parsed_h))
                        hours = parsed_h;
                }
                // 時間が0以下でも作業内容があれば取り込む（時間未入力のケース対応）
                if (hours <= 0 && !has_detail) continue;

                // ★v0.9.7修正：ソフト・機材(col_equip)・車両(col_vehicle)を動的列番号で取得
                // Type A（割合あり）：ソフト・機材=J列(10)、車両=L列(12)
                // Type B/C（割合なし）：ソフト・機材=I列(9)、車両=K列(11)
                string detail = GetStr(GetCellObject(sheet.Cell(r, 7)));
                string equip_name = GetStr(GetCellObject(sheet.Cell(r, col_equip)));
                string vehicle = GetStr(GetCellObject(sheet.Cell(r, col_vehicle)));

                rows.Add(new DailyReportRow
                {
                    // ▼ v0.9.7 追加：エラー時の行特定用
                    row_number = r,
                    sheet_name = sheet.Name,

                    work_date = report_date,
                    employee_name = current_name,
                    clock_in = current_clock_in,
                    clock_out = current_clock_out,
                    job_type = "",          // 旧形式には職種列なし → employeesテーブルから後で補完
                    category_code = category,
                    project_name = proj_name,
                    detail = detail,
                    hours = hours,
                    equipment_name = equip_name,
                    equipment_rate = 0m,    // 旧形式には損料列がないため0固定
                    vehicle = vehicle,
                    departure = "",
                    arrival = "",
                    distance = null,
                    travel_method = "",
                    remarks = "",
                });
            }
        }

        return rows;
    }

    // ============================================================
    // ▼▼▼ 追加：旧形式氏名の正規化（フルネーム→現行employees表記に変換） ▼▼▼
    // 旧形式は「苗字　名前」形式 or 半角カタカナのベトナム人フルネーム。
    // 現行employeesテーブルの表記（苗字のみ or ベトナム人略称）に合わせる。
    // ★v0.9.7修正：DBの legacy_name_map（旧形式氏名変換マスタ）を最優先で参照する
    //              第2引数 db_map は ReadLegacyExcel で事前にロード済みの辞書
    //              DBにヒットしない場合はフォールバックでハードコード辞書 → 苗字抽出と進む
    // ============================================================
    private static string normalize_legacy_name(
        string raw_name,
        Dictionary<string, string>? db_map = null)
    {
        if (string.IsNullOrWhiteSpace(raw_name)) return raw_name;
        string trimmed = raw_name.Trim();

        // ★優先① DBの legacy_name_map から検索（管理画面でユーザー登録した変換ルール）
        if (db_map != null && db_map.TryGetValue(trimmed, out string? db_name))
            return db_name;

        // フォールバック② ハードコード変換辞書（旧バージョンとの後方互換用）
        // legacy_name_map に登録されていなくても初期4人は変換可能にする保険
        var vietnam_map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ｸﾞｴﾝ ﾃｨｴﾝ ﾌｯｸ", "フック" },
            { "ﾌｧﾑ ｳﾞｧﾝ ﾀｲ",   "タイ"   },
            { "ﾌｧﾑ ﾃ ｽﾞｲ",     "ズイ"   },
            { "ｸﾞｴﾝ ﾊﾞ ﾀｲ",    "バタイ" },
        };
        if (vietnam_map.TryGetValue(trimmed, out string? nickname))
            return nickname;

        // フォールバック③ 半角カタカナを含む場合（マップにない新規ベトナム人社員）はそのまま返す
        // → employeesテーブルと一致しない場合は「不明」になるが、人員マスタに登録すれば次回から認識される
        if (trimmed.Any(c => c >= '\uFF61' && c <= '\uFF9F'))
            return trimmed;

        // フォールバック④ 日本人社員：全角スペース or 半角スペースで分割して苗字のみ抽出
        // 例：「仲　良智」→「仲」、「内藤　建郎」→「内藤」
        string[] parts = trimmed.Split(new char[] { '\u3000', ' ' },
                                       StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : trimmed;
    }

    // ▼▼▼ ★v0.9.7追加：DBから旧形式氏名変換マスタをロード ▼▼▼
    // legacy_name_map テーブルから legacy_name → display_name のマッピングを Dictionary に変換する
    // 取込時にこの辞書を normalize_legacy_name に渡すことで、UIで登録された変換ルールを動的に適用する
    // 同名キー重複時は後勝ち（DB登録順の最後の値を採用）
    // ============================================================
    private static Dictionary<string, string> load_legacy_name_map()
    {
        // 戻り値を初期化（DBエラー時は空辞書を返してフォールバック動作させる）
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = database_manager.create_connection();
            // 取得時に legacy_name と display_name の前後空白をTRIMして取り込む
            // → Excel上の表記揺れによる検索ミスを防ぐ
            var rows = conn.Query<(string legacy_name, string display_name)>(
                "SELECT TRIM(legacy_name) AS legacy_name, TRIM(display_name) AS display_name FROM legacy_name_map " +
                "WHERE legacy_name IS NOT NULL AND legacy_name != '' " +
                "  AND display_name IS NOT NULL AND display_name != ''");
            foreach (var row in rows)
            {
                // 同じキーが2件以上あれば後者で上書き（DB側でユニーク制約があれば発生しない）
                result[row.legacy_name] = row.display_name;
            }
        }
        catch (Exception ex)
        {
            // 旧バージョンのDBで legacy_name_map が存在しない場合等を想定
            // この場合は空辞書を返し、ハードコード辞書のみが使われる
            System.Diagnostics.Debug.WriteLine(
                $"legacy_name_map ロード失敗（フォールバックで継続）: {ex.Message}");
        }
        return result;
    }

    // 旧形式読込用：セル値をintに変換（数値以外は0）
    private static int GetInt(XLCellValue val)
    {
        if (val.IsNumber) return (int)val.GetNumber();
        if (int.TryParse(val.ToString(), out int r)) return r;
        return 0;
    }

    // ============================================================
    // DB操作
    // ============================================================
    private static async Task DeleteRangeAsync(
        SqliteConnection conn, SqliteTransaction tx,
        DateTime from, DateTime to)
    {
        string f = from.ToString("yyyy-MM-dd");
        string t_str = to.ToString("yyyy-MM-dd");

        // 子テーブルを先に削除
        await conn.ExecuteAsync(@"
            DELETE FROM daily_transport
            WHERE daily_report_id IN (
                SELECT id FROM daily_reports
                WHERE report_date BETWEEN @f AND @t)",
            new { f, t = t_str }, tx);

        await conn.ExecuteAsync(@"
            DELETE FROM daily_equipment
            WHERE daily_report_id IN (
                SELECT id FROM daily_reports
                WHERE report_date BETWEEN @f AND @t)",
            new { f, t = t_str }, tx);

        await conn.ExecuteAsync(
            "DELETE FROM daily_reports WHERE report_date BETWEEN @f AND @t",
            new { f, t = t_str }, tx);
    }

    // ▼ v0.9.7 修正：戻り値を Task → Task<List<string>> に変更
    //   返り値：今回の取込で新規 INSERT した employee_name のリスト
    //   既存社員のみで構成されていた場合は空リストを返す
    //   呼び出し元（ImportAsync）で ImportResult.newly_registered_employees にセットする
    private static async Task<List<string>> UpsertEmployeesAsync(
        SqliteConnection conn, SqliteTransaction tx,
        List<DailyReportRow> rows)
    {
        // ▼ v0.9.7 追加：新規INSERTした社員名を蓄積するリスト
        var newly_registered = new List<string>();

        var employees = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.employee_name))
            .GroupBy(r => r.employee_name)
            .Select(g => new { name = g.Key, job = g.First().job_type })
            .ToList();

        foreach (var emp in employees)
        {
            var exists = await conn.QueryFirstOrDefaultAsync<int?>(
                "SELECT id FROM employees WHERE employee_name = @name",
                new { name = emp.name }, tx);

            if (exists.HasValue)
            {
                // ▼▼▼ 修正：job_typeが空/「不明」のときはUPDATEしない ▼▼▼
                // 旧形式日報取込時はjob_type=「不明」になるため、
                // 現行形式で登録済の正しいjob_typeを上書きしてしまう問題を防ぐ。
                // 有効なjob_typeがある場合のみ更新する。
                if (!string.IsNullOrWhiteSpace(emp.job) && emp.job != "不明")
                {
                    await conn.ExecuteAsync(@"
                        UPDATE employees
                        SET job_type   = @job,
                            updated_at = datetime('now','localtime')
                        WHERE employee_name = @name",
                        new { job = emp.job, name = emp.name }, tx);
                }
            }
            else
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO employees (employee_name, job_type, created_at, updated_at)
                    VALUES (@name, @job, datetime('now','localtime'), datetime('now','localtime'))",
                    new { name = emp.name, job = emp.job }, tx);

                // ▼ v0.9.7 追加：新規 INSERT した名前を記録
                // 後で MessageBox で「以下の社員が新規登録されました」と通知するため
                newly_registered.Add(emp.name);
            }
        }

        // ▼ v0.9.7 追加：新規登録社員のリストを返す
        return newly_registered;
    }

    private static async Task UpsertProjectsAsync(
        SqliteConnection conn, SqliteTransaction tx,
        List<DailyReportRow> rows)
    {
        var projects = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.category_code))
            .GroupBy(r => r.category_code)
            .Select(g => new { code = g.Key, name = g.First().project_name })
            .ToList();

        foreach (var proj in projects)
        {
            var exists = await conn.QueryFirstOrDefaultAsync<int?>(
                "SELECT id FROM projects WHERE category_code = @code",
                new { code = proj.code }, tx);

            if (!exists.HasValue)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO projects (category_code, site_name, created_at)
                    VALUES (@code, @name, datetime('now','localtime'))",
                    new { code = proj.code, name = proj.name }, tx);
            }
        }
    }

    private static async Task<int> InsertDailyReportAsync(
        SqliteConnection conn, SqliteTransaction tx,
        DailyReportRow row, string batch_id)
    {
        return await conn.QueryFirstAsync<int>(@"
            INSERT INTO daily_reports (
                report_date, employee_name, job_type,
                category_code, project_name, detail, hours,
                clock_in, clock_out, overtime,
                start_location, end_location,
                import_batch, created_at, updated_at
            ) VALUES (
                @report_date, @employee_name, @job_type,
                @category_code, @project_name, @detail, @hours,
                @clock_in, @clock_out, @overtime,
                @start_location, @end_location,
                @import_batch,
                datetime('now','localtime'), datetime('now','localtime')
            ) RETURNING id",
            new
            {
                report_date = row.work_date.ToString("yyyy-MM-dd"),
                employee_name = row.employee_name,
                job_type = row.job_type,
                category_code = row.category_code,
                project_name = row.project_name,
                detail = row.detail,
                hours = (double)row.hours,
                clock_in = row.clock_in,
                clock_out = row.clock_out,
                overtime = row.overtime,
                start_location = row.start_location,
                end_location = row.end_location,
                import_batch = batch_id,
            }, tx);
    }

    private static async Task InsertTransportAsync(
        SqliteConnection conn, SqliteTransaction tx,
        DailyReportRow row, int report_id)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO daily_transport (
                daily_report_id,
                vehicle, departure, arrival,
                distance, travel_method, transport_cost
            ) VALUES (
                @report_id,
                @vehicle, @departure, @arrival,
                @distance, @travel_method, 0
            )",
            new
            {
                report_id,
                vehicle = row.vehicle,
                departure = row.departure,
                arrival = row.arrival,
                distance = row.distance.HasValue ? (object)(double)row.distance.Value : DBNull.Value,
                travel_method = row.travel_method,
            }, tx);
    }

    private static async Task InsertEquipmentAsync(
        SqliteConnection conn, SqliteTransaction tx,
        DailyReportRow row, int report_id)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO daily_equipment (
                daily_report_id, equipment_name, daily_rate, quantity
            ) VALUES (
                @report_id, @equipment_name, @daily_rate, 1
            )",
            new
            {
                report_id,
                equipment_name = row.equipment_name,
                daily_rate = (double)row.equipment_rate,
            }, tx);
    }

    private static async Task InsertOperationLogAsync(
        SqliteConnection conn, SqliteTransaction tx,
        ImportResult result, string imported_by)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO operation_logs (
                log_datetime, operator_name, operation_type,
                target_table, detail, record_count, file_path
            ) VALUES (
                datetime('now','localtime'),
                @operator, 'IMPORT',
                'daily_reports',
                @detail,
                @count,
                @path
            )",
            new
            {
                @operator = imported_by,
                detail = $"{result.fiscal_month_label} / " +
                           $"{result.date_from:yyyy/MM/dd}～{result.date_to:yyyy/MM/dd}",
                count = result.inserted_rows,
                path = result.file_name,
            }, tx);
    }

    // ============================================================
    // ▼▼▼ 追加：車両マスタチェック・登録（トランザクション外で実行） ▼▼▼
    // ============================================================
    // ============================================================
    // ▼▼▼ 追加：日報期間バリデーション ▼▼▼
    // 日報は21日始まり〜翌月20日締め（1ファイル＝1ヶ月）
    // 複数の会計期間にまたがるデータが混在している場合はエラーとする
    // ============================================================
    private static string? validate_fiscal_period(List<DailyReportRow> rows)
    {
        if (rows.Count == 0) return null;

        // 各行の会計期間を判定（21日以降は翌月の期間）
        // 例：2025/07/21〜2025/08/20 → 「2025年8月期」
        static (int year, int month) get_period(DateTime d)
        {
            if (d.Day >= 21)
            {
                // 翌月期間
                var next = d.AddMonths(1);
                return (next.Year, next.Month);
            }
            return (d.Year, d.Month);
        }

        var periods = rows
            .Select(r => get_period(r.work_date))
            .Distinct()
            .ToList();

        if (periods.Count <= 1) return null;

        // 複数期間が混在 → エラー内容を生成
        var period_labels = periods
            .OrderBy(p => p.year).ThenBy(p => p.month)
            .Select(p => $"{p.year}年{p.month}月期");

        var invalid_rows = rows
            .GroupBy(r => get_period(r.work_date))
            .OrderBy(g => g.Key.year).ThenBy(g => g.Key.month)
            .Select(g =>
            {
                var dates = g.Select(r => r.work_date.ToString("yyyy/MM/dd")).Distinct()
                             .OrderBy(d => d);
                return $"・{g.Key.year}年{g.Key.month}月期：{string.Join("、", dates)}";
            });

        return "日報ファイルに複数の会計期間が混在しています。\n" +
               "1ファイルは21日始まり〜翌月20日締めの1期間のみにしてください。\n\n" +
               "検出された期間：\n" +
               string.Join("\n", invalid_rows);
    }

    // ============================================================
    // ▼ 追加（v0.9.7）：データ行バリデーション
    //
    // 各行のデータが妥当か検査し、エラー情報のリストを返す。
    // ※ 呼び出し元（ImportAsync）でこのリストを使ってエラー行を rows から除外する
    //   バリデーション自体はここでは rows を変更しない（純粋な検査関数）
    //
    // 仕様（v0.9.7 確定）：
    //   #1-1：区分列に日付形式（"/" または "-" を含む文字列）→ エラー
    //         例：「2026/4/27」「03-15」など
    //   #3-1：時間が 24 時間以上 → エラー（休憩を含めて1日24時間が物理上限）
    //   #3-2：時間がマイナス値 → エラー
    //   #3-3：距離がマイナス値 → エラー（移動データがある場合のみ判定）
    //
    // 仕様外（バリデーションしない項目）：
    //   #1-2：区分列に純粋な数値のみ → OK（「3」等の単純な区分コードもありえるため）
    //   #2  ：氏名・日付の空欄 → ReadExcel/ReadLegacyExcel で既に行スキップ済み
    //   #2-3：業務名空欄 → そもそもありえない前提で検査不要
    //   #8-1：出勤＞退勤 → VBA 入力のため誤入力ほぼ無し（時間は入力値そのまま使用）
    //
    // パラメータ：
    //   rows      ：検査対象の全行
    //   file_name ：エラー情報に記録するファイル名（パスなし）
    // 戻り値：
    //   検出されたエラー一覧（0件なら空リスト）
    // ============================================================
    private static List<ImportValidationError> validate_data_rows(
        List<DailyReportRow> rows, string file_name)
    {
        var errors = new List<ImportValidationError>();

        foreach (var row in rows)
        {
            // ── #1-1：区分列の型チェック（日付形式の検出）──────────
            // 区分コードに "/" or "-" を含む文字列は「日付っぽい誤入力」とみなしてエラー
            // 例：「2026/4/27」「03-15」「7/1」など
            // 注：純粋な数値（"3" 等）や英数字混在（"EA94" 等）は OK
            if (!string.IsNullOrEmpty(row.category_code)
                && (row.category_code.Contains('/') || row.category_code.Contains('-')))
            {
                errors.Add(new ImportValidationError
                {
                    file_name = file_name,
                    sheet_name = row.sheet_name,
                    row_number = row.row_number,
                    column_name = "区分",
                    error_type = "型エラー",
                    detail = "区分列に日付形式が入力されています",
                    raw_value = row.category_code,
                });
                // この行は他のエラーがあっても重複検出しないよう次の行へ
                // （1行に複数エラーが出ると MessageBox が冗長になるため）
                continue;
            }

            // ── #3-1：時間の上限チェック（24h 以上はエラー）──────────
            // 1日に 24 時間以上の作業はあり得ないため誤入力と判定
            if (row.hours >= 24m)
            {
                errors.Add(new ImportValidationError
                {
                    file_name = file_name,
                    sheet_name = row.sheet_name,
                    row_number = row.row_number,
                    column_name = "時間",
                    error_type = "範囲外",
                    detail = "時間が24時間以上です",
                    raw_value = row.hours.ToString("0.##"),
                });
                continue;
            }

            // ── #3-2：時間のマイナスチェック ──────────
            // 時間にマイナス値はあり得ない（入力ミスと判定）
            if (row.hours < 0m)
            {
                errors.Add(new ImportValidationError
                {
                    file_name = file_name,
                    sheet_name = row.sheet_name,
                    row_number = row.row_number,
                    column_name = "時間",
                    error_type = "範囲外",
                    detail = "時間がマイナス値です",
                    raw_value = row.hours.ToString("0.##"),
                });
                continue;
            }

            // ── #3-3：距離のマイナスチェック ──────────
            // 移動データがある場合のみ判定（distance が null なら移動なしなのでスキップ）
            if (row.distance.HasValue && row.distance.Value < 0m)
            {
                errors.Add(new ImportValidationError
                {
                    file_name = file_name,
                    sheet_name = row.sheet_name,
                    row_number = row.row_number,
                    column_name = "距離",
                    error_type = "範囲外",
                    detail = "距離がマイナス値です",
                    raw_value = row.distance.Value.ToString("0.##"),
                });
                continue;
            }
        }

        return errors;
    }

    private async Task CheckAndRegisterVehiclesAsync(List<DailyReportRow> rows)
    {
        var import_vehicles = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.vehicle))
            .Select(r => r.vehicle)
            .Distinct()
            .ToList();

        if (import_vehicles.Count == 0) return;

        // 独立した接続で実行（トランザクション外）
        using var conn = database_manager.create_connection();
        var registered = (await conn.QueryAsync<string>(
            "SELECT vehicle_name FROM vehicles")).ToHashSet();

        var unknown = import_vehicles
            .Where(v => !registered.Contains(v))
            .ToList();

        if (unknown.Count == 0) return;

        bool should_add = true;
        if (on_unknown_vehicles_found != null)
            should_add = await on_unknown_vehicles_found(unknown.AsReadOnly());

        if (!should_add) return;

        foreach (var vehicle_name in unknown)
        {
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO vehicles (vehicle_name, is_active) VALUES (@name, 1)",
                new { name = vehicle_name });
        }
    }

    // ============================================================
    // セル値変換ヘルパー
    // ============================================================

    // ★新規追加: ClosedXML の XLCellValue を object? に変換
    private static object? GetCellObject(IXLCell cell)
    {
        var val = cell.Value;
        if (val.IsBlank) return null;
        return val.Type switch
        {
            XLDataType.DateTime => val.GetDateTime(),
            XLDataType.TimeSpan => val.GetTimeSpan(),
            XLDataType.Number => val.GetNumber(),
            XLDataType.Text => val.GetText(),
            XLDataType.Boolean => val.GetBoolean(),
            _ => null,
        };
    }

    private static bool TryParseDate(object? val, out DateTime result)
    {
        result = default;
        if (val is DateTime dt) { result = dt; return true; }
        if (val is double d)
        {
            try { result = DateTime.FromOADate(d); return true; }
            catch { return false; }
        }
        return DateTime.TryParse(val?.ToString(), out result);
    }

    private static string GetStr(object? val)
    {
        if (val == null) return string.Empty;
        string s = (val.ToString() ?? "").Trim();
        return s == "-" ? string.Empty : s;
    }

    private static string? GetTimeStr(object? val)
    {
        if (val is TimeSpan ts) return ts.ToString(@"hh\:mm");
        if (val is DateTime dt) return dt.ToString("HH:mm");
        string s = (val?.ToString() ?? "").Trim();
        return (s == "-" || string.IsNullOrWhiteSpace(s)) ? null : s;
    }

    private static decimal GetDec(object? val)
    {
        string s = (val?.ToString() ?? "").Trim();
        if (s == "-" || string.IsNullOrWhiteSpace(s)) return 0m;
        return decimal.TryParse(s, out decimal d) ? d : 0m;
    }

    private static decimal? GetNullDec(object? val)
    {
        string s = (val?.ToString() ?? "").Trim();
        if (s == "-" || string.IsNullOrWhiteSpace(s)) return null;
        return decimal.TryParse(s, out decimal d) ? d : null;
    }

    // ▼▼▼ 追加：区分コード正規化（大文字化 + 先頭ゼロ除去） ▼▼▼
    /// <summary>
    /// 区分コードを正規化する
    /// ・アルファベット部分を大文字化（ea94 → EA94）
    /// ・数字部分の先頭ゼロを除去（RD024 → RD24）
    /// </summary>
    public static string normalize_category_code(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code;

        // 大文字化
        code = code.ToUpperInvariant();

        // アルファベット部分と数字部分を分割
        int split_idx = 0;
        while (split_idx < code.Length && char.IsLetter(code[split_idx]))
            split_idx++;

        // 全部英字 or 全部数字の場合はそのまま返す
        if (split_idx == 0 || split_idx == code.Length) return code;

        string prefix = code[..split_idx];
        string number_part = code[split_idx..];

        // 数字部分が純粋な整数なら先頭ゼロを除去
        if (int.TryParse(number_part, out int num))
            return $"{prefix}{num}";

        return code;
    }
}