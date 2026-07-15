using System;
using System.Collections.Generic;
using System.Linq;

namespace EA_CostManager.Services
{
    /// <summary>
    /// ▼ 追加 [Sprint 9A]：大区分（会社）の構築・判定を共通化するサービス
    ///
    /// 【目的】
    /// これまで大区分（EA / RD / SM / その他）は cost_view_model 内にハードコードされており、
    /// 工数表など他画面から再利用できなかった。本サービスに切り出すことで、
    /// 原価集計・工数表の双方が同一の区分定義を共有する。
    /// 　※ 選択状態（どの大区分を選んでいるか）は各画面のViewModelが独自に保持するため、
    /// 　　 原価集計と工数表で別々の大区分を表示できる。
    ///
    /// 【Sprint 9A の方針：現状踏襲】
    /// 区分は MAIN_PREFIXES（EA / RD / SM）で固定し、それ以外はすべて「その他」に入れる。
    /// これは現在の cost_view_model の挙動と完全に同一であり、9A の時点で
    /// 原価集計と工数表の大区分構成が食い違わないようにするための措置。
    ///
    /// 【Sprint 9B での拡張予定】
    /// 現場コードの接頭辞を自動抽出し、一定件数（MIN_COUNT_FOR_GROUP）以上の接頭辞を
    /// 自動的に独立区分にする方式へ切り替える（build_groups_auto を有効化）。
    /// これにより新しい会社の現場が日報から入っても、区分タブが自動で生える。
    /// 併せて手動での区分割り当て（自動判定の上書き）を追加する予定。
    /// </summary>
    public static class ProjectGroupService
    {
        /// <summary>
        /// メイン区分（Sprint 9A：現状踏襲のため固定）。
        /// ※ 9B で自動抽出に切り替える際は、この配列への依存を build_groups_auto 側へ移す。
        /// </summary>
        private static readonly string[] MAIN_PREFIXES = { "EA", "RD", "SM" };

        /// <summary>「その他」区分を表す予約 prefix 値（ラベルではなく判定キーとして使う）</summary>
        public const string PREFIX_OTHER = "OTHER";

        /// <summary>「すべて」区分を表す予約 prefix 値（空文字＝フィルタなし）</summary>
        public const string PREFIX_ALL = "";

        /// <summary>
        /// ▼ Sprint 9B 用：接頭辞を独立区分にするための最小件数。
        /// 現在は build_groups_auto 内でのみ使用（9A の通常フローでは未使用）。
        /// </summary>
        public const int MIN_COUNT_FOR_GROUP = 3;

        /// <summary>
        /// 大区分の一覧を構築する（Sprint 9A：現状踏襲＝EA/RD/SM＋その他）。
        /// 件数0の区分は表示しない。先頭は必ず「すべて」。
        /// </summary>
        /// <param name="category_codes">対象となる現場の区分コード一覧（結合済みの親タブのみを渡すこと）</param>
        /// <returns>(label, prefix, count) のリスト。呼び出し側で project_group_item に変換する</returns>
        public static List<(string label, string prefix, int count)> build_groups(
            IEnumerable<string> category_codes)
        {
            var codes = category_codes?.ToList() ?? new List<string>();
            var result = new List<(string, string, int)>();

            // 「すべて」は常に先頭。件数は全現場数
            result.Add(("すべて", PREFIX_ALL, codes.Count));

            // メイン区分（EA / RD / SM）。0件の区分はタブを出さない
            foreach (var pfx in MAIN_PREFIXES)
            {
                int cnt = codes.Count(c => starts_with_prefix(c, pfx));
                if (cnt == 0) continue;
                result.Add((pfx, pfx, cnt));
            }

            // どのメイン区分にも該当しないものは「その他」へ
            int other_cnt = codes.Count(c => !MAIN_PREFIXES.Any(p => starts_with_prefix(c, p)));
            if (other_cnt > 0)
                result.Add(("その他", PREFIX_OTHER, other_cnt));

            return result;
        }

