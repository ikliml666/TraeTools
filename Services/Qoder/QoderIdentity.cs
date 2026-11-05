using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TraeTools.Services.Qoder;

/// <summary>
/// Qoder 的设备身份与 PKCE 工具。
///
/// <para>
/// 设备指纹刻意用 <c>MD5("{salt}:{uid}")</c>：上游服务端按同样算法重建这些值，
/// 换 SHA256 会让所有请求被判成非官方客户端。四个值用不同 salt，
/// 避免上游把机器码与会话码直接关联。
/// </para>
/// <para>
/// machine_id 优先读本机 Qoder 客户端留下的原生值；读不到才回落自造值并持久化
/// （持久化失败是硬失败——静默返回会让设备身份每次启动都漂移）。
/// </para>
/// </summary>
public static class QoderIdentity
{
    /// <summary>PKCE verifier 的字符表（RFC 7636 unreserved）。</summary>
    private const string VerifierAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    public static string DeriveId(string uid, string salt)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(salt + ":" + uid));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string MachineToken(string uid)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes("machinetoken:" + uid));
        var b64 = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return b64.Length > 43 ? b64[..43] : b64;
    }

    public static string MachineType(string uid) => DeriveId(uid, "machinetype")[..18];

    /// <summary>
    /// 取 machine_id：先读 Qoder 客户端留下的原生值，读不到则生成并持久化。
    /// </summary>
    public static string ResolveMachineId(QoderRegion region)
    {
        foreach (var candidate in NativeMachineIdPaths(region))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var text = File.ReadAllText(candidate).Trim();
                if (text.Length > 0) return text;
            }
            catch { /* 单个候选读不到就试下一个，最后才生成 */ }
        }

        var path = PersistedMachineIdPath(region);
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length > 0) return existing;
            }
            var generated = Guid.NewGuid().ToString();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, generated);
            return generated;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法持久化 Qoder 设备标识：" + path, ex);
        }
    }

    private static string[] NativeMachineIdPaths(QoderRegion region)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cliDir = region.Id == "cn" ? ".qoder-cn" : ".qoder";
        var appDir = region.Id == "cn" ? "com.qodercn.app.stable" : "com.qoder.app.stable";
        return new[]
        {
            Path.Combine(home, cliDir, ".auth", "machine_id"),
            Path.Combine(appData, appDir, "auth.machine-id"),
        };
    }

    private static string PersistedMachineIdPath(QoderRegion region)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "TraeTools", "qoder", "machine_id." + region.Id + ".txt");

    /// <summary>生成 PKCE 的 verifier 与 S256 challenge。</summary>
    public static (string Verifier, string Challenge) Pkce()
    {
        // 拒绝采样保证字符分布均匀：66 能整除的部分之外的字节全部重取。
        const int limit = 256 - (256 % 66);
        var chars = new char[64];
        var buffer = new byte[128];
        int filled = 0;
        while (filled < chars.Length)
        {
            RandomNumberGenerator.Fill(buffer);
            foreach (var b in buffer)
            {
                if (b >= limit) continue;
                chars[filled++] = VerifierAlphabet[b % 66];
                if (filled == chars.Length) break;
            }
        }
        var verifier = new string(chars);
        return (verifier, Challenge(verifier));
    }

    public static string Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Nonce(QoderRegion region)
        => region.NonceDashed ? Guid.NewGuid().ToString() : Guid.NewGuid().ToString("N");
}
