using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EA_DailyReport.Controls
{
    /// <summary>
    /// オートコンプリート + スクロール選択 UserControl（v0.1.6 新設）
    ///
    /// VBA UserForm の ComboBox 操作感を WPF で再現する
    /// DataGrid セル内・ダイアログ内のどちらでも使える汎用コントロール
    ///
    /// 使い方：
    ///   <local:AutoCompleteTextBox
    ///       Text="{Binding category_code, UpdateSourceTrigger=PropertyChanged}"
    ///       ItemsSource="{Binding DataContext.category_list,
    ///                     RelativeSource={RelativeSource AncestorType=Window}}"/>
    ///
    /// 操作：
    ///   セルクリック → 全候補ポップアップ表示
    ///   タイピング   → 候補絞り込み（部分一致・大文字小文字無視）
    ///   ↑↓キー       → 候補ハイライト移動
    ///   マウスホイール → スクロール
    ///   Enter / クリック → 選択確定
    ///   Esc          → ポップアップ閉じる
    ///   Tab          → 次のコントロールへ
    /// </summary>
    public partial class AutoCompleteTextBox : UserControl
    {
        // ────────────────────────────────────────────────
        // DependencyProperty 定義（XAML バインディング用）
        // ────────────────────────────────────────────────

        /// <summary>
        /// テキスト内容（双方向バインド可能）
        /// 既定で TwoWay バインドする（FrameworkPropertyMetadataOptions.BindsTwoWayByDefault）
        /// </summary>
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(AutoCompleteTextBox),
                new FrameworkPropertyMetadata(
                    "",
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    on_text_changed_dp));

        public string Text
        {
            get => (string)(GetValue(TextProperty) ?? "");
            set => SetValue(TextProperty, value);
        }

        /// <summary>候補リスト（IEnumerable<string> をバインドする想定）</summary>
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(IEnumerable),
                typeof(AutoCompleteTextBox),
                new PropertyMetadata(null));

        public IEnumerable? ItemsSource
        {
            get => (IEnumerable?)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        // ────────────────────────────────────────────────
        // 内部状態
        // ────────────────────────────────────────────────

        // TextBox から「自分でテキスト書き換えた」か「ユーザー入力か」を区別するフラグ
        // 候補選択時に Text プロパティを書き換えると TextChanged が再帰発火するのを防ぐ
        private bool _suppress_text_changed = false;

        // ────────────────────────────────────────────────
        // コンストラクタ
        // ────────────────────────────────────────────────

        public AutoCompleteTextBox()
        {
            InitializeComponent();
        }

        // ────────────────────────────────────────────────
        // DependencyProperty 変更通知
        // ────────────────────────────────────────────────

        /// <summary>
        /// 外部から Text プロパティが変更された時の同期処理
        /// （バインドソース更新で外側から値が来た場合）
        /// </summary>
        private static void on_text_changed_dp(
            DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not AutoCompleteTextBox ac) return;
            if (ac.input_box == null) return;

            string new_value = e.NewValue as string ?? "";
            if (ac.input_box.Text == new_value) return;

            // 内部 TextBox にだけ反映（TextChanged 発火を抑制）
            ac._suppress_text_changed = true;
            try { ac.input_box.Text = new_value; }
            finally { ac._suppress_text_changed = false; }
        }

        // ────────────────────────────────────────────────
        // TextBox イベント
        // ────────────────────────────────────────────────

        /// <summary>
        /// テキスト変更時：候補絞り込み + ポップアップ表示
        /// ユーザーがタイピングした場合のみ発動（_suppress_text_changed で再帰防止）
        /// </summary>
        private void input_box_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress_text_changed) return;

            // バインドソース側に新しい値を伝播
            // ※ Text プロパティ経由なので双方向バインドで親へ通知される
            Text = input_box.Text;

            // 候補リストを絞り込んで表示
            update_suggestions(filter: input_box.Text);
        }

        /// <summary>
        /// フォーカス取得時：全候補を表示（VBA ComboBox と同じ操作感）
        /// </summary>
        private void input_box_GotFocus(object sender, RoutedEventArgs e)
        {
            // フォーカス時はテキスト全選択（編集しやすく）
            input_box.SelectAll();
            // 全候補表示
            update_suggestions(filter: "");
        }

        /// <summary>
        /// フォーカス喪失時：ポップアップを閉じる
        /// ただし、ポップアップ内のクリックでフォーカスが外れた場合は除外する必要があるが、
        /// Popup の StaysOpen=False が大体の場合はうまく処理してくれる
        /// </summary>
        private void input_box_LostFocus(object sender, RoutedEventArgs e)
        {
            // ListBox に移ったケース対応のため少し遅延させる
            // → 即座に閉じると、ListBox クリック時に発火順の関係で閉じてしまう
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!input_box.IsKeyboardFocusWithin
                 && !suggest_list.IsKeyboardFocusWithin)
                {
                    suggest_popup.IsOpen = false;
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>
        /// キー入力処理：↑↓ / Enter / Esc / Tab
        /// マウスホイールは ListBox 標準で動作するため未処理
        /// </summary>
        private void input_box_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    // ポップアップ未表示なら開く・既に開いてたら下移動
                    if (!suggest_popup.IsOpen)
                    {
                        update_suggestions(filter: "");
                    }
                    else
                    {
                        move_selection(+1);
                    }
                    e.Handled = true;
                    break;

                case Key.Up:
                    if (suggest_popup.IsOpen)
                    {
                        move_selection(-1);
                        e.Handled = true;
                    }
                    break;

                case Key.Enter:
                    // ハイライト中の候補を選択確定
                    if (suggest_popup.IsOpen
                     && suggest_list.SelectedItem is string sel
                     && !string.IsNullOrEmpty(sel))
                    {
                        commit_selection(sel);
                        e.Handled = true;
                    }
                    break;

                case Key.Escape:
                    // ポップアップ閉じる
                    if (suggest_popup.IsOpen)
                    {
                        suggest_popup.IsOpen = false;
                        e.Handled = true;
                    }
                    break;

                case Key.Tab:
                    // 候補が選択済みなら確定してから次へ
                    if (suggest_popup.IsOpen
                     && suggest_list.SelectedItem is string seltab
                     && !string.IsNullOrEmpty(seltab))
                    {
                        commit_selection(seltab);
                    }
                    suggest_popup.IsOpen = false;
                    // Tab の標準動作（次のコントロールへフォーカス移動）は妨げない
                    break;
            }
        }

        // ────────────────────────────────────────────────
        // ListBox イベント
        // ────────────────────────────────────────────────

        /// <summary>
        /// ダブルクリックで選択確定
        /// </summary>
        private void suggest_list_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (suggest_list.SelectedItem is string sel && !string.IsNullOrEmpty(sel))
            {
                commit_selection(sel);
                e.Handled = true;
            }
        }

        /// <summary>
        /// シングルクリックでも選択確定（VBA ComboBox に近い操作感）
        /// </summary>
        private void suggest_list_PreviewMouseLeftButtonUp(
            object sender, MouseButtonEventArgs e)
        {
            // クリックされた要素が候補なら選択確定
            // FrameworkElement → DataContext を辿って項目を特定する
            if (e.OriginalSource is FrameworkElement fe
             && fe.DataContext is string sel
             && !string.IsNullOrEmpty(sel))
            {
                commit_selection(sel);
                e.Handled = true;
            }
        }

        // ────────────────────────────────────────────────
        // 内部処理
        // ────────────────────────────────────────────────

        /// <summary>
        /// 候補リストを絞り込んで ListBox に反映する
        /// filter が空文字なら全候補表示・指定があれば部分一致（大文字小文字無視）
        /// 候補が0件ならポップアップを閉じる
        /// </summary>
        private void update_suggestions(string filter)
        {
            if (ItemsSource == null)
            {
                suggest_popup.IsOpen = false;
                return;
            }

            // ItemsSource を List<string> として取り出す
            var all = ItemsSource.Cast<object>()
                                 .Select(o => o?.ToString() ?? "")
                                 .Where(s => !string.IsNullOrEmpty(s))
                                 .ToList();

            // 絞り込み（部分一致・大文字小文字無視）
            List<string> filtered;
            if (string.IsNullOrEmpty(filter))
            {
                filtered = all;
            }
            else
            {
                filtered = all.Where(s =>
                    s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                ).ToList();
            }

            // 候補なし → ポップアップ閉じる
            if (filtered.Count == 0)
            {
                suggest_popup.IsOpen = false;
                return;
            }

            // 完全一致1件のみで現在の入力と同じ → 表示する意味なし（重複表示防止）
            if (filtered.Count == 1
             && string.Equals(filtered[0], filter, StringComparison.Ordinal))
            {
                suggest_popup.IsOpen = false;
                return;
            }

            suggest_list.ItemsSource = filtered;

            // 既存入力に完全一致する候補があればハイライト
            int idx = filtered.FindIndex(s =>
                string.Equals(s, filter, StringComparison.Ordinal));
            suggest_list.SelectedIndex = idx >= 0 ? idx : 0;

            // ポップアップを表示
            suggest_popup.IsOpen = true;
        }

        /// <summary>
        /// ListBox 内の選択を上下に移動する（範囲外にならないように）
        /// </summary>
        private void move_selection(int delta)
        {
            if (suggest_list.Items.Count == 0) return;

            int next = suggest_list.SelectedIndex + delta;
            if (next < 0) next = 0;
            if (next >= suggest_list.Items.Count) next = suggest_list.Items.Count - 1;

            suggest_list.SelectedIndex = next;
            suggest_list.ScrollIntoView(suggest_list.SelectedItem);
        }

        /// <summary>
        /// 選択を確定する（テキストを反映 + ポップアップ閉じる）
        /// 末尾にカーソル移動して、続けて編集しやすくする
        /// </summary>
        private void commit_selection(string value)
        {
            _suppress_text_changed = true;
            try
            {
                input_box.Text = value;
                input_box.CaretIndex = value.Length;
            }
            finally { _suppress_text_changed = false; }

            // バインドソースに反映
            Text = value;

            suggest_popup.IsOpen = false;
        }
    }
}