        /// <summary>
        /// 指定された現場が、選択中の大区分に属するかを判定する。
        /// build_groups と判定基準を必ず一致させるため、フィルタ処理は必ず本メソッドを使うこと。
        /// </summary>
        /// <param name="category_code">現場の区分コード</param>
        /// <param name="group_prefix">選択中の大区分の prefix（"" ＝すべて／"OTHER" ＝その他）</param>
        public static bool matches(string category_code, string group_prefix)
        {
            // 「すべて」（prefix が空）は無条件で通す
            if (string.IsNullOrEmpty(group_prefix)) return true;

            // 「その他」＝メイン区分のいずれにも該当しないもの
            if (group_prefix == PREFIX_OTHER)
                return !MAIN_PREFIXES.Any(p => starts_with_prefix(category_code, p));

            // 通常の区分＝接頭辞一致
            return starts_with_prefix(category_code, group_prefix);
        }

        /// <summary>区分コードが指定の接頭辞で始まるか（大文字小文字は無視）</summary>
        private static bool starts_with_prefix(string? category_code, string prefix)
        {
            if (string.IsNullOrEmpty(category_code)) return false;
            return category_code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        // ▼ 以下は Sprint 9B 用の実装（9A では呼び出さない）
        // ============================================================

        /// <summary>
        /// ▼ Sprint 9B 用：現場コードの接頭辞を自動抽出して大区分を構築する。
        ///
        /// ・接頭辞＝コード先頭の連続するアルファベット（例：EA45 → "EA"、VT01 → "VT"）
        /// ・MIN_COUNT_FOR_GROUP 件以上ある接頭辞のみ独立区分にする
        /// ・それ未満の接頭辞、および接頭辞を持たないコード（日本語コード等）は「その他」へ
        /// ・並び順は件数の多い順（よく使う区分が左に来る）
        ///
        /// ※ 9A 時点では build_groups（固定 EA/RD/SM）を使用しており、本メソッドは未使用。
        /// 　 9B で原価集計・工数表の双方を本メソッドに切り替える。
        /// 　 切り替え時は matches 側も接頭辞自動抽出に対応させる必要がある点に注意。
        /// </summary>
        public static List<(string label, string prefix, int count)> build_groups_auto(
            IEnumerable<string> category_codes)
        {
            var codes = category_codes?.ToList() ?? new List<string>();
            var result = new List<(string, string, int)>();

            result.Add(("すべて", PREFIX_ALL, codes.Count));

            // 接頭辞ごとに件数を集計
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in codes)
            {
                string pfx = extract_prefix(c);
                if (pfx.Length == 0) continue; // 接頭辞なし（日本語コード等）は「その他」扱い
                counts[pfx] = counts.TryGetValue(pfx, out int n) ? n + 1 : 1;
            }

            // 一定件数以上の接頭辞のみ独立区分にする（件数の多い順）
            var mains = counts
                .Where(kv => kv.Value >= MIN_COUNT_FOR_GROUP)
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var kv in mains)
                result.Add((kv.Key, kv.Key, kv.Value));

            // 独立区分にならなかったもの＝「その他」
            var main_keys = new HashSet<string>(mains.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
            int other_cnt = codes.Count(c =>
            {
                string pfx = extract_prefix(c);
                return pfx.Length == 0 || !main_keys.Contains(pfx);
            });
            if (other_cnt > 0)
                result.Add(("その他", PREFIX_OTHER, other_cnt));

            return result;
        }

        /// <summary>
        /// ▼ Sprint 9B 用：区分コードから接頭辞（先頭の連続するアルファベット）を抽出する。
        /// 例：EA45 → "EA"、VT01 → "VT"、事務 → ""（接頭辞なし）
        /// </summary>
        public static string extract_prefix(string? category_code)
        {
            if (string.IsNullOrEmpty(category_code)) return "";
            int i = 0;
            while (i < category_code.Length && char.IsLetter(category_code[i]) && category_code[i] < 128)
                i++;
            return category_code.Substring(0, i).ToUpperInvariant();
        }
    }
}
