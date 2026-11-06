using System;
using System.Threading.Tasks;

namespace TraeCheckin;

/// <summary>
/// 签到请求的重试策略：9074（风控拒签）与 9095（该设备今日已被签）都表示
/// 「这个设备号当前签不了」，换一个全新设备号重签；其它业务码换号无效，如实上报。
/// 不依赖 MainViewModel，便于离线验证。
/// </summary>
internal static class CheckinRetry
{
    public const int MaxAttempts = 3;
    public const int RetryDelayMs = 30_000;
    private const int CodeDeviceRejected = 9074;
    private const int CodeDeviceAlreadyUsed = 9095;

    public static async Task<(CheckinStatus? Result, string FailureReason)> ClaimAsync(
        TraeApiClient api, TraeAccount acc, Func<string> newDeviceId,
        Action<string> log, Action persist, int delayMs = RetryDelayMs)
    {
        int lastCode = -1;
        string lastMessage = "";
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            log($"调用 ClaimAsync（第 {attempt}/{MaxAttempts} 次），DeviceId={acc.DeviceId}");
            var result = await api.ClaimAsync(acc.Token!, acc.DeviceId!);
            if (result == null)
            {
                // 网络/解析失败与设备号无关，换号重试没有意义
                log($"Claim 请求失败：{api.LastError ?? "null response"}");
                return (null, api.LastError ?? "签到失败：网络或接口异常");
            }
            if (result.code == 0)
            {
                log("Claim 成功：code=0");
                return (result, "");
            }

            lastCode = result.code;
            lastMessage = result.message ?? "";
            log($"Claim 失败：code={lastCode}, message={lastMessage}");
            if (lastCode != CodeDeviceRejected && lastCode != CodeDeviceAlreadyUsed)
                return (null, $"签到失败：{lastMessage}（code={lastCode}）");
            if (attempt == MaxAttempts) break;

            acc.DeviceId = newDeviceId();
            log($"换用新设备号 {acc.DeviceId}，等待 {delayMs / 1000} 秒后重签");
            await Task.Delay(delayMs);
        }
        persist();
        return (null, $"签到失败：{lastMessage}（code={lastCode}，已换号 {MaxAttempts - 1} 次仍被拒）");
    }
}
