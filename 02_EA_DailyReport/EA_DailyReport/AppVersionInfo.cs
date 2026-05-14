using System.Reflection;

namespace EA_DailyReport
{
    /// <summary>
    /// アプリのバージョン情報（v0.1.4：AssemblyVersion 動的取得方式）
    /// 設計書 10.6 に従い、csproj の &lt;Version&gt; と自動連動させる
    /// NAS の version.txt と比較してアップデートを検知する
    /// </summary>
    public static class AppVersionInfo
    {
        /// <summary>
        /// ▼ 修正：v0.1.4 設計書 10.6
        /// 現在のバージョン（Major.Minor.Patch）
        /// const ハードコードを廃止し、AssemblyVersion から動的取得する
        /// csproj の &lt;Version&gt; と自動連動するため、リリース時の更新忘れを防止できる
        ///
        /// 注意：const から static プロパティに変更したため、
        /// switch case / 属性引数 / デフォルト引数 では使用不可になる
        /// 通常の値参照（タイトルバー等）は問題なく動作する
        /// </summary>
        public static string CURRENT_VERSION =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        /// <summary>アプリ表示名</summary>
        public const string APP_NAME = "EA_DailyReport";

        /// <summary>
        /// ▼ 修正：NAS のアップデートフォルダパス
        /// CostManager 用 "01_Cost Manager" → 日報ソフト用 "02_DailyReport" に変更
        /// 設計書 9 章のパス定義（バックアップ：02_Updates\02_DailyReport）に合わせる
        /// </summary>
        public const string NAS_UPDATE_DIR =
            @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\02_Updates\02_DailyReport";

        /// <summary>NAS のバージョンファイルパス</summary>
        public static string NAS_VERSION_FILE =>
            System.IO.Path.Combine(NAS_UPDATE_DIR, "01_version.txt");

        /// <summary>
        /// バージョン文字列を比較して newer が current より新しいか判定する
        /// 書式：Major.Minor.Patch（例：1.0.0）
        /// </summary>
        public static bool is_newer(string current, string newer)
        {
            try
            {
                var c = System.Version.Parse(current);
                var n = System.Version.Parse(newer);
                return n > c;
            }
            catch
            {
                return false;
            }
        }
    }
}