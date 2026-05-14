using System.Net.NetworkInformation;

namespace EA_CostManager
{
    /// <summary>
    /// アプリ全体で現在のログインユーザーを保持する静的クラス
    /// App.xaml.cs の起動時に初期化される
    /// </summary>
    public static class UserSession
    {
        // 現在のログインユーザー情報
        public static int user_id { get; private set; } = 0;
        public static string user_name { get; private set; } = "未設定";
        public static int employee_id { get; private set; } = 0;
        public static bool is_admin { get; private set; } = false;
        public static string mac_address { get; private set; } = "";
        public static string pc_name { get; private set; } = "";

        // ログイン済みかどうか
        public static bool is_logged_in => user_id > 0;

        /// <summary>ログイン情報を設定する</summary>
        public static void set(int id, string name, int emp_id, bool admin)
        {
            user_id = id;
            user_name = name;
            employee_id = emp_id;
            is_admin = admin;
        }

        /// <summary>権限のみを更新する（ポーリングによる他PC権限変更の反映用）</summary>
        public static void update_role(bool admin)
        {
            is_admin = admin;
        }

        /// <summary>このPCのMACアドレスを取得する</summary>
        public static string fetch_mac_address()
        {
            if (!string.IsNullOrEmpty(mac_address)) return mac_address;

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // ループバック・仮想NIC等を除外して最初の物理NICを使用
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (!nic.GetPhysicalAddress().ToString().Any()) continue;

                    string mac = nic.GetPhysicalAddress().ToString();
                    if (mac.Length >= 12)
                    {
                        // XX:XX:XX:XX:XX:XX 形式に整形
                        mac_address = string.Join(":", Enumerable.Range(0, 6)
                            .Select(i => mac.Substring(i * 2, 2)));
                        return mac_address;
                    }
                }
            }
            catch { }

            mac_address = "00:00:00:00:00:00";
            return mac_address;
        }

        /// <summary>このPCのPC名を取得する</summary>
        public static string fetch_pc_name()
        {
            if (!string.IsNullOrEmpty(pc_name)) return pc_name;
            pc_name = Environment.MachineName;
            return pc_name;
        }
    }
}