using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EA_CostManager.Views
{
    /// <summary>
    /// ▼ 追加（v1.0.2）：起動時スプラッシュウィンドウ
    ///
    /// 【目的】
    /// ・起動処理（DB初期化・NAS接続・マイグレーション等）に数十秒かかる場合に
    ///   ユーザーへ「処理中であること」を可視化する
    /// ・「何も起きない」とユーザーが誤認してプロセスを多重起動するのを防ぐ
    ///
    /// 【使い方】
    /// ・App.OnStartup の冒頭で Show() し、最後に Close() する
    /// ・起動の各段階で update_status("...") を呼んでテキストを更新する
    ///
    /// 【注意点】
    /// ・update_status はバックグラウンドスレッドから呼ばれた場合も
    ///   Dispatcher 経由で安全に UI 更新する
    /// ・UI 強制再描画のため、テキスト更新後に DispatcherPriority.Render
    ///   レベルの空 Invoke を実行する
    /// </summary>
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();

            // ▼ 修正（v1.0.2 ハードコード除去）：バージョン番号のみ動的にセット
            //
            // 【変更の理由】
            // 以前の実装：
            //   version_text.Text = $"v{AppVersionInfo.CURRENT_VERSION}  /  業務用 原価集計ツール";
            //   → サブタイトル文字列（"業務用 原価集計ツール"）が xaml.cs にハードコード
            //   → xaml 側でサブタイトルを書き換えても、起動時に xaml.cs が上書きするため反映されない
            //
            // 新しい実装：
            //   xaml 側の version_text 内に Run を2つ配置する設計に変更：
            //     ・1つ目の Run : x:Name="version_number"（コードビハインドから上書き）
            //     ・2つ目の Run : サブタイトル文字列（xaml で完結・yori 編集領域）
            //   xaml.cs では version_number.Text のみ書き換えるため、
            //   xaml で定義したサブタイトル文字列を尊重できる。
            //
            // 【バージョン番号の取得元】
            // AppVersionInfo.CURRENT_VERSION は csproj の <Version> から自動取得。
            // csproj の <Version> を更新するだけで、全画面・全表示箇所に反映される
            // （MainWindow も同じ仕組みでバージョン表示を自動連動させている）。
            try
            {
                version_number.Text = $"v{AppVersionInfo.CURRENT_VERSION}";
            }
            catch
            {
                // 取得失敗時は xaml の初期値（例: "v?.?.?"）のまま表示される
            }

            // ▼ 追加（v1.0.2）：アイコンを実行ディレクトリから動的に読み込む
            // 【背景】
            // EA_CostManager.csproj では icon_fix.ico を以下のように扱っている：
            //   <None Include="..\icon_set\icon_fix.ico">
            //     <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
            //   </None>
            // これにより、ビルド後の出力ディレクトリ（exe と同じ場所）に
            // icon_fix.ico がコピーされる。pack URI ではなく、ファイルシステム
            // から実行時に読み込む方式を採用することで csproj 変更を回避する。
            //
            // 【PublishSingleFile への対応】
            // PublishSingleFile=true でも、CopyToOutputDirectory のファイルは
            // exe の隣に配置されるため、AppDomain.CurrentDomain.BaseDirectory
            // から相対パスで参照可能。
            //
            // 【ぼやけ対策（v1.0.2 第三弾）】
            // 第一弾（BitmapScalingMode=Fant 等）、第二弾（DecodePixelWidth=160）
            // を試しても改善しなかった真の原因が判明：
            //   ・icon_fix.ico は 16/32/48/64/128/256 の6解像度を含む
            //   ・BitmapImage で .ico を開くと、WPF が内部でサブ画像を選択するが、
            //     ICO 内で「最初」に並んでいる 16x16 を選ぶケースがある
            //   ・DecodePixelWidth は「デコード後のサイズ」指定であり、
            //     「どのサブ画像を選ぶか」を制御しない
            //   ・結果、16x16 を 160 にデコード（10倍拡大）= 強いボケ
            //
            // 【▼ 修正】BitmapImage → IconBitmapDecoder に変更
            // ・IconBitmapDecoder は ICO 専用デコーダーで、Frames プロパティから
            //   全サブ画像（BitmapFrame のコレクション）にアクセスできる
            // ・PixelWidth で降順ソートして最大解像度（256x256）を明示的に選択
            // ・DecodePixelWidth は使わない（フル解像度のまま Image 要素に渡し、
            //   Image 側の Width=80, BitmapScalingMode=Fant で縮小描画させる）
            // ・Image の縮小は GPU レンダリング時に Fant 補間で実行され、
            //   サブ画像選択ミスによる拡大ボケが完全に排除される
            //
            // 【フォールバック】
            // ファイルが見つからない / 読込失敗時はアイコン領域だけ非表示にする。
            // スプラッシュ自体は表示されるため起動には影響なし。
            try
            {
                string exe_dir = AppDomain.CurrentDomain.BaseDirectory;
                string icon_path = Path.Combine(exe_dir, "icon_fix.ico");

                if (File.Exists(icon_path))
                {
                    // IconBitmapDecoder で ICO の全サブ画像を取得
                    // BitmapCacheOption.OnLoad でファイルロックを残さない
                    var decoder = new IconBitmapDecoder(
                        new Uri(icon_path, UriKind.Absolute),
                        BitmapCreateOptions.None,
                        BitmapCacheOption.OnLoad);

                    // 最大解像度のサブ画像を明示的に選択
                    // icon_fix.ico は 256x256 を含むため、それが選ばれる
                    // （256 が無い場合は次に大きい 128 → 64 → ... と自動フォールバック）
                    var frame = decoder.Frames
                        .OrderByDescending(f => f.PixelWidth)
                        .FirstOrDefault();

                    if (frame != null)
                    {
                        // Freeze によりクロススレッド安全化（メインスレッド以外からも参照可能）
                        if (frame.CanFreeze && !frame.IsFrozen) frame.Freeze();
                        app_icon.Source = frame;
                    }
                    else
                    {
                        // サブ画像が1つも無い異常ケース（破損 ICO 等）
                        // ▼ 追加 [v1.0.3] 異常ケースをDebug出力に記録
                        //   旧実装は静かに非表示にしていたため、
                        //   「映ってたのに急に表示されなくなる人がいる」現象の原因追跡ができなかった。
                        System.Diagnostics.Debug.WriteLine(
                            $"[SplashWindow] icon_fix.ico にサブ画像が1つも無い（破損の可能性）: {icon_path}");
                        app_icon.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    // ▼ 追加 [v1.0.3] アイコンファイル不在をDebug出力に記録
                    //   インストール不備・アンチウイルスによる削除・PublishSingleFile展開失敗
                    //   などのケースで発生する可能性がある。
                    System.Diagnostics.Debug.WriteLine(
                        $"[SplashWindow] icon_fix.ico が見つかりません: {icon_path}");

                    // アイコンファイルが見つからない場合は非表示にする
                    app_icon.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                // ▼ 修正 [v1.0.3] catch{} の握りつぶしを廃止
                //   旧実装は例外内容を完全に隠していたため、
                //   IconBitmapDecoder が稀に投げる例外（ファイル権限・IOロック・破損ICO等）の
                //   原因追跡ができなかった。Debug出力に例外型とメッセージを記録する。
                System.Diagnostics.Debug.WriteLine(
                    $"[SplashWindow] アイコン読み込み例外: {ex.GetType().Name} : {ex.Message}");

                // 読込失敗時はアイコン領域を非表示にする
                // スプラッシュ全体の動作には影響させない
                try { app_icon.Visibility = Visibility.Collapsed; } catch { }
            }
        }

        /// <summary>
        /// ステータステキストを更新する
        /// ・UIスレッド／別スレッドのどちらから呼んでも安全
        /// ・更新後に強制再描画して、起動処理中でも画面が固まらないようにする
        /// </summary>
        /// <param name="text">表示するステータステキスト（例：「DB初期化中...」）</param>
        public void update_status(string text)
        {
            Action update_action = () =>
            {
                try
                {
                    status_text.Text = text;
                    // ▼ UI 強制再描画
                    // 起動処理は同期的に走るため、明示的に Dispatcher を回さないと
                    // ステータス変更がユーザーに見えないまま次の処理に進んでしまう
                    Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                }
                catch { /* スプラッシュ未表示等の例外は無視 */ }
            };

            if (Dispatcher.CheckAccess())
            {
                update_action();
            }
            else
            {
                Dispatcher.Invoke(update_action);
            }
        }
    }
}