using System;
using System.IO;
using System.Text.Json;

namespace EA_CostManager.Data
{
    /// <summary>
    /// ▼ 追加 [Sprint 8 / Phase 0]：NAS 書込ロック（つなぎの安全策・一時的）
    ///
    /// 【位置づけ】
    /// 現状は全PCが NAS 上の単一 SQLite を直接開いて読み書きしており、
    /// 2台が同時に書くと DB が破損する。これを防ぐため、
    /// 「NAS 上のロックファイルを1台だけが保持し、他は読取専用で開く」方式を入れる。
    ///
    /// ⚠️ これは最終設計の一部ではない。ローカルDBレプリカ＋変更ファイル共有（Phase 2）が
    ///    完成したら、各PCは自分のローカルを書くため同時編集が可能になり、本ロックは撤去する。
    ///
    /// 【なぜ SQLite 自身のロックを使わないか】
    /// SMB（ネットワーク共有）上では SQLite のファイルロック／WAL の共有メモリが不安定なため、
    /// アプリ側で独自のロックファイルを持つ。書きかけを読ませないよう .tmp→rename で原子的に更新する。
    /// （DIMS 設計書の受け渡しファイルと同じ考え方）
    /// </summary>
    public static class NasWriteLock
    {
        /// <summary>
        /// ロックが陳腐化したとみなす分数。既存のセッション掃除（5分でinactive化）と同じ基準にそろえる。
        /// この分数を超えて heartbeat が更新されていないロックは、異常終了したPCの残骸とみなして引き継ぐ。
        /// </summary>
        public const int STALE_MINUTES = 5;

        /// <summary>ロックファイルの1件分の内容（JSONで保存）</summary>
        public sealed class lock_info
        {
            public string device_id { get; set; } = "";     // このPCの識別子（MACアドレス）
            public string pc_name { get; set; } = "";
            public string user_name { get; set; } = "";
            public string acquired_at { get; set; } = "";    // 取得時刻（ISO8601・ローカル）
            public string heartbeat_at { get; set; } = "";   // 最終更新時刻（ISO8601・ローカル）
        }

        // このPCがロックを保持しているか（保持中のみ heartbeat 更新・解放を行う）
        private static bool _held;
        private static string _lock_path = "";

        /// <summary>ロックファイルのパス（NAS DB の隣に「&lt;db&gt;.lock」）</summary>
        private static string get_lock_path()
        {
            return database_manager.get_db_path() + ".lock";
        }

        /// <summary>このPCの識別子（MACアドレス）</summary>
        private static string device_id() => UserSession.fetch_mac_address();

        /// <summary>
        /// ロックの取得を試みる。
        /// </summary>
        /// <param name="holder">
        /// 取得できなかった場合に、現在ロックを保持している相手の表示名（ユーザー名/PC名）を返す。
        /// </param>
        /// <returns>
        /// true  … このPCが書込可能（ロックを保持した／自分の残骸を引き継いだ／陳腐化を奪取した）。<br/>
        /// false … 他の生きているPCが保持中。このPCは読取専用で開くべき。
        /// </returns>
        public static bool try_acquire(out string holder)
        {
            holder = "";
            _lock_path = get_lock_path();

            try
            {
                var existing = read_lock(_lock_path);

                if (existing != null && !is_stale(existing))
                {
                    // 生きているロックが存在する
                    if (string.Equals(existing.device_id, device_id(), StringComparison.OrdinalIgnoreCase))
                    {
                        // 自分自身の残骸（前回の異常終了など）→ そのまま引き継ぐ
                        write_lock(_lock_path, make_info());
                        _held = true;
                        return true;
                    }

                    // 他PCが保持中 → このPCは読取専用
                    holder = !string.IsNullOrWhiteSpace(existing.user_name)
                        ? existing.user_name
                        : (!string.IsNullOrWhiteSpace(existing.pc_name) ? existing.pc_name : existing.device_id);
                    _held = false;
                    return false;
                }

                // ロックが無い or 陳腐化 → 取得する
                write_lock(_lock_path, make_info());
                _held = true;
                return true;
            }
            catch (Exception ex)
            {
                // ▼ 失敗時の方針：フェイルセーフ＝書込可能（読取専用にはしない）。
                //   理由：ロックファイルは NAS DB と同じフォルダにあるため、ロックI/Oが失敗する状況では
                //   DB自体も到達不能で、そもそも書込が起きない可能性が高い。
                //   ここで読取専用に倒すと、一時的なI/O不調で編集不能になり実害が大きい。
                //   （Phase 0 導入前の挙動＝常に書込可能、に合わせる）
                System.Diagnostics.Debug.WriteLine($"NasWriteLock.try_acquire 失敗（書込可能として続行）: {ex.Message}");
                _held = true;
                return true;
            }
        }

        /// <summary>
        /// heartbeat を更新する（保持中のみ）。既存の2分タイマーから呼ぶ想定。
        /// 取得時点ではユーザー名が未確定（ログイン前）のことがあるため、確定後の名前をここで反映する。
        /// </summary>
        public static void heartbeat()
        {
            if (!_held) return;
            try
            {
                write_lock(_lock_path, make_info());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"NasWriteLock.heartbeat 失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// ロックを解放する（保持中のみ）。終了時に呼ぶ。ベストエフォート（失敗しても無視）。
        /// 解放漏れが起きても、次の起動が陳腐化（5分）として引き継ぐため致命的ではない。
        /// </summary>
        public static void release()
        {
            if (!_held) return;
            try
            {
                if (File.Exists(_lock_path)) File.Delete(_lock_path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"NasWriteLock.release 失敗: {ex.Message}");
            }
            finally
            {
                _held = false;
            }
        }

        /// <summary>このPCが現在ロックを保持しているか</summary>
        public static bool is_held => _held;

        // ---- 内部処理 ----

        private static lock_info make_info()
        {
            string now = DateTime.Now.ToString("o");   // ISO8601（ラウンドトリップ）
            return new lock_info
            {
                device_id = device_id(),
                pc_name = UserSession.fetch_pc_name(),
                user_name = UserSession.user_name,   // 取得時は "未設定" のこともある（heartbeat で更新される）
                acquired_at = now,
                heartbeat_at = now,
            };
        }

        private static bool is_stale(lock_info info)
        {
            // heartbeat_at が解釈できない場合は、壊れた/古い形式とみなして陳腐化（＝引き継ぎ可）とする
            if (!DateTime.TryParse(info.heartbeat_at, out var hb)) return true;

            // ※ PC間の時計ズレがあると誤判定しうる（DIMS 設計書でも指摘の論点）。
            //   5分という粗い窓なので通常は問題にならない。厳密化は将来の課題。
            return (DateTime.Now - hb).TotalMinutes > STALE_MINUTES;
        }

        private static lock_info? read_lock(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<lock_info>(json);
            }
            catch
            {
                // 読み取り不能・壊れている → 「ロック無し」と同等に扱い、呼び出し側で取得を試みさせる
                return null;
            }
        }

        private static void write_lock(string path, lock_info info)
        {
            // 書きかけを他PCに読ませないよう、一時ファイルに書いてから rename で確定する
            string tmp = path + ".tmp";
            string json = JsonSerializer.Serialize(info);
            File.WriteAllText(tmp, json);
            // .NET Core 3.0+ の File.Move(overwrite:true) は上書きリネーム
            File.Move(tmp, path, overwrite: true);
        }
    }
}
