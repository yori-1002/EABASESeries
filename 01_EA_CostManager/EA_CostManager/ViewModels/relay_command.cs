using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace EA_CostManager.ViewModels
{
    /// <summary>
    /// 非同期対応のRelayCommand
    /// ICommandを実装し、ViewModelからコマンドバインディングを可能にする
    /// </summary>
    public class relay_command : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Func<object?, bool>? _can_execute;

        public relay_command(Func<object?, Task> execute, Func<object?, bool>? can_execute = null)
        {
            _execute     = execute;
            _can_execute = can_execute;
        }

        public bool CanExecute(object? parameter)
            => _can_execute?.Invoke(parameter) ?? true;

        public async void Execute(object? parameter)
            => await _execute(parameter);

        public event EventHandler? CanExecuteChanged
        {
            add    => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
