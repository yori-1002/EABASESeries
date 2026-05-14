// UserSessionService.cs
// ログイン中ユーザーのセッション情報を管理するサービスクラス
// シングルトンパターンで全体から参照できるように設計

namespace EA_CostManager.Services
{
    public class UserSessionService
    {
        // シングルトンインスタンス
        private static UserSessionService? _instance;
        public static UserSessionService Instance => _instance ??= new UserSessionService();

        // コンストラクタをprivateにしてシングルトンを強制
        private UserSessionService() { }

        // ログイン中ユーザー名
        public string UserName { get; private set; } = string.Empty;

        // ログイン状態
        public bool IsLoggedIn { get; private set; } = false;

        // ログイン処理
        public void Login(string userName)
        {
            UserName = userName;
            IsLoggedIn = true;
        }

        // ログアウト処理
        public void Logout()
        {
            UserName = string.Empty;
            IsLoggedIn = false;
        }
    }
}