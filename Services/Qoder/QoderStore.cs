using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TraeTools.Services.Json;

namespace TraeTools.Services.Qoder;

/// <summary>
/// Qoder 账号存储：单独一个 <c>%APPDATA%\TraeTools\qoder\accounts.json</c>。
///
/// <para>
/// 不与 Trae 账号混存：两套凭据的字段、刷新契约、失效判定全不同，
/// 混进 config.json 会让 <see cref="TraeCheckin.AppConfig"/> 的落盘白名单变复杂。
/// </para>
/// <para>
/// 令牌用 DPAPI（当前用户作用域）加密，与 Trae 侧同一套保护；解密失败（换用户/重装）
/// 视为令牌丢失，由上层触发重新登录。
/// </para>
/// </summary>
public sealed class QoderStore
{
    private const string VaultVersion = "dpapi-v1";

    public List<QoderAccount> Accounts { get; private set; } = new();

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeTools", "qoder");

    public static string FilePath => Path.Combine(Dir, "accounts.json");

    public static QoderStore Load()
    {
        var store = new QoderStore();
        try
        {
            if (!File.Exists(FilePath)) return store;
            var text = File.ReadAllText(FilePath);
            var doc = AppJson.Deserialize<QoderStoreDocument>(text);
            if (doc?.Accounts == null) return store;

            var sealedVault = string.Equals(doc.VaultVersion, VaultVersion, StringComparison.Ordinal);
            foreach (var acc in doc.Accounts)
            {
                if (sealedVault)
                {
                    acc.AccessToken = Unprotect(acc.AccessToken);
                    acc.RefreshToken = Unprotect(acc.RefreshToken);
                    acc.PersonalToken = Unprotect(acc.PersonalToken);
                }
                store.Accounts.Add(acc);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Qoder 账号文件无法解析：" + FilePath, ex);
        }
        return store;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var doc = new QoderStoreDocument
        {
            VaultVersion = VaultVersion,
            Accounts = new List<QoderAccount>(Accounts.Count),
        };
        foreach (var acc in Accounts)
        {
            // 写入副本：加密后的令牌不能污染内存里的活对象
            doc.Accounts.Add(new QoderAccount
            {
                Id = acc.Id,
                Name = acc.Name,
                Domain = acc.Domain,
                AccessToken = Protect(acc.AccessToken),
                RefreshToken = Protect(acc.RefreshToken),
                PersonalToken = Protect(acc.PersonalToken),
                ExpiresAt = acc.ExpiresAt,
                AddedAt = acc.AddedAt,
                Remaining = acc.Remaining,
                Used = acc.Used,
                Total = acc.Total,
                IsExceeded = acc.IsExceeded,
                Plan = acc.Plan,
                LastClaimMessage = acc.LastClaimMessage,
                LastClaimAt = acc.LastClaimAt,
            });
        }
        var json = AppJson.Serialize(doc);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>按 Id 命中则更新令牌，否则新增。返回该账号。</summary>
    public QoderAccount Upsert(QoderUserInfo info, QoderRegion region, QoderDeviceToken token)
    {
        var existing = Accounts.Find(a => a.Id == info.Id);
        if (existing == null)
        {
            existing = new QoderAccount { Id = info.Id, AddedAt = DateTime.Now };
            Accounts.Add(existing);
        }
        existing.Name = string.IsNullOrEmpty(info.Name) ? null : info.Name;
        existing.Domain = region.Domain;
        existing.AccessToken = token.AccessToken;
        if (!string.IsNullOrEmpty(token.RefreshToken)) existing.RefreshToken = token.RefreshToken;
        existing.ExpiresAt = token.ExpiresAt;
        return existing;
    }

    public bool Remove(string id)
    {
        var idx = Accounts.FindIndex(a => a.Id == id);
        if (idx < 0) return false;
        Accounts.RemoveAt(idx);
        return true;
    }

    /// <summary>
    /// 只更新令牌字段。刷新路径专用——<see cref="Upsert"/> 是登录/导入路径，
    /// 用它回写刷新结果会把本地备注、昵称、积分等上游不知道的字段整条清掉。
    /// </summary>
    public bool SetTokens(string id, QoderTokenRefresh token)
    {
        var account = Accounts.Find(a => a.Id == id);
        if (account == null) return false;
        if (!string.IsNullOrEmpty(token.AccessToken)) account.AccessToken = token.AccessToken;
        if (!string.IsNullOrEmpty(token.RefreshToken)) account.RefreshToken = token.RefreshToken;
        if (token.ExpiresAt != null) account.ExpiresAt = token.ExpiresAt;
        return true;
    }

    private static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return enc;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}

/// <summary>落盘结构（外层容器，与账号列表分开便于未来加字段）。</summary>
internal sealed class QoderStoreDocument
{
    [JsonPropertyName("vaultVersion")] public string? VaultVersion { get; set; }
    [JsonPropertyName("accounts")] public List<QoderAccount>? Accounts { get; set; }
}
