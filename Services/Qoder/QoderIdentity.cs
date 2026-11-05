using System;
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
/// machine_id 每账号独立：授权 URL 里的 <c>machine_id</c> 决定上游把这个账号注册到哪台
/// 机器上，所有账号共用一份会让服务端把多个账号聚合成同一台设备。刻意不复用本机 Qoder
/// 客户端留下的原生值——那会把账号绑到真实设备，正是要隔离的东西。
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
    /// 生成一份新的设备标识，用于授权 URL 的 <c>machine_id</c>。
    /// 每次登录都取一个新值：它决定上游把这个账号注册到哪台机器，
    /// 账号之间绝不共用，上游才不会把多个账号聚合成同一台设备。
    /// </summary>
    public static string NewMachineId() => Guid.NewGuid().ToString();

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
