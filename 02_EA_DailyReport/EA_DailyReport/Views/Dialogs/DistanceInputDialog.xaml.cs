using System;
using System.Globalization;
using System.Windows;

namespace EA_DailyReport.Views.Dialogs
{
    /// <summary>
    /// 距離入力ダイアログ（v0.1.13 新規実装）
    ///
    /// ▼ 概要：
    /// InputDialog の MAP ボタンから呼び出されるサブダイアログ。
    /// Google Maps 起動と同時に表示され、ユーザーが地図で確認した距離を
    /// 整数 km で入力する。OK で InputDialog 側の txt_distance に転記される。
    ///
    /// ▼ 仕様（v0.1.13 で確定）：
    /// ・MAP ボタン押下 → Google Maps をブラウザで起動 → 直後に本ダイアログを表示
    /// ・初期値として呼出元の現在の距離を表示（コンストラクタ引数で受け取る）
    /// ・OK 時：入力値を整数 km に四捨五入（Math.Round + AwayFromZero）して
    ///   entered_distance プロパティに格納し、DialogResult=true で閉じる
    /// ・キャンセル時：entered_distance は null のまま、DialogResult=false で閉じる
    /// ・呼出元は entered_distance.HasValue で判定して txt_distance を上書き
    ///
    /// ▼ 入力検証：
    /// ・空欄 → MessageBox で警告
    /// ・数値以外 → MessageBox で警告
    /// ・負値 → MessageBox で警告
    /// ・1000 km 超 → MessageBox で警告（実務上ありえないため誤入力防止）
    ///
    /// ▼ 四捨五入の根拠：
    /// ・C# 標準の Math.Round(double) は MidpointRounding.ToEven（銀行家の丸め）で
    ///   0.5 → 0 になるため、日本の通常の四捨五入（0.5 → 1）にするには
    ///   MidpointRounding.AwayFromZero を明示する必要がある
    ///
    /// ▼ 将来拡張（Sprint 3 以降予定）：
    /// ・Google Routes API による距離自動取得を実装した場合、本ダイアログは
    ///   「API 取得結果の確認ダイアログ」として再利用可能（初期値に API 値を表示）
    /// </summary>
    public partial class DistanceInputDialog : Window
    {
        // ════════════════════════════════════════════════
        // ===== 公開プロパティ ============================
        // ════════════════════════════════════════════════

        /// <summary>
        /// 入力された距離（整数 km）。
        /// OK で閉じたとき → 整数値がセットされる。
        /// キャンセルで閉じたとき → null のまま。
        /// </summary>
        public int? entered_distance { get; private set; }

        // ════════════════════════════════════════════════
        // ===== コンストラクタ ============================
        // ════════════════════════════════════════════════

        /// <summary>
        /// デフォルトコンストラクタ（初期値なし）
        /// </summary>
        public DistanceInputDialog() : this("") { }

        /// <summary>
        /// 初期値を指定するコンストラクタ
        /// </summary>
        /// <param name="initial_distance">
        /// 呼出元の現在の距離（txt_distance.Text）。
        /// 数値以外（"0" 含む）はそのまま初期表示し、ユーザーが上書きする想定。
        /// 空文字の場合は空欄スタート。
        /// </param>
        public DistanceInputDialog(string initial_distance)
        {
            InitializeComponent();
            txt_distance_input.Text = initial_distance ?? "";
            Loaded += on_loaded;
        }

        /// <summary>
        /// Window Loaded：入力欄にフォーカス + 全選択（既存値の上書きを楽に）
        /// </summary>
        private void on_loaded(object sender, RoutedEventArgs e)
        {
            txt_distance_input.Focus();
            txt_distance_input.SelectAll();
        }

        // ════════════════════════════════════════════════
        // ===== OK ボタン =================================
        // ════════════════════════════════════════════════

        /// <summary>
        /// 「OK」ボタンクリック：入力値を検証 → 整数 km に四捨五入 → 閉じる
        ///
        /// ▼ 検証順序：
        /// 1. 空欄チェック
        /// 2. 数値変換チェック（double.TryParse）
        /// 3. 負値チェック
        /// 4. 上限チェック（1000 km）
        /// 5. 整数 km に四捨五入（AwayFromZero）
        /// </summary>
        private void btn_ok_Click(object sender, RoutedEventArgs e)
        {
            string input = txt_distance_input.Text?.Trim() ?? "";

            // ----- 1. 空欄チェック -----
            if (string.IsNullOrWhiteSpace(input))
            {
                MessageBox.Show(
                    "距離を入力してください。",
                    "入力エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txt_distance_input.Focus();
                return;
            }

            // ----- 2. 数値変換チェック -----
            // CultureInfo.InvariantCulture で「12.7」を確実にパース
            // （ロケールによっては "," が小数点になるため）
            if (!double.TryParse(input,
                                 NumberStyles.Float,
                                 CultureInfo.InvariantCulture,
                                 out double distance_km))
            {
                MessageBox.Show(
                    "数値で入力してください。\n例：13、12.7 など",
                    "入力エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txt_distance_input.Focus();
                txt_distance_input.SelectAll();
                return;
            }

            // ----- 3. 負値チェック -----
            if (distance_km < 0)
            {
                MessageBox.Show(
                    "0 以上の値を入力してください。",
                    "入力エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txt_distance_input.Focus();
                txt_distance_input.SelectAll();
                return;
            }

            // ----- 4. 上限チェック（誤入力防止：実務上 1000 km 超は通常ありえない）-----
            if (distance_km > 1000)
            {
                MessageBox.Show(
                    "距離が大きすぎます（上限：1000 km）。\n入力値を確認してください。",
                    "入力エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txt_distance_input.Focus();
                txt_distance_input.SelectAll();
                return;
            }

            // ----- 5. 整数 km に四捨五入（日本式の 0.5 → 1） -----
            // C# 標準の Math.Round(double) は銀行家の丸め（0.5 → 0）になるため
            // MidpointRounding.AwayFromZero を明示する必要がある
            int rounded_km = (int)Math.Round(distance_km, MidpointRounding.AwayFromZero);

            // ----- 6. 結果を格納して閉じる -----
            entered_distance = rounded_km;
            DialogResult = true;
            Close();
        }

        // ════════════════════════════════════════════════
        // ===== キャンセルボタン ==========================
        // ════════════════════════════════════════════════

        /// <summary>
        /// 「キャンセル」ボタンクリック：何も変更せずに閉じる
        /// （呼出元は entered_distance == null で判定し、txt_distance は変更しない）
        ///
        /// ▼ 注意：xaml の IsCancel="True" 設定により Esc キー押下でも本ハンドラが
        ///   呼ばれずに DialogResult=false で閉じられるため、ここでの明示的な
        ///   DialogResult 設定は冗長だが、Click 経由でも確実に閉じるために残す。
        /// </summary>
        private void btn_cancel_Click(object sender, RoutedEventArgs e)
        {
            entered_distance = null;
            DialogResult = false;
            Close();
        }
    }
}
