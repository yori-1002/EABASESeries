using System.Windows;

namespace EA_CostManager
{
    /// <summary>
    /// ▼ 追加 [Sprint 8 / Phase 0]：読取専用モード時に、ユーザー操作起点の保存を止めるUIガード。
    ///
    /// 【使い方】保存/変更を行うボタンClick・Commandの先頭で：
    ///   <code>if (ReadOnlyGuard.block_if_read_only()) return;</code>
    /// 読取専用なら敬語メッセージを出して true（＝中断すべき）を返す。
    ///
    /// これは「利用者に分かりやすく止める」層。万一この配線を取りこぼした保存経路があっても、
    /// サービス層の <see cref="Data.database_manager.ensure_writable"/>（例外を投げる hard backstop）で
    /// 二重に止める（codex 指摘の二重化）。完全な網羅は Phase 1 の書込層集約で担保する。
    ///
    /// ⚠️ Phase 0（つなぎの安全策）専用。ローカル保存方式（Phase 2）完成後は撤去する。
    /// </summary>
    public static class ReadOnlyGuard
    {
        /// <summary>
        /// 読取専用なら敬語メッセージを表示して true を返す（呼び出し側は return して処理を中断する）。
        /// 書込可能なら false を返す（処理を続行してよい）。
        /// </summary>
        public static bool block_if_read_only(Window? owner = null)
        {
            if (!UserSession.is_read_only) return false;

            string reason = string.IsNullOrWhiteSpace(UserSession.read_only_reason)
                ? "他の利用者が編集中のため" : UserSession.read_only_reason;

            string msg = $"{reason}、現在は閲覧のみのモードで開いております。\n" +
                         "保存・変更はできません。\n\n" +
                         "編集するには、他の利用者が終了した後にアプリを起動し直してください。";

            if (owner != null)
                MessageBox.Show(owner, msg, "閲覧のみのモード", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(msg, "閲覧のみのモード", MessageBoxButton.OK, MessageBoxImage.Information);

            return true;
        }
    }
}
