using CommunityToolkit.Mvvm.ComponentModel;

namespace EA_CostManager.ViewModels
{
    /// <summary>
    /// 全ViewModelの基底クラス
    /// </summary>
    public class base_view_model : ObservableObject
    {
        private bool _is_busy;
        public bool is_busy
        {
            get => _is_busy;
            set => SetProperty(ref _is_busy, value);
        }

        private string _status_message = string.Empty;
        public string status_message
        {
            get => _status_message;
            set => SetProperty(ref _status_message, value);
        }
    }
}