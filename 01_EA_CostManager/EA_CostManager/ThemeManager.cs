using System.Windows;
using System.Windows.Media;

namespace EA_CostManager
{
    /// <summary>
    /// カラーテーマとフォントサイズをApplication.Resourcesに適用するマネージャー
    /// App.xaml.cs の起動時と設定保存時に呼び出す（即時反映対応）
    /// </summary>
    public static class ThemeManager
    {
        public static void apply(UserSettings settings)
        {
            apply_theme(settings.theme);
            apply_font_size(settings.font_size);
        }

        private static void apply_theme(string theme)
        {
            var res = Application.Current.Resources;

            // サイドバーナビゲーション項目の色も合わせて変更
            switch (theme)
            {
                case "cyan": // 明るい青水色
                    set_color(res, "color_primary", "#1565C0");
                    set_color(res, "color_primary_light", "#1E88E5");
                    set_color(res, "color_accent", "#29B6F6");
                    set_color(res, "color_accent_hover", "#0288D1");
                    set_brush(res, "brush_primary", "#1565C0");
                    set_brush(res, "brush_primary_light", "#1E88E5");
                    set_brush(res, "brush_accent", "#29B6F6");
                    set_brush(res, "brush_accent_hover", "#0288D1");
                    set_brush(res, "brush_nav_selected", "#29B6F6");
                    break;

                case "blue": // 濃い青（元のデザイン）
                    set_color(res, "color_primary", "#1B2A4A");
                    set_color(res, "color_primary_light", "#2D4470");
                    set_color(res, "color_accent", "#3498DB");
                    set_color(res, "color_accent_hover", "#2980B9");
                    set_brush(res, "brush_primary", "#1B2A4A");
                    set_brush(res, "brush_primary_light", "#2D4470");
                    set_brush(res, "brush_accent", "#3498DB");
                    set_brush(res, "brush_accent_hover", "#2980B9");
                    set_brush(res, "brush_nav_selected", "#3498DB");
                    break;

                case "green": // 緑
                    set_color(res, "color_primary", "#1A3A2A");
                    set_color(res, "color_primary_light", "#2A5A3A");
                    set_color(res, "color_accent", "#27AE60");
                    set_color(res, "color_accent_hover", "#1E8449");
                    set_brush(res, "brush_primary", "#1A3A2A");
                    set_brush(res, "brush_primary_light", "#2A5A3A");
                    set_brush(res, "brush_accent", "#27AE60");
                    set_brush(res, "brush_accent_hover", "#1E8449");
                    set_brush(res, "brush_nav_selected", "#27AE60");
                    break;

                case "purple": // パープル
                    // ▼▼▼ 修正：サイドバーを暗すぎる黒紫から淡いミディアムパープルに変更 ▼▼▼
                    set_color(res, "color_primary", "#5C4A9E");
                    set_color(res, "color_primary_light", "#7460B8");
                    set_color(res, "color_accent", "#B39DDB");
                    set_color(res, "color_accent_hover", "#9575CD");
                    set_brush(res, "brush_primary", "#5C4A9E");
                    set_brush(res, "brush_primary_light", "#7460B8");
                    set_brush(res, "brush_accent", "#B39DDB");
                    set_brush(res, "brush_accent_hover", "#9575CD");
                    set_brush(res, "brush_nav_selected", "#B39DDB");
                    break;

                case "gray": // グレー
                    set_color(res, "color_primary", "#2C3E50");
                    set_color(res, "color_primary_light", "#3D5166");
                    set_color(res, "color_accent", "#7F8C8D");
                    set_color(res, "color_accent_hover", "#626567");
                    set_brush(res, "brush_primary", "#2C3E50");
                    set_brush(res, "brush_primary_light", "#3D5166");
                    set_brush(res, "brush_accent", "#7F8C8D");
                    set_brush(res, "brush_accent_hover", "#626567");
                    set_brush(res, "brush_nav_selected", "#7F8C8D");
                    break;

                case "pink": // ピンク
                    // ▼ 修正（v1.0.2 ピンクテーマ淡色化）：
                    //   ・color_primary を #C5719A に決定
                    //     （v1.0.1 の #9C5080 と完全な淡色 #E091B0 の中間トーン）
                    //   ・実機検証の経緯：
                    //     #E8A8BF → 文字が薄くなりすぎたためNG
                    //     #E091B0 → 「メニュー」等の薄グレー文字（#BDC3C7）が読みづらいためNG
                    //     #C5719A → 文字色を変更せずに可読性を確保しつつ淡くする落とし所
                    //   ・color_primary_light は primary より明るい色を維持
                    //     （マウスホバー時のコントラストを保つため、primary より明るく設定）
                    //   ・color_accent / color_accent_hover はナビ選択・ボタンホバー用のため
                    //     現状値を維持（変更すると他UIへの影響が大きいため）
                    set_color(res, "color_primary", "#C5719A");           // ▼ 修正 旧 #9C5080
                    set_color(res, "color_primary_light", "#D480A9");     // ▼ 修正 旧 #C06898
                    set_color(res, "color_accent", "#F48FB1");
                    set_color(res, "color_accent_hover", "#E91E63");
                    set_brush(res, "brush_primary", "#C5719A");           // ▼ 修正 旧 #9C5080
                    set_brush(res, "brush_primary_light", "#D480A9");     // ▼ 修正 旧 #C06898
                    set_brush(res, "brush_accent", "#F48FB1");
                    set_brush(res, "brush_accent_hover", "#E91E63");
                    set_brush(res, "brush_nav_selected", "#F48FB1");
                    break;
            }
        }

        private static void apply_theme_common(ResourceDictionary res)
        {
            // サイドバーを少し明るく（各テーマ共通・primaryより15%明るい）
            // SettingsPage等の左ナビ背景色として使用
        }

        private static void apply_font_size(string font_size)
        {
            var res = Application.Current.Resources;
            double base_size = font_size switch
            {
                "small" => 11.0,
                "large" => 14.0,
                _ => 12.0
            };
            res["font_size_base"] = base_size;
            res["font_size_small"] = base_size - 1;
            res["font_size_large"] = base_size + 3;
        }

        private static void set_color(ResourceDictionary res, string key, string hex)
        {
            try { res[key] = (Color)ColorConverter.ConvertFromString(hex); } catch { }
        }

        private static void set_brush(ResourceDictionary res, string key, string hex)
        {
            try { res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); } catch { }
        }
    }
}