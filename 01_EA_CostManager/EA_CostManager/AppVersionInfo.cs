namespace EA_CostManager
{
    /// <summary>
    /// [Sprint 6] アプリのバージョン情報
    ///
    /// ▼ v1.0.0 修正：CURRENT_VERSION を動的取得に変更
    ///   旧：const string CURRENT_VERSION = "0.9.7";（ハードコード・更新忘れの原因）
    ///   新：AssemblyVersion から自動取得（csproj の <Version> から自動連動）
    ///
    ///   この修正により、csproj の <Version> を上げるだけで全ての参照箇所
    ///   （Dashboard_view_model のバナー、MainWindow.xaml.cs のヘッダーバナー）
    ///   のバージョン表示が自動更新される。
    ///   バージョン文字列の更新忘れによる「アップデート後もバナーが消えない」
    ///   バグを根本解決する。
    ///
    ///   参照側のコード変更は不要：
    ///   ・const → static プロパティへの変更だが、参照は AppVersionInfo.CURRENT_VERSION
    ///     のままで動作する（C# はコンパイル時に自動解決）
    /// </summary>
    public static class AppVersionInfo
    {
        // ▼ v1.0.0 修正：const → 動的取得プロパティに変更
        //   AssemblyVersion（csproj の <AssemblyVersion>）から取得
        //   ToString(3) で前から3桁表示（例：1.0.0）→ 末尾の .0 を非表示
        /// <summary>現在のバージョン（Major.Minor.Patch）。AssemblyVersion から動的取得</summary>
        public static string CURRENT_VERSION =>
            System.Reflection.Assembly.GetExecutingAssembly()
                  .GetName().Version!.ToString(3);

        /// <summary>アプリ表示名</summary>
        public const string APP_NAME = "EA_CostManager";

        /// <summary>
        /// NASのアップデートフォルダパス
        /// version.txt と最新インストーラーをここに配置する
        /// </summary>
        public const string NAS_UPDATE_DIR =
            @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\02_Updates\01_Cost Manager";

        /// <summary>NASのバージョンファイルパス</summary>
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