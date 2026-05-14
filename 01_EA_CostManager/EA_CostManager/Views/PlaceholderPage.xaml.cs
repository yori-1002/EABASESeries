using System.Windows;
using System.Windows.Controls;

namespace EA_CostManager.Views
{
    public partial class PlaceholderPage : UserControl
    {
        public static readonly DependencyProperty PageTitleProperty =
            DependencyProperty.Register(nameof(PageTitle), typeof(string),
                typeof(PlaceholderPage),
                new PropertyMetadata("準備中", OnPageTitleChanged));

        public static readonly DependencyProperty PageDescriptionProperty =
            DependencyProperty.Register(nameof(PageDescription), typeof(string),
                typeof(PlaceholderPage),
                new PropertyMetadata("次のSprintで実装予定", OnPageDescriptionChanged));

        public string PageTitle
        {
            get => (string)GetValue(PageTitleProperty);
            set => SetValue(PageTitleProperty, value);
        }

        public string PageDescription
        {
            get => (string)GetValue(PageDescriptionProperty);
            set => SetValue(PageDescriptionProperty, value);
        }

        public PlaceholderPage()
        {
            InitializeComponent();
        }

        private static void OnPageTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PlaceholderPage page)
                page.txt_title.Text = e.NewValue?.ToString() ?? "";
        }

        private static void OnPageDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PlaceholderPage page)
                page.txt_description.Text = e.NewValue?.ToString() ?? "";
        }
    }
}