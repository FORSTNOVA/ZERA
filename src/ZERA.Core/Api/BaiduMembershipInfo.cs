using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZERA.Core.Api
{
    public sealed class BaiduMembershipInfo
    {
        public string Kind { get; private set; }
        public bool? IsMember { get; private set; }
        public DateTime? ExpiresUtc { get; private set; }
        public DateTime QueriedUtc { get; private set; }
        public string TrafficNote => "无法查询：当前已核实接口未提供剩余极速流量字段，请到官方客户端核对。";

        // Membership endpoint fields differ from loginInfo and the OAuth user-info API.
        public static BaiduMembershipInfo Parse(IDictionary<string, object> data)
        {
            if (data == null || !data.TryGetValue("error_code", out var code) || !long.TryParse(Convert.ToString(code, CultureInfo.InvariantCulture), out long error))
                throw new InvalidOperationException("百度会员接口未返回有效状态；无法判断会员身份。");
            if (error != 0) throw new InvalidOperationException("百度会员查询失败（代码 " + error + "），请检查登录状态后重试。");
            var current = Dict(Get(data, "current_product"));
            string cluster = Convert.ToString(Get(current, "detail_cluster"), CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(cluster)) cluster = Convert.ToString(Get(current, "cluster"), CultureInfo.InvariantCulture);
            var result = new BaiduMembershipInfo { QueriedUtc = DateTime.UtcNow };
            string reminderKey = null;
            switch (cluster)
            {
                case "svip": result.Kind = "SVIP 超级会员"; result.IsMember = true; reminderKey = "svip"; break;
                case "vip": result.Kind = "VIP 会员"; result.IsMember = true; reminderKey = "vip"; break;
                case "vipv2": result.Kind = "VIP 会员（vipv2）"; result.IsMember = true; reminderKey = "vipv2"; break;
                case "":
                    if (current == null || current.Count > 0) { result.Kind = "未知（响应缺少会员资料）"; break; }
                    result.Kind = "普通用户（无当前 VIP / SVIP 产品）"; result.IsMember = false; break;
                case "normal": result.Kind = "普通用户（无当前 VIP / SVIP 产品）"; result.IsMember = false; break;
                default: result.Kind = "未知会员产品（暂未识别）"; break;
            }
            if (reminderKey != null)
            {
                var reminder = Dict(Get(data, "reminder"));
                var product = Dict(Get(reminder, reminderKey));
                long? now = Number(Get(data, "currenttime")), seconds = Number(Get(product, "leftseconds"));
                if (now > 0 && seconds > 0 && seconds < 100L * 366 * 86400)
                {
                    try { result.ExpiresUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(checked(now.Value + seconds.Value)); }
                    catch (ArgumentOutOfRangeException) { }
                    catch (OverflowException) { }
                }
            }
            return result;
        }
        private static object Get(IDictionary<string, object> data, string key) => data != null && data.TryGetValue(key, out var value) ? value : null;
        private static IDictionary<string, object> Dict(object value) => value as IDictionary<string, object>;
        private static long? Number(object value) => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out long result) ? (long?)result : null;
    }
}
