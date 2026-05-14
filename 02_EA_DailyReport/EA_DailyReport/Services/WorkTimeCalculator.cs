using System;

namespace EA_DailyReport.Services
{
    /// <summary>
    /// 出退勤時刻から早朝・残業・深夜の分数を計算する純ロジック
    /// （v0.1.4 設計書 3.1・要件定義書 3.2）
    ///
    /// 早朝：8:00 より前の出勤時間分
    /// 残業：定時終了後の超過分（日本：17:30 以降、ベトナム：16:30 以降）
    /// 深夜：22:00 以降の時間分（残業とは重複しない）
    ///
    /// DB 依存なし・純ロジックのため単体テストが容易
    /// </summary>
    public static class WorkTimeCalculator
    {
        // ─── 定時定義（要件定義書 3.2） ───

        /// <summary>日本の定時開始（8:30）</summary>
        public static readonly TimeSpan JAPAN_WORK_START = new TimeSpan(8, 30, 0);

        /// <summary>日本の定時終了（17:30）</summary>
        public static readonly TimeSpan JAPAN_WORK_END = new TimeSpan(17, 30, 0);

        /// <summary>ベトナムの定時開始（7:30）</summary>
        public static readonly TimeSpan VIETNAM_WORK_START = new TimeSpan(7, 30, 0);

        /// <summary>ベトナムの定時終了（16:30）</summary>
        public static readonly TimeSpan VIETNAM_WORK_END = new TimeSpan(16, 30, 0);

        /// <summary>早朝判定の閾値（8:00 より前）</summary>
        public static readonly TimeSpan EARLY_MORNING_THRESHOLD = new TimeSpan(8, 0, 0);

        /// <summary>深夜判定の閾値（22:00 以降）</summary>
        public static readonly TimeSpan LATE_NIGHT_THRESHOLD = new TimeSpan(22, 0, 0);

        /// <summary>計算結果（分単位）</summary>
        public class CalcResult
        {
            public int early_morning_min { get; set; }
            public int overtime_min { get; set; }
            public int late_night_min { get; set; }
        }

        /// <summary>
        /// 出退勤時刻から早朝・残業・深夜の分数を計算する
        ///
        /// 計算ロジック：
        ///   1. 早朝 = max(0, min(退勤, 8:00) - 出勤)（出勤が 8:00 前のとき）
        ///   2. 残業 = max(0, min(退勤, 22:00) - max(出勤, 定時終了))
        ///   3. 深夜 = max(0, 退勤 - max(出勤, 22:00))
        /// </summary>
        /// <param name="clock_in">出勤時刻</param>
        /// <param name="clock_out">退勤時刻</param>
        /// <param name="work_end">定時終了時刻（省略時：日本 17:30）</param>
        /// <returns>計算結果（早朝・残業・深夜の分数）</returns>
        public static CalcResult calculate(
            TimeSpan clock_in, TimeSpan clock_out, TimeSpan? work_end = null)
        {
            var work_end_time = work_end ?? JAPAN_WORK_END;
            var result = new CalcResult();

            // 退勤が出勤以下の場合は計算不可（不正な入力 → 全て 0 で返す）
            if (clock_out <= clock_in)
                return result;

            // ─── 早朝（8:00 より前の出勤時間分） ───
            // 出勤が 8:00 前のときのみ発生
            if (clock_in < EARLY_MORNING_THRESHOLD)
            {
                var early_end = clock_out < EARLY_MORNING_THRESHOLD
                    ? clock_out
                    : EARLY_MORNING_THRESHOLD;
                result.early_morning_min = (int)(early_end - clock_in).TotalMinutes;
            }

            // ─── 残業（定時終了以降・22:00 まで） ───
            // 22:00 以降は深夜にカウントするため除外
            if (clock_out > work_end_time)
            {
                var overtime_start = clock_in > work_end_time ? clock_in : work_end_time;
                var overtime_end = clock_out > LATE_NIGHT_THRESHOLD
                    ? LATE_NIGHT_THRESHOLD
                    : clock_out;
                if (overtime_end > overtime_start)
                    result.overtime_min = (int)(overtime_end - overtime_start).TotalMinutes;
            }

            // ─── 深夜（22:00 以降） ───
            if (clock_out > LATE_NIGHT_THRESHOLD)
            {
                var late_start = clock_in > LATE_NIGHT_THRESHOLD
                    ? clock_in
                    : LATE_NIGHT_THRESHOLD;
                result.late_night_min = (int)(clock_out - late_start).TotalMinutes;
            }

            return result;
        }

        /// <summary>
        /// "HH:mm" 形式の文字列を TimeSpan に変換する
        /// 不正な形式や空文字の場合は null を返す
        /// </summary>
        public static TimeSpan? parse_time(string? time_str)
        {
            if (string.IsNullOrWhiteSpace(time_str))
                return null;
            if (TimeSpan.TryParse(time_str, out var ts))
                return ts;
            return null;
        }

        /// <summary>
        /// 分数を "H:mm" 形式の表示文字列に変換する
        /// 0 分の場合は "0:00"
        /// </summary>
        public static string format_minutes(int minutes)
        {
            if (minutes <= 0) return "0:00";
            int hours = minutes / 60;
            int mins = minutes % 60;
            return $"{hours}:{mins:00}";
        }
    }
}
